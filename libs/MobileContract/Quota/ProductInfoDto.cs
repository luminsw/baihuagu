namespace MobileContract.Quota;

/// <summary>
/// 可购买的配额产品
/// </summary>
public record ProductInfoDto
{
    public string ProductId { get; init; } = "";
    public string QuotaType { get; init; } = "";
    public int QuotaAmount { get; init; }
    public decimal Price { get; init; }
}
