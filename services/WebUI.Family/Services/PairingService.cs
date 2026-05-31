namespace WebUI.Services;

/// <summary>
/// 配对请求信息
/// </summary>
public class PairingRequest
{
    public string Challenge { get; set; } = string.Empty;
    public string DeviceInfo { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// 二维码内容
/// </summary>
public class QRCodeContent
{
    public string ServerUrl { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Challenge { get; set; } = string.Empty;
    public long ExpiresAt { get; set; }
    public string QrCodeData { get; set; } = string.Empty;
}

/// <summary>
/// 配对服务 - 管理二维码配对
/// </summary>
public class PairingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PairingService> _logger;

    public PairingService(IHttpClientFactory httpClientFactory, ILogger<PairingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// 获取二维码内容
    /// </summary>
    public async Task<QRCodeContent?> GetQRCodeAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("TaskRunnerApi");
            var content = await client.GetFromJsonAsync<QRCodeContent>("api/pairing/qrcode");
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取二维码内容失败");
            return null;
        }
    }

    /// <summary>
    /// 获取待处理的配对请求
    /// </summary>
    public async Task<List<PairingRequest>> GetPendingRequestsAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("TaskRunnerApi");
            var requests = await client.GetFromJsonAsync<List<PairingRequest>>("api/pairing/pending");
            return requests ?? new List<PairingRequest>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取待处理配对请求失败");
            return new List<PairingRequest>();
        }
    }
}
