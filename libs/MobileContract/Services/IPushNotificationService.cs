using MobileContract.Devices;

namespace MobileContract.Services;

/// <summary>
/// 推送通知接口 — 服务端向移动端发起同步推送
/// </summary>
public interface IPushNotificationService
{
    /// <summary>
    /// 向指定设备发送同步推送请求
    /// </summary>
    Task<bool> PushSyncAsync(string deviceId, string? vaultId, string action, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定设备的待处理推送请求（移动端轮询）
    /// </summary>
    Task<IReadOnlyList<PushSyncRequest>> PollPendingAsync(string deviceId, bool wait = false, CancellationToken cancellationToken = default);
}
