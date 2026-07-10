using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// 服务器地址配置控制器
    /// </summary>
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

        /// <summary>
        /// 获取服务器地址配置
        /// </summary>
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

        /// <summary>
        /// 更新服务器地址配置
        /// </summary>
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

    /// <summary>
    /// 更新服务器地址请求
    /// </summary>
    public class UpdateServerAddressRequest
    {
        /// <summary>
        /// 域名（广域网场景）。留空则使用局域网 HTTP 自动获取
        /// </summary>
        public string? Domain { get; set; }

        /// <summary>
        /// 服务器显示名称（如"百花谷服务器"），用于移动端展示。留空则使用系统 hostname。
        /// </summary>
        public string? DisplayName { get; set; }
    }
}
