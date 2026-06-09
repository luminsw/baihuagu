using TaskRunner.Core.Shared;
using TaskRunner.Services.Security;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;
using TaskRunner.Core.Shared.Security;

namespace TaskRunner.Controllers
{
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
    public class OneHopController : ControllerBase
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

        /// <summary>
        /// 移动端通过HTTP注册设备到OneHop发现列表
        /// </summary>
        [HttpPost("register-device")]
        public ActionResult RegisterDevice([FromBody] OneHopRegisterDeviceRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request?.DeviceId))
                {
                    return BadRequest(new { error = "DeviceId is required" });
                }

                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var deviceName = string.IsNullOrEmpty(request.DeviceName) ? "移动端设备" : request.DeviceName;
                var deviceType = string.IsNullOrEmpty(request.DeviceType) ? null : request.DeviceType;

                _oneHopManager.RegisterDevice(request.DeviceId, deviceName, ipAddress, deviceType);

                // 检查设备是否已授权，已授权则不再创建待授权请求
                var serverName = Environment.MachineName;

                // 安全验证：优先通过 deviceId 查找授权设备，防止 deviceName 碰撞攻击
                var authorizedDevice = _deviceService.GetAuthorizedDeviceById(request.DeviceId);
                if (authorizedDevice != null)
                {
                    return Ok(new
                    {
                        message = "设备已授权",
                        deviceId = request.DeviceId,
                        deviceName = deviceName,
                        serverName = serverName,
                        ipAddress = ipAddress,
                        requestId = authorizedDevice.DeviceId,
                        authorized = true,
                        accessToken = authorizedDevice.AccessToken,
                        sharedSecret = _signatureService.GetSharedSecret()
                    });
                }

                // 兼容旧设备：通过 deviceName 查找
                var deviceByName = _deviceService.GetAuthorizedDeviceByName(deviceName);
                if (deviceByName != null)
                {
                    // 旧数据兼容：如果数据库中 DeviceId 为空，允许恢复并更新 DeviceId
                    if (string.IsNullOrEmpty(deviceByName.DeviceId))
                    {
                        _logger.LogInformation("Legacy device recovery: name={DeviceName} updating DeviceId from empty to {RequestId}",
                            deviceName, request.DeviceId);
                        _deviceService.UpdateDeviceId(deviceByName.DeviceId ?? "", request.DeviceId, deviceName);
                        return Ok(new
                        {
                            message = "设备已授权（已更新设备标识）",
                            deviceId = request.DeviceId,
                            deviceName = deviceName,
                            serverName = serverName,
                            ipAddress = ipAddress,
                            requestId = request.DeviceId,
                            authorized = true,
                            accessToken = deviceByName.AccessToken,
                            sharedSecret = _signatureService.GetSharedSecret()
                        });
                    }

                    // deviceName 匹配但 deviceId 不匹配：可能是设备重置后 ANDROID_ID 变更
                    // 创建新的待授权请求，让用户在 WebUI 中手动确认授权（而非直接拒绝）
                    _logger.LogWarning("Device id mismatch: name={DeviceName} existingId={ExistingId} requestId={RequestId}, creating new pair request",
                        deviceName, deviceByName.DeviceId, request.DeviceId);
                    var pairRequest2 = _deviceService.SubmitLanDiscoveryRequest(deviceName, ipAddress);
                    return Ok(new
                    {
                        message = "设备标识已变更，请在 WebUI 中重新授权",
                        deviceId = request.DeviceId,
                        deviceName = deviceName,
                        serverName = serverName,
                        ipAddress = ipAddress,
                        requestId = pairRequest2.RequestId,
                        authorized = false
                    });
                }

                // 自动创建局域网发现待授权请求（无需扫码）
                var pairRequest = _deviceService.SubmitLanDiscoveryRequest(deviceName, ipAddress);

                return Ok(new
                {
                    message = "设备已注册，请在 WebUI 中授权",
                    deviceId = request.DeviceId,
                    deviceName = deviceName,
                    serverName = serverName,
                    ipAddress = ipAddress,
                    requestId = pairRequest.RequestId,
                    authorized = false,
                    accessToken = (string?)null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering device");
                return StatusCode(500, new { error = "Failed to register device", message = ex.Message });
            }
        }

        /// <summary>
        /// 发送同步请求到已连接的设备
        /// </summary>
        [HttpPost("sync")]
        public async Task<ActionResult> SendSyncRequest([FromQuery] string? vaultId, [FromQuery] long since = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(vaultId))
                {
                    return BadRequest(new { error = "必须指定知识库" });
                }

                var success = await _oneHopManager.SendSyncRequestAsync(vaultId, since);
                if (!success)
                {
                    return BadRequest(new { error = "No active connection or failed to send sync request" });
                }

                return Ok(new { 
                    message = "Sync request sent", 
                    vaultId = vaultId,
                    since = since 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending sync request");
                return StatusCode(500, new { error = "Failed to send sync request", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取OneHop服务信息（用于二维码生成）
        /// </summary>
        [HttpGet("info")]
        public ActionResult<object> GetServiceInfo()
        {
            try
            {
                // 获取本机IP地址
                var ipAddress = GetLocalIpAddress();
                var hostName = Environment.MachineName;
                var httpUrl = $"http://{ipAddress}:8788";
                var httpsUrl = $"https://{ipAddress}:8789";

                return Ok(new
                {
                    serviceId = "com.doctornotes.sync",
                    deviceName = hostName,
                    ipAddress = ipAddress,
                    port = 8788,
                    httpUrl = httpUrl,
                    httpsUrl = httpsUrl,
                    oneHopEnabled = _oneHopService.IsAvailable && _oneHopService.IsRunning,
                    supportedProtocols = new[] { "onehop", "http", "https" },
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OneHop service info");
                return StatusCode(500, new { error = "Failed to get service info", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取增强的二维码数据（包含OneHop信息）
        /// </summary>
        [HttpGet("qrcode")]
        public ActionResult<object> GetEnhancedQrCode()
        {
            try
            {
                // 获取本机IP地址
                var ipAddress = GetLocalIpAddress();
                var hostName = Environment.MachineName;
                var httpUrl = $"http://{ipAddress}:8788";
                var httpsUrl = $"https://{ipAddress}:8789";

                var qrData = new
                {
                    // 传统HTTP信息
                    httpUrl = httpUrl,
                    httpsUrl = httpsUrl,
                    hostName = hostName,
                    
                    // OneHop信息
                    oneHop = new
                    {
                        enabled = _oneHopService.IsAvailable && _oneHopService.IsRunning,
                        serviceId = "com.doctornotes.sync",
                        deviceId = Environment.MachineName,
                        deviceName = hostName,
                        ipAddress = ipAddress,
                        port = 8788,
                        capabilities = new[] { "file-sync", "manifest", "health-check" }
                    },
                    
                    // 时间戳
                    timestamp = DateTime.UtcNow.Ticks,
                    version = "2.0" // 二维码格式版本
                };

                return Ok(qrData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating enhanced QR code");
                // 回退到简单信息
                return Ok(new
                {
                    httpUrl = $"http://{GetLocalIpAddress()}:8788",
                    httpsUrl = $"https://{GetLocalIpAddress()}:8789",
                    hostName = Environment.MachineName
                });
            }
        }

        #region 工具方法

        private string ExtractIpFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "127.0.0.1";
            
            try
            {
                var uri = new Uri(url);
                return uri.Host;
            }
            catch
            {
                // 尝试从URL中提取IP地址
                var match = System.Text.RegularExpressions.Regex.Match(url, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
                return match.Success ? match.Value : "127.0.0.1";
            }
        }

        private int ExtractPortFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return 8788;
            
            try
            {
                var uri = new Uri(url);
                return uri.Port;
            }
            catch
            {
                return 8788;
            }
        }

        private string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
                return "127.0.0.1";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get local IP address");
                return "127.0.0.1";
            }
        }

        #endregion
    }
}