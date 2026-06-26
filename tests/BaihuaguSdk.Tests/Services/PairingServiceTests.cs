using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using Moq;
using System.Net;
using System.Text.Json;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

public class PairingServiceTests
{
    // ---- QR Code 解析测试 ----

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

    // ---- 设备注册测试（Mock HttpClient） ----

    [Fact]
    public async Task RegisterDeviceAsync_SuccessAuthorized_ReturnsSuccess()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        var response = new
        {
            success = true,
            data = new
            {
                authorized = true,
                sharedSecret = "secret-123",
                requestId = "req-456",
                deviceName = "TestDevice"
            }
        };
        handler.SetupResponse("/mg/onehop/register-device", HttpStatusCode.OK,
            JsonSerializer.Serialize(new { success = true, data = response.data }));

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.True(result.Success);
        Assert.True(result.Authorized);
        Assert.Equal("secret-123", result.SharedSecret);
        Assert.Equal("req-456", result.RequestId);
        Assert.Equal("TestDevice", result.DeviceName);
    }

    [Fact]
    public async Task RegisterDeviceAsync_SuccessUnauthorized_ReturnsUnauthorized()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/onehop/register-device", HttpStatusCode.OK,
            """{"success":true,"data":{"authorized":false,"requestId":"req-1"}}""");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.True(result.Success);
        Assert.False(result.Authorized);
        Assert.Equal("req-1", result.RequestId);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ServerError_ReturnsFailure()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/onehop/register-device", HttpStatusCode.InternalServerError, "Server error");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RegisterDeviceAsync_NotFound_ReturnsFailure()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/onehop/register-device", HttpStatusCode.NotFound, "Not found");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RegisterDeviceAsync_MalformedJson_ReturnsFailure()
    {
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var signerMock = new Mock<IRequestSigner>();
        signerMock.Setup(s => s.SignRequest(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>())).Returns(new Dictionary<string, string>());

        handler.SetupResponse("/mg/onehop/register-device", HttpStatusCode.OK, "not valid json");

        var service = new PairingServiceImpl(client, signerMock.Object, "device-1", "TestDevice");

        var result = await service.RegisterDeviceAsync("http://localhost:8788");

        Assert.False(result.Success);
    }

    [Fact]
    public void ParseQrCode_EmptyString_ReturnsNull()
    {
        Assert.Null(PairingServiceImpl.ParseQrCode(""));
    }

    [Fact]
    public void ParseQrCode_Null_ReturnsNull()
    {
        Assert.Null(PairingServiceImpl.ParseQrCode(null!));
    }
}
