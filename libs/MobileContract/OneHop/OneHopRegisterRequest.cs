namespace MobileContract.OneHop;

/// <summary>
/// 向 OneHop 网络注册设备请求
/// </summary>
public record OneHopRegisterRequest
{
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string? DeviceType { get; init; }
}
