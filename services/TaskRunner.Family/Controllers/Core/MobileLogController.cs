using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskRunner.Data.Entities;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// 移动端日志响应DTO（用于WebUI序列化）
    /// </summary>
    public class MobileLogDto
    {
        public string Id { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Level { get; set; } = "info";
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Context { get; set; } = "";
        public Dictionary<string, string>? Extra { get; set; }
        public DateTime ServerTimestamp { get; set; }
    }

    /// <summary>
    /// 移动端设备信息DTO
    /// </summary>
    public class MobileDeviceDto
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public int LogCount { get; set; }
        public DateTime LastLogTime { get; set; }
        public int ErrorCount { get; set; }
        public int WarnCount { get; set; }
    }

    /// <summary>
    /// 移动端日志统计DTO
    /// </summary>
    public class MobileLogStatsDto
    {
        public int Total { get; set; }
        public int Info { get; set; }
        public int Warn { get; set; }
        public int Error { get; set; }
        public int Devices { get; set; }
    }

    /// <summary>
    /// 移动端日志请求
    /// </summary>
    public class MobileLogRequest
    {
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? Level { get; set; }  // info, warn, error
        public string? Message { get; set; }
        public string? Timestamp { get; set; }
        public string? Context { get; set; }  // 上下文信息（如同步、搜索等）
        public Dictionary<string, string>? Extra { get; set; }  // 额外信息
    }

    /// <summary>
    /// 移动端日志查询参数
    /// </summary>
    public class MobileLogQuery
    {
        public string? DeviceId { get; set; }
        public string? Level { get; set; }
        public int Limit { get; set; } = 100;
        public int Offset { get; set; } = 0;
    }

    /// <summary>
    /// 批量日志请求体（匹配移动端RemoteLogService发送的格式）
    /// </summary>
    public class BatchLogRequest
    {
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public List<BatchLogRecord>? Logs { get; set; }
    }

    public class BatchLogRecord
    {
        public string? Level { get; set; }
        public string? Message { get; set; }
        public string? Timestamp { get; set; }
        public string? Context { get; set; }
        public Dictionary<string, string>? Extra { get; set; }
    }

    /// <summary>
    /// 移动端日志控制器
    /// 接收和存储移动端的日志信息
    /// </summary>
    [ApiController]
    [Route("api/mobile-logs")]
    [Route("mg/mobile-logs")]
    public class MobileLogController : ControllerBase
    {
        private readonly ILogger<MobileLogController> _logger;
        private readonly MobileLogService _logService;

        public MobileLogController(ILogger<MobileLogController> logger, MobileLogService logService)
        {
            _logger = logger;
            _logService = logService;
        }

        /// <summary>
        /// 接收移动端日志
        /// </summary>
        [HttpPost]
        public ActionResult ReceiveLog([FromBody] MobileLogRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Message))
                {
                    return BadRequest(new { error = "Message is required" });
                }

                var record = new MobileLogRecord
                {
                    DeviceId = request.DeviceId ?? "unknown",
                    DeviceName = request.DeviceName ?? "unknown",
                    Level = request.Level ?? "info",
                    Message = request.Message,
                    Timestamp = request.Timestamp ?? DateTime.UtcNow.ToString("o"),
                    Context = request.Context ?? "",
                    ExtraJson = request.Extra != null ? JsonSerializer.Serialize(request.Extra) : null
                };

                _logService.AddLog(record);

                _logger.LogDebug("Received mobile log from {DeviceName}: {Level} - {Message}",
                    record.DeviceName, record.Level, record.Message);

                return Ok(new { success = true, id = record.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to receive mobile log");
                return StatusCode(500, new { error = "Failed to save log" });
            }
        }

        /// <summary>
        /// 批量接收移动端日志
        /// </summary>
        [HttpPost("batch")]
        public ActionResult ReceiveLogs([FromBody] BatchLogRequest request)
        {
            try
            {
                _logger.LogInformation("[MobileLog] ReceiveLogs called, deviceId={DeviceId}, logCount={LogCount}", request.DeviceId ?? "unknown", request.Logs?.Count ?? 0);
                if (request.Logs == null || request.Logs.Count == 0)
                {
                    return Ok(new { success = true, count = 0 });
                }

                var deviceId = request.DeviceId ?? "unknown";
                var deviceName = request.DeviceName ?? "unknown";

                var records = new List<MobileLogRecord>();
                foreach (var log in request.Logs)
                {
                    if (string.IsNullOrEmpty(log.Message)) continue;

                    records.Add(new MobileLogRecord
                    {
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        Level = log.Level ?? "info",
                        Message = log.Message,
                        Timestamp = log.Timestamp ?? DateTime.UtcNow.ToString("o"),
                        Context = log.Context ?? "",
                        ExtraJson = log.Extra != null ? JsonSerializer.Serialize(log.Extra) : null
                    });
                }

                _logService.AddLogs(records);

                _logger.LogInformation("[MobileLog] Received {Count} mobile logs from {DeviceName}, total stored={Total}", records.Count, deviceName, _logService.GetTotalCount());

                return Ok(new { success = true, count = records.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to receive mobile logs batch");
                return StatusCode(500, new { error = "Failed to save logs" });
            }
        }

        /// <summary>
        /// 查询移动端日志
        /// </summary>
        [HttpGet]
        public ActionResult<List<MobileLogDto>> GetLogs([FromQuery] MobileLogQuery query)
        {
            try
            {
                var logs = _logService.GetLogs(
                    deviceId: query.DeviceId,
                    level: query.Level,
                    limit: query.Limit,
                    offset: query.Offset
                );

                var dtos = logs.Select(l => new MobileLogDto
                {
                    Id = l.Id.ToString(),
                    DeviceId = l.DeviceId,
                    DeviceName = l.DeviceName,
                    Level = l.Level,
                    Message = l.Message,
                    Timestamp = DateTime.TryParse(l.Timestamp, out var ts) ? ts : DateTime.UtcNow,
                    Context = l.Context,
                    Extra = !string.IsNullOrEmpty(l.ExtraJson) ? JsonSerializer.Deserialize<Dictionary<string, string>>(l.ExtraJson) : null,
                    ServerTimestamp = l.ServerTimestamp
                }).ToList();

                _logger.LogInformation("[MobileLog] GetLogs called, deviceId={DeviceId}, level={Level}, returned={Count}", query.DeviceId ?? "all", query.Level ?? "all", dtos.Count);

                return Ok(dtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get mobile logs");
                return StatusCode(500, new { error = "Failed to get logs" });
            }
        }

        /// <summary>
        /// 获取设备列表
        /// </summary>
        [HttpGet("devices")]
        public ActionResult<List<MobileDeviceDto>> GetDevices()
        {
            try
            {
                var devices = _logService.GetDevices();
                var dtos = devices.Select(d => new MobileDeviceDto
                {
                    DeviceId = d.TryGetValue("deviceId", out var did) ? did?.ToString() ?? "" : "",
                    DeviceName = d.TryGetValue("deviceName", out var dname) ? dname?.ToString() ?? "" : "",
                    LogCount = d.TryGetValue("logCount", out var lc) && lc is int lci ? lci : 0,
                    LastLogTime = d.TryGetValue("lastLogTime", out var lt) && DateTime.TryParse(lt?.ToString(), out var ltdt) ? ltdt : DateTime.MinValue,
                    ErrorCount = d.TryGetValue("errorCount", out var ec) && ec is int eci ? eci : 0,
                    WarnCount = d.TryGetValue("warnCount", out var wc) && wc is int wci ? wci : 0
                }).ToList();
                return Ok(dtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get devices");
                return StatusCode(500, new { error = "Failed to get devices" });
            }
        }

        /// <summary>
        /// 清空日志
        /// </summary>
        [HttpDelete]
        public ActionResult ClearLogs([FromQuery] string? deviceId)
        {
            try
            {
                _logService.ClearLogs(deviceId);
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear mobile logs");
                return StatusCode(500, new { error = "Failed to clear logs" });
            }
        }

        /// <summary>
        /// 获取日志统计信息
        /// </summary>
        [HttpGet("stats")]
        public ActionResult<MobileLogStatsDto> GetStats([FromQuery] string? deviceId)
        {
            try
            {
                var stats = _logService.GetStats(deviceId);
                return Ok(new MobileLogStatsDto
                {
                    Total = stats.TryGetValue("total", out var t) && t is int ti ? ti : 0,
                    Info = stats.TryGetValue("infoCount", out var i) && i is int ii ? ii : 0,
                    Warn = stats.TryGetValue("warnCount", out var w) && w is int wi ? wi : 0,
                    Error = stats.TryGetValue("errorCount", out var e) && e is int ei ? ei : 0,
                    Devices = stats.TryGetValue("deviceCount", out var d) && d is int di ? di : 0
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get log stats");
                return StatusCode(500, new { error = "Failed to get stats" });
            }
        }
    }
}
