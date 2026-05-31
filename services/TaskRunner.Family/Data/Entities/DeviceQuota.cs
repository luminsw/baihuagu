namespace TaskRunner.Data.Entities;

/// <summary>
/// 设备配额（付费同步 + AI构建）
/// </summary>
public class DeviceQuota
{
    public int Id { get; set; }

    /// <summary>
    /// 设备唯一标识
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// 设备显示名称
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// 付费知识库同步配额（剩余次数）
    /// </summary>
    public int PaidSyncQuota { get; set; } = 0;

    /// <summary>
    /// AI构建笔记配额（剩余次数）
    /// </summary>
    public int AiBuildQuota { get; set; } = 0;

    /// <summary>
    /// 总消费金额（元）
    /// </summary>
    public decimal TotalSpent { get; set; } = 0;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
