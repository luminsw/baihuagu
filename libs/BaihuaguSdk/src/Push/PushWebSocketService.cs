using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BaihuaguSdk.Push;

/// <summary>
/// WebSocket 实时推送服务。
/// 连接百花谷服务器 WebSocket 端点，接收同步推送通知。
/// 支持自动重连和 HTTP 轮询降级。
/// 与 Kotlin PushWebSocketService.kt 逻辑对齐。
/// </summary>
public class PushWebSocketService : IDisposable
{
    private const int ReconnectDelayMs = 5000;
    private const int MaxReconnectAttempts = 10;
    private const int PollIntervalMs = 60000;

    private readonly HttpClient _httpClient;
    private readonly Func<string, string, string, CancellationToken, Task>? _onSyncRequest;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _connectCts;
    private volatile bool _disposed;
    private int _reconnectAttempts;

    private string _serverBaseUrl = "";
    private string _deviceName = "";
    private string? _pendingPollUrl;

    /// <summary>收到同步推送时的回调（vaultId, vaultName, action）</summary>
    public Func<string, string, string, CancellationToken, Task>? OnSyncRequest
    {
        get => _onSyncRequest;
        init => _onSyncRequest = value;
    }

    /// <summary>连接状态变化回调</summary>
    public Action<bool>? OnConnectionStateChange { get; init; }

    /// <summary>日志回调</summary>
    public Action<string>? OnLog { get; init; }

    /// <summary>是否已连接到服务器</summary>
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public PushWebSocketService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>连接到百花谷服务器的 WebSocket 推送端点</summary>
    public async Task ConnectAsync(string baseUrl, string deviceName, CancellationToken ct = default)
    {
        if (_disposed) return;

        await DisconnectAsync();
        _serverBaseUrl = baseUrl.TrimEnd('/');
        _deviceName = deviceName;
        _reconnectAttempts = 0;

        var wsUrl = _serverBaseUrl.Replace("https://", "wss://")
            .Replace("http://", "ws://")
            + $"/ws/push?deviceName={Uri.EscapeDataString(deviceName)}";

        Log($"PushWebSocket connecting to {wsUrl}");
        _pendingPollUrl = null;

        _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ConnectLoopAsync(wsUrl, _connectCts.Token);
    }

    /// <summary>断开 WebSocket 连接</summary>
    public async Task DisconnectAsync()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                    "Client disconnect", CancellationToken.None);
            }
            catch { /* ignore */ }
        }
        _webSocket?.Dispose();
        _webSocket = null;
    }

    /// <summary>彻底销毁（释放所有资源）</summary>
    public void Dispose()
    {
        _disposed = true;
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _webSocket?.Dispose();
    }

    // ---- internal ----

    private async Task ConnectLoopAsync(string wsUrl, CancellationToken ct)
    {
        try
        {
            using var ws = new ClientWebSocket();
            _webSocket = ws;

            await ws.ConnectAsync(new Uri(wsUrl), ct);
            Log("PushWebSocket connected");
            _reconnectAttempts = 0;
            OnConnectionStateChange?.Invoke(true);

            // Read messages in a loop
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log($"PushWebSocket closed by server");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    HandleMessage(text);
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (WebSocketException ex)
        {
            Log($"PushWebSocket error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"PushWebSocket failure: {ex.Message}");
        }

        _webSocket = null;
        OnConnectionStateChange?.Invoke(false);

        if (!_disposed && !ct.IsCancellationRequested)
            ScheduleReconnect(ct);
    }

    private void HandleMessage(string text)
    {
        Log($"PushWebSocket message: {text}");
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "SyncRequest")
            {
                var vaultId = root.TryGetProperty("vaultId", out var v) ? (v.GetString() ?? "") : "";
                var vaultName = root.TryGetProperty("vaultName", out var vn) ? (vn.GetString() ?? vaultId) : vaultId;
                var action = root.TryGetProperty("action", out var a) ? (a.GetString() ?? "sync") : "sync";

                if (!string.IsNullOrEmpty(vaultId))
                    _ = OnSyncRequest?.Invoke(vaultId, vaultName, action, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Log($"PushWebSocket message parse error: {ex.Message}");
        }
    }

    private async void ScheduleReconnect(CancellationToken ct)
    {
        if (_disposed) return;

        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Log("PushWebSocket max reconnect attempts reached, falling back to HTTP polling");
            StartHttpPolling();
            return;
        }

        _reconnectAttempts++;
        Log($"PushWebSocket reconnecting (attempt {_reconnectAttempts})");

        try
        {
            await Task.Delay(ReconnectDelayMs, ct);
            if (!_disposed && !ct.IsCancellationRequested && !string.IsNullOrEmpty(_serverBaseUrl))
            {
                var wsUrl = _serverBaseUrl.Replace("https://", "wss://")
                    .Replace("http://", "ws://")
                    + $"/ws/push?deviceName={Uri.EscapeDataString(_deviceName)}";
                await ConnectLoopAsync(wsUrl, ct);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    // ---- HTTP polling fallback ----

    private void StartHttpPolling()
    {
        if (_disposed || string.IsNullOrEmpty(_serverBaseUrl)) return;

        _pendingPollUrl = $"{_serverBaseUrl}/mg/devices/push-pending?deviceName={Uri.EscapeDataString(_deviceName)}&wait=false";
        _ = PollLoopAsync();
    }

    private async Task PollLoopAsync()
    {
        while (!_disposed && _webSocket == null)
        {
            await Task.Delay(PollIntervalMs);
            if (_disposed || _webSocket != null) break;

            try
            {
                var resp = await _httpClient.GetStringAsync(_pendingPollUrl);
                if (!string.IsNullOrEmpty(resp))
                    HandleMessage(resp);
            }
            catch { /* best effort */ }
        }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
