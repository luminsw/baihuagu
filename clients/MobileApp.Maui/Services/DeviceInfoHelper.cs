namespace MobileApp.Maui.Services;

/// <summary>
/// 设备信息辅助类。跨平台获取设备 ID 和名称。
/// </summary>
public static class DeviceInfoHelper
{
    private const string DeviceIdKey = "persistent_device_id";

    public static string GetDeviceId()
    {
        // 优先从安全存储读取持久化 ID
        var stored = SecureStorage.Default.GetAsync(DeviceIdKey).Result;
        if (!string.IsNullOrEmpty(stored))
            return stored;

        // 首次启动：生成新 ID 并持久化
        var newId = Guid.NewGuid().ToString("N")[..16];
        SecureStorage.Default.SetAsync(DeviceIdKey, newId).Wait();
        return newId;
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
