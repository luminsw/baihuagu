using MobileContract.VaultSync;
using MobileContract.Services;

namespace TaskRunner.Services.Adapters;

/// <summary>
/// 知识库同步服务适配器（Family 版）
/// </summary>
public class MobileVaultSyncServiceAdapter : IVaultSyncService
{
    private readonly VaultSyncService _vaultSyncService;

    public MobileVaultSyncServiceAdapter(VaultSyncService vaultSyncService)
    {
        _vaultSyncService = vaultSyncService;
    }

    public Task<IReadOnlyList<VaultInfo>> GetVaultsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_vaultSyncService.GetVaults());
    }

    public Task<VaultManifestResponse> GetManifestAsync(string vaultId, string deviceId, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var (files, newCursor) = _vaultSyncService.ScanVaultManifest(vaultId);
        var vaultName = _vaultSyncService.GetVaultName(vaultId) ?? vaultId;

        return Task.FromResult(new VaultManifestResponse
        {
            VaultId = vaultId,
            VaultName = vaultName,
            Cursor = newCursor.ToString(),
            Files = files
        });
    }

    public Task<Stream> GetFileAsync(string relPath, string vaultId, string deviceId, CancellationToken cancellationToken = default)
    {
        var content = _vaultSyncService.ReadFile(vaultId, relPath);
        if (content == null)
            throw new FileNotFoundException($"文件不存在: {relPath}");

        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task<IReadOnlyList<CardRecord>> GetCardsAsync(string vaultId, string deviceId, string? deck = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_vaultSyncService.GetCards(vaultId));
    }

    public Task<IReadOnlyList<VaultBrowseItem>> BrowseVaultAsync(string vaultId, string? path = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_vaultSyncService.BrowseVault(vaultId, path));
    }
}
