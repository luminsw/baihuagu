using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace TaskRunner.Controllers;

/// <summary>
/// 日志查询API。支持按级别、类别、关键词、时间范围过滤JSON Lines日志。
/// 用法: GET /api/logs?level=WARN&amp;category=MDns&amp;search=multicast&amp;lines=100
///       GET /api/logs?level=ERR&amp;since=2026-04-12T14:00:00
/// </summary>
[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly ILogger<LogsController> _logger;
    private readonly string _logsDir;

    public LogsController(ILogger<LogsController> logger, IHostEnvironment env)
    {
        _logger = logger;
        _logsDir = Path.Combine(env.ContentRootPath ?? AppContext.BaseDirectory, "logs");
    }

    /// <summary>
    /// 查询日志
    /// </summary>
    /// <param name="level">最低级别过滤: DBG/INFO/WARN/ERR/CRIT</param>
    /// <param name="category">类别名过滤（前缀匹配，不区分大小写）</param>
    /// <param name="search">关键词搜索（消息内容包含）</param>
    /// <param name="since">起始时间 ISO8601</param>
    /// <param name="until">结束时间 ISO8601</param>
    /// <param name="lines">最大返回行数，默认100，最大1000</param>
    /// <param name="file">指定日志文件名（默认今天）</param>
    [HttpGet]
    public IActionResult GetLogs(
        [FromQuery] string? level,
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] string? since,
        [FromQuery] string? until,
        [FromQuery] int lines = 100,
        [FromQuery] string? file = null)
    {
        try
        {
            lines = Math.Clamp(lines, 1, 1000);

            // 确定日志文件
            var logFile = !string.IsNullOrEmpty(file)
                ? Path.Combine(_logsDir, file)
                : Path.Combine(_logsDir, $"taskrunner-{DateTime.Now:yyyyMMdd}.log");

            if (!System.IO.File.Exists(logFile))
            {
                return Ok(new { total = 0, logs = Array.Empty<object>(), file = logFile });
            }

            // 级别过滤阈值
            var minLevel = ParseLevel(level);

            // 时间过滤
            DateTime? sinceTime = null, untilTime = null;
            if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, out var st)) sinceTime = st;
            if (!string.IsNullOrEmpty(until) && DateTime.TryParse(until, out var ut)) untilTime = ut;

            var results = new List<Dictionary<string, JsonElement>>();

            // 从文件末尾往前读，取最新的匹配记录
            var allLines = System.IO.File.ReadAllLines(logFile);
            for (var i = allLines.Length - 1; i >= 0 && results.Count < lines; i--)
            {
                var line = allLines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                Dictionary<string, JsonElement>? entry;
                try
                {
                    entry = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                    if (entry == null) continue;
                }
                catch { continue; }

                // 级别过滤
                if (minLevel != null && entry.TryGetValue("Level", out var entryLevel))
                {
                    var entryLevelInt = LevelToInt(entryLevel.ValueKind == JsonValueKind.String ? entryLevel.GetString() ?? "" : "");
                    if (entryLevelInt < minLevel.Value) continue;
                }

                // 类别过滤
                if (!string.IsNullOrEmpty(category) && entry.TryGetValue("Cat", out var entryCat))
                {
                    var catStr = entryCat.ValueKind == JsonValueKind.String ? entryCat.GetString() ?? "" : "";
                    if (!catStr.StartsWith(category, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // 关键词搜索
                if (!string.IsNullOrEmpty(search) && entry.TryGetValue("Msg", out var entryMsg))
                {
                    var msgStr = entryMsg.ValueKind == JsonValueKind.String ? entryMsg.GetString() ?? "" : "";
                    if (!msgStr.Contains(search, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // 时间过滤
                if (sinceTime != null && entry.TryGetValue("Ts", out var entryTs))
                {
                    var tsStr = entryTs.ValueKind == JsonValueKind.String ? entryTs.GetString() ?? "" : "";
                    if (DateTime.TryParse(tsStr, out var ts) && ts < sinceTime.Value)
                        continue;
                }
                if (untilTime != null && entry.TryGetValue("Ts", out var entryTs2))
                {
                    var tsStr2 = entryTs2.ValueKind == JsonValueKind.String ? entryTs2.GetString() ?? "" : "";
                    if (DateTime.TryParse(tsStr2, out var ts2) && ts2 > untilTime.Value)
                        continue;
                }

                results.Add(entry);
            }

            // 按时间正序返回
            results.Reverse();

            return Ok(new { total = results.Count, logs = results, file = Path.GetFileName(logFile) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying logs");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 列出可用的日志文件
    /// </summary>
    [HttpGet("files")]
    public IActionResult ListLogFiles()
    {
        try
        {
            if (!Directory.Exists(_logsDir))
                return Ok(Array.Empty<object>());

            var files = Directory.GetFiles(_logsDir, "taskrunner-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new
                {
                    name = f.Name,
                    size = f.Length,
                    lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                })
                .ToList();

            return Ok(files);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static int? ParseLevel(string? level) => level?.ToUpperInvariant() switch
    {
        "TRCE" => 0,
        "DBG" or "DEBUG" => 1,
        "INFO" or "INFORMATION" => 2,
        "WARN" or "WARNING" => 3,
        "ERR" or "ERROR" => 4,
        "CRIT" or "CRITICAL" or "FATAL" => 5,
        _ => null
    };

    private static int LevelToInt(string level) => level.ToUpperInvariant() switch
    {
        "TRCE" => 0,
        "DBG" => 1,
        "INFO" => 2,
        "WARN" => 3,
        "ERR" => 4,
        "CRIT" => 5,
        _ => 2 // 默认当INFO处理
    };
}
