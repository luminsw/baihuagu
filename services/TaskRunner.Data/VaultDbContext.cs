using Microsoft.EntityFrameworkCore;
using TaskRunner.Data.Entities;

namespace TaskRunner.Data;

public class VaultDbContext : DbContext
{
    private string? _dbPath;

    public VaultDbContext(DbContextOptions<VaultDbContext> options) : base(options)
    {
    }

    public VaultDbContext()
    {
        _dbPath = GetDefaultDbPath();
    }

    public DbSet<Vault> Vaults => Set<Vault>();
    public DbSet<NoteEmbedding> NoteEmbeddings => Set<NoteEmbedding>();

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
        var dataDir = ResolveDataDir();
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, "vault.db");
    }

    internal static string ResolveDataDir()
    {
        var envDir = Environment.GetEnvironmentVariable("YJ_DATA_DIR");
        if (!string.IsNullOrEmpty(envDir))
            return envDir;

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var binDebug = Path.Combine("bin", "Debug");
        var binRelease = Path.Combine("bin", "Release");
        if (baseDir.Contains(binDebug) || baseDir.Contains(binRelease))
        {
            var index = baseDir.IndexOf(binDebug);
            if (index < 0) index = baseDir.IndexOf(binRelease);
            if (index > 0)
            {
                var projectDir = baseDir.Substring(0, index);
                var servicesDir = Path.GetDirectoryName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (servicesDir != null && Path.GetFileName(servicesDir) == "services")
                    return Path.Combine(servicesDir, "data");
                return Path.Combine(projectDir, "data");
            }
            return Path.Combine(baseDir, "data");
        }

        return Path.Combine(baseDir, "data");
    }

    public static string GetDbPath()
    {
        return GetDefaultDbPath();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
            entity.Property(e => e.Tags).HasMaxLength(500).HasDefaultValue("");
            entity.Property(e => e.Industry).HasMaxLength(100).HasDefaultValue("");
            entity.Property(e => e.IsDeleted).HasDefaultValue(false);
            entity.Property(e => e.DeletedAt).IsRequired(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");
        });

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
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

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
            else if (entry.Entity is NoteEmbedding noteEmbedding)
            {
                noteEmbedding.UpdatedAt = DateTime.Now;
            }
        }
    }
}