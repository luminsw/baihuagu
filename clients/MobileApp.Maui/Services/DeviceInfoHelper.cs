namespace MobileApp.Maui.Services;

/// <summary>
/// 设备信息辅助类。跨平台获取设备 ID 和名称。
/// ID 在首次初始化时从安全存储读取或生成，后续从内存缓存返回。
/// </summary>
public static class DeviceInfoHelper
{
    private const string DeviceIdKey = "persistent_device_id";
    private static string? _cachedDeviceId;

    /// <summary>
    /// 异步初始化设备 ID。应在 App 启动早期调用一次。
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (_cachedDeviceId != null)
            return;

        var stored = await SecureStorage.Default.GetAsync(DeviceIdKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(stored))
        {
            _cachedDeviceId = stored;
            return;
        }

        var newId = Guid.NewGuid().ToString("N")[..16];
        await SecureStorage.Default.SetAsync(DeviceIdKey, newId).ConfigureAwait(false);
        _cachedDeviceId = newId;
    }

    /// <summary>
    /// 获取设备 ID。调用前必须先执行 <see cref="InitializeAsync"/>。
    /// </summary>
    public static string GetDeviceId()
    {
        if (_cachedDeviceId == null)
            throw new InvalidOperationException("DeviceInfoHelper 尚未初始化，请先调用 InitializeAsync()。");
        return _cachedDeviceId;
    }

    public static string GetDeviceName()
    {
#if ANDROID
        return Android.OS.Build.Model ?? "Android Device";
#elif IOS
        return UIKit.UIDevice.CurrentDevice.Name ?? "iPhone";
#else
        return "Unknown Device";
#endif
    }
}
