using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;
using TaskRunner.Hubs;
using Microsoft.AspNetCore.SignalR;
using TaskRunner.Contracts.Devices;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// 设备管理 API（供 WebUI 使用）
    /// </summary>
    [ApiController]
    [Route("api/devices")]
    [Route("mg/devices")]
    public class DevicesController : ControllerBase
    {
        private readonly DeviceService _deviceService;
        private readonly IHubContext<DeviceHub> _hubContext;
        private readonly Services.SettingsService _settings;
        private readonly ILogger<DevicesController> _logger;

        public DevicesController(DeviceService deviceService, IHubContext<DeviceHub> hubContext, Services.SettingsService settings, ILogger<DevicesController> logger)
        {
            _deviceService = deviceService;
            _hubContext = hubContext;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// 获取待授权设备列表
        /// </summary>
        [HttpGet("pending")]
        public ActionResult<List<PendingDeviceDto>> GetPendingDevices()
        {
            var pendingRequests = _deviceService.GetPendingRequests();
            var dtos = pendingRequests.Select(r => new PendingDeviceDto
            {
                RequestId = r.RequestId,
                DeviceName = r.DeviceName,
                RequestTime = r.RequestTime,
                IpAddress = r.IpAddress
            }).ToList();

            return Ok(dtos);
        }

        /// <summary>
        /// 获取已授权设备列表
        /// </summary>
        [HttpGet("authorized")]
        public ActionResult<List<AuthorizedDeviceDto>> GetAuthorizedDevices()
        {
            var devices = _deviceService.GetAuthorizedDevices();
            var dtos = devices.Select(d => new AuthorizedDeviceDto
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                AuthorizedTime = d.AuthorizedTime,
                LastSyncTime = d.LastSyncTime,
                IpAddress = d.IpAddress,
                SyncCount = d.SyncCount,
                FirstSyncTime = d.FirstSyncTime,
                SyncedVaultIds = d.SyncedVaultIds
            }).ToList();

            return Ok(dtos);
        }

        /// <summary>
        /// 获取所有设备（包括历史记录）
        /// </summary>
        [HttpGet]
        public ActionResult<List<DeviceDto>> GetAllDevices()
        {
            var devices = _deviceService.GetAllDevices();
            var dtos = devices.Select(d => new DeviceDto
            {
                DeviceId = d.DeviceId,
                DeviceName = d.DeviceName,
                Status = d.Status.ToString(),
                FirstRequestTime = d.FirstRequestTime,
                AuthorizedTime = d.AuthorizedTime,
                LastSyncTime = d.LastSyncTime,
                IpAddress = d.IpAddress,
                SyncCount = d.SyncCount,
                FirstSyncTime = d.FirstSyncTime
            }).ToList();

            return Ok(dtos);
        }

        /// <summary>
        /// 授权设备
        /// </summary>
        [HttpPost("authorize")]
        public IActionResult AuthorizeDevice([FromBody] AuthorizeDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.RequestId))
            {
                return BadRequest(new { error = "请求ID不能为空" });
            }

            var (success, accessToken, error) = _deviceService.AuthorizeDevice(request.RequestId);
            
            if (!success)
            {
                return BadRequest(new { error });
            }

            _logger.LogInformation("设备已授权，请求ID: {RequestId}", request.RequestId);
            
            return Ok(new 
            { 
                success = true, 
                message = "设备已授权",
                accessToken 
            });
        }

        /// <summary>
        /// 拒绝设备配对请求
        /// </summary>
        [HttpPost("reject")]
        public IActionResult RejectDevice([FromBody] RejectDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.RequestId))
            {
                return BadRequest(new { error = "请求ID不能为空" });
            }

            var success = _deviceService.RejectRequest(request.RequestId);
            
            if (!success)
            {
                return BadRequest(new { error = "请求不存在或已处理" });
            }

            _logger.LogInformation("设备配对已拒绝，请求ID: {RequestId}", request.RequestId);
            
            return Ok(new { success = true, message = "已拒绝设备配对" });
        }

        /// <summary>
        /// 撤销设备授权
        /// </summary>
        [HttpPost("revoke")]
        public IActionResult RevokeDevice([FromBody] RevokeDeviceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.DeviceId))
            {
                return BadRequest(new { error = "设备ID不能为空" });
            }

            var success = _deviceService.RevokeDevice(request.DeviceId);
            
            if (!success)
            {
                return BadRequest(new { error = "设备不存在" });
            }

            _logger.LogInformation("设备授权已撤销，设备ID: {DeviceId}", request.DeviceId);
            
            return Ok(new { success = true, message = "已撤销设备授权" });
        }

        /// <summary>
        /// 推送知识库同步通知到移动端设备
        /// </summary>
        [HttpPost("push")]
        public async Task<IActionResult> PushToVault([FromBody] PushToVaultRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.DeviceId))
            {
                return BadRequest(new { error = "设备ID不能为空" });
            }

            if (string.IsNullOrWhiteSpace(request.VaultId))
            {
                return BadRequest(new { error = "必须指定知识库" });
            }

            var device = _deviceService.GetAuthorizedDevices().FirstOrDefault(d => d.DeviceId == request.DeviceId);
            if (device == null)
            {
                return BadRequest(new { error = "设备未授权或不存在" });
            }

            try
            {
                // 获取知识库名称
                var vaultName = _settings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)?.Name ?? request.VaultId ?? "";

                // 将推送请求存入队列（供移动端轮询获取）
                var pushRequest = _deviceService.AddPushRequest(request.DeviceId, device.DeviceName, request.VaultId, vaultName, request.Action);

                // 同时通过 SignalR 通知移动端（如果已连接）
                var notification = new
                {
                    type = "SyncRequest",
                    requestId = pushRequest.RequestId,
                    deviceId = request.DeviceId,
                    vaultId = request.VaultId ?? "",
                    vaultName = vaultName,
                    action = request.Action ?? "sync",
                    timestamp = DateTime.UtcNow
                };

                await _hubContext.Clients.Group($"device:{request.DeviceId}")
                    .SendAsync("SyncNotification", notification);

                _logger.LogInformation("已推送同步通知到设备 {DeviceId}，知识库: {VaultId}", 
                    request.DeviceId, request.VaultId);

                return Ok(new 
                { 
                    success = true, 
                    requestId = pushRequest.RequestId,
                    message = "已通知设备同步指定知识库"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "推送同步通知失败，设备ID: {DeviceId}", request.DeviceId);
                return Ok(new 
                { 
                    success = false, 
                    message = "推送通知暂不可用，请让移动端手动同步" 
                });
            }
        }

        /// <summary>
        /// 移动端轮询获取待处理的推送同步请求
        /// 支持通过 deviceId 或 deviceName 查询
        /// 支持长轮询：设置 wait=true 后服务端会挂起等待，有新推送时立即返回（最长15秒）
        /// </summary>
        [HttpGet("push-pending")]
        public async Task<ActionResult<List<PushSyncRequest>>> GetPendingPushRequests(
            [FromQuery] string? deviceId, 
            [FromQuery] string? deviceName,
            [FromQuery] bool wait = false,
            [FromQuery] int timeoutMs = 15000)
        {
            var query = !string.IsNullOrWhiteSpace(deviceId) ? deviceId : deviceName;
            _logger.LogInformation("[PushPending] query={Query}, deviceId={DeviceId}, deviceName={DeviceName}, wait={Wait}", query, deviceId, deviceName, wait);
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(new { error = "设备ID或设备名称不能为空" });
            }

            // 限制超时范围，防止资源滥用
            var effectiveTimeout = Math.Clamp(timeoutMs, 1000, 60000);
            var requests = await _deviceService.GetPendingPushRequestsAsync(query, wait, effectiveTimeout);
            _logger.LogInformation("[PushPending] query={Query}, returned {Count} requests", query, requests.Count);
            return Ok(requests);
        }

        /// <summary>
        /// 获取移动端统计信息
        /// </summary>
        [HttpGet("stats")]
        public ActionResult<MobileStats> GetMobileStats()
        {
            var stats = _deviceService.GetMobileStats();
            return Ok(stats);
        }
    }
}
