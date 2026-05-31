namespace MobileContract.VaultSync;

/// <summary>
/// 同步清单中的单个文件条目
/// </summary>
public record ManifestFile
{
    public string RelPath { get; init; } = "";
    public string Op { get; init; } = "upsert"; // upsert | delete
    public long Mtime { get; init; }
    public long Size { get; init; }
    public string? Sha256 { get; init; }
}
