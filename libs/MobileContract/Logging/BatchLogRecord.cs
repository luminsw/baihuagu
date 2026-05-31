namespace MobileContract.Logging;

/// <summary>
/// 批量日志中的单条记录
/// </summary>
public record BatchLogRecord
{
    public string? Level { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? Context { get; init; }
    public string? Extra { get; init; }
}
