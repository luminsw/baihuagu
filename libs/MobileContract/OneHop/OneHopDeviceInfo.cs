namespace MobileContract.OneHop;

/// <summary>
/// 局域网发现的 OneHop 设备信息
/// </summary>
public record OneHopDeviceInfo
{
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string? DeviceType { get; init; }
    public string? IpAddress { get; init; }
    public int? Port { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
}
