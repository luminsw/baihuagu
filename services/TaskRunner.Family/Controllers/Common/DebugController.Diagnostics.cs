using Microsoft.AspNetCore.Mvc;

namespace TaskRunner.Controllers;

public partial class DebugController : ControllerBase
{
    private bool IsAuthorized()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp != null && (System.Net.IPAddress.IsLoopback(remoteIp)
            || remoteIp.ToString() == "127.0.0.1"
            || remoteIp.ToString() == "::1"))
        {
            return true;
        }

        return false;
    }
}
