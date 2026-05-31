namespace MobileContract.Logging;

/// <summary>
/// 日志统计信息
/// </summary>
public record LogStats
{
    public int TotalCount { get; init; }
    public int DeviceCount { get; init; }
    public Dictionary<string, int> LevelCounts { get; init; } = new();
}
