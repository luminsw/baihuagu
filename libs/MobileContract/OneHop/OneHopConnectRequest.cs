namespace MobileContract.OneHop;

/// <summary>
/// 连接到 OneHop 设备请求
/// </summary>
public record OneHopConnectRequest
{
    public string DeviceId { get; init; } = "";
    public string? IpAddress { get; init; }
    public int? Port { get; init; }
}
