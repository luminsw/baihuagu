namespace WebUI.Services;

/// <summary>
/// 简单的状态服务 - 直接从API获取状态，不缓存，不订阅事件
/// </summary>
public class SimpleStatusService
{
    private readonly IApiService _apiService;
    private readonly VaultsService _vaultsService;
    private readonly ILogger<SimpleStatusService> _logger;

    public SimpleStatusService(IApiService apiService, VaultsService vaultsService, ILogger<SimpleStatusService> logger)
    {
        this._apiService = apiService;
        this._vaultsService = vaultsService;
        this._logger = logger;
    }

    /// <summary>
    /// 获取AI状态
    /// </summary>
    public async Task<AIStatusSummary> GetAIStatusAsync()
    {
        try
        {
            var providers = await _apiService.GetAiConfigProvidersAsync();
            var activeProvider = providers.FirstOrDefault(p => p.IsMain);
            
            return new AIStatusSummary
            {
                IsConfigured = activeProvider != null,
                ProviderName = activeProvider?.Name ?? "未配置",
                Model = activeProvider?.Models.FirstOrDefault(m => m.IsMain)?.Name ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取AI状态失败");
            return new AIStatusSummary { IsConfigured = false, ProviderName = "错误", Model = "" };
        }
    }

    /// <summary>
    /// 获取知识库状态
    /// </summary>
    public async Task<VaultStatusSummary> GetVaultStatusAsync()
    {
        try
        {
            var vaults = await _vaultsService.GetVaultsAsync();
            var activeVault = vaults.Vaults.FirstOrDefault();
            
            var status = VaultConfigurationStatus.NotConfigured;
            var pathExists = false;
            
            if (activeVault != null && !string.IsNullOrEmpty(activeVault.Path))
            {
                pathExists = Directory.Exists(activeVault.Path);
                status = pathExists ? VaultConfigurationStatus.Configured : VaultConfigurationStatus.PathNotFound;
            }

            return new VaultStatusSummary
            {
                IsConfigured = activeVault != null && pathExists,
                Status = status,
                ActiveVaultName = activeVault?.Name ?? "未选择",
                ActiveVaultPath = activeVault?.Path ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取知识库状态失败");
            return new VaultStatusSummary 
            { 
                IsConfigured = false, 
                Status = VaultConfigurationStatus.NotConfigured,
                ActiveVaultName = "错误", 
                ActiveVaultPath = "" 
            };
        }
    }
}
