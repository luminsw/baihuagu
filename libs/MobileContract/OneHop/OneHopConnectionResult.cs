namespace MobileContract.OneHop;

/// <summary>
/// OneHop 连接结果
/// </summary>
public record OneHopConnectionResult
{
    public bool Success { get; init; }
    public string? ConnectionId { get; init; }
    public string? Message { get; init; }
}
