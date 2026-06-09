using System.Security.Cryptography;

namespace TaskRunner.Services
{
    /// <summary>
    /// 配对服务 - 简化版，只负责生成二维码
    /// </summary>
    public class PairingService : IDisposable
    {
        private readonly ILogger<PairingService> _logger;
        
        public PairingService(ILogger<PairingService> logger)
        {
            _logger = logger;
            _logger.LogInformation("配对服务已初始化");
        }

        /// <summary>
        /// 生成二维码内容（统一单地址模型）
        /// </summary>
        public (string url, string hostName, string qrCodeData) GenerateQRCodeContent(
            string url, string hostName, string? deviceId = null)
        {
            // serverId 作为服务器唯一标识，即使 IP 变了也能识别为同一台服务器
            var serverId = deviceId ?? $"server-{Guid.NewGuid():N}";
            var qrCodeData = System.Text.Json.JsonSerializer.Serialize(new
            {
                serverId = serverId,
                baseUrl = url,
                hostName = hostName,
                deviceId = deviceId
            });
            
            _logger.LogInformation("生成二维码: Url={Url}, HostName={HostName}, ServerId={ServerId}", 
                url, hostName, serverId);
            
            return (url, hostName, qrCodeData);
        }

        public void Dispose()
        {
        }
    }
}
