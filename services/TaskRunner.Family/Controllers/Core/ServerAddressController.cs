using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;
using TaskRunner.Contracts.Core;

namespace TaskRunner.Controllers
{
    [ApiController]
    [Route("api/server-address")]
    public class ServerAddressController : ControllerBase
    {
        private readonly ServerAddressService _serverAddressService;
        private readonly ILogger<ServerAddressController> _logger;

        public ServerAddressController(
            ServerAddressService serverAddressService,
            ILogger<ServerAddressController> logger)
        {
            _serverAddressService = serverAddressService;
            _logger = logger;
        }

        [HttpGet]
        public ActionResult<object> GetSettings()
        {
            var settings = _serverAddressService.GetSettings();
            var (url, hostName) = _serverAddressService.GetQrCodeAddresses();

            return Ok(new
            {
                domain = settings.Domain,
                url = settings.Url,
                actualUrl = url,
                hostName = hostName,
                displayName = settings.DisplayName
            });
        }

        [HttpPost]
        public async Task<ActionResult<object>> UpdateSettings([FromBody] UpdateServerAddressRequest request)
        {
            try
            {
                var settings = await _serverAddressService.UpdateSettings(request.Domain ?? "", request.DisplayName ?? "");

                var (url, hostName) = _serverAddressService.GetQrCodeAddresses();

                return Ok(new
                {
                    success = true,
                    domain = settings.Domain,
                    url = settings.Url,
                    actualUrl = url,
                    hostName = hostName,
                    displayName = settings.DisplayName
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新服务器地址配置失败");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
    }
}
