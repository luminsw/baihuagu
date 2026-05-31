using Microsoft.EntityFrameworkCore;
using TaskRunner.Data.Entities;

namespace TaskRunner.Data;

/// <summary>
/// SQLite 数据库上下文 - 使用 Entity Framework Core
/// </summary>
public class AppDbContext : DbContext
{
    private readonly string _dbPath;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        _dbPath = Database.GetDbConnection().ConnectionString;
    }

    /// <summary>
    /// 无参构造函数（用于设计时工具，如迁移）
    /// </summary>
    public AppDbContext()
    {
        _dbPath = GetDefaultDbPath();
    }

    /// <summary>
    /// AI 提供商配置
    /// </summary>
    public DbSet<AiProviderSetting> AiProviderSettings => Set<AiProviderSetting>();

    /// <summary>
    /// 知识库
    /// </summary>
    public DbSet<Vault> Vaults => Set<Vault>();

    /// <summary>
    /// 已授权设备
    /// </summary>
    public DbSet<AuthorizedDevice> AuthorizedDevices => Set<AuthorizedDevice>();
    public DbSet<DeviceSyncLog> DeviceSyncLogs => Set<DeviceSyncLog>();

    /// <summary>
    /// 设备配额（付费同步 + AI构建）
    /// </summary>
    public DbSet<DeviceQuota> DeviceQuotas => Set<DeviceQuota>();

    /// <summary>
    /// 设备每日同步记录（频率限制）
    /// </summary>
    public DbSet<DeviceDailySync> DeviceDailySyncs => Set<DeviceDailySync>();

    /// <summary>
    /// 华为 IAP 购买记录
    /// </summary>
    public DbSet<IapPurchaseRecord> IapPurchaseRecords => Set<IapPurchaseRecord>();

    /// <summary>
    /// 任务历史
    /// </summary>
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();

    /// <summary>
    /// 服务器地址配置（用于移动端连接）
    /// </summary>
    public DbSet<ServerAddressSetting> ServerAddressSettings => Set<ServerAddressSetting>();

    /// <summary>
    /// OpenClaw 任务
    /// </summary>
    public DbSet<OpenClawTask> OpenClawTasks => Set<OpenClawTask>();

    /// <summary>
    /// AI 调用性能指标
    /// </summary>
    public DbSet<AiUsageMetric> AiUsageMetrics => Set<AiUsageMetric>();

    /// <summary>
    /// Benchmark 测试结果
    /// </summary>
    public DbSet<BenchmarkSessionEntity> BenchmarkSessions => Set<BenchmarkSessionEntity>();

    /// <summary>
    /// Onboarding 完成状态
    /// </summary>
    public DbSet<OnboardingState> OnboardingStates => Set<OnboardingState>();

    /// <summary>
    /// 初始化任务进度
    /// </summary>
    public DbSet<InitTaskProgress> InitTaskProgresses => Set<InitTaskProgress>();

    /// <summary>
    /// Embedding 模型配置
    /// </summary>
    public DbSet<EmbeddingConfig> EmbeddingConfigs => Set<EmbeddingConfig>();

    /// <summary>
    /// 学习者档案
    /// </summary>
    public DbSet<LearnerProfile> LearnerProfiles => Set<LearnerProfile>();

    /// <summary>
    /// 成就解锁记录
    /// </summary>
    public DbSet<Achievement> Achievements => Set<Achievement>();

    /// <summary>
    /// 学习活动记录
    /// </summary>
    public DbSet<StudyActivity> StudyActivities => Set<StudyActivity>();

    /// <summary>
    /// 笔记向量缓存
    /// </summary>
    public DbSet<NoteEmbedding> NoteEmbeddings => Set<NoteEmbedding>();

    /// <summary>
    /// 卡片复习状态
    /// </summary>
    public DbSet<CardReviewState> CardReviewStates => Set<CardReviewState>();

    /// <summary>
    /// 对话记忆条目
    /// </summary>
    public DbSet<ChatMemoryEntry> ChatMemoryEntries => Set<ChatMemoryEntry>();

    /// <summary>
    /// 移动端日志
    /// </summary>
    public DbSet<MobileLogRecord> MobileLogs => Set<MobileLogRecord>();

    /// <summary>
    /// 获取数据库文件路径
    /// </summary>
    public string DatabasePath => _dbPath;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dbPath = GetDefaultDbPath();
            optionsBuilder.UseSqlite($"Data Source={dbPath};Foreign Keys=True;");
        }
    }

    private static string GetDefaultDbPath()
    {
        string dataDir;
        var envDir = Environment.GetEnvironmentVariable("TASKRUNNER_DATA_DIR");
        if (!string.IsNullOrEmpty(envDir))
        {
            dataDir = envDir;
        }
        else
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // dotnet run 模式下 BaseDirectory 指向 bin/Debug 或 bin/Release，每次编译会被清理
            // 检测到此情况时回退到项目根目录的 data/，避免数据库丢失
            var binDebug = Path.Combine("bin", "Debug");
            var binRelease = Path.Combine("bin", "Release");
            if (baseDir.Contains(binDebug) || baseDir.Contains(binRelease))
            {
                var index = baseDir.IndexOf(binDebug);
                if (index < 0) index = baseDir.IndexOf(binRelease);
                if (index > 0)
                {
                    dataDir = Path.Combine(baseDir.Substring(0, index), "data");
                }
                else
                {
                    dataDir = Path.Combine(baseDir, "data");
                }
            }
            else
            {
                dataDir = Path.Combine(baseDir, "data");
            }
        }
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "taskrunner.db");
    }

    /// <summary>
    /// 获取默认数据库路径（供 Program.cs 使用）
    /// </summary>
    public static string GetDbPath()
    {
        return GetDefaultDbPath();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // AI 提供商配置
        modelBuilder.Entity<AiProviderSetting>(entity =>
        {
            entity.ToTable("AiProviderSettings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProviderId).IsUnique();
            entity.HasIndex(e => e.IsMain);

            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.BaseUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.EncryptedApiKey).HasMaxLength(2000);
            entity.Property(e => e.ModelsJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.SortOrder).HasDefaultValue(0);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.IsMain).HasDefaultValue(false);
            entity.Property(e => e.Tier).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 知识库
        modelBuilder.Entity<Vault>(entity =>
        {
            entity.ToTable("Vaults");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.VaultId).IsUnique();
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Path).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.IsActive).HasDefaultValue(false);
            entity.Property(e => e.IsPaid).HasDefaultValue(false);
            entity.Property(e => e.Tags).HasMaxLength(500).HasDefaultValue("");
            entity.Property(e => e.Industry).HasMaxLength(100).HasDefaultValue("");
            entity.Property(e => e.IsDeleted).HasDefaultValue(false);
            entity.Property(e => e.DeletedAt).IsRequired(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 已授权设备
        modelBuilder.Entity<AuthorizedDevice>(entity =>
        {
            entity.ToTable("AuthorizedDevices");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceId).IsUnique();
            entity.HasIndex(e => e.AccessToken).IsUnique();
            entity.HasIndex(e => e.Status);

            entity.Property(e => e.DeviceId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.AccessToken).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired().HasDefaultValue("Authorized");
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.AuthorizedTime).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 设备配额
        modelBuilder.Entity<DeviceQuota>(entity =>
        {
            entity.ToTable("DeviceQuotas");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceId).IsUnique();

            entity.Property(e => e.DeviceId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 设备每日同步记录
        modelBuilder.Entity<DeviceDailySync>(entity =>
        {
            entity.ToTable("DeviceDailySyncs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DeviceId, e.VaultId, e.SyncDate }).IsUnique();

            entity.Property(e => e.DeviceId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.SyncDate).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 华为 IAP 购买记录
        modelBuilder.Entity<IapPurchaseRecord>(entity =>
        {
            entity.ToTable("IapPurchaseRecords");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PurchaseToken).IsUnique();
            entity.HasIndex(e => e.DeviceId);

            entity.Property(e => e.DeviceId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ProductId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.PurchaseToken).HasMaxLength(500).IsRequired();
            entity.Property(e => e.QuotaType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 设备同步日志
        modelBuilder.Entity<DeviceSyncLog>(entity =>
        {
            entity.ToTable("DeviceSyncLogs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.SyncTime);

            entity.Property(e => e.DeviceId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DeviceName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.IpAddress).HasMaxLength(50);
            entity.Property(e => e.VaultId).HasMaxLength(50);
            entity.Property(e => e.SyncType).HasMaxLength(50).IsRequired().HasDefaultValue("manifest");
            entity.Property(e => e.SyncTime).HasDefaultValueSql("datetime('now')");
        });

        // 任务历史
        modelBuilder.Entity<TaskEntity>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TaskId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.TaskId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.TaskType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired().HasDefaultValue("Pending");
            entity.Property(e => e.Progress).HasDefaultValue(0);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 服务器地址配置
        modelBuilder.Entity<ServerAddressSetting>(entity =>
        {
            entity.ToTable("ServerAddressSettings");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Domain).HasMaxLength(500).IsRequired().HasDefaultValue("");
            entity.Property(e => e.Url).HasMaxLength(500).IsRequired().HasDefaultValue("");
            entity.Property(e => e.ServerInstanceId).HasMaxLength(100).IsRequired().HasDefaultValue("");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // OpenClaw 任务
        modelBuilder.Entity<OpenClawTask>(entity =>
        {
            entity.ToTable("OpenClawTasks");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TaskId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.TaskId).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Prompt).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired().HasDefaultValue("pending");
            entity.Property(e => e.ReportPath).HasMaxLength(1000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        // AI 调用性能指标
        modelBuilder.Entity<AiUsageMetric>(entity =>
        {
            entity.ToTable("AiUsageMetrics");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CalledAt);
            entity.HasIndex(e => e.ProviderId);
            entity.HasIndex(e => e.ModelId);
            entity.HasIndex(e => e.Operation);

            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ModelId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Operation).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.CalledAt).HasDefaultValueSql("datetime('now')");
        });

        // Benchmark 测试结果
        modelBuilder.Entity<BenchmarkSessionEntity>(entity =>
        {
            entity.ToTable("BenchmarkSessions");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.SessionId).IsUnique();
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.TestedAt);

            entity.Property(e => e.SessionId).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ModelName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ModelId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ResultsJson).IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.TestedAt).HasDefaultValueSql("datetime('now')");
        });

        // Onboarding 状态
        modelBuilder.Entity<OnboardingState>(entity =>
        {
            entity.ToTable("OnboardingStates");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.IsCompleted).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 初始化任务进度
        modelBuilder.Entity<InitTaskProgress>(entity =>
        {
            entity.ToTable("InitTaskProgresses");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TaskId).IsUnique();

            entity.Property(e => e.TaskId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.TaskType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.IsCompleted).HasDefaultValue(false);
            entity.Property(e => e.IsSkipped).HasDefaultValue(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // Embedding 配置
        modelBuilder.Entity<EmbeddingConfig>(entity =>
        {
            entity.ToTable("EmbeddingConfigs");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProviderId);

            entity.Property(e => e.ProviderId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(200).IsRequired();
            entity.Property(e => e.BaseUrl).HasMaxLength(500).IsRequired();
            entity.Property(e => e.EncryptedApiKey).HasMaxLength(2000);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 学习者档案
        modelBuilder.Entity<LearnerProfile>(entity =>
        {
            entity.ToTable("LearnerProfiles");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name);

            entity.Property(e => e.Name).HasMaxLength(50).IsRequired();
            entity.Property(e => e.AvatarEmoji).HasMaxLength(10);
            entity.Property(e => e.Color).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 成就解锁记录
        modelBuilder.Entity<Achievement>(entity =>
        {
            entity.ToTable("Achievements");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.LearnerId, e.Key }).IsUnique();

            entity.Property(e => e.Key).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Title).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Icon).HasMaxLength(20);
            entity.Property(e => e.Tier).HasMaxLength(20);
            entity.Property(e => e.Category).HasMaxLength(20);
            entity.Property(e => e.UnlockedAt).HasDefaultValueSql("datetime('now')");
        });

        // 学习活动记录
        modelBuilder.Entity<StudyActivity>(entity =>
        {
            entity.ToTable("StudyActivities");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.LearnerId);
            entity.HasIndex(e => new { e.LearnerId, e.VaultId, e.CreatedAt });

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ActivityType).HasMaxLength(30).IsRequired();
            entity.Property(e => e.CardId).HasMaxLength(100);
            entity.Property(e => e.Result).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 笔记向量缓存
        modelBuilder.Entity<NoteEmbedding>(entity =>
        {
            entity.ToTable("NoteEmbeddings");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.VaultId, e.NotePath }).IsUnique();

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.NotePath).HasMaxLength(500).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 卡片复习状态
        modelBuilder.Entity<CardReviewState>(entity =>
        {
            entity.ToTable("CardReviewStates");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.LearnerId, e.VaultId, e.CardId }).IsUnique();
            entity.HasIndex(e => new { e.LearnerId, e.VaultId, e.NextReviewDate });

            entity.Property(e => e.VaultId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.CardId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastResult).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

        // 对话记忆条目
        modelBuilder.Entity<ChatMemoryEntry>(entity =>
        {
            entity.ToTable("ChatMemoryEntries");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.SessionId, e.Round });

            entity.Property(e => e.SessionId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.UserSummary).IsRequired();
            entity.Property(e => e.AssistantSummary).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
        });
    }

    /// <summary>
    /// 保存更改时自动更新 UpdatedAt 字段
    /// </summary>
    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    /// <summary>
    /// 异步保存更改时自动更新 UpdatedAt 字段
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            if (entry.Entity is AiProviderSetting provider)
            {
                provider.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is Vault vault)
            {
                vault.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is AuthorizedDevice device)
            {
                device.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is TaskEntity task)
            {
                task.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is ServerAddressSetting setting)
            {
                setting.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is OpenClawTask task2)
            {
                // OpenClawTask 无 UpdatedAt
            }
            else if (entry.Entity is OnboardingState onboarding)
            {
                onboarding.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is InitTaskProgress initTask)
            {
                initTask.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is EmbeddingConfig embedding)
            {
                embedding.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is NoteEmbedding noteEmbedding)
            {
                noteEmbedding.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is CardReviewState reviewState)
            {
                reviewState.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is DeviceQuota deviceQuota)
            {
                deviceQuota.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is DeviceDailySync deviceDailySync)
            {
                deviceDailySync.UpdatedAt = DateTime.Now;
            }
        }
    }
}
