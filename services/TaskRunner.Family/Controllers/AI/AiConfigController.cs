using TaskRunner.Core.Shared.Notifications;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Data.Entities;
using TaskRunner.Models;
using TaskRunner.Services;
using TaskRunner.Core.Shared.Security;
using TaskRunner.Contracts.Ai;

namespace TaskRunner.Controllers;

/// <summary>
/// AI 配置管理 API - SQLite 加密存储
/// </summary>
[ApiController]
[Route("api/ai/config")]
public class AiConfigController : ControllerBase
{
    private readonly AiConfigService _aiConfigService;
    private readonly SettingsService _settingsService;
    private readonly WebUINotificationService _webUINotification;
    private readonly TaskRunner.Services.CapabilityService _capabilityService;
    private readonly ILogger<AiConfigController> _logger;

    public AiConfigController(
        AiConfigService aiConfigService,
        SettingsService settingsService,
        WebUINotificationService webUINotification,
        TaskRunner.Services.CapabilityService capabilityService,
        ILogger<AiConfigController> logger)
    {
        _aiConfigService = aiConfigService;
        _settingsService = settingsService;
        _webUINotification = webUINotification;
        _capabilityService = capabilityService;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有 AI 提供商配置（不含敏感信息）
    /// </summary>
    [HttpGet("providers")]
    public ActionResult<List<AiProviderViewModel>> GetProviders()
    {
        var providers = _aiConfigService.GetProviders();
        var summaries = _aiConfigService.GetApiKeySummaries();
        
        var result = providers.Select(p =>
        {
            var summary = summaries.FirstOrDefault(s => s.ProviderId == p.Id);
            return new AiProviderViewModel
            {
                Id = p.Id,
                Name = p.Name,
                BaseUrl = p.AiBaseUrl,
                AnthropicBaseUrl = p.AnthropicBaseUrl,
                IsMain = p.IsMain,
                Models = p.GetModelOptions().Select(m => new AiModelViewModel
                {
                    Name = m.Name,
                    IsPaid = m.IsPaid,
                    IsMain = m.IsMain
                }).ToList(),
                HasApiKey = summary?.HasApiKey ?? false,
                KeyMask = summary?.KeyMask,
                Tier = p.Tier
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// 获取 API Key 配置摘要（用于设置页面）
    /// </summary>
    [HttpGet("apikeys")]
    public ActionResult<List<ApiKeySummary>> GetApiKeySummaries()
    {
        return Ok(_aiConfigService.GetApiKeySummaries());
    }

    /// <summary>
    /// 获取单个提供商配置
    /// </summary>
    [HttpGet("providers/{providerId}")]
    public ActionResult<AiProviderViewModel> GetProvider(string providerId)
    {
        var provider = _aiConfigService.GetProvider(providerId);
        if (provider == null)
            return NotFound(new { error = $"Provider '{providerId}' not found" });

        var summary = _aiConfigService.GetApiKeySummaries().FirstOrDefault(s => s.ProviderId == providerId);

        return Ok(new AiProviderViewModel
        {
            Id = provider.Id,
            Name = provider.Name,
            BaseUrl = provider.AiBaseUrl,
            AnthropicBaseUrl = provider.AnthropicBaseUrl,
            IsMain = provider.IsMain,
            Models = provider.GetModelOptions().Select(m => new AiModelViewModel
            {
                Name = m.Name,
                IsPaid = m.IsPaid,
                IsMain = m.IsMain
            }).ToList(),
            HasApiKey = summary?.HasApiKey ?? false,
            KeyMask = summary?.KeyMask,
            Tier = provider.Tier
        });
    }

    /// <summary>
    /// 创建或更新 AI 提供商配置
    /// </summary>
    [HttpPost("providers")]
    public ActionResult SaveProvider([FromBody] SaveAiProviderRequest request)
    {
        try
        {
            // 验证 API Key 格式
            if (!string.IsNullOrEmpty(request.ApiKey) && !_aiConfigService.ValidateApiKeyFormat(request.ApiKey))
            {
                return BadRequest(new { error = "API Key 格式无效" });
            }

            // 构建模型 JSON
            var modelsJson = AiConfigService.SerializeModels(
                request.Models?.Select(m => new AiModelConfig
                {
                    Name = m.Name,
                    IsPaid = m.IsPaid,
                    IsMain = m.IsMain
                }).ToList() ?? new List<AiModelConfig>());

            var setting = new AiProviderSetting
            {
                ProviderId = request.Id.Trim(),
                ProviderName = request.Name.Trim(),
                BaseUrl = request.BaseUrl.Trim(),
                AnthropicBaseUrl = request.AnthropicBaseUrl?.Trim(),
                IsMain = request.IsMain,
                ModelsJson = modelsJson,
                SortOrder = request.SortOrder,
                IsEnabled = true,
                Tier = (int)request.Tier
            };

            _aiConfigService.SaveProvider(setting, request.ApiKey);
            
            // 清除缓存，确保新配置立即生效
            _settingsService.ClearAiProvidersCache();
            
            // 通知 WebUI 刷新全局状态
            _ = _webUINotification.NotifyAIStatusChangedAsync();
            
            _logger.LogInformation("已保存 AI 提供商配置: {ProviderId}", request.Id);
            return Ok(new { success = true, message = "配置已保存" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存 AI 配置失败");
            return StatusCode(500, new { error = $"保存失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 更新 API Key（独立接口，方便单独修改密钥）
    /// </summary>
    [HttpPost("providers/{providerId}/apikey")]
    public ActionResult UpdateApiKey(string providerId, [FromBody] UpdateApiKeyRequest request)
    {
        try
        {
            if (!_aiConfigService.ValidateApiKeyFormat(request.ApiKey))
            {
                return BadRequest(new { error = "API Key 格式无效" });
            }

            // 获取现有配置
            var existing = _aiConfigService.GetProvider(providerId);
            if (existing == null)
            {
                return NotFound(new { error = $"Provider '{providerId}' not found" });
            }

            // 保留其他配置，只更新 API Key
            var setting = new AiProviderSetting
            {
                ProviderId = existing.Id,
                ProviderName = existing.Name,
                BaseUrl = existing.AiBaseUrl,
                AnthropicBaseUrl = existing.AnthropicBaseUrl?.Trim(),
                IsMain = existing.IsMain,
                ModelsJson = AiConfigService.SerializeModels(existing.GetModelOptions()),
                SortOrder = 0,
                IsEnabled = true
            };

            _aiConfigService.SaveProvider(setting, request.ApiKey);

            // 通知 WebUI 刷新全局状态
            _ = _webUINotification.NotifyAIStatusChangedAsync();

            _logger.LogInformation("已更新 API Key: {ProviderId}", providerId);
            return Ok(new { success = true, message = "API Key 已更新" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 API Key 失败");
            return StatusCode(500, new { error = $"更新失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 删除 AI 提供商配置
    /// </summary>
    [HttpDelete("providers/{providerId}")]
    public ActionResult DeleteProvider(string providerId)
    {
        try
        {
            if (_aiConfigService.DeleteProvider(providerId))
            {
                // 清除缓存，确保配置变更立即生效
                _settingsService.ClearAiProvidersCache();

                // 通知 WebUI 刷新全局状态
                _ = _webUINotification.NotifyAIStatusChangedAsync();
                
                _logger.LogInformation("已删除 AI 提供商配置: {ProviderId}", providerId);
                return Ok(new { success = true, message = "配置已删除" });
            }
            return NotFound(new { error = $"Provider '{providerId}' not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除 AI 配置失败");
            return StatusCode(500, new { error = $"删除失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 获取主 AI 提供商的 API Key（明文，用于二维码扫描）
    /// </summary>
    [HttpGet("main-apikey")]
    public ActionResult GetMainApiKey()
    {
        try
        {
            var mainProvider = _aiConfigService.GetMainProvider();
            if (mainProvider == null)
            {
                return NotFound(new { error = "未配置主 AI 提供商" });
            }

            var apiKey = _aiConfigService.GetApiKey(mainProvider.Id);
            if (string.IsNullOrEmpty(apiKey))
            {
                return NotFound(new { error = "主 AI 提供商未设置 API Key" });
            }

            return Ok(new
            {
                providerId = mainProvider.Id,
                providerName = mainProvider.Name,
                apiKey = apiKey
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取主 AI API Key 失败");
            return StatusCode(500, new { error = $"获取失败: {ex.Message}" });
        }
    }

    /// <summary>
    /// 获取预设的知名 AI 提供商列表
    /// </summary>
    [HttpGet("presets")]
    public ActionResult<List<AiProviderPreset>> GetPresets()
    {
        var presets = new List<AiProviderPreset>
        {
            new()
            {
                Id = "siliconflow",
                Name = "硅基流动 (SiliconFlow)",
                BaseUrl = "https://api.siliconflow.cn/v1",
                Models = new()
                {
                    new() { Name = "deepseek-ai/DeepSeek-V3", IsPaid = false, IsMain = true },
                    new() { Name = "deepseek-ai/DeepSeek-R1", IsPaid = false, IsMain = false },
                    new() { Name = "Qwen/Qwen3.5-72B-Instruct", IsPaid = false, IsMain = false },
                    new() { Name = "BAAI/bge-large-zh-v1.5", IsPaid = false, IsMain = false }
                }
            },
            new()
            {
                Id = "volcano",
                Name = "火山引擎方舟 (Volcano Ark)",
                BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
                Models = new()
                {
                    new() { Name = "doubao-1-5-pro-32k-250115", IsPaid = true, IsMain = true },
                    new() { Name = "doubao-1-5-lite-32k-250115", IsPaid = true, IsMain = false },
                    new() { Name = "deepseek-r1-250120", IsPaid = true, IsMain = false },
                    new() { Name = "deepseek-v3-250324", IsPaid = true, IsMain = false }
                }
            },
            new()
            {
                Id = "aliyun",
                Name = "阿里云百炼 (Aliyun Bailian)",
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Models = new()
                {
                    new() { Name = "qwen-plus", IsPaid = true, IsMain = true },
                    new() { Name = "qwen-turbo", IsPaid = true, IsMain = false },
                    new() { Name = "qwen-max", IsPaid = true, IsMain = false },
                    new() { Name = "deepseek-v3", IsPaid = true, IsMain = false },
                    new() { Name = "deepseek-r1", IsPaid = true, IsMain = false }
                }
            },
            new()
            {
                Id = "deepseek",
                Name = "DeepSeek (官方)",
                BaseUrl = "https://api.deepseek.com/v1",
                Models = new()
                {
                    new() { Name = "deepseek-chat", IsPaid = true, IsMain = true },
                    new() { Name = "deepseek-reasoner", IsPaid = true, IsMain = false }
                }
            },
            new()
            {
                Id = "moonshot",
                Name = "Moonshot (月之暗面)",
                BaseUrl = "https://api.moonshot.cn/v1",
                Models = new()
                {
                    new() { Name = "moonshot-v1-8k", IsPaid = true, IsMain = false },
                    new() { Name = "moonshot-v1-32k", IsPaid = true, IsMain = true },
                    new() { Name = "moonshot-v1-128k", IsPaid = true, IsMain = false }
                }
            },
            new()
            {
                Id = "openai",
                Name = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                Models = new()
                {
                    new() { Name = "gpt-4o", IsPaid = true, IsMain = true },
                    new() { Name = "gpt-4o-mini", IsPaid = true, IsMain = false },
                    new() { Name = "gpt-4-turbo", IsPaid = true, IsMain = false },
                    new() { Name = "o3-mini", IsPaid = true, IsMain = false }
                }
            },
            new()
            {
                Id = "azure",
                Name = "Azure OpenAI",
                BaseUrl = "https://{your-resource}.openai.azure.com/openai/deployments/{deployment-id}",
                Models = new()
                {
                    new() { Name = "gpt-4o", IsPaid = true, IsMain = true },
                    new() { Name = "gpt-4", IsPaid = true, IsMain = false },
                    new() { Name = "gpt-35-turbo", IsPaid = true, IsMain = false }
                }
            },
            new()
            {
                Id = "ollama",
                Name = "本地 Ollama",
                BaseUrl = "http://localhost:11434/v1",
                Tier = AiModelTier.Tier2_Local,
                Models = new()
                {
                    new() { Name = "qwen2.5:14b", IsPaid = false, IsMain = true },
                    new() { Name = "deepseek-r1:14b", IsPaid = false, IsMain = false },
                    new() { Name = "llama3.1:8b", IsPaid = false, IsMain = false }
                }
            },
            new()
            {
                Id = "lmstudio",
                Name = "本地 LM Studio",
                BaseUrl = "http://localhost:1234/v1",
                Tier = AiModelTier.Tier2_Local,
                Models = new()
                {
                    new() { Name = "loaded-model", IsPaid = false, IsMain = true }
                }
            }
        };

        // 根据机器能力过滤本地 Provider 预设
        if (!_capabilityService.CanUse(TaskRunner.Services.LocalComputeFeature.AiConfigLocalProviderPresets))
        {
            presets = presets.Where(p =>
                !p.Id.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
                !p.Id.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return Ok(presets);
    }
}

// View Models
public class AiProviderViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? AnthropicBaseUrl { get; set; }
    public bool IsMain { get; set; }
    public List<AiModelViewModel> Models { get; set; } = new();
    public bool HasApiKey { get; set; }
    public string? KeyMask { get; set; }
    public TaskRunner.Contracts.Ai.AiModelTier Tier { get; set; }
}

public class AiModelViewModel
{
    public string Name { get; set; } = "";
    public bool IsPaid { get; set; }
    public bool IsMain { get; set; }
}


