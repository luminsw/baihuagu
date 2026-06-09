using TaskRunner.Core.Shared;
using MobileContract.Logging;
using MobileContract.Services;
using MLogRecord = MobileContract.Logging.MobileLogRecord;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services.Adapters;

/// <summary>
/// 日志服务适配器 — 将 MobileGateway 的 MobileLogService 适配到 MobileContract.ILogService
/// </summary>
public class MobileLogServiceAdapter : ILogService
{
    private readonly MobileLogService _logService;

    public MobileLogServiceAdapter(MobileLogService logService)
    {
        _logService = logService;
    }

    public Task<bool> SubmitLogAsync(MobileLogRequest request, CancellationToken cancellationToken = default)
    {
        _logService.AddLog(new TaskRunner.Data.Entities.MobileLogRecord
        {
            DeviceId = request.DeviceId ?? "",
            DeviceName = request.DeviceName ?? "",
            Level = request.Level ?? "info",
            Message = request.Message,
            Timestamp = (request.Timestamp ?? DateTimeOffset.UtcNow).ToString("o"),
            Context = request.Context ?? "",
            ExtraJson = !string.IsNullOrEmpty(request.Extra) ? System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["extra"] = request.Extra }) : null
        });
        return Task.FromResult(true);
    }

    public Task<bool> SubmitLogsBatchAsync(BatchLogRequest request, CancellationToken cancellationToken = default)
    {
        var records = request.Logs.Select(l => new TaskRunner.Data.Entities.MobileLogRecord
        {
            DeviceId = request.DeviceId ?? "",
            DeviceName = request.DeviceName ?? "",
            Level = l.Level ?? "info",
            Message = l.Message ?? "",
            Timestamp = (l.Timestamp ?? DateTimeOffset.UtcNow).ToString("o"),
            Context = l.Context ?? "",
            ExtraJson = !string.IsNullOrEmpty(l.Extra) ? System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["extra"] = l.Extra }) : null
        }).ToList();

        _logService.AddLogs(records);
        return Task.FromResult(true);
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
