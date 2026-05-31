using MobileContract.Quota;

namespace MobileContract.Services;

/// <summary>
/// 配额与应用内购买接口
/// </summary>
public interface IQuotaService
{
    /// <summary>
    /// 获取设备当前配额状态
    /// </summary>
    Task<QuotaInfoDto> GetQuotaAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取设备的购买历史
    /// </summary>
    Task<IReadOnlyList<OrderRecordDto>> GetOrdersAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有订单（管理端）
    /// </summary>
    Task<IReadOnlyList<OrderRecordDto>> GetAllOrdersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证应用内购买
    /// </summary>
    Task<VerifyPurchaseResult> VerifyPurchaseAsync(VerifyPurchaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 模拟购买（开发/测试用）
    /// </summary>
    Task<bool> SimulatePurchaseAsync(SimulatePurchaseRequest request, CancellationToken cancellationToken = default);
}
