namespace MobileContract.Quota;

/// <summary>
/// 模拟购买请求（开发/测试用）
/// </summary>
public record SimulatePurchaseRequest
{
    public string DeviceId { get; init; } = "";
    public string? DeviceName { get; init; }
    public string ProductId { get; init; } = "";
}
