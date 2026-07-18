using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;

namespace TaskRunner.Services;

/// <summary>
/// 启动编排 HostedService：在应用启动时执行数据库迁移、数据迁移和初始化同步。
/// 替代 StartupService 的构造函数副作用模式，使启动逻辑显式、可测试、可取消。
/// </summary>
public class StartupOrchestratorHostedService : IHostedService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IDbContextFactory<FamilyDbContext> _familyDbContextFactory;
    private readonly IDbContextFactory<AIDbContext> _aiDbContextFactory;
    private readonly VaultSettingsService _vaultSettings;
    private readonly LocalModelSettingsService _localModelSettings;
    private readonly ILogger<StartupOrchestratorHostedService> _logger;

    public StartupOrchestratorHostedService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IDbContextFactory<FamilyDbContext> familyDbContextFactory,
        IDbContextFactory<AIDbContext> aiDbContextFactory,
        VaultSettingsService vaultSettings,
        LocalModelSettingsService localModelSettings,
        ILogger<StartupOrchestratorHostedService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _familyDbContextFactory = familyDbContextFactory;
        _aiDbContextFactory = aiDbContextFactory;
        _vaultSettings = vaultSettings;
        _localModelSettings = localModelSettings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("启动编排开始...");

        try
        {
            LoadFromDatabase();
            _localModelSettings.LoadLocalModelConfigFromFile();
            TrySyncVaultsOnStartup();

            _logger.LogInformation("启动编排完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动编排部分失败，已记录错误但继续启动应用");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void LoadFromDatabase()
    {
        TryMigrateDatabase("Core", () =>
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            MigrateDatabase(dbContext, "Core");
        });

        TryMigrateDatabase("Family", () =>
        {
            using var familyDb = _familyDbContextFactory.CreateDbContext();
            MigrateDatabase(familyDb, "Family");
        });

        TryMigrateDatabase("AI", () =>
        {
            using var aiDb = _aiDbContextFactory.CreateDbContext();
            MigrateDatabase(aiDb, "AI");
        });
    }

    private void TryMigrateDatabase(string domainName, Action migrateAction)
    {
        try
        {
            migrateAction();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Domain} 数据库迁移失败，已记录错误但继续启动应用", domainName);
        }
    }

    private void MigrateDatabase(DbContext dbContext, string domainName)
    {
        try
        {
            try
            {
                var conn = dbContext.Database.GetDbConnection()?.ConnectionString;
                _logger.LogDebug("About to migrate {Domain} DB at: {ConnectionString}", domainName, conn);
            }
            catch
            {
                _logger.LogDebug("About to migrate {Domain} DB", domainName);
            }
            dbContext.Database.Migrate();
            _logger.LogDebug("{Domain} migrate completed successfully", domainName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Domain} 数据库迁移失败: {Message}", domainName, ex.Message);
            throw;
        }
    }

    private void TrySyncVaultsOnStartup()
    {
        var rootPath = _vaultSettings.VaultRootPathPreference;
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        if (!Directory.Exists(rootPath)) return;

        try
        {
            var (added, removed) = _vaultSettings.SyncVaultsWithFilesystem(rootPath);
            if (added > 0 || removed > 0)
            {
                _logger.LogInformation("启动时自动同步知识库完成：新增 {Added} 个，移除 {Removed} 个", added, removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动时自动同步知识库失败");
        }
    }
}
