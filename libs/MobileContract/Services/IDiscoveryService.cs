using MobileContract.Discovery;

namespace MobileContract.Services;

/// <summary>
/// 服务发现接口 — 移动端通过此接口获取后端连接信息
/// </summary>
public interface IDiscoveryService
{
    /// <summary>
    /// 获取当前服务发现信息（含 baseUrl、deviceId 等）
    /// </summary>
    Task<DiscoveryResponse> GetDiscoveryInfoAsync(CancellationToken cancellationToken = default);
}
