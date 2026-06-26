using System.Text.Json;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Transport;
using MobileContract.Services;

namespace BaihuaguSdk.Services;

/// <summary>
/// 远程日志服务。
/// 缓冲批量日志，定时或达到阈值后发送到百花谷服务器和 OpenObserve。
/// 与 Kotlin RemoteLogService.kt 逻辑对齐。
/// </summary>
public class LogServiceImpl : IRemoteLogService, IDisposable
{
    private const int MaxBufferSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly IRequestSigner _signer;
    private readonly string _deviceId;
    private readonly string _deviceName;

    private readonly List<LogEntry> _buffer = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _flushLoop;

    private string _serverUrl = "";
    private string _ooUrl = "";
    private string _ooAuth = "";
    private volatile bool _ooEnabled;
#pragma warning disable CS0414
    private volatile bool _disposed;
#pragma warning restore CS0414

    public LogServiceImpl(
        HttpClient httpClient, IRequestSigner signer,
        string deviceId, string deviceName)
    {
        _httpClient = httpClient;
        _signer = signer;
        _deviceId = deviceId;
        _deviceName = deviceName;
    }

    /// <summary>初始化远程日志（需指定百花谷服务器地址）</summary>
    /// <param name="serverUrl">百花谷服务器地址</param>
    /// <param name="ooHost">OpenObserve 主机，为空则禁用 OpenObserve</param>
    /// <param name="ooUsername">OpenObserve 用户名</param>
    /// <param name="ooPassword">OpenObserve 密码</param>
    public void Initialize(
        string serverUrl,
        string? ooHost = null,
        string? ooUsername = null,
        string? ooPassword = null)
    {
        UpdateServerUrl(serverUrl);
        _ooEnabled = !string.IsNullOrEmpty(ooHost);

        if (_ooEnabled)
        {
            var ooPort = DeriveOpenObservePort(serverUrl);
            _ooUrl = $"http://{ooHost}:{ooPort}/api/default/mobile/_json";

            var username = !string.IsNullOrEmpty(ooUsername) ? ooUsername : "root@localhost.com";
            var password = !string.IsNullOrEmpty(ooPassword) ? ooPassword : "Complexpass#123";
            _ooAuth = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
        }

        // Start background flush only once
        if (_flushLoop == null)
            _flushLoop = FlushLoopAsync(_cts.Token);
    }

    /// <summary>切换目标服务器地址</summary>
    public void UpdateServerUrl(string serverUrl)
    {
        _serverUrl = serverUrl;
    }

    /// <summary>强制刷新缓冲区到服务端</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
        => FlushBatchAsync();

    /// <summary>记录一条日志</summary>
    public void Log(string level, string message, string? context = null,
        Dictionary<string, string>? extra = null)
    {
        var entry = new LogEntry(
            level, message, DateTimeOffset.UtcNow.ToString("o"), context,
            extra != null ? JsonSerializer.Serialize(extra) : null);

        lock (_lock) { _buffer.Add(entry); }

        if (_buffer.Count >= MaxBufferSize)
            _ = FlushBatchAsync();
    }

    public void Info(string msg, string? ctx = null) => Log("INFO", msg, ctx);
    public void Warn(string msg, string? ctx = null) => Log("WARN", msg, ctx);
    public void Error(string msg, string? ctx = null) => Log("ERROR", msg, ctx);
    public void Debug(string msg, string? ctx = null) => Log("DEBUG", msg, ctx);

    /// <summary>强制刷新缓冲区（用于测试）</summary>
    internal Task ForceFlushAsync() => FlushBatchAsync();

    /// <summary>停止后台刷新并清空缓冲区</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _ = FlushBatchAsync(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    // ---- internal ----

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(FlushInterval, ct); } catch { break; }
            await FlushBatchAsync();
        }
    }

    private async Task FlushBatchAsync()
    {
        List<LogEntry> batch;
        lock (_lock)
        {
            if (_buffer.Count == 0) return;
            batch = new List<LogEntry>(_buffer);
            _buffer.Clear();
        }

        try
        {
            // Send to baihuagu server
            var transport = new HttpTransport(_httpClient, _signer, _serverUrl);
            var body = new
            {
                deviceId = _deviceId,
                deviceName = _deviceName,
                logs = batch.Select(e => new
                {
                    level = e.Level, message = e.Message,
                    timestamp = e.Timestamp, context = e.Context, extra = e.Extra
                })
            };
            await transport.PostJsonAsync<object>("/mg/mobile-logs/batch", body);

            // Send to OpenObserve (best effort)
            if (_ooEnabled && !string.IsNullOrEmpty(_ooUrl))
            {
                var ooRecords = batch.Select(e => new
                {
                    level = e.Level,
                    _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    message = e.Message,
                    device_id = _deviceId,
                    device_name = _deviceName,
                    context = e.Context,
                });
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, _ooUrl);
                    request.Headers.TryAddWithoutValidation("Authorization", $"Basic {_ooAuth}");
                    request.Content = new StringContent(
                        JsonSerializer.Serialize(ooRecords),
                        System.Text.Encoding.UTF8, "application/json");
                    await _httpClient.SendAsync(request);
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    private static int DeriveOpenObservePort(string serverUrl)
    {
        var uri = new Uri(serverUrl);
        return uri.Port switch
        {
            8788 => 5082,   // family server
            8787 => 5080,
            _ => 6080,      // official server default
        };
    }

    private record LogEntry(string Level, string Message, string Timestamp,
        string? Context, string? Extra);
}
