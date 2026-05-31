namespace MobileContract.Quota;

/// <summary>
/// 验证应用内购买请求
/// </summary>
public record VerifyPurchaseRequest
{
    public string DeviceId { get; init; } = "";
    public string? DeviceName { get; init; }
    public string ProductId { get; init; } = "";
    public string PurchaseToken { get; init; } = "";
    public string? OrderId { get; init; }
}
