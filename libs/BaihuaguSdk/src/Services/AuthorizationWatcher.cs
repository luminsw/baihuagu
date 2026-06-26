using BaihuaguSdk.Models;
using BaihuaguSdk.Push;
using MobileContract.Services;

namespace BaihuaguSdk.Services;

/// <summary>
/// 授权等待器。
/// 封装「注册设备 → 连接 WebSocket → 等待授权推送 → 轮询兜底」的完整流程。
/// </summary>
public class AuthorizationWatcher : IDisposable
{
    private readonly IDeviceRegistrationService _deviceRegistration;
    private readonly PushWebSocketService _pushService;

    private CancellationTokenSource? _pollCts;

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
    /// 优先使用 WebSocket 实时推送，WebSocket 不可用时回退到轮询。
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

        // 2. 连接 WebSocket
        var connected = await TryConnectWebSocketAsync(serverUrl, deviceName, webSocketConnectionTimeout ?? TimeSpan.FromSeconds(5), ct);
        WebSocketConnectionStateChanged?.Invoke(connected);

        // 3. 同时监听 WebSocket 授权事件和启动轮询，任一触发即返回
        var authorizedTcs = new TaskCompletionSource<AuthorizationResult>();

        async void OnPushAuthorized(object? sender, EventArgs e)
        {
            try
            {
                var result = await _deviceRegistration.RegisterDeviceAsync(serverUrl, ct);
                if (result is { Success: true, Authorized: true, SharedSecret: not null })
                {
                    authorizedTcs.TrySetResult(AuthorizationResult.Authorized(result.SharedSecret));
                }
            }
            catch (Exception ex)
            {
                authorizedTcs.TrySetException(ex);
            }
        }

        if (connected)
        {
            _pushService.Authorized += OnPushAuthorized;
        }

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = RunPollingAsync(serverUrl, pollInterval ?? TimeSpan.FromSeconds(3), authorizedTcs, _pollCts.Token);

        try
        {
            var result = await authorizedTcs.Task.WaitAsync(ct);
            if (result.IsAuthorized)
            {
                Authorized?.Invoke();
            }
            return result;
        }
        finally
        {
            StopPolling();
            if (connected)
            {
                _pushService.Authorized -= OnPushAuthorized;
            }
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

    private async Task<bool> TryConnectWebSocketAsync(string serverUrl, string deviceName, TimeSpan timeout, CancellationToken ct)
    {
        if (_pushService.IsConnected) return true;

        var connectedTcs = new TaskCompletionSource<bool>();
        void OnConnectionStateChange(bool connected)
        {
            if (connected) connectedTcs.TrySetResult(true);
        }

        _pushService.ConnectionStateChanged += OnConnectionStateChange;
        try
        {
            await _pushService.ConnectAsync(serverUrl, deviceName, ct);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                return await connectedTcs.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        finally
        {
            _pushService.ConnectionStateChanged -= OnConnectionStateChange;
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
        if (_pollCts != null)
        {
            _pollCts.Cancel();
            _pollCts.Dispose();
            _pollCts = null;
        }
    }

    public void Dispose()
    {
        StopPolling();
    }
}
