using System.Text.RegularExpressions;

namespace TaskRunner.Services;

/// <summary>
/// 知识库名称解析服务
/// 统一管理知识库名称的安全化、目录推断和显示名称回退
/// </summary>
public class VaultNameResolver
{
    /// <summary>
    /// 将任意字符串转换为安全的目录名（去除文件系统非法字符）
    /// </summary>
    public static string ToSafeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "未命名";

        // 去除 Windows/Linux 文件系统非法字符
        var safe = Regex.Replace(name.Trim(), @"[<>:""/\\|?*\x00-\x1f]", "_");
        // 去除首尾空格和点
        safe = safe.Trim('.', ' ');
        // 限制长度
        if (safe.Length > 100)
            safe = safe[..100];
        // 回退
        if (string.IsNullOrWhiteSpace(safe))
            safe = "未命名";

        return safe;
    }

    /// <summary>
    /// 获取唯一的目录路径。如果目标目录已存在，尝试追加序号
    /// </summary>
    public static string GetUniqueDirectoryPath(string parentDir, string name)
    {
        var safeName = ToSafeDirectoryName(name);
        var targetPath = Path.Combine(parentDir, safeName);

        if (!Directory.Exists(targetPath))
            return targetPath;

        // 目录已存在，尝试追加序号
        for (int i = 2; i <= 999; i++)
        {
            var candidate = Path.Combine(parentDir, $"{safeName}_{i}");
            if (!Directory.Exists(candidate))
                return candidate;
        }

        // 极端情况：使用时间戳
        return Path.Combine(parentDir, $"{safeName}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
    }

    /// <summary>
    /// 从目录路径推断知识库名称
    /// 对于 mobile/{行业}/{名称}/ 结构，取最深层目录名
    /// 对于 local/{行业}/{名称}/ 结构，取最深层目录名
    /// </summary>
    public static string InferNameFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "未命名";

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "未命名" : name;
    }

    /// <summary>
    /// 从目录路径推断行业名称
    /// 对于 mobile/{行业}/{名称}/ 结构，取中间层目录名
    /// </summary>
    public static string InferIndustryFromPath(string path, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
            return "移动端生成";

        try
        {
            var relative = Path.GetRelativePath(rootPath.TrimEnd(Path.DirectorySeparatorChar), path.TrimEnd(Path.DirectorySeparatorChar));
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .ToList();

            // mobile/{行业}/{名称} → 行业是第二级
            if (parts.Count >= 3 && parts[0].Equals("mobile", StringComparison.OrdinalIgnoreCase))
                return parts[1];

            // local/{行业}/{名称} → 行业是第二级
            if (parts.Count >= 3 && parts[0].Equals("local", StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VaultNameResolver] 从路径推断行业失败: {ex.Message}, path={path}, rootPath={rootPath}");
        }

        return "移动端生成";
    }

    /// <summary>
    /// 获取知识库的显示名称
    /// 优先使用数据库中的名称，其次从路径推断，最后回退默认值
    /// </summary>
    public static string GetDisplayName(string? dbName, string vaultId, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(dbName))
            return dbName.Trim();

        if (!string.IsNullOrWhiteSpace(fallback))
            return fallback.Trim();

        return $"知识库-{vaultId[..8]}";
    }
}
