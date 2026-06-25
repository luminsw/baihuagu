using BaihuaguSdk.Services;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

public class PairingServiceTests
{
    [Fact]
    public void ParseQrCode_NewFormatBaseUrl_ReturnsContent()
    {
        var json = """{"serverId":"abc","baseUrl":"http://192.168.1.1:8788","hostName":"百花谷"}""";
        var result = PairingServiceImpl.ParseQrCode(json);
        Assert.NotNull(result);
        Assert.Equal("abc", result!.ServerId);
        Assert.Equal("http://192.168.1.1:8788", result.BaseUrl);
        Assert.Equal("百花谷", result.HostName);
    }

    [Fact]
    public void ParseQrCode_OldFormatServerUrl_ReturnsContent()
    {
        var json = """{"serverUrl":"http://old.example.com:8788","hostName":"老版本服务器"}""";
        var result = PairingServiceImpl.ParseQrCode(json);
        Assert.NotNull(result);
        Assert.Equal("http://old.example.com:8788", result!.ServerUrl);
    }

    [Fact]
    public void ParseQrCode_DualAddress_ReturnsContent()
    {
        var json = """{"serverId":"s1","httpUrl":"http://192.168.1.1:8788","httpsUrl":"https://example.com","hostName":"双栈服务器"}""";
        var result = PairingServiceImpl.ParseQrCode(json);
        Assert.NotNull(result);
        Assert.Equal("http://192.168.1.1:8788", result!.HttpUrl);
        Assert.Equal("https://example.com", result.HttpsUrl);
    }

    [Fact]
    public void ParseQrCode_NoHostName_ReturnsNull()
    {
        var json = """{"serverId":"abc","baseUrl":"http://example.com"}""";
        Assert.Null(PairingServiceImpl.ParseQrCode(json));
    }

    [Fact]
    public void ParseQrCode_NoUrl_ReturnsNull()
    {
        var json = """{"hostName":"only name"}""";
        Assert.Null(PairingServiceImpl.ParseQrCode(json));
    }

    [Fact]
    public void ParseQrCode_InvalidJson_ReturnsNull()
    {
        Assert.Null(PairingServiceImpl.ParseQrCode("not json"));
    }

    [Fact]
    public void GetServerAddresses_BaseUrl_Preferred()
    {
        var content = new QrCodeContent
        {
            ServerId = "svr-1",
            BaseUrl = "http://192.168.1.1:8788",
            HttpUrl = "http://old", HttpsUrl = "https://old",
            HostName = "测试服务器", DeviceId = "dev-1"
        };
        var addrs = PairingServiceImpl.GetServerAddresses(content);
        Assert.Equal("svr-1", addrs.ServerId);
        Assert.Equal("http://192.168.1.1:8788", addrs.HttpUrl);
        Assert.Equal("http://192.168.1.1:8788", addrs.HttpsUrl);
    }

    [Fact]
    public void GetServerAddresses_FallsBackToDeviceId()
    {
        var content = new QrCodeContent
        {
            BaseUrl = "http://example.com",
            HostName = "test",
            DeviceId = "fallback-device-id"
        };
        var addrs = PairingServiceImpl.GetServerAddresses(content);
        Assert.Equal("fallback-device-id", addrs.ServerId);
    }

    [Fact]
    public void GetServerAddresses_NoId_GeneratesTimestamp()
    {
        var content = new QrCodeContent
        {
            BaseUrl = "http://example.com",
            HostName = "test"
        };
        var addrs = PairingServiceImpl.GetServerAddresses(content);
        Assert.StartsWith("server-", addrs.ServerId);
    }
}
