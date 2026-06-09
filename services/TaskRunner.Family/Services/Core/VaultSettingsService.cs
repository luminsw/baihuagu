using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;
using TaskRunner.Data.Entities;
using TaskRunner.Contracts.Vaults;

namespace TaskRunner.Services;

/// <summary>
/// 知识库配置服务 - 从 SettingsService 中提取，专注管理 Vault 配置
/// </summary>
public class VaultSettingsService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<VaultSettingsService> _logger;
    private readonly object _vaultPathLock = new();

    public VaultSettingsService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<VaultSettingsService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public string VaultRootPathPreference
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TASKRUNNER_VAULT_ROOT");
            if (!string.IsNullOrWhiteSpace(env))
                return env.TrimEnd('/', '\\');

            var isDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ||
                           File.Exists("/.dockerenv");

            if (isDocker)
                return "/opt/yj-family/vaults";

            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, ".yj-vaults");
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

            var vault = new Vault
            {
                VaultId = vaultId,
                Name = trimmedName,
                Path = normalizedPath,
                IsActive = false,
                Industry = trimmedIndustry
            };

            dbContext.Vaults.Add(vault);
            dbContext.SaveChanges();

            _logger.LogInformation("新建知识库: {Name} ({Path})", trimmedName, normalizedPath);

            return new VaultConfig
            {
                Id = vaultId,
                Name = trimmedName,
                Path = normalizedPath,
                Industry = trimmedIndustry
            };
        }
    }

    public bool ActivateVault(string vaultId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.IsActive = true;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool RemoveVault(string vaultId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null || vault.IsDeleted) return false;

            vault.IsDeleted = true;
            vault.DeletedAt = DateTime.UtcNow;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool RestoreVault(string vaultId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId && v.IsDeleted);
            if (vault == null) return false;

            vault.IsDeleted = false;
            vault.DeletedAt = null;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool EmptyTrash()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var trashVaults = dbContext.Vaults.Where(v => v.IsDeleted).ToList();
            if (trashVaults.Count == 0) return false;

            foreach (var vault in trashVaults)
            {
                dbContext.Vaults.Remove(vault);
            }
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool UpdateVaultName(string vaultId, string newName)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
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
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
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
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
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
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.Tags = tags;
            dbContext.SaveChanges();
            return true;
        }
    }

    public bool UpdateVaultIndustry(string vaultId, string industry)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault(v => v.VaultId == vaultId);
            if (vault == null) return false;

            vault.Industry = industry.Trim();
            dbContext.SaveChanges();
            return true;
        }
    }

    public void SetVaultPath(string? vaultPath)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        lock (_vaultPathLock)
        {
            var vault = dbContext.Vaults.FirstOrDefault();
            if (vault != null)
            {
                vault.Path = vaultPath ?? "";
                dbContext.SaveChanges();
            }
        }
    }

    public string VaultPath
    {
        get
        {
            var active = GetActiveVault();
            if (active != null && !string.IsNullOrEmpty(active.Path))
                return active.Path;
            return Environment.GetEnvironmentVariable("TASK_RUNNER_VAULT_ROOT") ?? "";
        }
    }

    public string NotesPath
    {
        get
        {
            var active = GetActiveVault();
            if (active != null && !string.IsNullOrEmpty(active.Path))
                return Path.Combine(active.Path, "notes");
            return "";
        }
    }

    public string CardsPath
    {
        get
        {
            var active = GetActiveVault();
            if (active != null && !string.IsNullOrEmpty(active.Path))
                return Path.Combine(active.Path, "cards");
            return "";
        }
    }

    public (int added, int removed) SyncVaultsWithFilesystem(string rootPath)
    {
        int added = 0, removed = 0;

        if (!Directory.Exists(rootPath))
            return (added, removed);

        var dbVaults = GetVaults().ToDictionary(v => v.Path, v => v);
        var fsVaults = new HashSet<string>();

        foreach (var dir in Directory.EnumerateDirectories(rootPath))
        {
            var notesDir = Path.Combine(dir, "notes");
            var cardsDir = Path.Combine(dir, "cards");
            if (Directory.Exists(notesDir) || Directory.Exists(cardsDir))
            {
                fsVaults.Add(dir);
                if (!dbVaults.ContainsKey(dir))
                {
                    var name = Path.GetFileName(dir);
                    try
                    {
                        AddVault(name, dir, "其他");
                        added++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "同步知识库时跳过重复: {Path}", dir);
                    }
                }
            }
        }

        foreach (var dbVault in dbVaults.Values)
        {
            if (!fsVaults.Contains(dbVault.Path) && !dbVault.Path.Contains("builtin"))
            {
                RemoveVault(dbVault.Id);
                removed++;
            }
        }

        return (added, removed);
    }

    private static List<string> ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return new List<string>();
        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }
}
