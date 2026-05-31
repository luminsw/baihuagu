namespace MobileContract.VaultSync;

/// <summary>
/// 笔记元数据
/// </summary>
public record NoteMetadata
{
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTimeOffset Modified { get; init; }
    public long Size { get; init; }
    public string? Hash { get; init; }
}
