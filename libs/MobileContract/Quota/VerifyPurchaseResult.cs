namespace MobileContract.Quota;

/// <summary>
/// 购买验证结果
/// </summary>
public record VerifyPurchaseResult
{
    public bool IsVerified { get; init; }
    public string Status { get; init; } = "";
    public string? Message { get; init; }
    public string? RawResponse { get; init; }
}
