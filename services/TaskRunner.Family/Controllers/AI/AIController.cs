using TaskRunner.Core.Shared;
using TaskRunner.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using TaskRunner.Models;
using TaskRunner.Contracts.Ai;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace TaskRunner.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB 限制，防止 DoS
    public partial class AIController : ControllerBase
    {
        private readonly Services.AiSettingsService _aiSettings;
        private readonly Services.VaultSettingsService _vaultSettings;
        private readonly Services.AiClientService _aiClientService;
        private readonly Services.LocalModelDeploymentService _localDeployment;
        private readonly Services.RagService _ragService;
        private readonly DefaultPromptProvider _scenePromptService;
        private readonly Services.AiFunctionService _aiFunctionService;
        private readonly Services.ChatMemoryService _chatMemoryService;
        private readonly Services.AnkiCardGenerator _cardGenerator;
        private readonly TaskManager _taskManager;
        private readonly ILogger<AIController> _logger;

        public AIController(
            Services.AiSettingsService aiSettings,
            Services.VaultSettingsService vaultSettings,
            Services.AiClientService aiClientService,
            Services.LocalModelDeploymentService localDeployment,
            Services.RagService ragService,
            DefaultPromptProvider scenePromptService,
            Services.AiFunctionService aiFunctionService,
            Services.ChatMemoryService chatMemoryService,
            Services.AnkiCardGenerator cardGenerator,
            TaskManager taskManager,
            ILogger<AIController> logger)
        {
            _aiSettings = aiSettings;
            _vaultSettings = vaultSettings;
            _aiClientService = aiClientService;
            _localDeployment = localDeployment;
            _scenePromptService = scenePromptService;
            _ragService = ragService;
            _aiFunctionService = aiFunctionService;
            _chatMemoryService = chatMemoryService;
            _cardGenerator = cardGenerator;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <summary>
        /// 列出已配置的 AI 提供方（不含密钥），包含模型列表，供前端多选拆分等使用。
        /// </summary>
        [HttpGet("providers")]
        public ActionResult<List<AiProviderPublicDto>> GetProviders()
        {
            var list = _aiSettings.GetAiProviders()
                .Select(p => new AiProviderPublicDto
                {
                    Id = p.Id,
                    Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
                    IsMain = p.IsMain,
                    Models = p.GetModelOptions().Select(m => new AiModelPublicDto
                    {
                        Name = m.Name,
                        IsPaid = m.IsPaid,
                        IsMain = m.IsMain
                    }).ToList()
                })
                .ToList();
            return Ok(list);
        }
        private async Task<string> CallAiApiAsync(string query, string model, string providerId = "")
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, query)
            };
            return await CallAiApiAsync(messages, model, providerId);
        }

        private async Task<string> CallAiApiAsync(List<ChatMessage> messages, string model, string providerId = "", bool enableTools = false, CancellationToken ct = default)
        {
            var providers = _aiSettings.GetAiProviders();
            var provider = string.IsNullOrEmpty(providerId)
                ? providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
                : providers.FirstOrDefault(p => p.Id == providerId);

            if (provider == null)
                throw new Exception("未找到可用的AI提供商");

            var apiEndpoint = provider.AiBaseUrl.TrimEnd('/') + "/chat/completions";
            _logger.LogInformation("调用 AI API: {Endpoint}, 提供商：{Provider}, 模型：{Model}, tools={Tools}", apiEndpoint, provider.Name, model, enableTools);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                // 确保消息列表有 System 提示
                if (!messages.Any(m => m.Role == ChatRole.System))
                {
                    messages.Insert(0, new ChatMessage(ChatRole.System, GetSystemPrompt(provider.Id)));
                }

                var options = Services.AiClientService.BuildChatOptions();
                IList<AITool>? tools = null;
                if (enableTools)
                {
                    tools = _aiFunctionService.GetAllTools();
                    options.Tools = tools;
                }

                var response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                    provider, model, messages, options, linkedCts.Token);

                // 检查是否有 Function Call
                if (enableTools && tools != null)
                {
                    var functionCalls = response.Messages
                        .SelectMany(m => m.Contents)
                        .OfType<FunctionCallContent>()
                        .ToList();

                    if (functionCalls.Count > 0)
                    {
                        _logger.LogInformation("AI 请求执行 {Count} 个函数", functionCalls.Count);

                        // 添加 AI 的 function call 消息
                        var assistantContents = response.Messages.SelectMany(m => m.Contents).ToList();
                        messages.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

                        // 执行每个 function call
                        foreach (var fc in functionCalls)
                        {
                            var tool = tools.FirstOrDefault(t => t is AIFunction f && f.Name == fc.Name);
                            if (tool is AIFunction aiFunc)
                            {
                                try
                                {
                                    var result = await aiFunc.InvokeAsync(new AIFunctionArguments(fc.Arguments), linkedCts.Token);
                                    messages.Add(new ChatMessage(ChatRole.Tool,
                                        new[] { new FunctionResultContent(fc.CallId, result?.ToString() ?? "") }));
                                    _logger.LogInformation("函数 {Name} 执行成功", fc.Name);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "函数 {Name} 执行失败", fc.Name);
                                    messages.Add(new ChatMessage(ChatRole.Tool,
                                        new[] { new FunctionResultContent(fc.CallId, $"执行失败：{ex.Message}") }));
                                }
                            }
                            else
                            {
                                messages.Add(new ChatMessage(ChatRole.Tool,
                                    new[] { new FunctionResultContent(fc.CallId, $"未找到函数：{fc.Name}") }));
                            }
                        }

                        // 重新调用 AI 获取最终回复（不再传 Tools，避免无限循环）
                        var finalOptions = Services.AiClientService.BuildChatOptions();
                        var finalResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                            provider, model, messages, finalOptions, linkedCts.Token);
                        return finalResponse.Text ?? "";
                    }
                }

                var content = response.Text;

                // Qwen 等模型在启用工具调用时可能返回空内容（不调用工具也不回复）
                // 自动回退：禁用 tools 后重试一次
                if (string.IsNullOrWhiteSpace(content) && enableTools)
                {
                    _logger.LogWarning("AI ({Provider}/{Model}) 返回内容为空，尝试禁用工具调用后重试", provider.Name, model);
                    var retryOptions = Services.AiClientService.BuildChatOptions();
                    var retryResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                        provider, model, messages, retryOptions, linkedCts.Token, operation: "chat-retry-no-tools");
                    content = retryResponse.Text;
                    _logger.LogInformation("AI ({Provider}/{Model}) 重试结果：{HasContent}", provider.Name, model, !string.IsNullOrWhiteSpace(content));
                }

                if (string.IsNullOrWhiteSpace(content))
                    throw new Exception("AI 返回内容为空");

                return content;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AI API 请求失败");
                // 尝试提取更友好的错误信息
                throw new Exception(ParseAiErrorMessage(ex));
            }
        }

        /// <summary>
        /// 从异常中提取友好的错误信息
        /// </summary>
        private string ParseAiErrorMessage(Exception ex)
        {
            var msg = ex.Message;

            // OpenAI SDK 会将 HTTP 错误包装在 ClientResultException 中
            if (msg.Contains("Model disabled", StringComparison.OrdinalIgnoreCase))
                return $"模型不可用。请前往【设置】→【AI 配置】更换其他模型";

            if (msg.Contains("api key", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                return $"API Key 无效或已过期。请检查【设置】→【AI 配置】中的 API Key 设置";

            if (msg.Contains("balance", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
                return $"账户余额不足或配额已用完";

            return $"AI API 请求失败：{msg}";
        }

        private static bool IsLocalProvider(AiProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(provider?.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        private Note ParseNote(string aiContent, string query)
        {
            var lines = aiContent.Split('\n');
            var title = lines.FirstOrDefault(l => l.StartsWith("# "))?.TrimStart('#').Trim() ?? $"关于：{query}";
            
            return new Note
            {
                Title = title,
                FilePath = $"AI 生成/{GenerateSafeFileName(title)}",
                Content = aiContent,
                Summary = aiContent.Length > 100 ? aiContent.Substring(0, 100) + "..." : aiContent
            };
        }

        private async Task SaveNote(Note note, string vaultPath)
        {
            if (string.IsNullOrEmpty(vaultPath)) return;

            var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
            var fullPath = System.IO.Path.Combine(notesRoot, note.FilePath + ".md");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            await System.IO.File.WriteAllTextAsync(fullPath, note.Content);
            _logger.LogInformation("笔记已保存：{Path}", fullPath);
        }

        private string GenerateNoteId(string title)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(title))[..8];
        }

        private string GenerateSafeFileName(string title)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var invalidSet = new HashSet<char>(invalid);
            return string.Concat(title.Where(c => !invalidSet.Contains(c)).Take(50));
        }

        private string GetSystemPrompt(string providerId)
        {
            var activeVault = _vaultSettings.GetActiveVault();
            var industry = activeVault?.Industry ?? "";
            var template = _scenePromptService.GetTemplateByName(industry);
            var prompt = template.ChatSystemPrompt;

            // Qwen (阿里云百炼) 在启用 function calling 时容易返回空内容
            // 通过 system prompt 明确指示模型直接回答，仅在明确要求时才调用工具
            if (string.Equals(providerId, "aliyun", StringComparison.OrdinalIgnoreCase))
            {
                prompt += "\n\n【重要指令】请直接回答用户的问题，给出完整、详细的回复。" +
                          "只有在用户明确要求搜索知识库、获取时间、查看系统状态或创建笔记时，才调用相应工具。" +
                          "如果用户只是询问一般性知识问题，请正常回答，不要返回空内容，也不要调用任何工具。";
            }

            return prompt;
        }

        /// <summary>
        /// 异步版本：使用三层记忆系统构建消息列表
        /// </summary>
        private async Task<List<ChatMessage>> BuildMessagesWithMemoryAsync(
            List<ChatHistoryItem>? history, string providerId, string model,
            string currentMessage, string? sessionId = null, CancellationToken ct = default)
        {
            return await _chatMemoryService.BuildMessagesWithMemoryAsync(
                history, providerId, model, currentMessage, sessionId, ct);
        }

        /// <summary>
        /// 解析提供商和模型
        /// </summary>
        private (AiProviderConfig Provider, string Model) ResolveProviderAndModel(string? providerId, string? model)
        {
            var providers = _aiSettings.GetAiProviders();
            var provider = string.IsNullOrEmpty(providerId)
                ? (string.IsNullOrEmpty(model)
                    ? providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
                    : providers.FirstOrDefault(p =>
                        p.Models.Any(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
                      ?? providers.FirstOrDefault(p => p.IsMain)
                      ?? providers.FirstOrDefault())
                : providers.FirstOrDefault(p => p.Id == providerId);

            if (provider == null)
                throw new Exception("未找到指定的AI提供商");

            var modelOptions = provider.GetModelOptions();
            var resolvedModel = !string.IsNullOrEmpty(model)
                ? model
                : modelOptions.FirstOrDefault(m => m.IsMain)?.Name 
                  ?? modelOptions.FirstOrDefault()?.Name 
                  ?? "Qwen/Qwen2.5-14B-Instruct";

            return (provider, resolvedModel);
        }

        private class Note
        {
            public string Title { get; set; } = string.Empty;
            public string FilePath { get; set; } = string.Empty;
            public string Summary { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }
}
