using BaihuaguSdk.Push;
using BaihuaguSdk.Services;
using MobileContract.Services;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

/// <summary>
/// 验证 SDK 实现类正确实现了 MobileContract 中定义的移动端接口。
/// </summary>
public class ContractAlignmentTests
{
    [Fact]
    public void LogServiceImpl_Implements_IRemoteLogService()
    {
        Assert.True(typeof(IRemoteLogService).IsAssignableFrom(typeof(LogServiceImpl)));
    }

    [Fact]
    public void PairingServiceImpl_Implements_IPairingService()
    {
        Assert.True(typeof(IPairingService).IsAssignableFrom(typeof(PairingServiceImpl)));
    }

    [Fact]
    public void PairingServiceImpl_Implements_IDeviceRegistrationService()
    {
        Assert.True(typeof(IDeviceRegistrationService).IsAssignableFrom(typeof(PairingServiceImpl)));
    }

    [Fact]
    public void SyncServiceImpl_Implements_ISyncService()
    {
        Assert.True(typeof(ISyncService).IsAssignableFrom(typeof(SyncServiceImpl)));
    }

    [Fact]
    public void PushPollingServiceImpl_Implements_IPushPollingService()
    {
        Assert.True(typeof(IPushPollingService).IsAssignableFrom(typeof(PushPollingServiceImpl)));
    }
}
