using System.Text.Json.Serialization;

namespace TaskRunner.Contracts.Mobile;

/// <summary>
/// 订单记录（IAP 购买记录）
/// </summary>
public class OrderRecordDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = "";

    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    [JsonPropertyName("quotaAdded")]
    public int QuotaAdded { get; set; }

    [JsonPropertyName("quotaType")]
    public string QuotaType { get; set; } = "";

    [JsonPropertyName("isVerified")]
    public bool IsVerified { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}
