namespace BaihuaguSdk.Models;

/// <summary>
/// 设备注册结果。
/// 对应 Kotlin DeviceRegistrationService.kt 中的 RegisterDeviceResult。
/// </summary>
public record RegisterDeviceResult
{
    public bool Success { get; init; }
    public bool Authorized { get; init; }
    public string? SharedSecret { get; init; }
    public string? RequestId { get; init; }
    public string? DeviceName { get; init; }
}
