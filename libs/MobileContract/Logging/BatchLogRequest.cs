namespace MobileContract.Logging;

/// <summary>
/// 批量日志上传请求
/// </summary>
public record BatchLogRequest
{
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public IReadOnlyList<BatchLogRecord> Logs { get; init; } = Array.Empty<BatchLogRecord>();
}
