namespace MobileContract.OneHop;

/// <summary>
/// OneHop 服务状态
/// </summary>
public record OneHopStatus
{
    public bool IsRunning { get; init; }
    public string? ServiceName { get; init; }
    public string? IpAddress { get; init; }
    public int Port { get; init; }
    public IReadOnlyList<string> SupportedProtocols { get; init; } = Array.Empty<string>();
}
