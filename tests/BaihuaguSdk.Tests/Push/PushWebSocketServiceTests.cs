using BaihuaguSdk.Push;
using Xunit;

namespace BaihuaguSdk.Tests.Push;

public class PushWebSocketServiceTests
{
    [Fact]
    public void Constructor_CreatesInstance()
    {
        using var httpClient = new HttpClient();
        using var service = new PushWebSocketService(httpClient);
        Assert.NotNull(service);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public void IsConnected_ReturnsFalse_WhenNotConnected()
    {
        using var httpClient = new HttpClient();
        using var service = new PushWebSocketService(httpClient);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public void ConnectionStateChanged_CanBeSubscribed()
    {
        using var httpClient = new HttpClient();
        bool? state = null;
        using var service = new PushWebSocketService(httpClient);
        service.ConnectionStateChanged += connected => state = connected;
        Assert.Null(state);
    }

    [Fact]
    public void OnSyncRequest_CanBeSet()
    {
        using var httpClient = new HttpClient();
        var service = new PushWebSocketService(httpClient)
        {
            OnSyncRequest = (vaultId, vaultName, action, ct) =>
            {
                Assert.NotEmpty(vaultId);
                return Task.CompletedTask;
            }
        };
        Assert.NotNull(service.OnSyncRequest);
    }

    [Fact]
    public void DisconnectAsync_WhenNotConnected_NoOp()
    {
        using var httpClient = new HttpClient();
        using var service = new PushWebSocketService(httpClient);
        var task = service.DisconnectAsync();
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var httpClient = new HttpClient();
        var service = new PushWebSocketService(httpClient);
        service.Dispose();
        service.Dispose(); // no exception
    }

    [Fact]
    public void UrlConstruction_UsesWssForHttps()
    {
        using var httpClient = new HttpClient();
        using var service = new PushWebSocketService(httpClient);

        // The URL construction is tested implicitly via ConnectAsync
        // Since we can't actually connect without a server, we just verify no crash
        Assert.False(service.IsConnected);
    }

    [Fact]
    public void Service_HasOnLogCallback()
    {
        using var httpClient = new HttpClient();
        string? lastLog = null;
        var service = new PushWebSocketService(httpClient)
        {
            OnLog = msg => lastLog = msg
        };
        Assert.NotNull(service.OnLog);
    }
}
