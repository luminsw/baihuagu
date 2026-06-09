namespace TaskRunner.Data.Entities;

/// <summary>
/// 设备每日同步记录（频率限制）
/// </summary>
public class DeviceDailySync
{
    public int Id { get; set; }

    /// <summary>
    /// 设备唯一标识
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// 知识库ID
    /// </summary>
    public string VaultId { get; set; } = "";

    /// <summary>
    /// 同步日期（UTC，只存日期部分）
    /// </summary>
    public DateTime SyncDate { get; set; }

    /// <summary>
    /// 当日同步次数
    /// </summary>
    public int SyncCount { get; set; } = 0;

    /// <summary>
    /// 是否消耗了付费配额
    /// </summary>
    public bool UsedPaidQuota { get; set; } = false;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
