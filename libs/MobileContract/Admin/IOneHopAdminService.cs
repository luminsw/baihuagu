using MobileContract.OneHop;

namespace MobileContract.Admin;

/// <summary>
/// OneHop 局域网直连管理接口 —— 仅由服务端管理后台（WebUI.Family）使用。
/// 管理服务器的局域网发现、连接状态。
/// </summary>
public interface IOneHopAdminService
{
    /// <summary>获取 OneHop 服务状态</summary>
    Task<OneHopStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>获取已发现的局域网设备</summary>
    Task<IReadOnlyList<OneHopDeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>连接到指定的 OneHop 设备</summary>
    Task<OneHopConnectionResult> ConnectAsync(OneHopConnectRequest request, CancellationToken cancellationToken = default);

    /// <summary>断开当前 OneHop 连接</summary>
    Task<bool> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>启动局域网设备发现</summary>
    Task<bool> StartDiscoveryAsync(CancellationToken cancellationToken = default);

    /// <summary>停止局域网设备发现</summary>
    Task<bool> StopDiscoveryAsync(CancellationToken cancellationToken = default);
}
