using TaskRunner.Core.Shared;
using MobileContract.Admin;
using MobileContract.Logging;
using MLogRecord = MobileContract.Logging.MobileLogRecord;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services.Adapters;

/// <summary>
/// 日志服务适配器 — 将 MobileGateway 的 MobileLogService 适配到 MobileContract.Admin.ILogAdminService
/// </summary>
public class MobileLogServiceAdapter : ILogAdminService
{
    private readonly MobileLogService _logService;

    public MobileLogServiceAdapter(MobileLogService logService)
    {
        _logService = logService;
    }

    public Task<IReadOnlyList<MLogRecord>> QueryLogsAsync(MobileLogQuery query, CancellationToken cancellationToken = default)
    {
        var logs = _logService.GetLogs(query.DeviceId, query.Level, query.Limit, query.Offset);
        var result = logs.Select(MapToContract).ToList();
        return Task.FromResult<IReadOnlyList<MLogRecord>>(result);
    }

    public Task<LogStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = _logService.GetStats();
        var result = new LogStats
        {
            TotalCount = stats.TryGetValue("totalCount", out var tc) && tc is int total ? total : 0,
            DeviceCount = stats.TryGetValue("deviceCount", out var dc) && dc is int dev ? dev : 0,
            LevelCounts = new Dictionary<string, int>()
        };
        return Task.FromResult(result);
    }

    public Task<bool> ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        _logService.ClearLogs();
        return Task.FromResult(true);
    }

    private static MLogRecord MapToContract(TaskRunner.Data.Entities.MobileLogRecord record)
    {
        return new MLogRecord
        {
            Id = record.Id.ToString(),
            DeviceId = record.DeviceId,
            DeviceName = record.DeviceName,
            Level = record.Level,
            Message = record.Message,
            Timestamp = DateTimeOffset.TryParse(record.Timestamp, out var ts) ? ts : DateTimeOffset.UtcNow,
            Context = record.Context,
            Extra = record.ExtraJson
        };
    }
}
