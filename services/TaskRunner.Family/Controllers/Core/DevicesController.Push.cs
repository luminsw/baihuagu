using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;
using TaskRunner.Core.Shared.Hubs;
using Microsoft.AspNetCore.SignalR;
using TaskRunner.Contracts.Devices;

namespace TaskRunner.Controllers
{
    public partial class DevicesController : ControllerBase
    {
        [HttpPost("push")]
        public async Task<IActionResult> PushToVault([FromBody] PushToVaultRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.DeviceId))
                return BadRequest(new { error = "设备ID不能为空" });

            if (string.IsNullOrWhiteSpace(request.VaultId))
                return BadRequest(new { error = "必须指定知识库" });

            var device = _deviceService.GetAuthorizedDevices().FirstOrDefault(d => d.DeviceId == request.DeviceId);
            if (device == null)
                return BadRequest(new { error = "设备未授权或不存在" });

            try
            {
                var vaultName = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)?.Name ?? request.VaultId ?? "";
                var pushRequest = _deviceService.AddPushRequest(request.DeviceId, device.DeviceName, request.VaultId, vaultName, request.Action);

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

                _logger.LogInformation("已推送同步通知到设备 {DeviceId}，知识库: {VaultId}", request.DeviceId, request.VaultId);

                return Ok(new { success = true, requestId = pushRequest.RequestId, message = "已通知设备同步指定知识库" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "推送同步通知失败，设备ID: {DeviceId}", request.DeviceId);
                return Ok(new { success = false, message = "推送通知暂不可用，请让移动端手动同步" });
            }
        }

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
                return BadRequest(new { error = "设备ID或设备名称不能为空" });

            var effectiveTimeout = Math.Clamp(timeoutMs, 1000, 60000);
            var requests = await _deviceService.GetPendingPushRequestsAsync(query, wait, effectiveTimeout);
            _logger.LogInformation("[PushPending] query={Query}, returned {Count} requests", query, requests.Count);
            return Ok(requests);
        }
    }
}
