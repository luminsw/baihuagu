using MobileContract.Services;

namespace MobileApp.Maui.Services;

/// <summary>
/// IVaultStorageAdapter 的文件系统实现。
/// 将知识库文件写入应用私有目录。
/// </summary>
public class VaultStorageAdapter : IVaultStorageAdapter
{
    private readonly string _vaultRoot;

    public VaultStorageAdapter(string vaultRoot)
    {
        _vaultRoot = vaultRoot;
    }

    public Task EnsureDirForFileAsync(string relPath)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return Task.CompletedTask;
    }

    public Task WriteTextFileAsync(string relPath, string content, long mtime = 0)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        EnsureDirForFileAsync(relPath).Wait();
        File.WriteAllText(fullPath, content, System.Text.Encoding.UTF8);
        if (mtime > 0) File.SetLastWriteTimeUtc(fullPath, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime);
        return Task.CompletedTask;
    }

    public Task WriteBinaryFileAsync(string relPath, byte[] content, long mtime = 0)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        EnsureDirForFileAsync(relPath).Wait();
        File.WriteAllBytes(fullPath, content);
        if (mtime > 0) File.SetLastWriteTimeUtc(fullPath, DateTimeOffset.FromUnixTimeMilliseconds(mtime).UtcDateTime);
        return Task.CompletedTask;
    }

    public Task DeleteFileIfExistsAsync(string relPath)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> FileExistsAsync(string relPath)
    {
        return Task.FromResult(File.Exists(Path.Combine(_vaultRoot, relPath)));
    }

    public Task<long> GetFileMtimeAsync(string relPath)
    {
        var fullPath = Path.Combine(_vaultRoot, relPath);
        if (!File.Exists(fullPath)) return Task.FromResult(0L);
        var mtime = File.GetLastWriteTimeUtc(fullPath);
        return Task.FromResult(new DateTimeOffset(mtime).ToUnixTimeMilliseconds());
    }
}
