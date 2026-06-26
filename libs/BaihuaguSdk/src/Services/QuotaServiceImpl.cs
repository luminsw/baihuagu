using MobileContract.Quota;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Transport;

namespace BaihuaguSdk.Services;

/// <summary>
/// 配额与购买服务实现。
/// 与 Kotlin QuotaService.kt 逻辑对齐。
/// </summary>
public class QuotaServiceImpl
{
    private readonly HttpClient _httpClient;
    private readonly IRequestSigner _signer;
    private readonly string _baseUrl;

    public QuotaServiceImpl(HttpClient httpClient, IRequestSigner signer, string baseUrl)
    {
        _httpClient = httpClient;
        _signer = signer;
        _baseUrl = baseUrl;
    }

    /// <summary>获取设备当前配额状态</summary>
    public async Task<QuotaInfoDto> GetQuotaAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        
        var transport = new HttpTransport(_httpClient, _signer, _baseUrl, deviceId: deviceId);
        var resp = await transport.GetJsonAsync<QuotaInfoDto>("/mg/mobile/quota", ct: ct);
        if (!resp.IsSuccess)
            throw new HttpRequestException(resp.ErrorMessage ?? "查询配额失败");
        return resp.Data!;
    }

    /// <summary>获取设备购买历史</summary>
    public async Task<IReadOnlyList<OrderRecordDto>> GetOrdersAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        
        var transport = new HttpTransport(_httpClient, _signer, _baseUrl, deviceId: deviceId);
        var resp = await transport.GetJsonAsync<List<OrderRecordDto>>("/mg/mobile/orders", ct: ct);
        if (!resp.IsSuccess)
            throw new HttpRequestException(resp.ErrorMessage ?? "查询订单失败");
        return resp.Data ?? new List<OrderRecordDto>();
    }

    /// <summary>模拟购买（开发/测试用）</summary>
    public async Task<bool> SimulatePurchaseAsync(string deviceId, string productId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentException.ThrowIfNullOrEmpty(productId);
        
        var transport = new HttpTransport(_httpClient, _signer, _baseUrl, deviceId: deviceId);
        var body = new { deviceId, productId };
        var resp = await transport.PostJsonAsync<object>("/mg/mobile/purchase", body, ct: ct);
        return resp.IsSuccess;
    }
}
