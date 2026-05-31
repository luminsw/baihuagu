namespace MobileContract.Logging;

/// <summary>
/// 单条移动端日志上传请求
/// </summary>
public record MobileLogRequest
{
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public string? Level { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset? Timestamp { get; init; }
    public string? Context { get; init; }
    public string? Extra { get; init; }
}
