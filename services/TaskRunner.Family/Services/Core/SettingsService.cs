using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;
using TaskRunner.Models;
using TaskRunner.Data;
using TaskRunner.Data.Entities;
using TaskRunner.Contracts.Vaults;

namespace TaskRunner.Services
{
    public class SettingsService
    {
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SettingsService> _logger;
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly IDbContextFactory<FamilyDbContext> _familyDbContextFactory;
        private readonly IDbContextFactory<AIDbContext> _aiDbContextFactory;
        private IReadOnlyList<AiProviderConfig>? _aiProvidersCache;

        private readonly object _vaultPathLock = new();
        private string? _runtimeVaultId;

        public SettingsService(
            IConfiguration configuration, 
            IServiceProvider serviceProvider, 
            IDbContextFactory<AppDbContext> dbContextFactory,
            IDbContextFactory<FamilyDbContext> familyDbContextFactory,
            IDbContextFactory<AIDbContext> aiDbContextFactory,
            ILogger<SettingsService> logger)
        {
            _configuration = configuration;
            _serviceProvider = serviceProvider;
            _dbContextFactory = dbContextFactory;
            _familyDbContextFactory = familyDbContextFactory;
            _aiDbContextFactory = aiDbContextFactory;
            _logger = logger;

            LoadFromDatabase();
            LoadLocalModelConfigFromFile();

            // 启动时自动同步知识库
            TrySyncVaultsOnStartup();
        }

        private void TrySyncVaultsOnStartup()
        {
            var rootPath = VaultRootPathPreference;
            if (string.IsNullOrWhiteSpace(rootPath)) return;
            if (!Directory.Exists(rootPath)) return;

            try
            {
                var (added, removed) = SyncVaultsWithFilesystem(rootPath);
                if (added > 0 || removed > 0)
                {
                    _logger.LogInformation("启动时自动同步知识库完成：新增 {Added} 个，移除 {Removed} 个", added, removed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动时自动同步知识库失败");
            }
        }

        /// <summary>
        /// 获取配置文件目录，优先使用 TASKRUNNER_DATA_DIR 环境变量，避免发布时丢失
        /// </summary>
        public static string GetConfigDirectory()
        {
            var dataDir = Environment.GetEnvironmentVariable("TASKRUNNER_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                return dataDir;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// 从数据库加载配置到内存缓存
        /// </summary>
        private void LoadFromDatabase()
        {
            // 迁移 Core 数据库
            using var dbContext = _dbContextFactory.CreateDbContext();
            MigrateDatabase(dbContext, "Core");

            // 迁移 Family 数据库
            using var familyDb = _familyDbContextFactory.CreateDbContext();
            MigrateDatabase(familyDb, "Family");

            // 迁移 AI 数据库
            using var aiDb = _aiDbContextFactory.CreateDbContext();
            MigrateDatabase(aiDb, "AI");

            // 密钥迁移：检测并修复因加密密钥变化导致的 API Key 无法解密问题
            MigrateApiKeysIfNeeded(aiDb);

            // 架构拆分：从旧数据库（taskrunner.db）迁移 AI/Family 数据到新数据库
            MigrateLegacyDataIfNeeded();

            var firstVault = dbContext.Vaults
                .OrderBy(v => v.CreatedAt)
                .FirstOrDefault();

            if (firstVault != null)
            {
                _runtimeVaultId = firstVault.VaultId;
            }
            else
            {
                _runtimeVaultId = Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>
        /// 检测并迁移因加密密钥变化导致无法解密的 API Key。
        /// 场景：用户未设置 YJ_ENCRYPTION_KEY，容器重建后机器指纹变化，
        /// 导致之前加密的 API Key 无法解密。
        ///
        /// 迁移策略：
        /// 1. 如果 .yj-key 文件已存在：尝试用旧版机器指纹解密失败的 Key，成功后用 .yj-key 重新加密
        /// 2. 如果 .yj-key 文件不存在：尝试用旧版机器指纹解密所有 Key，成功后生成 .yj-key 并重新加密
        ///    如果没有旧数据能解密，直接生成 .yj-key（确保后续加密使用固定密钥）
        /// </summary>
        private void MigrateApiKeysIfNeeded(TaskRunner.Data.AIDbContext dbContext)
        {
            try
            {
                var providers = dbContext.AiProviderSettings
                    .Where(p => !string.IsNullOrEmpty(p.EncryptedApiKey))
                    .ToList();

                var keyFileExists = File.Exists(TaskRunner.Core.Shared.Security.AesApiKeyEncryption.KeyFilePath);

                if (!keyFileExists && providers.Count == 0)
                {
                    // 没有旧数据，直接生成固定密钥文件，确保后续加密稳定
                    TaskRunner.Core.Shared.Security.AesApiKeyEncryption.GenerateKeyFile();
                    _logger.LogInformation("已自动生成固定加密密钥文件：{KeyFile}", TaskRunner.Core.Shared.Security.AesApiKeyEncryption.KeyFilePath);
                    return;
                }

                if (providers.Count == 0)
                    return;

                var legacyFingerprint = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.GetLegacyMachineFingerprint();
                var migratedCount = 0;
                var needsKeyFile = !keyFileExists;

                foreach (var provider in providers)
                {
                    // 先用当前密钥尝试解密
                    var currentDecrypted = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.Decrypt(provider.EncryptedApiKey!);
                    if (!string.IsNullOrEmpty(currentDecrypted))
                        continue; // 当前密钥能解密，无需迁移

                    // 当前密钥无法解密，尝试用旧版机器指纹解密
                    var legacyDecrypted = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.DecryptWithFingerprint(
                        provider.EncryptedApiKey!, legacyFingerprint);

                    if (string.IsNullOrEmpty(legacyDecrypted))
                    {
                        _logger.LogWarning("API Key 无法解密（Provider={ProviderId}），可能使用了已丢失的 YJ_ENCRYPTION_KEY。请重新在 WebUI 中设置 API Key。", provider.ProviderId);
                        continue;
                    }

                    // 旧密钥能解密，需要生成固定密钥文件（如果还没有）
                    if (needsKeyFile)
                    {
                        TaskRunner.Core.Shared.Security.AesApiKeyEncryption.GenerateKeyFile();
                        needsKeyFile = false;
                        _logger.LogInformation("已生成固定加密密钥文件，准备迁移 API Key");
                    }

                    // 用新密钥重新加密
                    var reEncrypted = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.Encrypt(legacyDecrypted);
                    provider.EncryptedApiKey = reEncrypted;
                    migratedCount++;
                    _logger.LogInformation("API Key 已自动迁移：Provider={ProviderId}（加密密钥从机器指纹升级到固定密钥）", provider.ProviderId);
                }

                // 如果所有 Key 都能用当前密钥解密，但 .yj-key 还不存在，说明当前用的是 YJ_ENCRYPTION_KEY
                // 自动持久化到 .yj-key，避免环境变量丢失后无法解密
                if (!keyFileExists && !needsKeyFile && migratedCount == 0)
                {
                    var envKey = Environment.GetEnvironmentVariable("YJ_ENCRYPTION_KEY");
                    if (!string.IsNullOrWhiteSpace(envKey))
                    {
                        TaskRunner.Core.Shared.Security.AesApiKeyEncryption.GenerateKeyFile();
                        // 重新加密所有 Key（从环境变量密钥迁移到文件密钥）
                        foreach (var provider in providers)
                        {
                            var decrypted = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.Decrypt(provider.EncryptedApiKey!);
                            if (!string.IsNullOrEmpty(decrypted))
                            {
                                provider.EncryptedApiKey = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.Encrypt(decrypted);
                                migratedCount++;
                            }
                        }
                        _logger.LogInformation("已将 YJ_ENCRYPTION_KEY 持久化到密钥文件，并迁移 {Count} 个 API Key", migratedCount);
                    }
                    else
                    {
                        // 当前用的是机器指纹，直接生成 .yj-key（但机器指纹会变化，这是个问题）
                        // 为了稳定性，生成 .yj-key 并重新加密
                        TaskRunner.Core.Shared.Security.AesApiKeyEncryption.GenerateKeyFile();
                        foreach (var provider in providers)
                        {
                            var decrypted = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.Decrypt(provider.EncryptedApiKey!);
                            if (!string.IsNullOrEmpty(decrypted))
                            {
                                provider.EncryptedApiKey = TaskRunner.Core.Shared.Security.AesApiKeyEncryption.Encrypt(decrypted);
                                migratedCount++;
                            }
                        }
                        _logger.LogInformation("已生成固定加密密钥文件，并将 {Count} 个 API Key 从机器指纹迁移到固定密钥", migratedCount);
                    }
                }

                if (migratedCount > 0)
                {
                    dbContext.SaveChanges();
                    _logger.LogInformation("API Key 迁移完成：共迁移 {Count} 个提供商的加密密钥", migratedCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API Key 迁移过程出错: {Message}", ex.Message);
            }
        }

        private void MigrateDatabase(DbContext dbContext, string domainName)
        {
            try
            {
                _logger.LogDebug("About to migrate {Domain} DB at: {ConnectionString}", domainName, dbContext.Database.GetDbConnection().ConnectionString);
                dbContext.Database.Migrate();
                _logger.LogDebug("{Domain} migrate completed successfully", domainName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Domain} migrate FAILED", domainName);
                _logger.LogError(ex, "{Domain} 数据库迁移失败: {Message}", domainName, ex.Message);
                throw;
            }
        }

        public int AiRequestTimeoutMinutes
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_TIMEOUT_MINUTES");
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                    return v;
                var cfg = _configuration["AiRequestTimeoutMinutes"];
                if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                    return c;
                return 5;
            }
        }

        public int AiRequestMaxAttempts
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_MAX_ATTEMPTS");
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                    return v;
                var cfg = _configuration["AiRequestMaxAttempts"];
                if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                    return c;
                return 3;
            }
        }

        public int AiRequestInitialBackoffMs
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_INITIAL_BACKOFF_MS");
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                    return v;
                var cfg = _configuration["AiRequestInitialBackoffMs"];
                if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                    return c;
                return 1000;
            }
        }

        public int AiRequestMaxBackoffMs
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_REQUEST_MAX_BACKOFF_MS");
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var v) && v > 0)
                    return v;
                var cfg = _configuration["AiRequestMaxBackoffMs"];
                if (!string.IsNullOrWhiteSpace(cfg) && int.TryParse(cfg, out var c) && c > 0)
                    return c;
                return 30000;
            }
        }

        public IReadOnlyList<VaultConfig> GetVaults()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                return dbContext.Vaults
                    .Where(v => !v.IsDeleted)
                    .OrderBy(v => v.CreatedAt)
                    .Select(v => new VaultConfig
                    {
                        Id = v.VaultId,
                        Name = v.Name,
                        Path = v.Path,
                        CreatedAt = v.CreatedAt,
                        IsPaid = v.IsPaid,
                        Tags = ParseTags(v.Tags),
                        Industry = v.Industry
                    })
                    .ToList();
            }
        }

        public VaultConfig? GetActiveVault()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults
                    .Where(v => !v.IsDeleted)
                    .OrderBy(v => v.CreatedAt)
                    .FirstOrDefault();

                if (vault == null) return null;
                return new VaultConfig
                {
                    Id = vault.VaultId,
                    Name = vault.Name,
                    Path = vault.Path,
                    CreatedAt = vault.CreatedAt,
                    IsPaid = vault.IsPaid,
                    Tags = ParseTags(vault.Tags)
                };
            }
        }

        public IReadOnlyList<VaultConfig> GetTrashVaults()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                return dbContext.Vaults
                    .Where(v => v.IsDeleted)
                    .OrderByDescending(v => v.DeletedAt)
                    .Select(v => new VaultConfig
                    {
                        Id = v.VaultId,
                        Name = v.Name,
                        Path = v.Path,
                        CreatedAt = v.CreatedAt,
                        IsPaid = v.IsPaid,
                        Tags = ParseTags(v.Tags),
                        Industry = v.Industry
                    })
                    .ToList();
            }
        }

        public VaultConfig AddVault(string name, string path, string industry)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vaultId = Guid.NewGuid().ToString("N");
                var trimmedName = name.Trim();
                var trimmedIndustry = industry.Trim();

                // 规范化路径：如果未指定路径，使用标准路径 VaultRootPathPreference/local/行业/名称/
                var normalizedPath = path.Trim();
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    var vaultRoot = VaultRootPathPreference;
                    if (string.IsNullOrWhiteSpace(vaultRoot))
                    {
                        vaultRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "vaults");
                    }
                    normalizedPath = Path.Combine(vaultRoot, "local", trimmedIndustry, trimmedName);
                }

                // 查重：同名或同路径均不允许重复创建
                var existingByName = dbContext.Vaults
                    .FirstOrDefault(v => v.Name == trimmedName && !v.IsDeleted);
                if (existingByName != null)
                {
                    throw new InvalidOperationException($"知识库名称 '{trimmedName}' 已存在，请勿重复创建。");
                }

                var existingByPath = dbContext.Vaults
                    .FirstOrDefault(v => v.Path == normalizedPath && !v.IsDeleted);
                if (existingByPath != null)
                {
                    throw new InvalidOperationException($"知识库路径 '{normalizedPath}' 已被 '{existingByPath.Name}' 占用，请勿重复创建。");
                }

                var vault = new Data.Entities.Vault
                {
                    VaultId = vaultId,
                    Name = trimmedName,
                    Path = normalizedPath,
                    IsActive = false,
                    Industry = trimmedIndustry
                };

                dbContext.Vaults.Add(vault);
                dbContext.SaveChanges();

                // 第一个知识库时初始化 runtime ID
                var allVaultsCount = dbContext.Vaults.Count();
                if (allVaultsCount == 1)
                {
                    _runtimeVaultId = vaultId;
                }

                return new VaultConfig
                {
                    Id = vaultId,
                    Name = vault.Name,
                    Path = vault.Path,
                    CreatedAt = vault.CreatedAt,
                    Industry = vault.Industry
                };
            }
        }

        public bool ActivateVault(string vaultId)
        {
            // 已废弃：不再支持切换"当前知识库"，所有知识库平等
            throw new NotSupportedException("ActivateVault 已废弃：不再支持切换当前知识库");
        }

        public bool RemoveVault(string vaultId)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults
                    .FirstOrDefault(v => v.VaultId == vaultId);

                if (vault == null || vault.IsDeleted) return false;

                // 将知识库目录移入回收站
                if (!string.IsNullOrWhiteSpace(vault.Path) && Directory.Exists(vault.Path))
                {
                    var trashPath = GetTrashPath(vault);
                    var trashDir = Path.GetDirectoryName(trashPath);
                    if (!string.IsNullOrEmpty(trashDir))
                    {
                        Directory.CreateDirectory(trashDir);
                    }
                    try
                    {
                        Directory.Move(vault.Path, trashPath);
                        _logger.LogInformation("知识库已移入回收站: {Name} -> {TrashPath}", vault.Name, trashPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "移入回收站失败: {Path}，中止删除操作", vault.Path);
                        return false;
                    }
                }

                vault.IsDeleted = true;
                vault.DeletedAt = DateTime.UtcNow;
                dbContext.SaveChanges();

                // 如果删除的是第一个知识库，更新 runtime ID
                var remaining = dbContext.Vaults.Where(v => !v.IsDeleted).OrderBy(v => v.CreatedAt).FirstOrDefault();
                if (remaining != null)
                {
                    _runtimeVaultId = remaining.VaultId;
                }
                else
                {
                    _runtimeVaultId = Guid.NewGuid().ToString("N");
                }

                return true;
            }
        }

        public bool RestoreVault(string vaultId)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
                if (vault == null || !vault.IsDeleted) return false;

                if (Directory.Exists(vault.Path))
                {
                    _logger.LogWarning("恢复知识库失败，原始路径已被占用: {Path}", vault.Path);
                    return false;
                }

                var trashPath = FindTrashPath(vault);
                if (trashPath == null || !Directory.Exists(trashPath))
                {
                    _logger.LogWarning("恢复知识库失败，回收站中找不到目录: {Name}", vault.Name);
                    return false;
                }

                var parentDir = Path.GetDirectoryName(vault.Path);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                try
                {
                    Directory.Move(trashPath, vault.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "恢复知识库时移动目录失败: {TrashPath} -> {Path}", trashPath, vault.Path);
                    return false;
                }

                vault.IsDeleted = false;
                vault.DeletedAt = null;
                dbContext.SaveChanges();

                _logger.LogInformation("知识库已恢复: {Name} -> {Path}", vault.Name, vault.Path);
                return true;
            }
        }

        public bool EmptyTrash()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var trashVaults = dbContext.Vaults.Where(v => v.IsDeleted).ToList();
                foreach (var vault in trashVaults)
                {
                    var trashPath = FindTrashPath(vault);
                    if (trashPath != null && Directory.Exists(trashPath))
                    {
                        try
                        {
                            Directory.Delete(trashPath, true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "清空回收站时删除目录失败，保留数据库记录: {Path}", trashPath);
                            continue; // 物理删除失败，跳过 DB 记录移除
                        }
                    }
                    dbContext.Vaults.Remove(vault);
                }
                dbContext.SaveChanges();
                _logger.LogInformation("回收站已清空，永久删除 {Count} 个知识库", trashVaults.Count);
                return true;
            }
        }

        private string GetDeletedBasePath()
        {
            var root = VaultRootPathPreference;
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, "deleted");
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "deleted");
        }

        private string GetTrashPath(Data.Entities.Vault vault)
        {
            var deletedBase = GetDeletedBasePath();
            var trashPath = Path.Combine(deletedBase, vault.Name);
            if (!Directory.Exists(trashPath)) return trashPath;

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            return Path.Combine(deletedBase, $"{vault.Name}_{timestamp}");
        }

        private string? FindTrashPath(Data.Entities.Vault vault)
        {
            var deletedBase = GetDeletedBasePath();

            var exactPath = Path.Combine(deletedBase, vault.Name);
            if (Directory.Exists(exactPath)) return exactPath;

            var dirs = Directory.Exists(deletedBase)
                ? Directory.GetDirectories(deletedBase, $"{vault.Name}_*")
                : Array.Empty<string>();

            if (dirs.Length > 0) return dirs.OrderByDescending(d => d).First();

            return null;
        }

        public bool UpdateVaultName(string vaultId, string newName)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults
                    .FirstOrDefault(v => v.VaultId == vaultId);
                    
                if (vault == null) return false;

                vault.Name = newName.Trim();
                dbContext.SaveChanges();
                return true;
            }
        }

        public bool UpdateVaultPath(string vaultId, string newPath)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults
                    .FirstOrDefault(v => v.VaultId == vaultId);
                    
                if (vault == null) return false;

                vault.Path = newPath.Trim();
                dbContext.SaveChanges();
                return true;
            }
        }

        public bool UpdateVaultPaid(string vaultId, bool isPaid)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults
                    .FirstOrDefault(v => v.VaultId == vaultId);
                    
                if (vault == null) return false;

                vault.IsPaid = isPaid;
                dbContext.SaveChanges();
                return true;
            }
        }

        public bool UpdateVaultTags(string vaultId, string tags)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults
                    .FirstOrDefault(v => v.VaultId == vaultId);

                if (vault == null) return false;

                vault.Tags = tags.Trim();
                dbContext.SaveChanges();
                return true;
            }
        }

        public bool UpdateVaultIndustry(string vaultId, string industry)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var vault = dbContext.Vaults
                    .FirstOrDefault(v => v.VaultId == vaultId);

                if (vault == null) return false;

                vault.Industry = industry.Trim();
                dbContext.SaveChanges();
                return true;
            }
        }

        private static List<string> ParseTags(string? tags)
        {
            if (string.IsNullOrWhiteSpace(tags)) return new List<string>();
            return tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
        }

        public void SetVaultPath(string? vaultPath)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var nextPath = string.IsNullOrWhiteSpace(vaultPath) ? null : vaultPath.Trim();

                // 更新第一个知识库的路径
                var activeVault = dbContext.Vaults
                    .OrderBy(v => v.CreatedAt)
                    .FirstOrDefault();
                    
                if (activeVault != null && nextPath != null)
                {
                    var changed = !string.Equals(activeVault.Path, nextPath, StringComparison.OrdinalIgnoreCase);
                    activeVault.Path = nextPath;
                    dbContext.SaveChanges();
                    
                    if (changed)
                    {
                        _runtimeVaultId = Guid.NewGuid().ToString("N");
                    }
                }
            }
        }

        public void ClearAiProvidersCache()
        {
            _aiProvidersCache = null;
            _logger.LogInformation("AI 提供商缓存已清除");
        }

        public IReadOnlyList<AiProviderConfig> GetAiProviders()
        {
            if (_aiProvidersCache != null)
                return _aiProvidersCache;

            try
            {
                var aiConfigService = _serviceProvider.GetService(typeof(AiConfigService)) as AiConfigService;
                var dbProviders = aiConfigService?.GetProviders();
                if (dbProviders != null && dbProviders.Count > 0)
                {
                    _aiProvidersCache = dbProviders;
                    return _aiProvidersCache;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从数据库加载 AI 提供商配置失败，回退到 appsettings.json");
            }

            var list = _configuration.GetSection("Ai").Get<List<AiProviderConfig>>() ?? new List<AiProviderConfig>();
            _aiProvidersCache = list;
            return _aiProvidersCache;
        }

        public AiProviderConfig? GetAiProvider(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;
            return GetAiProviders().FirstOrDefault(p =>
                p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public AiProviderConfig? GetMainAiProvider()
        {
            try
            {
                var aiConfigService = _serviceProvider.GetService(typeof(AiConfigService)) as AiConfigService;
                var mainFromDb = aiConfigService?.GetMainProvider();
                if (mainFromDb != null)
                    return mainFromDb;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从数据库加载主 AI 提供商失败，回退到配置文件中查找");
            }

            var list = GetAiProviders();
            var main = list.FirstOrDefault(p => p.IsMain);
            if (main != null)
                return main;
            return list.FirstOrDefault();
        }

        public string GetApiKeyForProvider(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return "";

            var idTrim = providerId.Trim();

            try
            {
                var aiConfigService = _serviceProvider.GetService(typeof(AiConfigService)) as AiConfigService;
                var keyFromDb = aiConfigService?.GetApiKey(idTrim);
                if (!string.IsNullOrEmpty(keyFromDb))
                    return keyFromDb;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从数据库加载 AI 提供商 API Key 失败: {ProviderId}", idTrim);
            }

            return "";
        }

        public virtual string GetAiApiKey(string providerId)
        {
            return GetApiKeyForProvider(providerId);
        }

        public string AiApiKey => GetApiKeyForProvider(GetMainAiProvider()?.Id ?? "");

        public string AiApiUrl
        {
            get
            {
                var envUrl = Environment.GetEnvironmentVariable("TASK_RUNNER_AI_API_URL");
                if (!string.IsNullOrEmpty(envUrl))
                    return envUrl;

                var main = GetMainAiProvider();
                if (main != null && !string.IsNullOrWhiteSpace(main.AiBaseUrl))
                    return main.AiBaseUrl.TrimEnd('/');

                return _configuration["AiBaseUrl"]?.TrimEnd('/')
                    ?? "https://coding.dashscope.aliyuncs.com/v1";
            }
        }

        public string AiModel => GetModelForProvider(GetMainAiProvider()?.Id ?? "");

        public string GetModelForProvider(string providerId, string? model = null)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return _configuration["AiModel"] ?? "Qwen/Qwen2.5-14B-Instruct";

            var provider = GetAiProvider(providerId);
            if (provider == null)
                return _configuration["AiModel"] ?? "Qwen/Qwen2.5-14B-Instruct";

            var models = provider.GetModelOptions();
            if (models.Count == 0)
                return _configuration["AiModel"] ?? "Qwen/Qwen2.5-14B-Instruct";

            if (!string.IsNullOrWhiteSpace(model))
            {
                var matched = models.FirstOrDefault(m => 
                    m.Name.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                    return matched.Name;
            }

            var mainModel = models.FirstOrDefault(m => m.IsMain);
            return mainModel?.Name ?? models[0].Name;
        }

        public string VaultPath
        {
            get
            {
                var activeVault = GetActiveVault();
                if (activeVault != null)
                    return activeVault.Path;

                return Environment.GetEnvironmentVariable("TASK_RUNNER_VAULT_ROOT")
                    ?? _configuration["VaultPath"]
                    ?? "";
            }
        }

        public string VaultId => _runtimeVaultId ?? "";

        public string NotesPath =>
            string.IsNullOrEmpty(VaultPath) ? "" : Path.Combine(VaultPath, "notes");

        public string CardsPath =>
            string.IsNullOrEmpty(VaultPath) ? "" : Path.Combine(VaultPath, "cards");

        public string SemanticEmbeddingUrl =>
            Environment.GetEnvironmentVariable("TASK_RUNNER_EMBEDDING_URL")
            ?? _configuration["EmbeddingUrl"]
            ?? "";

        public string SemanticEmbeddingModel =>
            Environment.GetEnvironmentVariable("TASK_RUNNER_EMBEDDING_MODEL")
            ?? _configuration["EmbeddingModel"]
            ?? "";

        #region Vault Root Path (环境变量驱动)

        /// <summary>
        /// 知识库根路径（使用约定的固定路径，不再支持手动配置）
        /// Docker 模式：/opt/yj-family/vaults
        /// 非 Docker 模式：~/.yj-vaults
        /// </summary>
        public string VaultRootPathPreference
        {
            get
            {
                // 优先使用环境变量（Docker 部署时设置）
                var env = Environment.GetEnvironmentVariable("TASKRUNNER_VAULT_ROOT");
                if (!string.IsNullOrWhiteSpace(env))
                    return env.TrimEnd('/', '\\');
                
                // 检测是否在 Docker 容器中运行
                var isDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ||
                               File.Exists("/.dockerenv");
                
                if (isDocker)
                {
                    // Docker 模式：使用固定路径
                    return "/opt/yj-family/vaults";
                }
                else
                {
                    // 非 Docker 模式：使用用户主目录
                    var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    return Path.Combine(homeDir, ".yj-vaults");
                }
            }
        }

        /// <summary>
        /// 同步根目录下的知识库与数据库记录：新增目录补录，已删除目录清库
        /// 本地知识库在 {root}/local/ 下按行业/名称组织
        /// 移动端推送知识库在 {root}/mobile/ 下按 vaultId 组织
        /// </summary>
        public (int added, int removed) SyncVaultsWithFilesystem(string rootPath)
        {
            if (!Directory.Exists(rootPath))
            {
                throw new DirectoryNotFoundException($"知识库根目录不存在: {rootPath}");
            }

            int added = 0;
            int removed = 0;
            using var dbContext = _dbContextFactory.CreateDbContext();
            lock (_vaultPathLock)
            {
                var existingVaults = dbContext.Vaults
                    .Where(v => !v.IsDeleted)
                    .ToList();

                var existingPaths = existingVaults
                    .Select(v => v.Path)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 收集文件系统中所有看起来像知识库的目录
                var fsVaultPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. 扫描本地知识库：{root}/local/{行业}/{名称}/
                var localDir = Path.Combine(rootPath, "local");
                if (Directory.Exists(localDir))
                {
                    foreach (var industryDir in Directory.GetDirectories(localDir))
                    {
                        var industryName = Path.GetFileName(industryDir);

                        // 防御：如果第一层目录本身直接包含 notes/ 或 cards/，则它自身就是一个知识库
                        var topNotesDir = Path.Combine(industryDir, "notes");
                        var topCardsDir = Path.Combine(industryDir, "cards");
                        if (Directory.Exists(topNotesDir) || Directory.Exists(topCardsDir))
                        {
                            fsVaultPaths.Add(industryDir);
                            if (!existingPaths.Contains(industryDir))
                            {
                                dbContext.Vaults.Add(new Data.Entities.Vault
                                {
                                    VaultId = Guid.NewGuid().ToString("N"),
                                    Name = industryName,
                                    Path = industryDir,
                                    IsActive = false,
                                    Industry = industryName,
                                    Source = "local"
                                });
                                existingPaths.Add(industryDir);
                                added++;
                            }
                            continue;
                        }

                        foreach (var vaultDir in Directory.GetDirectories(industryDir))
                        {
                            var vaultName = Path.GetFileName(vaultDir);
                            if (vaultName.Equals("notes", StringComparison.OrdinalIgnoreCase)
                                || vaultName.Equals("cards", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogDebug("SyncVaultsWithFilesystem 跳过保留目录名: {VaultDir}", vaultDir);
                                continue;
                            }

                            var notesDir = Path.Combine(vaultDir, "notes");
                            var cardsDir = Path.Combine(vaultDir, "cards");

                            bool looksLikeVault = Directory.Exists(notesDir)
                                || Directory.Exists(cardsDir);

                            if (!looksLikeVault)
                            {
                                _logger.LogWarning("SyncVaultsWithFilesystem 跳过非知识库目录（缺少 notes/ 或 cards/）: {VaultDir}", vaultDir);
                                continue;
                            }

                            fsVaultPaths.Add(vaultDir);

                            if (existingPaths.Contains(vaultDir))
                            {
                                _logger.LogDebug("SyncVaultsWithFilesystem 跳过重复路径: {VaultDir}", vaultDir);
                                continue;
                            }

                            var vault = new Data.Entities.Vault
                            {
                                VaultId = Guid.NewGuid().ToString("N"),
                                Name = vaultName,
                                Path = vaultDir,
                                IsActive = false,
                                Industry = industryName,
                                Source = "local"
                            };
                            dbContext.Vaults.Add(vault);
                            existingPaths.Add(vaultDir);
                            added++;
                        }
                    }
                }

                // 2. 扫描移动端推送知识库：{root}/mobile/{行业}/{名称}/
                var mobileDir = Path.Combine(rootPath, "mobile");
                if (Directory.Exists(mobileDir))
                {
                    foreach (var industryDir in Directory.GetDirectories(mobileDir))
                    {
                        var industryName = Path.GetFileName(industryDir);

                        // 防御：如果行业目录本身直接包含 notes/ 或 cards/，则它自身就是一个知识库（无行业子目录的旧结构兼容）
                        var topNotesDir = Path.Combine(industryDir, "notes");
                        var topCardsDir = Path.Combine(industryDir, "cards");
                        if (Directory.Exists(topNotesDir) || Directory.Exists(topCardsDir))
                        {
                            fsVaultPaths.Add(industryDir);
                            if (!existingPaths.Contains(industryDir))
                            {
                                var vaultName = VaultNameResolver.InferNameFromPath(industryDir);
                                dbContext.Vaults.Add(new Data.Entities.Vault
                                {
                                    VaultId = Guid.NewGuid().ToString("N"),
                                    Name = vaultName,
                                    Path = industryDir,
                                    IsActive = false,
                                    Industry = industryName,
                                    Source = "mobile"
                                });
                                existingPaths.Add(industryDir);
                                added++;
                            }
                            continue;
                        }

                        foreach (var vaultDir in Directory.GetDirectories(industryDir))
                        {
                            var vaultName = Path.GetFileName(vaultDir);
                            if (vaultName.Equals("notes", StringComparison.OrdinalIgnoreCase)
                                || vaultName.Equals("cards", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogDebug("SyncVaultsWithFilesystem 跳过保留目录名: {VaultDir}", vaultDir);
                                continue;
                            }

                            var notesDir = Path.Combine(vaultDir, "notes");
                            var cardsDir = Path.Combine(vaultDir, "cards");

                            bool looksLikeVault = Directory.Exists(notesDir)
                                || Directory.Exists(cardsDir);

                            if (!looksLikeVault)
                            {
                                _logger.LogWarning("SyncVaultsWithFilesystem 跳过非知识库目录（缺少 notes/ 或 cards/）: {VaultDir}", vaultDir);
                                continue;
                            }

                            fsVaultPaths.Add(vaultDir);

                            if (existingPaths.Contains(vaultDir))
                            {
                                _logger.LogDebug("SyncVaultsWithFilesystem 跳过重复路径: {VaultDir}", vaultDir);
                                continue;
                            }

                            var vault = new Data.Entities.Vault
                            {
                                VaultId = Guid.NewGuid().ToString("N"),
                                Name = vaultName,
                                Path = vaultDir,
                                IsActive = false,
                                Industry = industryName,
                                Source = "mobile"
                            };
                            dbContext.Vaults.Add(vault);
                            existingPaths.Add(vaultDir);
                            added++;
                        }
                    }
                }

                // 软删除数据库中已不存在对应目录的记录（改为 IsDeleted=true，保留元数据）
                foreach (var vault in existingVaults)
                {
                    if (!fsVaultPaths.Contains(vault.Path))
                    {
                        _logger.LogWarning("SyncVaultsWithFilesystem 软删除知识库（物理目录不存在）: {VaultName} {Path}", vault.Name, vault.Path);
                        vault.IsDeleted = true;
                        vault.DeletedAt = DateTime.UtcNow;
                        removed++;
                    }
                }

                if (added > 0 || removed > 0)
                {
                    dbContext.SaveChanges();
                    _logger.LogInformation("同步知识库完成：新增 {Added} 个，移除 {Removed} 个", added, removed);
                }
            }
            return (added, removed);
        }

        #endregion

        #region Local Model Download Directory

        private string? _localModelDownloadDirectory;
        private string? _preferredDownloadSource;
        private bool? _useChinaMirror;

        /// <summary>
        /// 本地模型下载目录
        /// </summary>
        public string LocalModelDownloadDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(_localModelDownloadDirectory))
                    return _localModelDownloadDirectory;

                // 优先环境变量
                var envDir = Environment.GetEnvironmentVariable("TASKRUNNER_LOCAL_MODEL_DIR");
                if (!string.IsNullOrEmpty(envDir))
                    return envDir;

                // 配置文件
                var cfgDir = _configuration["LocalAI:DownloadDirectory"];
                if (!string.IsNullOrEmpty(cfgDir))
                    return cfgDir;

                // 默认值按平台
                return GetDefaultLocalModelDirectory();
            }
            set
            {
                _localModelDownloadDirectory = value;
                SaveLocalModelConfigToFile();
            }
        }

        /// <summary>
        /// 模型下载源偏好
        /// </summary>
        public string PreferredDownloadSource
        {
            get => _preferredDownloadSource ?? "auto";
            set
            {
                _preferredDownloadSource = value;
                SaveLocalModelConfigToFile();
            }
        }

        /// <summary>
        /// 是否优先使用国内镜像
        /// </summary>
        public bool UseChinaMirror
        {
            get => _useChinaMirror ?? true;
            set
            {
                _useChinaMirror = value;
                SaveLocalModelConfigToFile();
            }
        }

        private static string GetDefaultLocalModelDirectory()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Path.Combine(home, ".ollama", "models");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Path.Combine(home, ".ollama", "models");
            // Linux / WSL
            return Path.Combine(home, ".ollama", "models");
        }

        public void LoadLocalModelConfigFromFile()
        {
            try
            {
                var configPath = Path.Combine(GetConfigDirectory(), "local-model.config.json");
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    var data = System.Text.Json.JsonSerializer.Deserialize<LocalModelConfig>(json);
                    _localModelDownloadDirectory = data?.DownloadDirectory;
                    _preferredDownloadSource = data?.PreferredSource;
                    if (data?.UseChinaMirror.HasValue == true)
                        _useChinaMirror = data.UseChinaMirror.Value;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载本地模型配置失败");
            }
        }

        private void SaveLocalModelConfigToFile()
        {
            try
            {
                var configPath = Path.Combine(GetConfigDirectory(), "local-model.config.json");
                var data = new LocalModelConfig
                {
                    DownloadDirectory = _localModelDownloadDirectory,
                    PreferredSource = _preferredDownloadSource,
                    UseChinaMirror = _useChinaMirror
                };
                var json = System.Text.Json.JsonSerializer.Serialize(data);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存本地模型配置失败");
            }
        }

        private class LocalModelConfig
        {
            public string? DownloadDirectory { get; set; }
            public string? PreferredSource { get; set; }
            public bool? UseChinaMirror { get; set; }
        }

        /// <summary>
        /// 架构拆分数据迁移：从旧数据库（taskrunner.db）迁移 AI/Family 数据到新数据库（ai.db/family.db）。
        /// 此迁移只在检测到新数据库为空时执行一次。
        /// </summary>
        private void MigrateLegacyDataIfNeeded()
        {
            var legacyDbPath = AppDbContext.GetDbPath();
            if (!File.Exists(legacyDbPath))
                return;

            try
            {
                // 1. 迁移 AI 数据
                using var aiDb = _aiDbContextFactory.CreateDbContext();
                if (!aiDb.AiProviderSettings.Any())
                {
                    MigrateAiDataFromLegacy(legacyDbPath, aiDb);
                }

                // 2. 迁移 Family 数据（只迁移有实际数据的表，跳过空表）
                using var familyDb = _familyDbContextFactory.CreateDbContext();
                var hasFamilyData = familyDb.Tasks.Any()
                    || familyDb.OpenClawTasks.Any()
                    || familyDb.LearnerProfiles.Any()
                    || familyDb.Achievements.Any()
                    || familyDb.StudyActivities.Any()
                    || familyDb.CardReviewStates.Any()
                    || familyDb.OnboardingStates.Any()
                    || familyDb.InitTaskProgresses.Any(p => p.IsCompleted);
                if (!hasFamilyData)
                {
                    MigrateFamilyDataFromLegacy(legacyDbPath, familyDb);
                }

                // 3. 修复 Core 表结构（ServerAddressSettings 缺少 Domain 列）
                FixCoreSchema(legacyDbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "架构拆分数据迁移失败");
            }
        }

        private void MigrateAiDataFromLegacy(string legacyDbPath, AIDbContext aiDb)
        {
            using var connection = new SqliteConnection($"Data Source={legacyDbPath}");
            connection.Open();

            int total = 0;

            // AiProviderSettings
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT ProviderId, ProviderName, BaseUrl, AnthropicBaseUrl, EncryptedApiKey, IsMain, ModelsJson, SortOrder, IsEnabled, Tier, CreatedAt, UpdatedAt FROM AiProviderSettings";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    aiDb.AiProviderSettings.Add(new AiProviderSetting
                    {
                        ProviderId = reader.GetString(0),
                        ProviderName = reader.GetString(1),
                        BaseUrl = reader.GetString(2),
                        AnthropicBaseUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                        EncryptedApiKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                        IsMain = reader.GetBoolean(5),
                        ModelsJson = reader.GetString(6),
                        SortOrder = reader.GetInt32(7),
                        IsEnabled = reader.GetBoolean(8),
                        Tier = reader.GetInt32(9),
                        CreatedAt = reader.GetDateTime(10),
                        UpdatedAt = reader.GetDateTime(11)
                    });
                    total++;
                }
            }

            // AiUsageMetrics
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT CalledAt, ProviderId, ProviderName, ModelId, Operation, LatencyMs, InputTokens, OutputTokens, TotalTokens, TokensPerSecond, IsSuccess, ErrorMessage FROM AiUsageMetrics";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    aiDb.AiUsageMetrics.Add(new AiUsageMetric
                    {
                        CalledAt = reader.GetDateTime(0),
                        ProviderId = reader.GetString(1),
                        ProviderName = reader.GetString(2),
                        ModelId = reader.GetString(3),
                        Operation = reader.GetString(4),
                        LatencyMs = reader.GetInt64(5),
                        InputTokens = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                        OutputTokens = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        TotalTokens = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        TokensPerSecond = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        IsSuccess = reader.GetBoolean(10),
                        ErrorMessage = reader.IsDBNull(11) ? null : reader.GetString(11)
                    });
                    total++;
                }
            }

            // EmbeddingConfigs
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT ProviderId, Model, BaseUrl, EncryptedApiKey, Dimensions, IsEnabled, CreatedAt, UpdatedAt FROM EmbeddingConfigs";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    aiDb.EmbeddingConfigs.Add(new EmbeddingConfig
                    {
                        ProviderId = reader.GetString(0),
                        Model = reader.GetString(1),
                        BaseUrl = reader.GetString(2),
                        EncryptedApiKey = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Dimensions = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        IsEnabled = reader.GetBoolean(5),
                        CreatedAt = reader.GetDateTime(6),
                        UpdatedAt = reader.GetDateTime(7)
                    });
                    total++;
                }
            }

            // NoteEmbeddings
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT VaultId, NotePath, VectorJson, Dimensions, CreatedAt, UpdatedAt FROM NoteEmbeddings";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    aiDb.NoteEmbeddings.Add(new NoteEmbedding
                    {
                        VaultId = reader.GetString(0),
                        NotePath = reader.GetString(1),
                        VectorJson = reader.GetString(2),
                        Dimensions = reader.GetInt32(3),
                        CreatedAt = reader.GetDateTime(4),
                        UpdatedAt = reader.GetDateTime(5)
                    });
                    total++;
                }
            }

            aiDb.SaveChanges();
            _logger.LogInformation("已从旧数据库迁移 {Count} 条 AI 数据到 ai.db", total);
        }

        private void MigrateFamilyDataFromLegacy(string legacyDbPath, FamilyDbContext familyDb)
        {
            using var connection = new SqliteConnection($"Data Source={legacyDbPath}");
            connection.Open();

            int total = 0;

            // InitTaskProgresses（只迁移有实际进度的，跳过空记录）
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT TaskId, TaskType, IsCompleted, IsSkipped, CompletedAt, CreatedAt, UpdatedAt FROM InitTaskProgresses WHERE IsCompleted = 1 OR IsSkipped = 1";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var existing = familyDb.InitTaskProgresses.FirstOrDefault(p => p.TaskId == reader.GetString(0));
                    if (existing != null)
                    {
                        existing.IsCompleted = reader.GetBoolean(2);
                        existing.IsSkipped = reader.GetBoolean(3);
                        existing.CompletedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                        existing.UpdatedAt = reader.GetDateTime(6);
                    }
                    else
                    {
                        familyDb.InitTaskProgresses.Add(new InitTaskProgress
                        {
                            TaskId = reader.GetString(0),
                            TaskType = reader.GetString(1),
                            IsCompleted = reader.GetBoolean(2),
                            IsSkipped = reader.GetBoolean(3),
                            CompletedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                            CreatedAt = reader.GetDateTime(5),
                            UpdatedAt = reader.GetDateTime(6)
                        });
                    }
                    total++;
                }
            }

            familyDb.SaveChanges();
            _logger.LogInformation("已从旧数据库迁移 {Count} 条 Family 数据到 family.db", total);
        }

        private void FixCoreSchema(string legacyDbPath)
        {
            using var connection = new SqliteConnection($"Data Source={legacyDbPath}");
            connection.Open();

            // 检查 ServerAddressSettings 是否缺少 Domain 列
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(ServerAddressSettings)";
                using var reader = cmd.ExecuteReader();
                bool hasDomain = false;
                while (reader.Read())
                {
                    if (reader.GetString(1) == "Domain")
                    {
                        hasDomain = true;
                        break;
                    }
                }

                if (!hasDomain)
                {
                    using var alterCmd = connection.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE ServerAddressSettings ADD COLUMN Domain TEXT DEFAULT ''";
                    alterCmd.ExecuteNonQuery();
                    _logger.LogInformation("已为 ServerAddressSettings 表添加 Domain 列");
                }
            }
        }

        #endregion

    }
}
