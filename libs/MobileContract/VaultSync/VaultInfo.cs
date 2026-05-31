namespace MobileContract.VaultSync;

/// <summary>
/// 知识库基本信息
/// </summary>
public record VaultInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Industry { get; init; }
    public bool IsPaid { get; init; }
}
