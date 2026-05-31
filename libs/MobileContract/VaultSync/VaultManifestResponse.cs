namespace MobileContract.VaultSync;

/// <summary>
/// 增量同步清单响应
/// </summary>
public record VaultManifestResponse
{
    public string Cursor { get; init; } = "";
    public string? VaultId { get; init; }
    public string? VaultName { get; init; }
    public IReadOnlyList<ManifestFile> Files { get; init; } = Array.Empty<ManifestFile>();
    public Quota.QuotaInfoDto? QuotaInfo { get; init; }
}
