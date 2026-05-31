namespace MobileContract.VaultSync;

/// <summary>
/// 知识库目录浏览项
/// </summary>
public record VaultBrowseItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsDirectory { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public long? Size { get; init; }
}
