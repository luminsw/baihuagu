using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services;

/// <summary>
/// 设备配额与同步控制服务
/// 管理：付费知识库同步配额、AI构建配额、每日同步频率限制
/// </summary>
public class DeviceQuotaService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<DeviceQuotaService> _logger;

    // 华为 IAP 商品映射：商品ID → (配额类型, 配额数量)
    public static readonly Dictionary<string, (string quotaType, int quotaAmount, decimal price)> ProductCatalog = new()
    {
        ["sync_5"] = ("sync", 5, 1.00m),
        ["sync_20"] = ("sync", 20, 3.00m),
        ["ai_build_5"] = ("ai_build", 5, 2.00m),
        ["ai_build_20"] = ("ai_build", 20, 5.00m),
    };

    public DeviceQuotaService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<DeviceQuotaService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// 获取或创建设备配额记录
    /// </summary>
    public DeviceQuota GetOrCreateQuota(string deviceId, string deviceName = "")
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var quota = dbContext.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota == null)
        {
            quota = new DeviceQuota
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                PaidSyncQuota = 0,
                AiBuildQuota = 0,
                TotalSpent = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            dbContext.DeviceQuotas.Add(quota);
            dbContext.SaveChanges();
            _logger.LogInformation("创建设备配额记录: {DeviceId}", deviceId);
        }
        return quota;
    }

    /// <summary>
    /// 查询设备当前配额
    /// </summary>
    public (int paidSyncQuota, int aiBuildQuota, decimal totalSpent) GetQuota(string deviceId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var quota = dbContext.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota == null) return (0, 0, 0);
        return (quota.PaidSyncQuota, quota.AiBuildQuota, quota.TotalSpent);
    }

    /// <summary>
    /// 检查并扣除同步配额
    /// 返回：(是否允许同步, 错误信息)
    /// </summary>
    public (bool allowed, string? error) CheckAndDeductSyncQuota(string deviceId, string vaultId, bool isPaidVault)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var today = DateTime.UtcNow.Date;

        // 1. 检查每日频率限制（无论免费付费，同一天同知识库只能同步1次）
        var dailyRecord = dbContext.DeviceDailySyncs
            .FirstOrDefault(d => d.DeviceId == deviceId && d.VaultId == vaultId && d.SyncDate == today);

        if (dailyRecord != null && dailyRecord.SyncCount >= 1)
        {
            return (false, "今日已同步过该知识库，请明天再试");
        }

        // 2. 免费知识库：只记录频率，不检查配额
        if (!isPaidVault)
        {
            RecordSyncInternal(dbContext, deviceId, vaultId, today, usedPaidQuota: false);
            dbContext.SaveChanges();
            return (true, null);
        }

        // 3. 付费知识库：检查并扣除配额
        var quota = dbContext.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota == null || quota.PaidSyncQuota <= 0)
        {
            return (false, "同步配额不足，请在应用商店购买同步流量包");
        }

        quota.PaidSyncQuota--;
        RecordSyncInternal(dbContext, deviceId, vaultId, today, usedPaidQuota: true);
        dbContext.SaveChanges();

        _logger.LogInformation("扣除同步配额: DeviceId={DeviceId}, VaultId={VaultId}, 剩余={Remaining}",
            deviceId, vaultId, quota.PaidSyncQuota);
        return (true, null);
    }

    /// <summary>
    /// 检查并扣除 AI 构建配额
    /// </summary>
    public (bool allowed, string? error) CheckAndDeductAiBuildQuota(string deviceId)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var quota = dbContext.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota == null || quota.AiBuildQuota <= 0)
        {
            return (false, "AI构建配额不足，请在应用商店购买AI笔记包");
        }

        quota.AiBuildQuota--;
        dbContext.SaveChanges();

        _logger.LogInformation("扣除AI构建配额: DeviceId={DeviceId}, 剩余={Remaining}",
            deviceId, quota.AiBuildQuota);
        return (true, null);
    }

    /// <summary>
    /// 增加配额（购买成功后调用）
    /// </summary>
    public void AddQuota(string deviceId, string quotaType, int amount, decimal price, string deviceName = "")
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        var quota = dbContext.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota == null)
        {
            quota = new DeviceQuota
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            dbContext.DeviceQuotas.Add(quota);
        }

        if (quotaType == "sync")
        {
            quota.PaidSyncQuota += amount;
        }
        else if (quotaType == "ai_build")
        {
            quota.AiBuildQuota += amount;
        }

        quota.TotalSpent += price;
        quota.UpdatedAt = DateTime.UtcNow;
        dbContext.SaveChanges();

        _logger.LogInformation("增加配额: DeviceId={DeviceId}, Type={Type}, Amount={Amount}, Price={Price}",
            deviceId, quotaType, amount, price);
    }

    /// <summary>
    /// 根据商品ID解析配额信息
    /// </summary>
    public static (string quotaType, int quotaAmount, decimal price)? ParseProduct(string productId)
    {
        if (ProductCatalog.TryGetValue(productId, out var info))
        {
            return info;
        }
        return null;
    }

    private static void RecordSyncInternal(AppDbContext dbContext, string deviceId, string vaultId, DateTime today, bool usedPaidQuota)
    {
        var record = dbContext.DeviceDailySyncs
            .FirstOrDefault(d => d.DeviceId == deviceId && d.VaultId == vaultId && d.SyncDate == today);

        if (record == null)
        {
            record = new DeviceDailySync
            {
                DeviceId = deviceId,
                VaultId = vaultId,
                SyncDate = today,
                SyncCount = 0,
                UsedPaidQuota = usedPaidQuota,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            dbContext.DeviceDailySyncs.Add(record);
        }

        record.SyncCount++;
        record.UsedPaidQuota = usedPaidQuota;
        record.UpdatedAt = DateTime.UtcNow;
    }
}
