using TaskRunner.Core.Shared.Notifications;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Data.Entities;
using TaskRunner.Models;
using TaskRunner.Services;
using TaskRunner.Core.Shared.Security;
using TaskRunner.Contracts.Ai;

namespace TaskRunner.Controllers;

public partial class AiConfigController
{
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


