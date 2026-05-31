using MobileContract.VaultSync;

namespace MobileContract.Services;

/// <summary>
/// 知识库同步接口 — 清单、文件、卡片、浏览
/// </summary>
public interface IVaultSyncService
{
    /// <summary>
    /// 获取所有可用知识库列表
    /// </summary>
    Task<IReadOnlyList<VaultInfo>> GetVaultsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取增量同步清单
    /// </summary>
    /// <param name="vaultId">知识库ID</param>
    /// <param name="deviceId">设备ID（用于配额追踪）</param>
    /// <param name="cursor">同步游标，空表示从头开始</param>
    Task<VaultManifestResponse> GetManifestAsync(string vaultId, string deviceId, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载单个文件内容
    /// </summary>
    Task<Stream> GetFileAsync(string relPath, string vaultId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取知识库中的卡片列表
    /// </summary>
    Task<IReadOnlyList<CardRecord>> GetCardsAsync(string vaultId, string deviceId, string? deck = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 浏览知识库目录结构
    /// </summary>
    Task<IReadOnlyList<VaultBrowseItem>> BrowseVaultAsync(string vaultId, string? path = null, CancellationToken cancellationToken = default);
}
