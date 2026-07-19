using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;
using TaskRunner.Contracts.Pairing;

namespace TaskRunner.Controllers;

public partial class PairingController : ControllerBase
{
    private IActionResult HandleGetQRCode()
    {
        var (url, hostName) = _serverAddressService.GetQrCodeAddresses();
        
        var (baseUrl, name, qrCodeData) = _pairingService.GenerateQRCodeContent(
            url, hostName, _oneHopService.DeviceId);
        
        _logger.LogInformation("生成二维码: Url={Url}, HostName={HostName}", 
            url, hostName);
        
        return Ok(new ServerQRResponse
        {
            Url = baseUrl,
            HostName = name,
            ServerId = _oneHopService.DeviceId,
            DeviceId = _oneHopService.DeviceId,
            QrCodeData = qrCodeData
        });
    }
}
