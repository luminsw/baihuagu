using MobileContract.Logging;

namespace MobileContract.Services;

/// <summary>
/// 移动端日志收集接口
/// </summary>
public interface ILogService
{
    /// <summary>
    /// 提交单条日志
    /// </summary>
    Task<bool> SubmitLogAsync(MobileLogRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量提交日志
    /// </summary>
    Task<bool> SubmitLogsBatchAsync(BatchLogRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询日志（管理端）
    /// </summary>
    Task<IReadOnlyList<MobileLogRecord>> QueryLogsAsync(MobileLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取日志统计
    /// </summary>
    Task<LogStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空日志（管理端）
    /// </summary>
    Task<bool> ClearLogsAsync(CancellationToken cancellationToken = default);
}
