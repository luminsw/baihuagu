using TaskRunner.Core.Shared;
using TaskRunner.Core.Shared.Security;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;

namespace TaskRunner.Controllers;
    /// <summary>
    /// OneHop设备信息响应
    /// </summary>
    public class OneHopDeviceResponse
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string ServiceId { get; set; } = string.Empty;
        public int SignalStrength { get; set; }
        public DateTime DiscoveredAt { get; set; }
        public Dictionary<string, string> ExtraData { get; set; } = new();
    }

    /// <summary>
    /// OneHop连接信息响应
    /// </summary>
    public class OneHopConnectionResponse
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public DateTime ConnectedAt { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// OneHop连接请求
    /// </summary>
    public class OneHopConnectRequest
    {
        public string DeviceId { get; set; } = string.Empty;
    }

    /// <summary>
    /// OneHop服务状态响应
    /// </summary>
    public class OneHopStatusResponse
    {
        public bool IsAvailable { get; set; }
        public bool IsRunning { get; set; }
        public string ServiceId { get; set; } = string.Empty;
        public int Port { get; set; }
        public int DiscoveredDevicesCount { get; set; }
        public OneHopConnectionResponse? CurrentConnection { get; set; }
    }

    /// <summary>
    /// OneHop设备注册请求
    /// </summary>
    public class OneHopRegisterDeviceRequest
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string? DeviceType { get; set; }
    }

    /// <summary>
    /// OneHop API控制器
    /// 提供OneHop服务的管理和状态查询接口
    /// </summary>
    [ApiController]
    [Route("api/onehop")]
    [Route("mg/onehop")]
    public partial class OneHopController : ControllerBase
    {
        private readonly ILogger<OneHopController> _logger;
        private readonly OneHopManager _oneHopManager;
        private readonly IOneHopService _oneHopService;
        private readonly DeviceService _deviceService;
        private readonly RequestSignatureService _signatureService;

        public OneHopController(
            ILogger<OneHopController> logger,
            OneHopManager oneHopManager,
            IOneHopService oneHopService,
            DeviceService deviceService,
            RequestSignatureService signatureService)
        {
            _logger = logger;
            _oneHopManager = oneHopManager;
            _oneHopService = oneHopService;
            _deviceService = deviceService;
            _signatureService = signatureService;
        }

        /// <summary>
        /// 获取OneHop服务状态
        /// </summary>
        [HttpGet("status")]
        public ActionResult<OneHopStatusResponse> GetStatus()
        {
            try
            {
                var connection = _oneHopManager.GetConnectionStatus();
                var devices = _oneHopManager.GetDiscoveredDevices();

                var response = new OneHopStatusResponse
                {
                    IsAvailable = _oneHopService.IsAvailable,
                    IsRunning = _oneHopService.IsRunning,
                    ServiceId = "com.doctornotes.sync",
                    Port = _oneHopService is OneHopService ohs ? ohs.Port : 0,
                    DiscoveredDevicesCount = devices.Count,
                    CurrentConnection = connection != null ? new OneHopConnectionResponse
                    {
                        DeviceId = connection.DeviceId,
                        DeviceName = connection.DeviceName,
                        IpAddress = connection.IpAddress,
                        Port = connection.Port,
                        ConnectedAt = connection.ConnectedAt,
                        Status = connection.Status.ToString()
                    } : null
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OneHop status");
                return StatusCode(500, new { error = "Failed to get OneHop status", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取发现的设备列表
        /// </summary>
        [HttpGet("devices")]
        public ActionResult<IEnumerable<OneHopDeviceResponse>> GetDevices()
        {
            try
            {
                var devices = _oneHopManager.GetDiscoveredDevices();
                var response = devices.Select(d => new OneHopDeviceResponse
                {
                    DeviceId = d.DeviceId,
                    DeviceName = d.DeviceName,
                    ServiceId = d.ServiceId,
                    SignalStrength = d.SignalStrength,
                    DiscoveredAt = d.DiscoveredAt,
                    ExtraData = d.ExtraData
                }).ToList();

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OneHop devices");
                return StatusCode(500, new { error = "Failed to get devices", message = ex.Message });
            }
        }

        /// <summary>
        /// 连接到指定设备
        /// </summary>
        [HttpPost("connect")]
        public async Task<ActionResult<OneHopConnectionResponse>> Connect([FromBody] OneHopConnectRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.DeviceId))
                {
                    return BadRequest(new { error = "DeviceId is required" });
                }

                var connection = await _oneHopManager.ConnectToDeviceAsync(request.DeviceId);
                if (connection == null)
                {
                    return NotFound(new { error = $"Device not found or connection failed: {request.DeviceId}" });
                }

                var response = new OneHopConnectionResponse
                {
                    DeviceId = connection.DeviceId,
                    DeviceName = connection.DeviceName,
                    IpAddress = connection.IpAddress,
                    Port = connection.Port,
                    ConnectedAt = connection.ConnectedAt,
                    Status = connection.Status.ToString()
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to device: {DeviceId}", request.DeviceId);
                return StatusCode(500, new { error = "Connection failed", message = ex.Message });
            }
        }

        /// <summary>
        /// 断开当前连接
        /// </summary>
        [HttpPost("disconnect")]
        public async Task<ActionResult> Disconnect()
        {
            try
            {
                await _oneHopManager.DisconnectAsync();
                return Ok(new { message = "Disconnected successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disconnecting");
                return StatusCode(500, new { error = "Disconnect failed", message = ex.Message });
            }
        }

        /// <summary>
        /// 开始设备发现
        /// </summary>
        [HttpPost("discovery/start")]
        public async Task<ActionResult> StartDiscovery()
        {
            try
            {
                await _oneHopService.StartDiscoveryAsync();
                return Ok(new { message = "Discovery started" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting discovery");
                return StatusCode(500, new { error = "Failed to start discovery", message = ex.Message });
            }
        }

        /// <summary>
        /// 停止设备发现
        /// </summary>
        [HttpPost("discovery/stop")]
        public async Task<ActionResult> StopDiscovery()
        {
            try
            {
                await _oneHopService.StopDiscoveryAsync();
                return Ok(new { message = "Discovery stopped" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping discovery");
                return StatusCode(500, new { error = "Failed to stop discovery", message = ex.Message });
            }
        }
}
