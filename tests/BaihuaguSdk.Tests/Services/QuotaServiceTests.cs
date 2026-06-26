using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Transport;
using Moq;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

public class QuotaServiceTests
{
    private readonly Mock<IRequestSigner> _signerMock = new();
    private readonly HttpClient _httpClient;

    public QuotaServiceTests()
    {
        _httpClient = new HttpClient();
        _signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());
    }

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, "http://localhost:8788");
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_EmptyBaseUrl_Accepted()
    {
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, "");
        Assert.NotNull(service);
    }

    [Fact]
    public async Task GetQuotaAsync_NullDeviceId_Throws()
    {
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, "http://localhost:8788");

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetQuotaAsync(null!));
    }

    [Fact]
    public async Task GetOrdersAsync_NullDeviceId_Throws()
    {
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, "http://localhost:8788");

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetOrdersAsync(null!));
    }

    [Fact]
    public async Task SimulatePurchaseAsync_EmptyProductId_Throws()
    {
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, "http://localhost:8788");

        await Assert.ThrowsAsync<ArgumentException>(() => service.SimulatePurchaseAsync("device-1", ""));
    }

    [Fact]
    public async Task SimulatePurchaseAsync_NullDeviceId_Throws()
    {
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, "http://localhost:8788");

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SimulatePurchaseAsync(null!, "product-1"));
    }

    [Fact]
    public async Task SimulatePurchaseAsync_NullProductId_Throws()
    {
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, "http://localhost:8788");

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SimulatePurchaseAsync("device-1", null!));
    }
}