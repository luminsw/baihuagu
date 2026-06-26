using MobileContract.Logging;

namespace MobileContract.Admin;

/// <summary>
/// 移动端日志管理接口 —— 仅由服务端管理后台（WebUI.Family）使用。
/// </summary>
public interface ILogAdminService
{
    /// <summary>查询已上报的移动端日志</summary>
    Task<IReadOnlyList<MobileLogRecord>> QueryLogsAsync(MobileLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>获取日志统计</summary>
    Task<LogStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>清空日志</summary>
    Task<bool> ClearLogsAsync(CancellationToken cancellationToken = default);
}
