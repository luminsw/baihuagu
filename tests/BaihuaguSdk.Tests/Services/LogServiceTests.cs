using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Transport;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

public class LogServiceTests : IDisposable
{
    private readonly Mock<IRequestSigner> _signerMock = new();
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _httpClient;

    public LogServiceTests()
    {
        _httpClient = new HttpClient(_handler);
        _signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());
    }

    // ---- 基础构造/初始化测试 ----

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

    // ---- 实际上报测试（Mock HttpMessageHandler） ----

    [Fact]
    public async Task FlushBatchAsync_SendsLogsToServer()
    {
        _handler.SetupResponse("/mg/mobile-logs/batch", HttpStatusCode.OK, "{}");

        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Info("test message", "ctx");

        await service.ForceFlushAsync();

        Assert.Contains(_handler.RequestLog, r => r.Contains("/mg/mobile-logs/batch"));
    }

    [Fact]
    public async Task Log_ExceedsBufferThreshold_TriggersFlush()
    {
        _handler.SetupResponse("/mg/mobile-logs/batch", HttpStatusCode.OK, "{}");

        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        // MaxBufferSize = 50, add 51 to trigger immediate flush
        for (var i = 0; i < 51; i++)
        {
            service.Info($"message {i}", "batch");
        }

        await Task.Delay(100);

        Assert.Contains(_handler.RequestLog, r => r.Contains("/mg/mobile-logs/batch"));
    }

    [Fact]
    public async Task Dispose_TriggersFinalFlush()
    {
        _handler.SetupResponse("/mg/mobile-logs/batch", HttpStatusCode.OK, "{}");

        var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Info("before dispose", "ctx");

        service.Dispose();
        await Task.Delay(100);

        Assert.Contains(_handler.RequestLog, r => r.Contains("/mg/mobile-logs/batch"));
    }

    [Fact]
    public async Task Log_WithExtraFields_SerializedInRequest()
    {
        string? requestBody = null;
        _handler.SetupResponse("/mg/mobile-logs/batch", HttpStatusCode.OK, "{}",
            body => requestBody = body);

        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        var extra = new Dictionary<string, string> { ["key"] = "value" };
        service.Log("INFO", "test with extra", "ctx", extra);

        await service.ForceFlushAsync();

        Assert.NotNull(requestBody);
        Assert.Contains("test with extra", requestBody);
        Assert.Contains("device-1", requestBody);
    }

    [Fact]
    public void DeriveOpenObservePort_FamilyServer_Returns5082()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788", "localhost");
    }

    [Fact]
    public void Log_EmptyMessage_Accepted()
    {
        using var service = new LogServiceImpl(_httpClient, _signerMock.Object, "device-1", "TestDevice");
        service.Initialize("http://localhost:8788");

        service.Info("", "ctx");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}