namespace MobileContract.Logging;

/// <summary>
/// 存储在服务器端的移动端日志记录
/// </summary>
public record MobileLogRecord
{
    public string Id { get; init; } = "";
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public string? Level { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public string? Context { get; init; }
    public string? Extra { get; init; }
}
