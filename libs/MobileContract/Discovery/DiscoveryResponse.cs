namespace MobileContract.Discovery;

/// <summary>
/// 服务发现响应，移动端通过此信息连接后端
/// </summary>
public record DiscoveryResponse
{
    public string ServiceId { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string HttpUrl { get; init; } = "";
    public string HostName { get; init; } = "";
    public int Port { get; init; }
    public string DeviceId { get; init; } = "";
    public string ServerId { get; init; } = "";
}
