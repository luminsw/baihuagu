namespace MobileContract.Pairing;

/// <summary>
/// 认证配置响应（包含共享密钥用于请求签名）
/// </summary>
public record AuthConfigResponse
{
    public string? SharedSecret { get; init; }
    public string? Message { get; init; }
}
