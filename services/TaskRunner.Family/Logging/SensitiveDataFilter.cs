using System.Text.RegularExpressions;

namespace TaskRunner.Logging;

/// <summary>
/// 敏感数据过滤器 - 用于脱敏日志中的敏感信息
/// </summary>
public static class SensitiveDataFilter
{
    // 敏感字段名（不区分大小写）
    private static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "api-key",
        "password", "pwd",
        "secret", "client_secret",
        "token", "access_token", "refresh_token",
        "authorization",
        "cookie",
        "credential",
        "private_key", "privatekey"
    };

    // 敏感数据模式（正则表达式）
    private static readonly List<RegexPattern> SensitivePatterns = new()
    {
        // API Key 格式（如 sk-xxx、ak-xxx）
        new RegexPattern(@"\b(sk-|ak-)[a-zA-Z0-9]{8,}\b", "***"),

        // Bearer Token
        new RegexPattern(@"Bearer\s+[a-zA-Z0-9_\-\.]{10,}", "Bearer ***"),

        // Basic Auth
        new RegexPattern(@"Basic\s+[a-zA-Z0-9+/]{10,}=?=", "Basic ***"),

        // 密码字段（JSON 格式）- 匹配 "password": "xxx"
        new RegexPattern("\"(password|pwd|secret|apikey|api_key)\\s*:\\s*\"[^\"]*\"", "\"$1\": \"***\""),

        // 密码字段（URL 查询参数）- 匹配 ?password=xxx
        new RegexPattern(@"([?&](password|pwd|secret|apikey|api_key|token)=)[^&]*", "$1***")
    };

    private class RegexPattern
    {
        public Regex Pattern { get; }
        public string Replacement { get; }

        public RegexPattern(string pattern, string replacement)
        {
            Pattern = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Replacement = replacement;
        }
    }

    /// <summary>
    /// 脱敏字符串中的敏感信息
    /// </summary>
    public static string Filter(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? "";

        var result = input;

        // 应用所有敏感模式
        foreach (var patternInfo in SensitivePatterns)
        {
            try
            {
                result = patternInfo.Pattern.Replace(result, patternInfo.Replacement);
            }
            catch
            {
                // 正则替换失败时继续
            }
        }

        return result;
    }

    /// <summary>
    /// 检查字段名是否为敏感字段
    /// </summary>
    public static bool IsSensitiveField(string fieldName)
    {
        return SensitiveFieldNames.Contains(fieldName);
    }

    /// <summary>
    /// 部分遮盖字符串（显示前4位）
    /// </summary>
    public static string MaskString(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length <= 4)
            return "***";

        return input[..4] + "***";
    }
}
