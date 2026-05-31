using MobileContract.Pairing;

namespace MobileContract.Services;

/// <summary>
/// 设备配对接口 — 管理移动端与后端的配对、认证流程
/// </summary>
public interface IPairingService
{
    /// <summary>
    /// 获取当前配对码和设备ID
    /// </summary>
    Task<PairCodeResponse> GetPairCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 刷新配对码
    /// </summary>
    Task<PairCodeResponse> RefreshPairCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用配对码配对设备
    /// </summary>
    Task<PairResponse> PairDeviceAsync(PairRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查配对/授权状态
    /// </summary>
    Task<PairResponse> CheckPairStatusAsync(string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证访问令牌是否有效
    /// </summary>
    Task<bool> VerifyTokenAsync(VerifyTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取认证配置（共享密钥等）
    /// </summary>
    Task<AuthConfigResponse> GetAuthConfigAsync(AuthConfigRequest request, CancellationToken cancellationToken = default);
}
