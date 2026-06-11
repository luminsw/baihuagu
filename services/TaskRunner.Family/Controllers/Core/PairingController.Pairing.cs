using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;

namespace TaskRunner.Controllers;

public partial class PairingController : ControllerBase
{
    private ActionResult<object> HandleGetQRCode()
    {
        var (url, hostName) = _serverAddressService.GetQrCodeAddresses();
        
        var (baseUrl, name, qrCodeData) = _pairingService.GenerateQRCodeContent(
            url, hostName, _oneHopService.DeviceId);
        
        _logger.LogInformation("生成二维码: Url={Url}, HostName={HostName}", 
            url, hostName);
        
        return Ok(new
        {
            url = baseUrl,
            hostName = name,
            serverId = _oneHopService.DeviceId,
            deviceId = _oneHopService.DeviceId,
            qrCodeData = qrCodeData
        });
    }
}
