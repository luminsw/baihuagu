using System.Text.Json;
using BaihuaguSdk.Models;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Transport;

namespace BaihuaguSdk.Services;

/// <summary>
/// 配对服务实现。
/// 处理二维码解析、设备注册、配对码流程。
/// 与 Kotlin PairingService.kt + DeviceRegistrationService.kt 逻辑对齐。
/// </summary>
public class PairingServiceImpl
{
    private readonly HttpClient _httpClient;
    private readonly IRequestSigner _signer;
    private readonly string _deviceId;
    private readonly string _deviceName;

    public PairingServiceImpl(
        HttpClient httpClient, IRequestSigner signer,
        string deviceId, string deviceName)
    {
        _httpClient = httpClient;
        _signer = signer;
        _deviceId = deviceId;
        _deviceName = deviceName;
    }

    // ---- QR Code parsing ----

    /// <summary>解析二维码 JSON 内容</summary>
    public static QrCodeContent? ParseQrCode(string content)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<QrCodeContent>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed == null || string.IsNullOrEmpty(parsed.HostName))
                return null;

            var hasNewFormat = !string.IsNullOrEmpty(parsed.HttpUrl) ||
                               !string.IsNullOrEmpty(parsed.HttpsUrl) ||
                               !string.IsNullOrEmpty(parsed.BaseUrl);
            var hasOldFormat = !string.IsNullOrEmpty(parsed.ServerUrl);
            return (hasNewFormat || hasOldFormat) ? parsed : null;
        }
        catch { return null; }
    }

    /// <summary>从二维码内容提取服务器地址</summary>
    public static ServerAddresses GetServerAddresses(QrCodeContent content)
    {
        var serverId = FirstNonEmpty(content.ServerId, content.DeviceId)
                       ?? $"server-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        // 新版统一地址（baseUrl）优先
        if (!string.IsNullOrEmpty(content.BaseUrl))
        {
            return new ServerAddresses(serverId, content.BaseUrl, content.BaseUrl,
                content.HostName, content.DeviceId);
        }

        // 双地址格式
        var http = content.HttpUrl ?? content.ServerUrl ?? "";
        var https = content.HttpsUrl ?? content.HttpUrl ?? content.ServerUrl ?? "";
        return new ServerAddresses(serverId, http, https, content.HostName, content.DeviceId);
    }

    // ---- Device Registration ----

    /// <summary>向服务器注册本机设备</summary>
    public async Task<RegisterDeviceResult> RegisterDeviceAsync(string serverUrl)
    {
        try
        {
            var transport = new HttpTransport(_httpClient, _signer, serverUrl);
            var body = new { deviceId = _deviceId, deviceName = _deviceName, deviceType = "maui" };
            var resp = await transport.PostJsonAsync<JsonElement>(
                "/mg/onehop/register-device", body);

            if (resp.IsSuccess)
            {
                var root = resp.Data!;
                var success = GetBool(root, "success");
                if (!success)
                    return new RegisterDeviceResult { Success = false };

                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    return new RegisterDeviceResult
                    {
                        Success = true,
                        Authorized = GetBool(data, "authorized"),
                        SharedSecret = GetString(data, "sharedSecret"),
                        RequestId = GetString(data, "requestId"),
                        DeviceName = GetString(data, "deviceName")
                    };
                }

                return new RegisterDeviceResult { Success = true };
            }

            return new RegisterDeviceResult { Success = false };
        }
        catch
        {
            return new RegisterDeviceResult { Success = false };
        }
    }

    // ---- helpers ----

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static string NormalizeUrl(string url) =>
        url.TrimEnd('/').ToLowerInvariant();
}

/// <summary>二维码内容（与 Kotlin QRCodeContent 对齐）</summary>
public record QrCodeContent
{
    public string? ServerId { get; init; }
    public string? HttpUrl { get; init; }
    public string? HttpsUrl { get; init; }
    public string? BaseUrl { get; init; }
    public string? ServerUrl { get; init; }
    public string HostName { get; init; } = "";
    public string? DeviceId { get; init; }
}

/// <summary>解析后的服务器地址</summary>
public record ServerAddresses(
    string ServerId,
    string HttpUrl,
    string HttpsUrl,
    string HostName,
    string? DeviceId = null);
