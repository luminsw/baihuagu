namespace MobileContract.Quota;

/// <summary>
/// 购买订单记录
/// </summary>
public record OrderRecordDto
{
    public string Id { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string? DeviceName { get; init; }
    public string ProductId { get; init; } = "";
    public string? OrderId { get; init; }
    public int QuotaAdded { get; init; }
    public string QuotaType { get; init; } = "";
    public bool IsVerified { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
