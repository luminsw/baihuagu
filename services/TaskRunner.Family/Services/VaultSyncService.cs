using MobileContract.VaultSync;

namespace TaskRunner.Services;

/// <summary>
/// 知识库同步服务 — 封装清单扫描、文件读取、卡片读取、目录浏览等核心业务逻辑
/// </summary>
public class VaultSyncService
{
    private readonly SettingsService _settings;
    private readonly ILogger<VaultSyncService> _logger;

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".obsidian", ".trash", "node_modules", ".vscode"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".json", ".txt", ".csv", ".yaml", ".yml"
    };

    public VaultSyncService(SettingsService settings, ILogger<VaultSyncService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有可用知识库列表
    /// </summary>
    public IReadOnlyList<VaultInfo> GetVaults()
    {
        return _settings.GetVaults()
            .Select(v => new VaultInfo
            {
                Id = v.Id,
                Name = v.Name,
                Path = v.Path,
                Industry = v.Industry,
                IsPaid = v.IsPaid
            })
            .ToList();
    }

    /// <summary>
    /// 获取指定知识库的名称
    /// </summary>
    public string? GetVaultName(string vaultId)
    {
        var vault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        return vault?.Name;
    }

    /// <summary>
    /// 扫描知识库文件，返回增量同步清单
    /// </summary>
    public (IReadOnlyList<ManifestFile> Files, long Cursor) ScanVaultManifest(string vaultId)
    {
        var targetVault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        var baseVaultPath = targetVault?.Path;

        if (string.IsNullOrEmpty(baseVaultPath) || !System.IO.Directory.Exists(baseVaultPath))
        {
            return (Array.Empty<ManifestFile>(), 0L);
        }

        var files = new List<ManifestFile>();
        long maxMtime = 0;

        var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
        if (System.IO.Directory.Exists(notesPath))
        {
            ScanDirectory(notesPath, notesPath, files, ref maxMtime, "");
        }

        var cardsPath = System.IO.Path.Combine(baseVaultPath, "cards");
        if (System.IO.Directory.Exists(cardsPath))
        {
            ScanDirectory(cardsPath, cardsPath, files, ref maxMtime, "cards/");
        }

        if (files.Count == 0 && !System.IO.Directory.Exists(notesPath) && !System.IO.Directory.Exists(cardsPath))
        {
            ScanDirectory(baseVaultPath, baseVaultPath, files, ref maxMtime, "");
        }

        return (files, maxMtime);
    }

    private void ScanDirectory(string rootPath, string currentPath, List<ManifestFile> files, ref long maxMtime, string pathPrefix = "")
    {
        foreach (var dir in System.IO.Directory.GetDirectories(currentPath))
        {
            var dirName = System.IO.Path.GetFileName(dir);
            if (ExcludedDirs.Contains(dirName)) continue;
            ScanDirectory(rootPath, dir, files, ref maxMtime, pathPrefix);
        }

        foreach (var file in System.IO.Directory.GetFiles(currentPath))
        {
            var ext = System.IO.Path.GetExtension(file);
            if (!AllowedExtensions.Contains(ext)) continue;

            var relativePath = pathPrefix + file.Substring(rootPath.Length).TrimStart('/', '\\');
            relativePath = relativePath.Replace('\\', '/').TrimStart('/');
            var modified = System.IO.File.GetLastWriteTime(file);
            var modifiedUnix = new DateTimeOffset(modified).ToUnixTimeSeconds();

            if (modifiedUnix > maxMtime)
                maxMtime = modifiedUnix;

            if (string.IsNullOrWhiteSpace(relativePath)) continue;

            var fileInfo = new System.IO.FileInfo(file);
            if (fileInfo.Length == 0) continue;

            files.Add(new ManifestFile
            {
                RelPath = relativePath,
                Op = "upsert",
                Mtime = modifiedUnix,
                Size = fileInfo.Length,
                Sha256 = modifiedUnix.ToString()
            });
        }
    }

    /// <summary>
    /// 读取单个文件内容
    /// </summary>
    public string? ReadFile(string vaultId, string relPath)
    {
        var baseVaultPath = ResolveVaultPath(vaultId);
        if (string.IsNullOrEmpty(baseVaultPath)) return null;

        relPath = relPath.Replace("..", "").Replace(":", "").TrimStart('/');
        var ext = System.IO.Path.GetExtension(relPath);

        if (!AllowedExtensions.Contains(ext))
            return null;

        string filePath;
        if (relPath.StartsWith("cards/") || relPath.StartsWith("notes/"))
        {
            filePath = System.IO.Path.Combine(baseVaultPath, relPath);
        }
        else
        {
            var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
            filePath = System.IO.Path.Combine(notesPath, relPath);
        }

        if (!System.IO.File.Exists(filePath))
            return null;

        return System.IO.File.ReadAllText(filePath);
    }

    /// <summary>
    /// 获取知识库中的卡片列表
    /// </summary>
    public IReadOnlyList<CardRecord> GetCards(string vaultId)
    {
        var targetVault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        if (targetVault == null || string.IsNullOrEmpty(targetVault.Path))
            return Array.Empty<CardRecord>();

        var cardsPath = System.IO.Path.Combine(targetVault.Path, "cards");
        if (!System.IO.Directory.Exists(cardsPath))
            return Array.Empty<CardRecord>();

        var result = new List<CardRecord>();
        var files = System.IO.Directory.GetFiles(cardsPath, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = System.IO.File.ReadAllText(file);
                var cardsArray = System.Text.Json.JsonSerializer.Deserialize<List<MobileCardItem>>(json);
                if (cardsArray != null)
                {
                    result.AddRange(cardsArray.Select(card => new CardRecord
                    {
                        Front = card.Front,
                        Back = card.Back,
                        Deck = card.Deck,
                        Tags = card.Tags ?? new List<string>(),
                        Source = card.Source,
                        NotePath = System.IO.Path.GetFileName(file)
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析卡片文件失败：{File}", file);
            }
        }

        return result;
    }

    /// <summary>
    /// 浏览知识库目录结构
    /// </summary>
    public IReadOnlyList<VaultBrowseItem> BrowseVault(string vaultId, string? path = null)
    {
        var baseVaultPath = ResolveVaultPath(vaultId);
        if (string.IsNullOrEmpty(baseVaultPath))
            return Array.Empty<VaultBrowseItem>();

        var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
        var effectiveRoot = System.IO.Directory.Exists(notesPath) ? notesPath : baseVaultPath;

        var targetPath = string.IsNullOrEmpty(path)
            ? effectiveRoot
            : System.IO.Path.Combine(effectiveRoot, path.Trim('/').Replace('/', System.IO.Path.DirectorySeparatorChar));

        var fullRootPath = System.IO.Path.GetFullPath(effectiveRoot);
        var fullTargetPath = System.IO.Path.GetFullPath(targetPath);
        if (!fullTargetPath.StartsWith(fullRootPath, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<VaultBrowseItem>();

        if (!System.IO.Directory.Exists(targetPath))
            return Array.Empty<VaultBrowseItem>();

        var items = new List<VaultBrowseItem>();

        foreach (var dir in System.IO.Directory.GetDirectories(targetPath))
        {
            var dirName = System.IO.Path.GetFileName(dir);
            if (ExcludedDirs.Contains(dirName)) continue;
            var relativePath = dir.Substring(fullRootPath.Length).TrimStart('/', '\\').Replace('\\', '/');
            items.Add(new VaultBrowseItem
            {
                Name = dirName,
                Path = relativePath,
                IsDirectory = true,
                Modified = System.IO.Directory.GetLastWriteTime(dir)
            });
        }

        foreach (var file in System.IO.Directory.GetFiles(targetPath, "*.md"))
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
            var relativePath = file.Substring(fullRootPath.Length).TrimStart('/', '\\').Replace('\\', '/');
            var fileInfo = new System.IO.FileInfo(file);
            items.Add(new VaultBrowseItem
            {
                Name = fileName,
                Path = relativePath[..^3],
                IsDirectory = false,
                Size = fileInfo.Length,
                Modified = fileInfo.LastWriteTime
            });
        }

        return items.OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name).ToList();
    }

    private string? ResolveVaultPath(string vaultId)
    {
        var vault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        return vault?.Path;
    }

    private class MobileCardItem
    {
        public string Front { get; set; } = "";
        public string Back { get; set; } = "";
        public string? Deck { get; set; }
        public List<string> Tags { get; set; } = new();
        public string? Source { get; set; }
    }
}
