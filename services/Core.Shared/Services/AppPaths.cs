namespace TaskRunner.Services;

/// <summary>
/// 应用路径工具类：提供配置目录等静态路径计算
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// 获取配置文件目录，优先使用 TASKRUNNER_DATA_DIR 环境变量，避免发布时丢失
    /// </summary>
    public static string GetConfigDirectory()
    {
        var dataDir = Environment.GetEnvironmentVariable("TASKRUNNER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(dataDir))
        {
            Directory.CreateDirectory(dataDir);
            return dataDir;
        }
        return AppDomain.CurrentDomain.BaseDirectory;
    }
}
