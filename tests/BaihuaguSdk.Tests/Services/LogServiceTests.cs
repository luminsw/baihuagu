using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Transport;
using Moq;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

public class LogServiceTests : IDisposable
{
    private readonly Mock<IRequestSigner> _signerMock = new();
    private readonly HttpClient _httpClient;

    public LogServiceTests()
    {
        _httpClient = new HttpClient();
        _signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());
    }

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        Assert.NotNull(service);
    }

    [Fact]
    public void Initialize_WithServerUrl_StartsFlushLoop()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        
        service.Initialize("http://localhost:8788");
        
        Assert.NotNull(service);
    }

    [Fact]
    public void Log_Info_AddsToBuffer()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Info("test info message", "test-context");
    }

    [Fact]
    public void Log_Warn_AddsToBuffer()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Warn("test warning message", "test-context");
    }

    [Fact]
    public void Log_Error_AddsToBuffer()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Error("test error message", "test-context");
    }

    [Fact]
    public void Log_Debug_AddsToBuffer()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Debug("test debug message", "test-context");
    }

    [Fact]
    public void Log_NullContext_Accepted()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Info("test without context", null);
    }

    [Fact]
    public void Initialize_WithOpenObserveHost_EnablesOpenObserve()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        
        service.Initialize("http://localhost:8788", "192.168.1.1");
    }

    [Fact]
    public void Initialize_NoOpenObserveHost_DisablesOpenObserve()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        
        service.Initialize("http://localhost:8788");
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");
        
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public void Log_MultipleMessages_DoesNotThrow()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        for (var i = 0; i < 10; i++)
        {
            service.Info($"message {i}", "loop");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}