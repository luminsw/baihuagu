using Microsoft.AspNetCore.SignalR;

namespace TaskRunner.Hubs
{
    /// <summary>
    /// 设备配对Hub - 简化版
    /// </summary>
    public class DeviceHub : Hub
    {
        private readonly ILogger<DeviceHub> _logger;

        public DeviceHub(ILogger<DeviceHub> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 客户端连接时
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("客户端连接: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        /// <summary>
        /// 客户端断开时
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("客户端断开: {ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// 移动端订阅配对结果通知
        /// </summary>
        public async Task SubscribeToPairing(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                var groupName = $"pairing:{sessionId}";
                await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
                _logger.LogInformation("移动端订阅配对通知: ConnectionId={ConnectionId}, Group={Group}, SessionId={SessionId}", 
                    Context.ConnectionId, groupName, sessionId);
            }
        }

        /// <summary>
        /// WebUI订阅配对请求通知
        /// </summary>
        public async Task SubscribeToPairingRequests()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, "webui");
            _logger.LogInformation("WebUI订阅配对请求: ConnectionId={ConnectionId}", Context.ConnectionId);
        }

        /// <summary>
        /// 移动端订阅设备专属推送通知（同步请求等）
        /// </summary>
        public async Task SubscribeToDevice(string deviceId)
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                var groupName = $"device:{deviceId}";
                await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
                _logger.LogInformation("移动端订阅设备推送通知: ConnectionId={ConnectionId}, Group={Group}, DeviceId={DeviceId}", 
                    Context.ConnectionId, groupName, deviceId);
            }
        }
    }
}
