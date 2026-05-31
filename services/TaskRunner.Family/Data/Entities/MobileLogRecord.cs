namespace TaskRunner.Data.Entities;

/// <summary>
/// 移动端日志记录（持久化到 SQLite）
/// </summary>
public class MobileLogRecord
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Context { get; set; } = "";
    public string? ExtraJson { get; set; }
    public DateTime ServerTimestamp { get; set; } = DateTime.UtcNow;
}
