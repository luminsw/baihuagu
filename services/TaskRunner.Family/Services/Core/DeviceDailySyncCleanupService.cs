using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;

namespace TaskRunner.Services;

/// <summary>
/// 定期清理过期的 DeviceDailySyncs 记录
/// 保留最近 90 天的数据，防止数据库无限膨胀
/// </summary>
public class DeviceDailySyncCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DeviceDailySyncCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6);
    private readonly int _retentionDays = 90;

    public DeviceDailySyncCleanupService(
        IServiceProvider serviceProvider,
        ILogger<DeviceDailySyncCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeviceDailySync Cleanup Service started (retention: {RetentionDays} days, interval: {Interval})",
            _retentionDays, _cleanupInterval);

        // 启动时立即执行一次清理
        await CleanupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            await CleanupAsync(stoppingToken);
        }

        _logger.LogInformation("DeviceDailySync Cleanup Service stopped");
    }

    private async Task CleanupAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            using var dbContext = dbContextFactory.CreateDbContext();

            var cutoffDate = DateTime.UtcNow.Date.AddDays(-_retentionDays);

            var oldRecords = await dbContext.DeviceDailySyncs
                .Where(d => d.SyncDate < cutoffDate)
                .ToListAsync(stoppingToken);

            if (oldRecords.Count > 0)
            {
                dbContext.DeviceDailySyncs.RemoveRange(oldRecords);
                await dbContext.SaveChangesAsync(stoppingToken);
                _logger.LogInformation("Cleaned up {Count} DeviceDailySync records older than {CutoffDate:yyyy-MM-dd}",
                    oldRecords.Count, cutoffDate);
            }
            else
            {
                _logger.LogDebug("No DeviceDailySync records to clean up (cutoff: {CutoffDate:yyyy-MM-dd})",
                    cutoffDate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during DeviceDailySync cleanup");
        }
    }
}
