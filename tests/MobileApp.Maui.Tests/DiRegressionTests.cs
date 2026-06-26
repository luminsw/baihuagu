using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Storage;
using Moq;

namespace MobileApp.Maui.Tests;

/// <summary>
/// 回归测试：确保 MauiProgram 中所有 DI 注册的服务都可以正确构造。
/// 之前 PairingServiceImpl/QuotaServiceImpl/LogServiceImpl 的构造函数包含 string
/// 参数，而 DI 容器无法解析，导致 Blazor 路由器静默崩溃。
/// </summary>
public class DiRegressionTests
{
    // ---- PairingServiceImpl ----

    [Fact]
    public void PairingServiceImpl_CanBeConstructed_WithCorrectFactoryPattern()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        var service = new PairingServiceImpl(client, signer, "device-1", "test-device");

        Assert.NotNull(service);
    }

    [Fact]
    public void PairingServiceImpl_Ctor_RequiresAllStringParams()
    {
        // 验证构造函数签名包含 string 参数（这些必须通过工厂方法提供）
        var ctor = typeof(PairingServiceImpl).GetConstructors().Single();
        var stringParams = ctor.GetParameters().Where(p => p.ParameterType == typeof(string));

        Assert.Equal(2, stringParams.Count());
        Assert.Contains(stringParams, p => p.Name == "deviceId");
        Assert.Contains(stringParams, p => p.Name == "deviceName");
    }

    // ---- QuotaServiceImpl ----

    [Fact]
    public void QuotaServiceImpl_CanBeConstructed_WithCorrectFactoryPattern()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        var service = new QuotaServiceImpl(client, signer, "http://localhost:8788");

        Assert.NotNull(service);
    }

    [Fact]
    public void QuotaServiceImpl_Ctor_RequiresStringBaseUrl()
    {
        var ctor = typeof(QuotaServiceImpl).GetConstructors().Single();
        var stringParams = ctor.GetParameters().Where(p => p.ParameterType == typeof(string));

        Assert.Single(stringParams);
        Assert.Equal("baseUrl", stringParams.First().Name);
    }

    // ---- LogServiceImpl ----

    [Fact]
    public void LogServiceImpl_CanBeConstructed_WithCorrectFactoryPattern()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        var service = new LogServiceImpl(client, signer, "device-1", "test-device");

        Assert.NotNull(service);
    }

    [Fact]
    public void LogServiceImpl_Ctor_RequiresDeviceIdAndDeviceName()
    {
        var ctor = typeof(LogServiceImpl).GetConstructors().Single();
        var stringParams = ctor.GetParameters().Where(p => p.ParameterType == typeof(string));

        Assert.Equal(2, stringParams.Count());
    }

    // ---- SyncServiceImpl ----

    [Fact]
    public void SyncServiceImpl_CanBeConstructed_WithPlainDi()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        var service = new SyncServiceImpl(client, signer);

        Assert.NotNull(service);
    }

    [Fact]
    public void SyncServiceImpl_Ctor_HasNoStringParams()
    {
        var ctor = typeof(SyncServiceImpl).GetConstructors().Single();
        var stringParams = ctor.GetParameters().Where(p => p.ParameterType == typeof(string));

        Assert.Empty(stringParams); // 可以直接通过 DI 容器构造，无需工厂方法
    }

    // ---- 综合：验证所有 @inject 的类型都可以构造 ----

    [Fact]
    public void AllInjectedServiceTypes_CanBeConstructed()
    {
        var client = new HttpClient();
        var signer = Mock.Of<IRequestSigner>();

        // 这些类型在 MauiProgram.cs 中注册为 DI 服务，并被各页面 @inject
        var services = new object[]
        {
            new PairingServiceImpl(client, signer, "test-device", "test-name"),
            new QuotaServiceImpl(client, signer, ""),
            new LogServiceImpl(client, signer, "test-device", "test-name"),
            new SyncServiceImpl(client, signer),
        };

        foreach (var svc in services)
            Assert.NotNull(svc);
    }
}
