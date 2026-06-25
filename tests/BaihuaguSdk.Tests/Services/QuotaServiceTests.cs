using BaihuaguSdk.Services;
using BaihuaguSdk.Transport;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

public class QuotaServiceTests
{
    [Fact]
    public void HttpCodeMessage_AllCodes()
    {
        Assert.Equal("设备未授权，请先完成配对", HttpTransport.HttpCodeMessage(401));
        Assert.Equal("请求太频繁，请稍后再试", HttpTransport.HttpCodeMessage(429));
        Assert.Equal("服务器内部错误", HttpTransport.HttpCodeMessage(500));
    }

    [Fact]
    public void NormalizeBaseUrl_WithPath_HandlesCorrectly()
    {
        Assert.Equal("http://192.168.1.1:8788", HttpTransport.NormalizeBaseUrl("http://192.168.1.1:8788/"));
        Assert.Equal("http://192.168.1.1:8789", HttpTransport.NormalizeBaseUrl("http://192.168.1.1:8789"));
    }
}
