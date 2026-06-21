using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using TaskRunner.Data;
using TaskRunner.Data.Entities;
using TaskRunner.Services;
using Xunit;

namespace TaskRunner.Family.Tests.Services;

public class DeviceQuotaServiceTests
{
    private static DbContextOptions<AppDbContext> CreateInMemoryOptions(string databaseName)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
    }

    private static IDbContextFactory<AppDbContext> CreateFactory(DbContextOptions<AppDbContext> options)
    {
        return new InMemoryDbContextFactory<AppDbContext>(options);
    }

    private class InMemoryDbContextFactory<TContext> : IDbContextFactory<TContext> where TContext : DbContext, new()
    {
        private readonly DbContextOptions<TContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<TContext> options) => _options = options;
        public TContext CreateDbContext() => (TContext)Activator.CreateInstance(typeof(TContext), _options)!;
        public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    [Fact]
    public void ProductCatalog_ContainsExpectedProducts()
    {
        Assert.Equal(4, DeviceQuotaService.ProductCatalog.Count);
        Assert.Contains("sync_5", DeviceQuotaService.ProductCatalog);
        Assert.Contains("sync_20", DeviceQuotaService.ProductCatalog);
        Assert.Contains("ai_build_5", DeviceQuotaService.ProductCatalog);
        Assert.Contains("ai_build_20", DeviceQuotaService.ProductCatalog);
    }

    [Fact]
    public void ProductCatalog_SyncProducts_HaveCorrectValues()
    {
        var sync5 = DeviceQuotaService.ProductCatalog["sync_5"];
        Assert.Equal("sync", sync5.quotaType);
        Assert.Equal(5, sync5.quotaAmount);
        Assert.Equal(1.00m, sync5.price);

        var sync20 = DeviceQuotaService.ProductCatalog["sync_20"];
        Assert.Equal("sync", sync20.quotaType);
        Assert.Equal(20, sync20.quotaAmount);
        Assert.Equal(3.00m, sync20.price);
    }

    [Fact]
    public void ProductCatalog_AiBuildProducts_HaveCorrectValues()
    {
        var aiBuild5 = DeviceQuotaService.ProductCatalog["ai_build_5"];
        Assert.Equal("ai_build", aiBuild5.quotaType);
        Assert.Equal(5, aiBuild5.quotaAmount);
        Assert.Equal(2.00m, aiBuild5.price);

        var aiBuild20 = DeviceQuotaService.ProductCatalog["ai_build_20"];
        Assert.Equal("ai_build", aiBuild20.quotaType);
        Assert.Equal(20, aiBuild20.quotaAmount);
        Assert.Equal(5.00m, aiBuild20.price);
    }

    [Fact]
    public void ParseProduct_ValidProductId_ReturnsInfo()
    {
        var result = DeviceQuotaService.ParseProduct("sync_5");
        Assert.NotNull(result);
        Assert.Equal("sync", result.Value.quotaType);
        Assert.Equal(5, result.Value.quotaAmount);
        Assert.Equal(1.00m, result.Value.price);
    }

    [Fact]
    public void ParseProduct_InvalidProductId_ReturnsNull()
    {
        var result = DeviceQuotaService.ParseProduct("invalid_product");
        Assert.Null(result);
    }

    [Fact]
    public void ParseProduct_NullProductId_ReturnsNull()
    {
        var result = DeviceQuotaService.ParseProduct(null);
        Assert.Null(result);
    }

    [Fact]
    public void GetOrCreateQuota_NewDevice_CreatesRecord()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var result = service.GetOrCreateQuota("test-device-1", "Test Device");

        Assert.NotNull(result);
        Assert.Equal("test-device-1", result.DeviceId);
        Assert.Equal("Test Device", result.DeviceName);
        Assert.Equal(0, result.PaidSyncQuota);
        Assert.Equal(0, result.AiBuildQuota);
        Assert.Equal(0, result.TotalSpent);
        Assert.NotNull(result.CreatedAt);
        Assert.NotNull(result.UpdatedAt);
    }

    [Fact]
    public void GetOrCreateQuota_ExistingDevice_ReturnsExisting()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        db.DeviceQuotas.Add(new DeviceQuota
        {
            DeviceId = "existing-device",
            DeviceName = "Existing",
            PaidSyncQuota = 10,
            AiBuildQuota = 5,
            TotalSpent = 10.00m,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        });
        db.SaveChanges();

        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var result = service.GetOrCreateQuota("existing-device", "Updated Name");

        Assert.Equal("existing-device", result.DeviceId);
        Assert.Equal("Existing", result.DeviceName);
        Assert.Equal(10, result.PaidSyncQuota);
        Assert.Equal(5, result.AiBuildQuota);
    }

    [Fact]
    public void GetQuota_ExistingDevice_ReturnsValues()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        db.DeviceQuotas.Add(new DeviceQuota
        {
            DeviceId = "test-device",
            PaidSyncQuota = 5,
            AiBuildQuota = 3,
            TotalSpent = 5.00m
        });
        db.SaveChanges();

        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (syncQuota, aiBuildQuota, totalSpent) = service.GetQuota("test-device");

        Assert.Equal(5, syncQuota);
        Assert.Equal(3, aiBuildQuota);
        Assert.Equal(5.00m, totalSpent);
    }

    [Fact]
    public void GetQuota_NonExistingDevice_ReturnsZero()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (syncQuota, aiBuildQuota, totalSpent) = service.GetQuota("non-existing");

        Assert.Equal(0, syncQuota);
        Assert.Equal(0, aiBuildQuota);
        Assert.Equal(0, totalSpent);
    }

    [Fact]
    public void CheckAndDeductSyncQuota_FreeVault_AllowsSync()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (allowed, error) = service.CheckAndDeductSyncQuota("device1", "vault1", isPaidVault: false);

        Assert.True(allowed);
        Assert.Null(error);
    }

    [Fact]
    public void CheckAndDeductSyncQuota_PaidVault_WithQuota_AllowsSync()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        var factory = CreateFactory(options);
        
        using (var db = factory.CreateDbContext())
        {
            db.DeviceQuotas.Add(new DeviceQuota
            {
                DeviceId = "device1",
                PaidSyncQuota = 5
            });
            db.SaveChanges();
        }

        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (allowed, error) = service.CheckAndDeductSyncQuota("device1", "vault1", isPaidVault: true);

        Assert.True(allowed);
        Assert.Null(error);
        
        using (var db = factory.CreateDbContext())
        {
            var updatedQuota = db.DeviceQuotas.First(q => q.DeviceId == "device1");
            Assert.Equal(4, updatedQuota.PaidSyncQuota);
        }
    }

    [Fact]
    public void CheckAndDeductSyncQuota_PaidVault_NoQuota_RefusesSync()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (allowed, error) = service.CheckAndDeductSyncQuota("device1", "vault1", isPaidVault: true);

        Assert.False(allowed);
        Assert.NotNull(error);
    }

    [Fact]
    public void CheckAndDeductSyncQuota_DailyLimit_Exceeded_RefusesSync()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        db.DeviceDailySyncs.Add(new DeviceDailySync
        {
            DeviceId = "device1",
            VaultId = "vault1",
            SyncDate = DateTime.UtcNow.Date,
            SyncCount = 1
        });
        db.SaveChanges();

        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (allowed, error) = service.CheckAndDeductSyncQuota("device1", "vault1", isPaidVault: false);

        Assert.False(allowed);
        Assert.Contains("今日已同步", error);
    }

    [Fact]
    public void CheckAndDeductAiBuildQuota_WithQuota_AllowsBuild()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        var factory = CreateFactory(options);
        
        using (var db = factory.CreateDbContext())
        {
            db.DeviceQuotas.Add(new DeviceQuota
            {
                DeviceId = "device1",
                AiBuildQuota = 5
            });
            db.SaveChanges();
        }

        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (allowed, error) = service.CheckAndDeductAiBuildQuota("device1");

        Assert.True(allowed);
        Assert.Null(error);
        
        using (var db = factory.CreateDbContext())
        {
            var updatedQuota = db.DeviceQuotas.First(q => q.DeviceId == "device1");
            Assert.Equal(4, updatedQuota.AiBuildQuota);
        }
    }

    [Fact]
    public void CheckAndDeductAiBuildQuota_NoQuota_RefusesBuild()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        var (allowed, error) = service.CheckAndDeductAiBuildQuota("device1");

        Assert.False(allowed);
        Assert.Contains("AI构建配额不足", error);
    }

    [Fact]
    public void AddQuota_SyncType_IncreasesSyncQuota()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        service.AddQuota("device1", "sync", 10, 5.00m, "Test Device");

        var quota = db.DeviceQuotas.First(q => q.DeviceId == "device1");
        Assert.Equal(10, quota.PaidSyncQuota);
        Assert.Equal(0, quota.AiBuildQuota);
        Assert.Equal(5.00m, quota.TotalSpent);
    }

    [Fact]
    public void AddQuota_AiBuildType_IncreasesAiBuildQuota()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        using var db = new AppDbContext(options);
        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var factory = CreateFactory(options);
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        service.AddQuota("device1", "ai_build", 5, 2.00m);

        var quota = db.DeviceQuotas.First(q => q.DeviceId == "device1");
        Assert.Equal(0, quota.PaidSyncQuota);
        Assert.Equal(5, quota.AiBuildQuota);
        Assert.Equal(2.00m, quota.TotalSpent);
    }

    [Fact]
    public void AddQuota_ExistingDevice_UpdatesQuota()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        var factory = CreateFactory(options);
        
        using (var db = factory.CreateDbContext())
        {
            db.DeviceQuotas.Add(new DeviceQuota
            {
                DeviceId = "device1",
                PaidSyncQuota = 5,
                AiBuildQuota = 3,
                TotalSpent = 3.00m
            });
            db.SaveChanges();
        }

        var mockLogger = new Mock<ILogger<DeviceQuotaService>>();
        var service = new DeviceQuotaService(factory, mockLogger.Object);

        service.AddQuota("device1", "sync", 10, 5.00m);

        using (var db = factory.CreateDbContext())
        {
            var quota = db.DeviceQuotas.First(q => q.DeviceId == "device1");
            Assert.Equal(15, quota.PaidSyncQuota);
            Assert.Equal(3, quota.AiBuildQuota);
            Assert.Equal(8.00m, quota.TotalSpent);
        }
    }
}