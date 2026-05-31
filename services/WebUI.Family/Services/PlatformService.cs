namespace WebUI.Services;

/// <summary>
/// 平台信息服务 - 获取服务器操作系统信息
/// </summary>
public class PlatformService
{
    private readonly IApiService _apiService;
    private string? _cachedPlatform;
    private DateTime _lastFetchTime = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);

    public PlatformService(IApiService apiService)
    {
        _apiService = apiService;
    }

    /// <summary>
    /// 获取服务器操作系统平台
    /// </summary>
    public async Task<string> GetPlatformAsync()
    {
        if (_cachedPlatform != null && DateTime.Now - _lastFetchTime < _cacheDuration)
        {
            return _cachedPlatform;
        }

        try
        {
            var result = await _apiService.GetPlatformAsync();
            _cachedPlatform = result?.OsName ?? "Unknown";
            _lastFetchTime = DateTime.Now;
            return _cachedPlatform;
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// 获取路径输入框的占位符示例
    /// </summary>
    public async Task<string> GetPathPlaceholderAsync()
    {
        // 使用原始结果来判断操作系统类型
        var result = await _apiService.GetPlatformAsync();
        
        if (result == null)
        {
            return @"例如：/home/username/Vaults/Headache 或 D:\Vaults\Headache";
        }
        
        if (result.IsWindows)
        {
            return @"例如：D:\Vaults\Headache";
        }
        if (result.IsMacOS)
        {
            return "例如：/Users/username/Vaults/Headache";
        }
        // Linux 或其他
        return "例如：/home/username/Vaults/Headache";
    }
}
