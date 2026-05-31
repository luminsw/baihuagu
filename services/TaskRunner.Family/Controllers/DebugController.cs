using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;
using TaskRunner.Data.Entities;

namespace TaskRunner.Controllers;

/// <summary>
/// 调试接口 - 用于开发和测试时修改配额等限制数据
/// 仅本地访问时可用
/// </summary>
[ApiController]
[Route("api/debug")]
public class DebugController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly Services.DeviceQuotaService _quotaService;
    private readonly Services.SettingsService _settingsService;
    private readonly ILogger<DebugController> _logger;

    public DebugController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        Services.DeviceQuotaService quotaService,
        Services.SettingsService settingsService,
        ILogger<DebugController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _quotaService = quotaService;
        _settingsService = settingsService;
        _logger = logger;
    }

    private bool IsAuthorized()
    {
        // 本地请求直接放行
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp != null && (System.Net.IPAddress.IsLoopback(remoteIp)
            || remoteIp.ToString() == "127.0.0.1"
            || remoteIp.ToString() == "::1"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 重置指定设备今日的同步配额记录
    /// </summary>
    [HttpPost("reset-quota")]
    public IActionResult ResetQuota([FromQuery] string deviceId)
    {
        if (!IsAuthorized())
            return StatusCode(403, new { error = "调试接口仅允许本地访问" });

        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 不能为空" });

        using var db = _dbContextFactory.CreateDbContext();
        var today = DateTime.UtcNow.Date;
        var deleted = db.DeviceDailySyncs
            .Where(d => d.DeviceId == deviceId && d.SyncDate == today)
            .ExecuteDelete();

        _logger.LogWarning("[Debug] 重置设备今日配额记录: {DeviceId}, 删除 {Count} 条", deviceId, deleted);
        return Ok(new { success = true, deleted, message = $"已重置设备 {deviceId} 的今日同步记录" });
    }

    /// <summary>
    /// 为设备添加付费同步配额（老版余额）
    /// </summary>
    [HttpPost("grant-paid")]
    public IActionResult GrantPaidQuota([FromQuery] string deviceId, [FromQuery] int amount = 100)
    {
        if (!IsAuthorized())
            return StatusCode(403, new { error = "调试接口仅允许本地访问" });

        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 不能为空" });

        using var db = _dbContextFactory.CreateDbContext();
        var quota = db.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota == null)
        {
            quota = new DeviceQuota
            {
                DeviceId = deviceId,
                DeviceName = "debug",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.DeviceQuotas.Add(quota);
        }

        quota.PaidSyncQuota += amount;
        quota.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();

        _logger.LogWarning("[Debug] 授予设备付费配额: {DeviceId} +{Amount}, 当前余额 {Balance}", deviceId, amount, quota.PaidSyncQuota);
        return Ok(new { success = true, deviceId, added = amount, paidSyncQuota = quota.PaidSyncQuota });
    }

    /// <summary>
    /// 为设备添加 AI 构建配额
    /// </summary>
    [HttpPost("grant-ai-build")]
    public IActionResult GrantAiBuildQuota([FromQuery] string deviceId, [FromQuery] int amount = 100)
    {
        if (!IsAuthorized())
            return StatusCode(403, new { error = "调试接口仅允许本地访问" });

        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 不能为空" });

        using var db = _dbContextFactory.CreateDbContext();
        var quota = db.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota == null)
        {
            quota = new DeviceQuota
            {
                DeviceId = deviceId,
                DeviceName = "debug",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.DeviceQuotas.Add(quota);
        }

        quota.AiBuildQuota += amount;
        quota.UpdatedAt = DateTime.UtcNow;
        db.SaveChanges();

        _logger.LogWarning("[Debug] 授予设备 AI 构建配额: {DeviceId} +{Amount}, 当前余额 {Balance}", deviceId, amount, quota.AiBuildQuota);
        return Ok(new { success = true, deviceId, added = amount, aiBuildQuota = quota.AiBuildQuota });
    }

    /// <summary>
    /// 彻底清空设备所有配额数据（包括历史每日记录）
    /// </summary>
    [HttpPost("clear-device")]
    public IActionResult ClearDevice([FromQuery] string deviceId)
    {
        if (!IsAuthorized())
            return StatusCode(403, new { error = "调试接口仅允许本地访问" });

        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 不能为空" });

        using var db = _dbContextFactory.CreateDbContext();
        var dailyDeleted = db.DeviceDailySyncs.Where(d => d.DeviceId == deviceId).ExecuteDelete();
        var quota = db.DeviceQuotas.FirstOrDefault(q => q.DeviceId == deviceId);
        if (quota != null)
        {
            db.DeviceQuotas.Remove(quota);
        }
        db.SaveChanges();

        _logger.LogWarning("[Debug] 清空设备所有数据: {DeviceId}, 删除 {DailyCount} 条日记录", deviceId, dailyDeleted);
        return Ok(new { success = true, deviceId, dailyDeleted, quotaRemoved = quota != null });
    }

    /// <summary>
    /// 查看设备当前配额状态
    /// </summary>
    [HttpGet("quota-status")]
    public IActionResult GetQuotaStatus([FromQuery] string deviceId)
    {
        if (!IsAuthorized())
            return StatusCode(403, new { error = "调试接口仅允许本地访问" });

        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(new { error = "deviceId 不能为空" });

        var (paidSync, aiBuild, totalSpent) = _quotaService.GetQuota(deviceId);

        using var db = _dbContextFactory.CreateDbContext();
        var today = DateTime.UtcNow.Date;
        var todayRecord = db.DeviceDailySyncs
            .FirstOrDefault(d => d.DeviceId == deviceId && d.SyncDate == today);

        return Ok(new
        {
            deviceId,
            paidSyncQuota = paidSync,
            aiBuildQuota = aiBuild,
            totalSpent,
            todaySyncCount = todayRecord?.SyncCount ?? 0,
            todayVaultId = todayRecord?.VaultId
        });
    }
}
