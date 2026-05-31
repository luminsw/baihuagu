using MobileContract.Quota;
using MobileContract.Services;

namespace TaskRunner.Services.Adapters;

/// <summary>
/// 配额服务适配器 — 将 DeviceQuotaService 适配到 MobileContract.IQuotaService
/// </summary>
public class MobileQuotaServiceAdapter : IQuotaService
{
    private readonly DeviceQuotaService _quotaService;

    public MobileQuotaServiceAdapter(DeviceQuotaService quotaService)
    {
        _quotaService = quotaService;
    }

    public Task<QuotaInfoDto> GetQuotaAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var quota = _quotaService.GetOrCreateQuota(deviceId);
        var (paidSync, aiBuild, totalSpent) = _quotaService.GetQuota(deviceId);

        var result = new QuotaInfoDto
        {
            NoteLimit = 0,
            NotesUsed = 0,
            VaultLimit = 0,
            VaultsUsed = 0,
            StreakDays = 0,
            InGracePeriod = false,
            RolloverNotes = 0,
            PaidSyncQuota = paidSync,
            AiBuildQuota = aiBuild,
            TotalSpent = totalSpent,
            Message = $"付费同步配额: {paidSync}, AI构建配额: {aiBuild}",
            Products = DeviceQuotaService.ProductCatalog.Select(p => new ProductInfoDto
            {
                ProductId = p.Key,
                QuotaType = p.Value.quotaType,
                QuotaAmount = p.Value.quotaAmount,
                Price = p.Value.price
            }).ToList()
        };

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<OrderRecordDto>> GetOrdersAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        // Family 端暂无订单历史查询实现
        return Task.FromResult<IReadOnlyList<OrderRecordDto>>(Array.Empty<OrderRecordDto>());
    }

    public Task<IReadOnlyList<OrderRecordDto>> GetAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<OrderRecordDto>>(Array.Empty<OrderRecordDto>());
    }

    public Task<VerifyPurchaseResult> VerifyPurchaseAsync(VerifyPurchaseRequest request, CancellationToken cancellationToken = default)
    {
        // Family 端暂无华为 IAP 验证实现
        return Task.FromResult(new VerifyPurchaseResult
        {
            IsVerified = false,
            Status = "not_implemented",
            Message = "家庭版暂不支持应用内购买验证"
        });
    }

    public Task<bool> SimulatePurchaseAsync(SimulatePurchaseRequest request, CancellationToken cancellationToken = default)
    {
        var product = DeviceQuotaService.ProductCatalog
            .FirstOrDefault(p => p.Key == request.ProductId);

        if (product.Key == null)
            return Task.FromResult(false);

        _quotaService.AddQuota(
            request.DeviceId,
            product.Value.quotaType,
            product.Value.quotaAmount,
            product.Value.price,
            request.DeviceName ?? "");

        return Task.FromResult(true);
    }
}
