using System.Text.Json.Serialization;

namespace TaskRunner.Contracts.Devices;

/// <summary>
/// 已授权设备信息
/// </summary>
public class AuthorizedDeviceDto
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("authorizedTime")]
    public DateTime? AuthorizedTime { get; set; }

    [JsonPropertyName("lastSyncTime")]
    public DateTime? LastSyncTime { get; set; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("syncCount")]
    public int SyncCount { get; set; }

    [JsonPropertyName("firstSyncTime")]
    public DateTime? FirstSyncTime { get; set; }

    [JsonPropertyName("syncedVaultIds")]
    public List<string> SyncedVaultIds { get; set; } = new();
}
