using Microsoft.EntityFrameworkCore;
using TaskRunner.Data.Entities;

namespace TaskRunner.Data;

/// <summary>
/// SQLite 数据库上下文 - 使用 Entity Framework Core
/// </summary>
public class AppDbContext : DbContext
{
    private string? _dbPath;

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// 无参构造函数（用于设计时工具，如迁移）
    /// </summary>
    public AppDbContext()
    {
        _dbPath = GetDefaultDbPath();
    }

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
    /// 服务器地址配置（用于移动端连接）
    /// </summary>
    public DbSet<ServerAddressSetting> ServerAddressSettings => Set<ServerAddressSetting>();

    /// <summary>
    /// 移动端日志
    /// </summary>
    public DbSet<MobileLogRecord> MobileLogs => Set<MobileLogRecord>();

    /// <summary>
    /// 获取数据库文件路径
    /// </summary>
    public string DatabasePath
    {
        get
        {
            if (_dbPath != null)
                return _dbPath;
            try
            {
                _dbPath = Database.GetDbConnection().ConnectionString;
                return _dbPath;
            }
            catch (InvalidOperationException)
            {
                return "InMemory";
            }
        }
    }

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
        var envDir = Environment.GetEnvironmentVariable("YJ_DATA_DIR");
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
            if (entry.Entity is Vault vault)
            {
                vault.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is AuthorizedDevice device)
            {
                device.UpdatedAt = DateTime.Now;
            }
            else if (entry.Entity is ServerAddressSetting setting)
            {
                setting.UpdatedAt = DateTime.Now;
            }
        }
    }
}
