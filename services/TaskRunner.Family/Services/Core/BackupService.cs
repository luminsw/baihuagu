using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;
using TaskRunner.Data.Entities;
using TaskRunner.Services.Security;

namespace TaskRunner.Services;

/// <summary>
/// 全量备份恢复服务 - 备份所有配置与数据，支持跨平台恢复
///
/// 备份内容：
/// - SQLite 数据库（5 张表导出为 JSON）
/// - JSON 配置文件（webui.settings.json 等，知识库根路径改为环境变量 TASKRUNNER_VAULT_ROOT 配置）
/// - 知识库文件（notes/, cards/）
/// - WebUI 配置（webui.settings.json, user_preferences.json）
///
/// 跨平台支持：
/// - 路径存储为相对路径 + 根路径标记，恢复时重映射
/// - API Key 在备份时解密并用备份密码重新加密，恢复时用目标机器指纹重新加密
/// - 备份格式为 ZIP 内含 JSON，无二进制兼容问题
///
/// 备份格式：
///   backup.zip
///   ├── manifest.json          # 元数据（版本、时间、源平台、校验和）
///   ├── db/
///   │   ├── vaults.json        # Vaults 表数据
///   │   ├── ai_providers.json  # AiProviderSettings 表数据（API Key 用备份密码加密）
///   │   ├── tasks.json         # Tasks 表数据
///   │   ├── devices.json       # AuthorizedDevices 表数据
///   │   └── server_address.json # ServerAddressSettings 表数据
///   ├── config/
///   │   ├── webui_settings.json
///   │   └── user_preferences.json
///   └── vaults/
///       └── {vault_name}/      # 每个知识库一个目录
///           ├── notes/
///           └── cards/
/// </summary>
public class BackupService
{
    private readonly SettingsService _settings;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ApiKeyProtectionService _apiKeyProtection;
    private readonly DataEncryptionService _dataEncryption;
    private readonly ILogger<BackupService> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public BackupService(
        SettingsService settings,
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyProtectionService apiKeyProtection,
        DataEncryptionService dataEncryption,
        ILogger<BackupService> logger)
    {
        _settings = settings;
        _dbContextFactory = dbContextFactory;
        _apiKeyProtection = apiKeyProtection;
        _dataEncryption = dataEncryption;
        _logger = logger;
    }

    #region Create Full Backup

    /// <summary>
    /// 创建全量备份
    /// </summary>
    public async Task<FullBackupResult> CreateFullBackupAsync(string? backupDir = null, string? password = null, CancellationToken cancellationToken = default)
    {
        var timestamp = DateTime.UtcNow;
        var backupFileName = $"doctor_notes_backup_{timestamp:yyyyMMdd_HHmmss}.zip";
        var backupFullPath = string.IsNullOrEmpty(backupDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoctorNotesBackups", backupFileName)
            : Path.Combine(backupDir, backupFileName);

        var backupDirPath = Path.GetDirectoryName(backupFullPath);
        if (!string.IsNullOrEmpty(backupDirPath) && !Directory.Exists(backupDirPath))
        {
            Directory.CreateDirectory(backupDirPath);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"dn_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. 导出数据库表为 JSON
            await ExportDatabaseAsync(tempDir, password, cancellationToken);

            // 2. 导出配置文件
            ExportConfigFiles(tempDir);

            // 3. 导出知识库文件
            await ExportVaultFilesAsync(tempDir, cancellationToken);

            // 4. 写入 manifest
            var manifest = new BackupManifest
            {
                Version = 2,
                CreatedAt = timestamp,
                SourcePlatform = Environment.OSVersion.Platform.ToString(),
                SourceOS = Environment.OSVersion.ToString(),
                SourceMachineName = Environment.MachineName,
                HasPassword = !string.IsNullOrEmpty(password),
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "unknown"
            };
            var manifestJson = JsonSerializer.Serialize(manifest, _jsonOpts);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "manifest.json"), manifestJson);

            // 5. 创建 ZIP
            ZipFile.CreateFromDirectory(tempDir, backupFullPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            _logger.LogInformation("全量备份创建成功：{Path}，大小：{Size} bytes", backupFullPath, new FileInfo(backupFullPath).Length);

            return new FullBackupResult
            {
                Success = true,
                BackupPath = backupFullPath,
                BackupTime = timestamp,
                FileSize = new FileInfo(backupFullPath).Length
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("创建全量备份已取消");
            return new FullBackupResult
            {
                Success = false,
                Error = "备份已取消"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建全量备份失败");
            return new FullBackupResult
            {
                Success = false,
                Error = ex.Message
            };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "清理临时目录失败: {TempDir}", tempDir); }
            }
        }
    }

    /// <summary>
    /// 导出数据库表为 JSON
    /// </summary>
    private async Task ExportDatabaseAsync(string tempDir, string? password, CancellationToken cancellationToken)
    {
        var dbDir = Path.Combine(tempDir, "db");
        Directory.CreateDirectory(dbDir);

        using var db = _dbContextFactory.CreateDbContext();

        cancellationToken.ThrowIfCancellationRequested();

        // Vaults - 路径转为相对路径
        var vaults = await db.Vaults.ToListAsync(cancellationToken);
        var vaultRootPath = _settings.VaultRootPathPreference;
        var vaultsData = vaults.Select(v => new
        {
            v.Id, v.VaultId, v.Name,
            Path = v.Path,
            RelativePath = MakeRelativePath(v.Path, vaultRootPath),
            v.IsActive, v.CreatedAt, v.UpdatedAt
        }).ToList();
        await File.WriteAllTextAsync(Path.Combine(dbDir, "vaults.json"),
            JsonSerializer.Serialize(vaultsData, _jsonOpts));

        // AiProviderSettings - API Key 解密后用备份密码重新加密
        var providers = await db.AiProviderSettings.ToListAsync(cancellationToken);
        var providersData = providers.Select(p =>
        {
            var plainApiKey = _apiKeyProtection.Decrypt(p.EncryptedApiKey);
            string protectedApiKey;
            if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(plainApiKey))
            {
                // 用备份密码加密 API Key
                protectedApiKey = _dataEncryption.Encrypt(plainApiKey, password);
            }
            else if (!string.IsNullOrEmpty(plainApiKey))
            {
                // 无备份密码时，使用机器指纹加密（避免明文存储）
                protectedApiKey = _apiKeyProtection.Encrypt(plainApiKey);
            }
            else
            {
                protectedApiKey = "";
            }

            return new
            {
                p.Id, p.ProviderId, p.ProviderName, p.BaseUrl,
                ProtectedApiKey = protectedApiKey,
                KeyProtection = !string.IsNullOrEmpty(password) ? "BackupPassword" : "MachineKey",
                p.ModelsJson, p.IsMain, p.IsEnabled, p.SortOrder,
                p.CreatedAt, p.UpdatedAt
            };
        }).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(dbDir, "ai_providers.json"),
            JsonSerializer.Serialize(providersData, _jsonOpts), cancellationToken);

        // Tasks
        var tasks = await db.Tasks.ToListAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dbDir, "tasks.json"),
            JsonSerializer.Serialize(tasks, _jsonOpts), cancellationToken);

        // AuthorizedDevices
        var devices = await db.AuthorizedDevices.ToListAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dbDir, "devices.json"),
            JsonSerializer.Serialize(devices, _jsonOpts), cancellationToken);

        // ServerAddressSettings - 不导出 ServerInstanceId（恢复时重新生成）
        var serverAddr = await db.ServerAddressSettings.ToListAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dbDir, "server_address.json"),
            JsonSerializer.Serialize(serverAddr, _jsonOpts), cancellationToken);
    }

    /// <summary>
    /// 导出配置文件
    /// </summary>
    private void ExportConfigFiles(string tempDir)
    {
        var configDir = Path.Combine(tempDir, "config");
        Directory.CreateDirectory(configDir);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // vault.root.path.json 已废弃（知识库根路径改为环境变量配置），不再备份
        // 旧备份中可能仍有此文件，但恢复时会被忽略

        // WebUI 配置文件（可能在 WebUI 的 base directory）
        var webuiSettingsPath = Path.Combine(baseDir, "webui.settings.json");
        if (File.Exists(webuiSettingsPath))
        {
            File.Copy(webuiSettingsPath, Path.Combine(configDir, "webui_settings.json"), overwrite: true);
        }

        var userPrefsPath = Path.Combine(baseDir, "data", "user_preferences.json");
        if (File.Exists(userPrefsPath))
        {
            File.Copy(userPrefsPath, Path.Combine(configDir, "user_preferences.json"), overwrite: true);
        }
    }

    /// <summary>
    /// 导出知识库文件
    /// </summary>
    private async Task ExportVaultFilesAsync(string tempDir, CancellationToken cancellationToken)
    {
        var vaultsDir = Path.Combine(tempDir, "vaults");
        Directory.CreateDirectory(vaultsDir);

        using var db = _dbContextFactory.CreateDbContext();
        var vaults = await db.Vaults.ToListAsync(cancellationToken);

        foreach (var vault in vaults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(vault.Path) || !Directory.Exists(vault.Path))
                continue;

            // 用知识库名称作为目录名（替换不安全字符）
            var safeName = string.Join("_", vault.Name.Split(Path.GetInvalidFileNameChars()));
            var vaultBackupDir = Path.Combine(vaultsDir, safeName);
            Directory.CreateDirectory(vaultBackupDir);

            // 复制 notes/ 和 cards/ 子目录
            var notesDir = Path.Combine(vault.Path, "notes");
            if (Directory.Exists(notesDir))
            {
                CopyDirectory(notesDir, Path.Combine(vaultBackupDir, "notes"), cancellationToken: cancellationToken);
            }

            var cardsDir = Path.Combine(vault.Path, "cards");
            if (Directory.Exists(cardsDir))
            {
                CopyDirectory(cardsDir, Path.Combine(vaultBackupDir, "cards"), cancellationToken: cancellationToken);
            }

            // 复制 images/ 子目录（如果存在）
            var imagesDir = Path.Combine(vault.Path, "images");
            if (Directory.Exists(imagesDir))
            {
                CopyDirectory(imagesDir, Path.Combine(vaultBackupDir, "images"), cancellationToken: cancellationToken);
            }
        }
    }

    #endregion

    #region Restore Full Backup

    /// <summary>
    /// 恢复全量备份
    /// </summary>
    public async Task<FullRestoreResult> RestoreFullBackupAsync(
        string backupPath,
        string? password = null,
        string? vaultRootPathOverride = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath))
        {
            return new FullRestoreResult { Success = false, Error = "备份文件不存在" };
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"dn_restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. 解压备份
            ZipFile.ExtractToDirectory(backupPath, tempDir);

            // 2. 读取并验证 manifest
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return new FullRestoreResult { Success = false, Error = "无效的备份文件：缺少 manifest.json" };
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);
            if (manifest == null || manifest.Version < 2)
            {
                return new FullRestoreResult { Success = false, Error = "不支持的备份格式版本" };
            }

            if (manifest.HasPassword && string.IsNullOrEmpty(password))
            {
                return new FullRestoreResult { Success = false, Error = "此备份有密码保护，请输入密码" };
            }

            // 3. 确定知识库根路径
            var vaultRootPath = !string.IsNullOrEmpty(vaultRootPathOverride)
                ? vaultRootPathOverride
                : _settings.VaultRootPathPreference;

            // 4. 恢复数据库
            var dbResult = await RestoreDatabaseAsync(tempDir, password, vaultRootPath, overwrite, cancellationToken);
            if (!dbResult)
            {
                return new FullRestoreResult { Success = false, Error = "恢复数据库失败" };
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 5. 恢复配置文件
            RestoreConfigFiles(tempDir);

            // 6. 恢复知识库文件
            await RestoreVaultFilesAsync(tempDir, vaultRootPath, overwrite, cancellationToken);

            _logger.LogInformation("全量备份恢复成功：{Path}", backupPath);

            return new FullRestoreResult
            {
                Success = true,
                SourcePlatform = manifest.SourcePlatform,
                SourceOS = manifest.SourceOS,
                RestoredAt = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("恢复全量备份已取消");
            return new FullRestoreResult
            {
                Success = false,
                Error = "恢复已取消"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复全量备份失败");
            return new FullRestoreResult
            {
                Success = false,
                Error = ex.Message
            };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "清理临时目录失败: {TempDir}", tempDir); }
            }
        }
    }

    /// <summary>
    /// 恢复数据库
    /// </summary>
    private async Task<bool> RestoreDatabaseAsync(string tempDir, string? password, string vaultRootPath, bool overwrite, CancellationToken cancellationToken)
    {
        var dbDir = Path.Combine(tempDir, "db");
        if (!Directory.Exists(dbDir)) return true;

        using var db = _dbContextFactory.CreateDbContext();

        cancellationToken.ThrowIfCancellationRequested();

        // Vaults
        var vaultsPath = Path.Combine(dbDir, "vaults.json");
        if (File.Exists(vaultsPath))
        {
            var json = await File.ReadAllTextAsync(vaultsPath, cancellationToken);
            var vaultsData = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (vaultsData != null)
            {
                if (overwrite)
                {
                    db.Vaults.RemoveRange(db.Vaults);
                }

                foreach (var v in vaultsData)
                {
                    var path = v.GetProperty("Path").GetString() ?? "";
                    var relativePath = v.TryGetProperty("RelativePath", out var rp) ? rp.GetString() : null;

                    // 跨平台路径重映射：优先使用相对路径 + 新根路径
                    var finalPath = !string.IsNullOrEmpty(relativePath) && !string.IsNullOrEmpty(vaultRootPath)
                        ? Path.Combine(vaultRootPath, relativePath)
                        : RemapPath(path);

                    var vault = new Vault
                    {
                        VaultId = v.GetProperty("VaultId").GetString() ?? Guid.NewGuid().ToString(),
                        Name = v.GetProperty("Name").GetString() ?? "",
                        Path = finalPath,
                        IsActive = v.TryGetProperty("IsActive", out var ia) && ia.GetBoolean(),
                        CreatedAt = v.GetProperty("CreatedAt").GetDateTime(),
                        UpdatedAt = v.GetProperty("UpdatedAt").GetDateTime()
                    };

                    if (!db.Vaults.Any(x => x.VaultId == vault.VaultId))
                    {
                        db.Vaults.Add(vault);
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // AiProviderSettings
        var providersPath = Path.Combine(dbDir, "ai_providers.json");
        if (File.Exists(providersPath))
        {
            var json = await File.ReadAllTextAsync(providersPath, cancellationToken);
            var providersData = JsonSerializer.Deserialize<List<JsonElement>>(json);
            if (providersData != null)
            {
                if (overwrite)
                {
                    db.AiProviderSettings.RemoveRange(db.AiProviderSettings);
                }

                foreach (var p in providersData)
                {
                    var protectedApiKey = p.GetProperty("ProtectedApiKey").GetString() ?? "";
                    var keyProtection = p.TryGetProperty("KeyProtection", out var kp) ? kp.GetString() : "Plaintext";

                    // 解密 API Key
                    string plainApiKey = "";
                    if (protectedApiKey.StartsWith("PLAINTEXT:"))
                    {
                        // 旧格式兼容（历史备份中的明文 API Key）
                        plainApiKey = protectedApiKey["PLAINTEXT:".Length..];
                    }
                    else if (keyProtection == "MachineKey")
                    {
                        // 使用机器指纹加密（同一台机器恢复时可用）
                        plainApiKey = _apiKeyProtection.Decrypt(protectedApiKey);
                    }
                    else if (keyProtection == "BackupPassword" && !string.IsNullOrEmpty(password))
                    {
                        try
                        {
                            plainApiKey = _dataEncryption.Decrypt(protectedApiKey, password);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "解密 API Key 失败，可能密码错误");
                        }
                    }

                    // 用目标机器指纹重新加密
                    var encryptedApiKey = !string.IsNullOrEmpty(plainApiKey)
                        ? _apiKeyProtection.Encrypt(plainApiKey)
                        : "";

                    var provider = new AiProviderSetting
                    {
                        ProviderId = p.GetProperty("ProviderId").GetString() ?? Guid.NewGuid().ToString(),
                        ProviderName = p.GetProperty("ProviderName").GetString() ?? "",
                        BaseUrl = p.GetProperty("BaseUrl").GetString() ?? "",
                        EncryptedApiKey = encryptedApiKey,
                        ModelsJson = p.GetProperty("ModelsJson").GetString() ?? "[]",
                        IsMain = p.TryGetProperty("IsMain", out var im) && im.GetBoolean(),
                        IsEnabled = p.TryGetProperty("IsEnabled", out _) || !p.TryGetProperty("IsEnabled", out _),
                        SortOrder = p.TryGetProperty("SortOrder", out var so) ? so.GetInt32() : 0,
                        CreatedAt = p.GetProperty("CreatedAt").GetDateTime(),
                        UpdatedAt = p.GetProperty("UpdatedAt").GetDateTime()
                    };

                    if (!db.AiProviderSettings.Any(x => x.ProviderId == provider.ProviderId))
                    {
                        db.AiProviderSettings.Add(provider);
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Tasks
        var tasksPath = Path.Combine(dbDir, "tasks.json");
        if (File.Exists(tasksPath))
        {
            var json = await File.ReadAllTextAsync(tasksPath, cancellationToken);
            var tasksData = JsonSerializer.Deserialize<List<TaskEntity>>(json);
            if (tasksData != null)
            {
                if (overwrite)
                {
                    db.Tasks.RemoveRange(db.Tasks);
                }

                foreach (var task in tasksData)
                {
                    if (!db.Tasks.Any(t => t.TaskId == task.TaskId))
                    {
                        db.Tasks.Add(task);
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // AuthorizedDevices - 恢复但标记为需要重新授权
        var devicesPath = Path.Combine(dbDir, "devices.json");
        if (File.Exists(devicesPath))
        {
            var json = await File.ReadAllTextAsync(devicesPath, cancellationToken);
            var devicesData = JsonSerializer.Deserialize<List<AuthorizedDevice>>(json);
            if (devicesData != null)
            {
                // 不覆盖现有设备，仅添加不存在的
                foreach (var device in devicesData)
                {
                    if (!db.AuthorizedDevices.Any(d => d.DeviceId == device.DeviceId))
                    {
                        device.Status = "PendingReauth"; // 标记需要重新授权
                        db.AuthorizedDevices.Add(device);
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        // ServerAddressSettings - 不恢复 ServerInstanceId，保留本机的
        // （ServerInstanceId 是本机唯一标识，不应跨机器复制）

        return true;
    }

    /// <summary>
    /// 恢复配置文件
    /// </summary>
    private void RestoreConfigFiles(string tempDir)
    {
        var configDir = Path.Combine(tempDir, "config");
        if (!Directory.Exists(configDir)) return;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // vault_root_path.json 不再使用（知识库根路径已改为环境变量 TASKRUNNER_VAULT_ROOT 配置）

        // webui_settings.json → webui.settings.json
        var webuiSrc = Path.Combine(configDir, "webui_settings.json");
        if (File.Exists(webuiSrc))
        {
            File.Copy(webuiSrc, Path.Combine(baseDir, "webui.settings.json"), overwrite: true);
        }

        // user_preferences.json
        var userPrefsSrc = Path.Combine(configDir, "user_preferences.json");
        if (File.Exists(userPrefsSrc))
        {
            var dataDir = Path.Combine(baseDir, "data");
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
            File.Copy(userPrefsSrc, Path.Combine(dataDir, "user_preferences.json"), overwrite: true);
        }
    }

    /// <summary>
    /// 恢复知识库文件
    /// </summary>
    private async Task RestoreVaultFilesAsync(string tempDir, string vaultRootPath, bool overwrite, CancellationToken cancellationToken)
    {
        var vaultsSrcDir = Path.Combine(tempDir, "vaults");
        if (!Directory.Exists(vaultsSrcDir)) return;

        using var db = _dbContextFactory.CreateDbContext();
        var dbVaults = await db.Vaults.ToListAsync(cancellationToken);

        foreach (var vaultDir in Directory.GetDirectories(vaultsSrcDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vaultName = Path.GetFileName(vaultDir);

            // 查找匹配的数据库记录
            var dbVault = dbVaults.FirstOrDefault(v =>
                string.Equals(v.Name, vaultName, StringComparison.OrdinalIgnoreCase));

            if (dbVault == null || string.IsNullOrEmpty(dbVault.Path))
            {
                _logger.LogWarning("跳过无数据库记录的知识库目录：{Name}", vaultName);
                continue;
            }

            // 确保目标目录存在
            if (!Directory.Exists(dbVault.Path))
            {
                Directory.CreateDirectory(dbVault.Path);
            }

            // 恢复 notes/
            var notesSrc = Path.Combine(vaultDir, "notes");
            if (Directory.Exists(notesSrc))
            {
                var notesDest = Path.Combine(dbVault.Path, "notes");
                if (!Directory.Exists(notesDest)) Directory.CreateDirectory(notesDest);
                CopyDirectory(notesSrc, notesDest, overwrite, cancellationToken: cancellationToken);
            }

            // 恢复 cards/
            var cardsSrc = Path.Combine(vaultDir, "cards");
            if (Directory.Exists(cardsSrc))
            {
                var cardsDest = Path.Combine(dbVault.Path, "cards");
                if (!Directory.Exists(cardsDest)) Directory.CreateDirectory(cardsDest);
                CopyDirectory(cardsSrc, cardsDest, overwrite, cancellationToken: cancellationToken);
            }

            // 恢复 images/
            var imagesSrc = Path.Combine(vaultDir, "images");
            if (Directory.Exists(imagesSrc))
            {
                var imagesDest = Path.Combine(dbVault.Path, "images");
                if (!Directory.Exists(imagesDest)) Directory.CreateDirectory(imagesDest);
                CopyDirectory(imagesSrc, imagesDest, overwrite, cancellationToken: cancellationToken);
            }
        }
    }

    #endregion

    #region Validate & List

    /// <summary>
    /// 验证备份文件
    /// </summary>
    public async Task<BackupValidationResult> ValidateFullBackupAsync(string backupPath, string? password = null)
    {
        if (!File.Exists(backupPath))
        {
            return new BackupValidationResult { IsValid = false, Error = "文件不存在" };
        }

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"dn_validate_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ZipFile.ExtractToDirectory(backupPath, tempDir);

                var manifestPath = Path.Combine(tempDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return new BackupValidationResult { IsValid = false, Error = "缺少 manifest.json" };
                }

                var manifestJson = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestJson);

                if (manifest == null)
                {
                    return new BackupValidationResult { IsValid = false, Error = "manifest.json 格式错误" };
                }

                // 统计备份内容
                var dbDir = Path.Combine(tempDir, "db");
                var configDir = Path.Combine(tempDir, "config");
                var vaultsDir = Path.Combine(tempDir, "vaults");

                return new BackupValidationResult
                {
                    IsValid = true,
                    Version = manifest.Version,
                    CreatedAt = manifest.CreatedAt,
                    SourcePlatform = manifest.SourcePlatform,
                    SourceOS = manifest.SourceOS,
                    HasPassword = manifest.HasPassword,
                    HasDatabase = Directory.Exists(dbDir),
                    HasConfig = Directory.Exists(configDir),
                    HasVaults = Directory.Exists(vaultsDir),
                    VaultCount = Directory.Exists(vaultsDir) ? Directory.GetDirectories(vaultsDir).Length : 0
                };
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch (Exception cleanupEx) { _logger.LogWarning(cleanupEx, "清理临时目录失败: {TempDir}", tempDir); }
                }
            }
        }
        catch (Exception ex)
        {
            return new BackupValidationResult { IsValid = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// 获取备份列表
    /// </summary>
    public List<BackupFileInfo> GetBackupList(string? backupPath = null)
    {
        var backupDir = string.IsNullOrEmpty(backupPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DoctorNotesBackups")
            : backupPath;

        if (!Directory.Exists(backupDir))
        {
            return new List<BackupFileInfo>();
        }

        return Directory.GetFiles(backupDir, "doctor_notes_backup_*.zip")
            .Concat(Directory.GetFiles(backupDir, "backup_*.zip"))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Select(f => new BackupFileInfo
            {
                Path = f,
                FileName = Path.GetFileName(f),
                Size = new FileInfo(f).Length,
                CreationTime = File.GetCreationTime(f)
            })
            .ToList();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// 将绝对路径转为相对于 vaultRootPath 的相对路径
    /// </summary>
    private static string? MakeRelativePath(string path, string? basePath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(basePath))
            return null;

        try
        {
            var fullBase = Path.GetFullPath(basePath);
            var fullPath = Path.GetFullPath(path);

            if (fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullPath[fullBase.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                // 统一使用正斜杠，跨平台兼容
                return relative.Replace('\\', '/');
            }
        }
        catch { /* 路径解析失败，回退返回 null */ }

        return null;
    }

    /// <summary>
    /// 跨平台路径重映射
    /// Windows D:\Vaults\头疼 → Linux /home/user/vaults/头疼
    /// </summary>
    private static string RemapPath(string originalPath)
    {
        if (string.IsNullOrEmpty(originalPath))
            return originalPath;

        // 如果路径在当前系统有效，直接返回
        if (Directory.Exists(originalPath) || File.Exists(originalPath))
            return originalPath;

        // 尝试常见路径映射
        // Windows → Linux: D:\path → /mnt/d/path (WSL) 或保持原样
        // Linux → Windows: /home/user/path → C:\home\user\path

        // 无法自动映射，返回原路径让用户手动调整
        return originalPath;
    }

    /// <summary>
    /// 复制目录
    /// </summary>
    private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(dir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            CopyDirectory(dir, targetSubDir, overwrite, cancellationToken);
        }
    }

    #endregion
}

#region Backup Data Types

/// <summary>
/// 备份清单
/// </summary>
public class BackupManifest
{
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public string SourcePlatform { get; set; } = "";
    public string SourceOS { get; set; } = "";
    public string SourceMachineName { get; set; } = "";
    public bool HasPassword { get; set; }
    public string AppVersion { get; set; } = "";
}

/// <summary>
/// 全量备份结果
/// </summary>
public class FullBackupResult
{
    public bool Success { get; set; }
    public string? BackupPath { get; set; }
    public DateTime? BackupTime { get; set; }
    public long? FileSize { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 全量恢复结果
/// </summary>
public class FullRestoreResult
{
    public bool Success { get; set; }
    public string? SourcePlatform { get; set; }
    public string? SourceOS { get; set; }
    public DateTime? RestoredAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 备份验证结果
/// </summary>
public class BackupValidationResult
{
    public bool IsValid { get; set; }
    public int Version { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? SourcePlatform { get; set; }
    public string? SourceOS { get; set; }
    public bool HasPassword { get; set; }
    public bool HasDatabase { get; set; }
    public bool HasConfig { get; set; }
    public bool HasVaults { get; set; }
    public int VaultCount { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 备份文件信息
/// </summary>
public class BackupFileInfo
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreationTime { get; set; }
}

#endregion
