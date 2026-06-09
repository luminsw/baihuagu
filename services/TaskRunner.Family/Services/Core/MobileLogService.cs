using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services
{
    /// <summary>
    /// 移动端日志服务
    /// 使用 SQLite 持久化存储日志
    /// </summary>
    public class MobileLogService
    {
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly ILogger<MobileLogService>? _logger;

        public MobileLogService(IDbContextFactory<AppDbContext> dbContextFactory, ILogger<MobileLogService>? logger = null)
        {
            _dbContextFactory = dbContextFactory;
            _logger = logger;
        }

        /// <summary>
        /// 添加单条日志
        /// </summary>
        public void AddLog(MobileLogRecord record)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            dbContext.MobileLogs.Add(record);
            dbContext.SaveChanges();

            WriteToServerLog(record);
        }

        /// <summary>
        /// 批量添加日志
        /// </summary>
        public void AddLogs(List<MobileLogRecord> records)
        {
            if (records.Count == 0) return;

            using var dbContext = _dbContextFactory.CreateDbContext();
            dbContext.MobileLogs.AddRange(records);
            dbContext.SaveChanges();

            foreach (var record in records)
            {
                WriteToServerLog(record);
            }
        }

        /// <summary>
        /// 将移动端日志写入服务端日志系统
        /// </summary>
        private void WriteToServerLog(MobileLogRecord record)
        {
            if (_logger == null) return;

            var msg = $"[Mobile:{record.DeviceName}] {record.Message}";
            if (!string.IsNullOrEmpty(record.Context))
            {
                msg = $"[Mobile:{record.DeviceName}:{record.Context}] {record.Message}";
            }

            switch (record.Level.ToLower())
            {
                case "error":
                    _logger.LogError("{Msg}", msg);
                    break;
                case "warn":
                    _logger.LogWarning("{Msg}", msg);
                    break;
                default:
                    _logger.LogInformation("{Msg}", msg);
                    break;
            }
        }

        /// <summary>
        /// 获取当前存储的日志总数
        /// </summary>
        public int GetTotalCount()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            return dbContext.MobileLogs.Count();
        }

        /// <summary>
        /// 查询日志
        /// </summary>
        public List<MobileLogRecord> GetLogs(string? deviceId = null, string? level = null, int limit = 100, int offset = 0)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            var query = dbContext.MobileLogs.AsQueryable();

            if (!string.IsNullOrEmpty(deviceId))
            {
                query = query.Where(l => l.DeviceId == deviceId);
            }

            if (!string.IsNullOrEmpty(level))
            {
                query = query.Where(l => l.Level == level);
            }

            return query
                .OrderByDescending(l => l.ServerTimestamp)
                .Skip(offset)
                .Take(limit)
                .ToList();
        }

        /// <summary>
        /// 获取设备列表
        /// </summary>
        public List<Dictionary<string, object>> GetDevices()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            var deviceGroups = dbContext.MobileLogs
                .GroupBy(l => l.DeviceId)
                .Select(g => new
                {
                    DeviceId = g.Key,
                    DeviceName = g.OrderByDescending(l => l.ServerTimestamp).Select(l => l.DeviceName).FirstOrDefault() ?? "",
                    LogCount = g.Count(),
                    LastLogTime = g.Max(l => l.ServerTimestamp),
                    ErrorCount = g.Count(l => l.Level == "error"),
                    WarnCount = g.Count(l => l.Level == "warn")
                })
                .ToList();

            var devices = deviceGroups
                .Select(g => new Dictionary<string, object>
                {
                    ["deviceId"] = g.DeviceId,
                    ["deviceName"] = g.DeviceName,
                    ["logCount"] = g.LogCount,
                    ["lastLogTime"] = g.LastLogTime.ToString("o"),
                    ["errorCount"] = g.ErrorCount,
                    ["warnCount"] = g.WarnCount
                })
                .OrderByDescending(d => d["lastLogTime"])
                .ToList();

            return devices;
        }

        /// <summary>
        /// 清空日志
        /// </summary>
        public void ClearLogs(string? deviceId = null)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            if (string.IsNullOrEmpty(deviceId))
            {
                dbContext.MobileLogs.ExecuteDelete();
            }
            else
            {
                dbContext.MobileLogs.Where(l => l.DeviceId == deviceId).ExecuteDelete();
            }
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public Dictionary<string, object> GetStats(string? deviceId = null)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            var query = dbContext.MobileLogs.AsQueryable();
            if (!string.IsNullOrEmpty(deviceId))
            {
                query = query.Where(l => l.DeviceId == deviceId);
            }

            var total = query.Count();
            var errorCount = query.Count(l => l.Level == "error");
            var warnCount = query.Count(l => l.Level == "warn");
            var infoCount = query.Count(l => l.Level == "info");

            return new Dictionary<string, object>
            {
                ["total"] = total,
                ["errorCount"] = errorCount,
                ["warnCount"] = warnCount,
                ["infoCount"] = infoCount,
                ["deviceCount"] = query.Select(l => l.DeviceId).Distinct().Count()
            };
        }
    }
}
