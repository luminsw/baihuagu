using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;

namespace TaskRunner.Services.Strategies;

/// <summary>
/// 家庭版同步授权策略：Bearer Token + 设备授权验证
/// </summary>
public class FamilySyncAuthorizationStrategy : ISyncAuthorizationStrategy
{
    private readonly DeviceService _deviceService;

    public FamilySyncAuthorizationStrategy(DeviceService deviceService)
    {
        _deviceService = deviceService;
    }

    public ActionResult? ValidateManifest(HttpContext httpContext, string vaultId, string? deviceId)
    {
        if (!ValidateDeviceAuthorization(httpContext))
        {
            return new UnauthorizedObjectResult(new { error = "设备未授权，请先完成配对" });
        }

        return null;
    }

    public ActionResult? ValidateFile(HttpContext httpContext, string vaultId, string? deviceId)
    {
        if (!ValidateDeviceAuthorization(httpContext))
        {
            return new UnauthorizedObjectResult(new { error = "设备未授权，请先完成配对" });
        }

        return null;
    }

    private bool ValidateDeviceAuthorization(HttpContext httpContext)
    {
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
        {
            var token = authHeader.Substring("Bearer ".Length).Trim();
            if (_deviceService.ValidateAccessToken(token))
            {
                return true;
            }
        }

        // Family 版局域网环境：如果请求来自已授权设备的 IP，也允许同步
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(remoteIp))
        {
            var authorizedDevices = _deviceService.GetAuthorizedDevices();
            if (authorizedDevices.Any(d => remoteIp.Equals(d.IpAddress, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
