using TaskRunner.Core.Shared;
using TaskRunner.Core.Shared.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskRunner.Data;
using TaskRunner.Services;
using TaskRunner.Services.Strategies;
using TaskRunner.Contracts.Vaults;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// 配对请求
    /// </summary>
    public class PairRequest
    {
        public string? PairCode { get; set; }
        public string? DeviceName { get; set; }
    }

    /// <summary>
    /// 配对响应
    /// </summary>
    public class PairResponse
    {
        public string? RequestId { get; set; }
        public string? AccessToken { get; set; }
        public long ExpiresIn { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
    }

    [ApiController]
    public class PairController : ControllerBase
    {
        private readonly DeviceService _deviceService;
        private readonly ILogger<PairController> _logger;
        private readonly IOneHopService _oneHopService;
        private readonly IPairingStrategy _pairingStrategy;

        public PairController(DeviceService deviceService, ILogger<PairController> logger, IOneHopService oneHopService, IPairingStrategy pairingStrategy)
        {
            _deviceService = deviceService;
            _logger = logger;
            _oneHopService = oneHopService;
            _pairingStrategy = pairingStrategy;
        }

        /// <summary>
        /// 配对端点（移动端使用）
        /// 1. 新设备：提交配对请求，返回 requestId，等待授权
        /// 2. 已授权设备：直接返回 accessToken
        /// </summary>
        [HttpPost("/vault/pair")]
        [HttpPost("/pair")]
        [HttpPost("/mg/pair")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult<PairResponse> Pair([FromBody] PairRequest request)
        {
            if (string.IsNullOrEmpty(request?.PairCode))
            {
                return BadRequest(new { error = "配对码不能为空" });
            }

            // 验证配对码
            if (!_deviceService.ValidatePairCode(request.PairCode))
            {
                return BadRequest(new { error = "配对码错误" });
            }

            var deviceName = request.DeviceName ?? "未知设备";
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

            // 检查该设备名称是否已授权
            var existingDevice = _deviceService.GetAuthorizedDeviceByName(deviceName);
            if (existingDevice != null)
            {
                // 设备已授权，直接返回令牌
                _logger.LogInformation("已授权设备重新配对: {DeviceName}", deviceName);
                return Ok(new PairResponse
                {
                    AccessToken = existingDevice.AccessToken,
                    ExpiresIn = 3600 * 24 * 365, // 1年
                    Status = "authorized",
                    Message = "设备已授权"
                });
            }

            // 通过策略执行配对（cloud 自动授权 / family 提交审批）
            return _pairingStrategy.Pair(deviceName, ipAddress, request.PairCode);
        }

        /// <summary>
        /// 获取当前配对码及服务器设备ID（用于移动端验证服务器身份）
        /// </summary>
        [HttpGet("/vault/pair/code")]
        [HttpGet("/pair/code")]
        [HttpGet("/mg/pair/code")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult<object> GetPairCode()
        {
            var code = _deviceService.GetPairCode();
            return Ok(new { pairCode = code, deviceId = _oneHopService.DeviceId });
        }

        /// <summary>
        /// 刷新配对码（生成新的随机配对码）
        /// </summary>
        [HttpPost("/vault/pair/code/refresh")]
        [HttpPost("/pair/code/refresh")]
        [HttpPost("/mg/pair/code/refresh")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult<object> RefreshPairCode()
        {
            var newCode = _deviceService.RefreshPairCode();
            _logger.LogInformation("配对码已通过 API 刷新");
            return Ok(new { pairCode = newCode, message = "配对码已刷新" });
        }
    }
}
