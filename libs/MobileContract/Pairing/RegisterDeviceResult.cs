namespace MobileContract.Pairing;

/// <summary>
/// 移动设备通过 OneHop HTTP 注册到服务器的结果。
/// 与 Kotlin DeviceRegistrationService / ArkTS ServerRegistrationHelper 对齐。
/// </summary>
public record RegisterDeviceResult
{
    public bool Success { get; init; }

    public bool Authorized { get; init; }

    public string? SharedSecret { get; init; }

    public string? RequestId { get; init; }

    public string? DeviceName { get; init; }
}
