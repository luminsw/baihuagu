namespace MobileContract.Services;

/// <summary>
/// 移动端远程日志服务接口。
/// 缓冲日志并在后台批量上报到百花谷服务器，与 Kotlin RemoteLogService / ArkTS RemoteLogService 对齐。
/// </summary>
public interface IRemoteLogService
{
    /// <summary>初始化远程日志目标服务器与可选的 OpenObserve 后端</summary>
    void Initialize(string serverUrl, string? ooHost = null, string? ooUsername = null, string? ooPassword = null);

    /// <summary>切换目标服务器地址</summary>
    void UpdateServerUrl(string serverUrl);

    /// <summary>记录一条任意级别的日志</summary>
    void Log(string level, string message, string? context = null, Dictionary<string, string>? extra = null);

    /// <summary>记录 INFO 级别日志</summary>
    void Info(string message, string? context = null);

    /// <summary>记录 WARN 级别日志</summary>
    void Warn(string message, string? context = null);

    /// <summary>记录 ERROR 级别日志</summary>
    void Error(string message, string? context = null);

    /// <summary>记录 DEBUG 级别日志</summary>
    void Debug(string message, string? context = null);

    /// <summary>强制刷新缓冲区到服务端</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
