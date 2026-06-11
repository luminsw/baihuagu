using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskRunner.Data.Entities;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    [ApiController]
    [Route("api/mobile-logs")]
    [Route("mg/mobile-logs")]
    public partial class MobileLogController : ControllerBase
    {
        private readonly ILogger<MobileLogController> _logger;
        private readonly MobileLogService _logService;

        public MobileLogController(ILogger<MobileLogController> logger, MobileLogService logService)
        {
            _logger = logger;
            _logService = logService;
        }

        [HttpPost]
        public ActionResult ReceiveLog([FromBody] MobileLogRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Message))
                    return BadRequest(new { error = "Message is required" });

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
                return Ok(new { success = true, id = record.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to receive mobile log");
                return StatusCode(500, new { error = "Failed to save log" });
            }
        }

        [HttpPost("batch")]
        public ActionResult ReceiveLogs([FromBody] BatchLogRequest request)
        {
            try
            {
                if (request.Logs == null || request.Logs.Count == 0)
                    return Ok(new { success = true, count = 0 });

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
                return Ok(new { success = true, count = records.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to receive mobile logs batch");
                return StatusCode(500, new { error = "Failed to save logs" });
            }
        }

        [HttpGet]
        public ActionResult<List<MobileLogDto>> GetLogs([FromQuery] MobileLogQuery query)
        {
            try
            {
                var logs = _logService.GetLogs(query.DeviceId, query.Level, query.Limit, query.Offset);
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
                return Ok(dtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get mobile logs");
                return StatusCode(500, new { error = "Failed to get logs" });
            }
        }

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
