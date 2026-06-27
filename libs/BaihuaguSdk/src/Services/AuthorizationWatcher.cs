using BaihuaguSdk.Models;
using BaihuaguSdk.Push;
using MobileContract.Services;

namespace BaihuaguSdk.Services;

/// <summary>
/// 授权等待器。
/// 封装「注册设备 → 连接 WebSocket → 等待授权推送 → 轮询兜底」的完整流程。
/// WebSocket 连接成功时只依赖推送，断开或连接失败时才启动轮询兜底。
/// </summary>
public class AuthorizationWatcher : IDisposable
{
    private readonly IDeviceRegistrationService _deviceRegistration;
    private readonly PushWebSocketService _pushService;
    private readonly object _pollLock = new();

    private CancellationTokenSource? _watchCts;
    private CancellationTokenSource? _pollCts;
    private volatile bool _webSocketEverConnected;

    public AuthorizationWatcher(IDeviceRegistrationService deviceRegistration, PushWebSocketService pushService)
    {
        _deviceRegistration = deviceRegistration;
        _pushService = pushService;
    }

    /// <summary>WebSocket 连接状态变化。</summary>
    public event Action<bool>? WebSocketConnectionStateChanged;

    /// <summary>收到授权通知（无论是 WebSocket 还是轮询）。</summary>
    public event Action? Authorized;

    /// <summary>
    /// 等待设备被授权。
    /// 优先使用 WebSocket 实时推送；WebSocket 连接失败或断开后回退到轮询。
    /// </summary>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="deviceName">设备名称，用于 WebSocket 连接标识。</param>
    /// <param name="webSocketConnectionTimeout">等待 WebSocket 建立连接的超时时间。</param>
    /// <param name="pollInterval">轮询间隔。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<AuthorizationResult> WaitForAuthorizationAsync(
        string serverUrl,
        string deviceName,
        TimeSpan? webSocketConnectionTimeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        // 1. 立即检查一次
        var immediate = await CheckAuthorizationAsync(serverUrl, ct);
        if (immediate.IsAuthorized) return immediate;

        StopWatching();
        _watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchCt = _watchCts.Token;

        var authorizedTcs = new TaskCompletionSource<AuthorizationResult>();
        var pollIntervalResolved = pollInterval ?? TimeSpan.FromSeconds(3);
        var connectionTimeout = webSocketConnectionTimeout ?? TimeSpan.FromSeconds(5);

        // 2. 监听 WebSocket 授权推送
        EventHandler pushAuthorizedHandler = (s, e) =>
            OnPushAuthorized(s, e, serverUrl, authorizedTcs, watchCt);
        _pushService.Authorized += pushAuthorizedHandler;

        // 3. 监听 WebSocket 连接状态，连上时不轮询，断开后才启动轮询兜底
        void OnConnectionStateChange(bool connected)
        {
            WebSocketConnectionStateChanged?.Invoke(connected);
            if (connected)
            {
                _webSocketEverConnected = true;
                StopPolling();
            }
            else if (_webSocketEverConnected)
            {
                StartPolling(serverUrl, pollIntervalResolved, authorizedTcs, watchCt);
            }
        }
        _pushService.ConnectionStateChanged += OnConnectionStateChange;

        try
        {
            // 4. 启动 WebSocket 连接
            await _pushService.ConnectAsync(serverUrl, deviceName, watchCt);

            // 5. 若 WebSocket 在指定超时内仍未连上，启动轮询兜底
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(connectionTimeout, watchCt);
                    if (!authorizedTcs.Task.IsCompleted)
                    {
                        StartPolling(serverUrl, pollIntervalResolved, authorizedTcs, watchCt, fallbackFromTimeout: true);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                }
            }, watchCt);

            var result = await authorizedTcs.Task.WaitAsync(watchCt);
            if (result.IsAuthorized)
            {
                Authorized?.Invoke();
            }
            return result;
        }
        finally
        {
            _pushService.Authorized -= pushAuthorizedHandler;
            _pushService.ConnectionStateChanged -= OnConnectionStateChange;
            StopWatching();
        }
    }

    /// <summary>立即查询一次授权状态。</summary>
    public async Task<AuthorizationResult> CheckAuthorizationAsync(string serverUrl, CancellationToken ct = default)
    {
        var result = await _deviceRegistration.RegisterDeviceAsync(serverUrl, ct);
        if (result is { Success: true, Authorized: true, SharedSecret: not null })
        {
            return AuthorizationResult.Authorized(result.SharedSecret);
        }

        if (result is { Success: true, Authorized: false })
        {
            return AuthorizationResult.NotAuthorized(result.RequestId);
        }

        return AuthorizationResult.Failed(result?.ErrorMessage ?? "注册失败");
    }

    /// <summary>
    /// WebSocket 授权推送事件处理器。
    /// 注：事件处理器签名本身必须是 void；所有异常已通过 TrySetException/TrySetCanceled 捕获，不会逃逸。
    /// </summary>
    private async void OnPushAuthorized(
        object? sender,
        EventArgs e,
        string serverUrl,
        TaskCompletionSource<AuthorizationResult> authorizedTcs,
        CancellationToken ct)
    {
        try
        {
            var result = await _deviceRegistration.RegisterDeviceAsync(serverUrl, ct);
            if (result is { Success: true, Authorized: true, SharedSecret: not null })
            {
                authorizedTcs.TrySetResult(AuthorizationResult.Authorized(result.SharedSecret));
            }
        }
        catch (OperationCanceledException)
        {
            authorizedTcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            authorizedTcs.TrySetException(ex);
        }
    }

    private void StartPolling(
        string serverUrl,
        TimeSpan interval,
        TaskCompletionSource<AuthorizationResult> authorizedTcs,
        CancellationToken ct,
        bool fallbackFromTimeout = false)
    {
        lock (_pollLock)
        {
            if (_pollCts != null) return; // 已在轮询
            if (fallbackFromTimeout && _webSocketEverConnected) return; // WebSocket 已连上，无需超时兜底
            _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = RunPollingAsync(serverUrl, interval, authorizedTcs, _pollCts.Token);
        }
    }

    private async Task RunPollingAsync(
        string serverUrl,
        TimeSpan interval,
        TaskCompletionSource<AuthorizationResult> authorizedTcs,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !authorizedTcs.Task.IsCompleted)
            {
                await Task.Delay(interval, ct);
                if (authorizedTcs.Task.IsCompleted || ct.IsCancellationRequested) break;

                try
                {
                    var result = await _deviceRegistration.RegisterDeviceAsync(serverUrl, ct);
                    if (result is { Success: true, Authorized: true, SharedSecret: not null })
                    {
                        authorizedTcs.TrySetResult(AuthorizationResult.Authorized(result.SharedSecret));
                        break;
                    }
                }
                catch
                {
                    // 轮询兜底：静默忽略单次失败
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
    }

    private void StopPolling()
    {
        CancellationTokenSource? cts;
        lock (_pollLock)
        {
            cts = _pollCts;
            _pollCts = null;
        }

        if (cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void StopWatching()
    {
        StopPolling();
        if (_watchCts != null)
        {
            _watchCts.Cancel();
            _watchCts.Dispose();
            _watchCts = null;
        }
        _webSocketEverConnected = false;
    }

    public void Dispose()
    {
        StopWatching();
    }
}
