namespace TaskRunner.Data.Entities;

/// <summary>
/// 华为 IAP 购买记录
/// </summary>
public class IapPurchaseRecord
{
    public int Id { get; set; }

    /// <summary>
    /// 设备ID
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// 华为商品ID
    /// </summary>
    public string ProductId { get; set; } = "";

    /// <summary>
    /// 华为购买令牌（用于服务端验证）
    /// </summary>
    public string PurchaseToken { get; set; } = "";

    /// <summary>
    /// 华为订单号
    /// </summary>
    public string? OrderId { get; set; }

    /// <summary>
    /// 增加的配额数量
    /// </summary>
    public int QuotaAdded { get; set; }

    /// <summary>
    /// 配额类型：sync / ai_build
    /// </summary>
    public string QuotaType { get; set; } = "";

    /// <summary>
    /// 是否已通过华为验证
    /// </summary>
    public bool IsVerified { get; set; } = false;

    public DateTime CreatedAt { get; set; }
}
