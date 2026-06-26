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
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
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
    public async Task GetQuotaAsync_WithValidBaseUrl_CreatesCorrectTransport()
    {
        var baseUrl = "http://192.168.1.1:8788";
        var deviceId = "test-device-001";
        
        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, baseUrl);

        try
        {
            await service.GetQuotaAsync(deviceId);
        }
        catch (HttpRequestException)
        {
        }

        _signerMock.Verify(s => s.SignRequest(
            "GET", It.Is<string>(u => u.Contains(baseUrl)),
            null, baseUrl), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetOrdersAsync_WithValidBaseUrl_CreatesCorrectTransport()
    {
        var baseUrl = "http://localhost:8788";
        var deviceId = "device-123";

        var service = new QuotaServiceImpl(_httpClient, _signerMock.Object, baseUrl);

        try
        {
            await service.GetOrdersAsync(deviceId);
        }
        catch (HttpRequestException)
        {
        }

        _signerMock.Verify(s => s.SignRequest(
            "GET", It.Is<string>(u => u.Contains(baseUrl)),
            null, baseUrl), Times.AtLeastOnce);
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
}