using BaihuaguSdk.Transport;
using Xunit;

namespace BaihuaguSdk.Tests.Transport;

public class HttpTransportTests
{
    [Fact]
    public void NormalizeBaseUrl_IpOnly_AddsHttpAndPort()
    {
        Assert.Equal("http://192.168.1.1:8788", HttpTransport.NormalizeBaseUrl("192.168.1.1"));
    }

    [Fact]
    public void NormalizeBaseUrl_HttpUrl_KeepsScheme()
    {
        Assert.Equal("http://192.168.1.1:8788", HttpTransport.NormalizeBaseUrl("http://192.168.1.1:8788"));
    }

    [Fact]
    public void NormalizeBaseUrl_HttpsUrl_KeepsScheme()
    {
        Assert.Equal("https://example.com", HttpTransport.NormalizeBaseUrl("https://example.com"));
    }

    [Fact]
    public void NormalizeBaseUrl_TrailingSlash_Removed()
    {
        Assert.Equal("http://example.com:8788", HttpTransport.NormalizeBaseUrl("http://example.com:8788/"));
    }

    [Fact]
    public void NormalizeBaseUrl_Empty_ReturnsEmpty()
    {
        Assert.Equal("", HttpTransport.NormalizeBaseUrl(""));
    }

    [Fact]
    public void ExtractServerError_ValidJson_ReturnsError()
    {
        var result = HttpTransport.ExtractServerError("{\"error\":\"something went wrong\"}");
        Assert.Equal("something went wrong", result);
    }

    [Fact]
    public void ExtractServerError_NoErrorField_ReturnsNull()
    {
        Assert.Null(HttpTransport.ExtractServerError("{\"ok\":true}"));
    }

    [Fact]
    public void ExtractServerError_EmptyBody_ReturnsNull()
    {
        Assert.Null(HttpTransport.ExtractServerError(""));
        Assert.Null(HttpTransport.ExtractServerError(null));
    }

    [Fact]
    public void ExtractServerError_InvalidJson_ReturnsNull()
    {
        Assert.Null(HttpTransport.ExtractServerError("not json"));
    }

    [Fact]
    public void HttpCodeMessage_CommonCodes()
    {
        Assert.Equal("请求参数错误", HttpTransport.HttpCodeMessage(400));
        Assert.Equal("设备未授权，请先完成配对", HttpTransport.HttpCodeMessage(401));
        Assert.Equal("没有权限访问", HttpTransport.HttpCodeMessage(403));
        Assert.Equal("请求的资源不存在", HttpTransport.HttpCodeMessage(404));
        Assert.Equal("请求太频繁，请稍后再试", HttpTransport.HttpCodeMessage(429));
        Assert.Equal("服务器内部错误", HttpTransport.HttpCodeMessage(500));
        Assert.Equal("服务暂时不可用", HttpTransport.HttpCodeMessage(502));
        Assert.Equal("服务暂时不可用", HttpTransport.HttpCodeMessage(503));
    }

    [Fact]
    public void HttpCodeMessage_UnknownCode_ReturnsGeneric()
    {
        Assert.Equal("服务器错误（418）", HttpTransport.HttpCodeMessage(418));
    }

    [Fact]
    public void ApiResponse_Ok()
    {
        var resp = ApiResponse<string>.Ok("data", 200);
        Assert.True(resp.IsSuccess);
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("data", resp.Data);
        Assert.Null(resp.ErrorMessage);
    }

    [Fact]
    public void ApiResponse_Fail()
    {
        var resp = ApiResponse<string>.Fail(404, "not found", "raw");
        Assert.False(resp.IsSuccess);
        Assert.Equal(404, resp.StatusCode);
        Assert.Equal("not found", resp.ErrorMessage);
        Assert.Equal("raw", resp.RawBody);
    }
}
