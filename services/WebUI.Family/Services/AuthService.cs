using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace WebUI.Services;

/// <summary>
/// 认证服务 - 管理用户认证状态
/// 使用随机令牌 + 过期时间，替代基于日期的弱令牌
/// </summary>
public class AuthService
{
    // Cookie 名称
    public const string AuthCookieName = "webui_auth";
    // Cookie 有效期（天）
    public const int CookieExpiryDays = 7;
    // 令牌有效期（小时）
    private const int TokenExpiryHours = 24;

    // 活跃会话令牌（tokenId -> 过期时间）
    private readonly ConcurrentDictionary<string, DateTime> _activeTokens = new();

    // CLI 一次性令牌（token -> 过期时间）
    private readonly ConcurrentDictionary<string, DateTime> _cliTokens = new();
    // CLI 令牌有效期（分钟）
    private const int CliTokenExpiryMinutes = 5;

    public AuthService()
    {
    }

    /// <summary>
    /// 生成认证令牌 - 随机令牌 + 过期时间
    /// </summary>
    public Task<string> GenerateAuthTokenAsync()
    {
        // 生成随机令牌
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        
        // 存储到活跃会话中
        var expiry = DateTime.UtcNow.AddHours(TokenExpiryHours);
        _activeTokens[token] = expiry;
        
        // 清理过期令牌
        CleanupExpiredTokens();
        
        return Task.FromResult(token);
    }

    /// <summary>
    /// 验证认证令牌是否有效
    /// </summary>
    public Task<bool> ValidateAuthTokenAsync(string token)
    {
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(false);

        // 检查活跃会话中是否存在且未过期
        if (_activeTokens.TryGetValue(token, out var expiry))
        {
            if (DateTime.UtcNow < expiry)
                return Task.FromResult(true);

            // 已过期，移除
            _activeTokens.TryRemove(token, out _);
            return Task.FromResult(false);
        }

        return Task.FromResult(false);
    }


    /// <summary>
    /// 登出 - 移除指定令牌
    /// </summary>
    public void RevokeToken(string token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            _activeTokens.TryRemove(token, out _);
        }
    }

    /// <summary>
    /// 清理过期令牌
    /// </summary>
    private void CleanupExpiredTokens()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _activeTokens)
        {
            if (now >= kvp.Value)
            {
                _activeTokens.TryRemove(kvp.Key, out _);
            }
        }
    }

    #region CLI Token

    /// <summary>
    /// 生成 CLI 一次性访问令牌（仅供本机命令行工具使用）
    /// </summary>
    public string GenerateCliToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(24);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

        _cliTokens[token] = DateTime.UtcNow.AddMinutes(CliTokenExpiryMinutes);
        CleanupExpiredCliTokens();
        return token;
    }

    /// <summary>
    /// 验证 CLI 令牌是否有效（一次性使用，验证后立即删除）
    /// </summary>
    public bool ValidateCliToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        // 原子操作：尝试移除并获取过期时间，防止并发重放攻击
        if (_cliTokens.TryRemove(token, out var expiry))
        {
            if (DateTime.UtcNow < expiry)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 清理过期 CLI 令牌
    /// </summary>
    private void CleanupExpiredCliTokens()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _cliTokens.ToArray())
        {
            if (now >= kvp.Value)
            {
                _cliTokens.TryRemove(kvp.Key, out _);
            }
        }
    }

    #endregion
}
