namespace MobileContract.Logging;

/// <summary>
/// 移动端日志查询条件
/// </summary>
public record MobileLogQuery
{
    public string? DeviceId { get; init; }
    public string? Level { get; init; }
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}
