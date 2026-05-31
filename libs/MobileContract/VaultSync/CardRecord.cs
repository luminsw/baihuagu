namespace MobileContract.VaultSync;

/// <summary>
/// Anki 卡片记录
/// </summary>
public record CardRecord
{
    public string Front { get; init; } = "";
    public string Back { get; init; } = "";
    public string? Deck { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? Source { get; init; }
    public string? NotePath { get; init; }
}
