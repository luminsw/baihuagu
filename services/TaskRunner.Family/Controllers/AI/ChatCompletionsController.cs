using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using TaskRunner.Models;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// OpenAI 兼容 Chat Completions API Facade
    /// 让 Cursor/Claude Code/Windsurf 等工具可以直接使用 yj 的 AI 服务
    /// </summary>
    [ApiController]
    [Route("api/chat/completions")]
    public class ChatCompletionsController : ControllerBase
    {
        private readonly AiSettingsService _aiSettings;
        private readonly VaultSettingsService _vaultSettings;
        private readonly AiClientService _aiClientService;
        private readonly RagService _ragService;
        private readonly McpServerService _mcpServerService;
        private readonly LocalModelDeploymentService _localDeployment;
        private readonly DefaultPromptProvider _scenePromptService;
        private readonly ILogger<ChatCompletionsController> _logger;

        public ChatCompletionsController(
            AiSettingsService aiSettings,
            VaultSettingsService vaultSettings,
            AiClientService aiClientService,
            RagService ragService,
            McpServerService mcpServerService,
            LocalModelDeploymentService localDeployment,
            DefaultPromptProvider scenePromptService,
            ILogger<ChatCompletionsController> logger)
        {
            _aiSettings = aiSettings;
            _vaultSettings = vaultSettings;
            _aiClientService = aiClientService;
            _ragService = ragService;
            _mcpServerService = mcpServerService;
            _localDeployment = localDeployment;
            _scenePromptService = scenePromptService;
            _logger = logger;
        }

        /// <summary>
        /// OpenAI 兼容 Chat Completions 端点
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateChatCompletion([FromBody] OpenAiChatRequest request)
        {
            try
            {
                if (request.Messages == null || request.Messages.Count == 0)
                    return BadRequest(new { error = "messages 不能为空" });

                // 解析 model 参数，格式: "provider/model" 或直接使用默认 provider
                var (providerId, modelId) = ParseModel(request.Model);
                var provider = _aiSettings.GetAiProvider(providerId)
                    ?? _aiSettings.GetMainAiProvider()
                    ?? throw new Exception("未配置 AI 提供商");

                var model = string.IsNullOrWhiteSpace(modelId) ? GetDefaultModel(provider) : modelId;

                // 转换消息格式
                var messages = request.Messages.Select(m => new ChatMessage(
                    ParseRole(m.Role),
                    m.Content ?? ""
                )).ToList();

                // 注入系统提示（知识库助手上下文）
                InjectSystemPrompt(messages);

                // RAG 增强：检索知识库上下文
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages, HttpContext.RequestAborted);

                // 调用 AI
                var options = new ChatOptions();
                if (request.Temperature.HasValue)
                    options.Temperature = (float)request.Temperature.Value;
                if (request.MaxTokens.HasValue)
                    options.MaxOutputTokens = request.MaxTokens.Value;

                var sw = Stopwatch.StartNew();
                ChatResponse response;

                if (request.Stream == true)
                {
                    return await HandleStreamingAsync(provider, model, messages, options, HttpContext.RequestAborted);
                }

                // 非流式模式支持工具调用
                if (request.Tools?.Count > 0)
                {
                    var toolResult = await HandleToolCallingAsync(provider, model, messages, options, request.Tools, HttpContext.RequestAborted);
                    if (toolResult != null) return Ok(toolResult);
                }

                response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                    provider, model, messages, options, HttpContext.RequestAborted);
                sw.Stop();

                var result = new OpenAiChatResponse
                {
                    Id = $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = $"{provider.Id}/{model}",
                    Choices =
                    [
                        new OpenAiChoice
                        {
                            Index = 0,
                            Message = new OpenAiMessage
                            {
                                Role = "assistant",
                                Content = response.Text ?? ""
                            },
                            FinishReason = "stop"
                        }
                    ],
                    Usage = new OpenAiUsage
                    {
                        PromptTokens = (int)(response.Usage?.InputTokenCount ?? 0),
                        CompletionTokens = (int)(response.Usage?.OutputTokenCount ?? 0),
                        TotalTokens = (int)((response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0))
                    }
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenAI 兼容 Chat Completion 失败");
                return StatusCode(500, new { error = new { message = ex.Message, type = "internal_error" } });
            }
        }

        /// <summary>
        /// 列出可用模型（OpenAI 兼容格式）
        /// </summary>
        [HttpGet("models")]
        public IActionResult ListModels()
        {
            var models = _aiSettings.GetAiProviders()
                .SelectMany(p => p.GetModelOptions().Select(m => new OpenAiModel
                {
                    Id = $"{p.Id}/{m.Name}",
                    Object = "model",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    OwnedBy = p.Id
                }))
                .ToList();

            // 添加简化格式的模型 ID
            var allModels = models.ToList();
            foreach (var m in models)
            {
                if (m.Id.Contains('/'))
                {
                    var simpleName = m.Id.Split('/')[1];
                    if (!allModels.Any(x => x.Id == simpleName))
                    {
                        allModels.Add(new OpenAiModel
                        {
                            Id = simpleName,
                            Object = "model",
                            Created = m.Created,
                            OwnedBy = m.OwnedBy
                        });
                    }
                }
            }

            return Ok(new { data = allModels, @object = "list" });
        }

        private async Task<IActionResult> HandleStreamingAsync(
            AiProviderConfig provider, string model,
            List<ChatMessage> messages, ChatOptions options, CancellationToken ct)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");

            var id = $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}";
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            try
            {
                // 流式模式下 RAG 增强
                var (enrichedMessages, wasEnriched) = await _ragService.TryEnrichForStreamingAsync(messages, ct);
                if (wasEnriched)
                {
                    var searchChunk = new OpenAiChatStreamChunk
                    {
                        Id = id,
                        Created = created,
                        Model = $"{provider.Id}/{model}",
                        Choices = [new OpenAiStreamChoice
                        {
                            Index = 0,
                            Delta = new OpenAiDelta { Content = "🔍 正在检索知识库...\n\n" },
                            FinishReason = null
                        }]
                    };
                    await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(searchChunk)}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
                messages = enrichedMessages;

                var client = _aiClientService.CreateChatClient(provider, model);
                await foreach (var chunk in client.GetStreamingResponseAsync(messages, options, ct))
                {
                    if (string.IsNullOrEmpty(chunk.Text)) continue;

                    var sseData = new OpenAiChatStreamChunk
                    {
                        Id = id,
                        Created = created,
                        Model = $"{provider.Id}/{model}",
                        Choices =
                        [
                            new OpenAiStreamChoice
                            {
                                Index = 0,
                                Delta = new OpenAiDelta { Content = chunk.Text },
                                FinishReason = null
                            }
                        ]
                    };

                    await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(sseData)}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }

                // 发送结束标记
                var doneChunk = new OpenAiChatStreamChunk
                {
                    Id = id,
                    Created = created,
                    Model = $"{provider.Id}/{model}",
                    Choices =
                    [
                        new OpenAiStreamChoice
                        {
                            Index = 0,
                            Delta = new OpenAiDelta(),
                            FinishReason = "stop"
                        }
                    ]
                };
                await Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(doneChunk)}\n\n", ct);
                await Response.WriteAsync("data: [DONE]\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // 客户端取消，如果是本地 provider，卸载模型释放资源
                if (IsLocalProvider(provider))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogInformation("用户取消后自动卸载本地模型: {Provider} {Model}", provider.Id, model);
                            await _localDeployment.UnloadModelAsync(provider.Id, model);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "取消后卸载模型失败: {Provider} {Model}", provider.Id, model);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSE 流式响应失败");
                await Response.WriteAsync($"data: {{\"error\":\"{ex.Message.Replace("\"", "\\\"")}\"}}\n\n", ct);
            }

            return new EmptyResult();
        }

        /// <summary>
        /// 注入场景化系统提示
        /// </summary>
        private void InjectSystemPrompt(List<ChatMessage> messages)
        {
            if (messages.Any(m => m.Role == ChatRole.System)) return;

            var activeVault = _vaultSettings.GetActiveVault();
            var industry = activeVault?.Industry ?? "";
            var template = _scenePromptService.GetTemplateByName(industry);
            var systemPrompt = template.ChatSystemPrompt;

            if (activeVault != null)
            {
                systemPrompt += $"\n\n知识库：{activeVault.Name}。回答问题时请结合知识库内容。";
            }

            messages.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));
        }

        private static string GetDefaultModel(AiProviderConfig provider)
        {
            // 从 Models 列表找 IsMain
            var mainModel = provider.Models.FirstOrDefault(m => m.IsMain);
            if (mainModel != null) return mainModel.Name;

            return provider.Models.FirstOrDefault()?.Name ?? "default";
        }

        /// <summary>
        /// 处理工具调用：在系统提示中注入工具描述，解析模型输出的工具调用，执行后重新生成
        /// </summary>
        private async Task<OpenAiChatResponse?> HandleToolCallingAsync(
            AiProviderConfig provider, string model,
            List<ChatMessage> messages, ChatOptions options,
            List<OpenAiTool> tools, CancellationToken ct)
        {
            try
            {
                // 在系统提示中注入工具描述
                var toolDescriptions = string.Join("\n", tools.Select(t =>
                    $"- {t.Function.Name}: {t.Function.Description}"));

                var systemIndex = messages.FindIndex(m => m.Role == ChatRole.System);
                if (systemIndex >= 0)
                {
                    var original = messages[systemIndex].Text;
                    messages[systemIndex] = new ChatMessage(ChatRole.System,
                        original + $"\n\n你有以下工具可用：\n{toolDescriptions}\n\n" +
                        "如果需要调用工具，请在回复开头使用以下格式（严格 JSON）：\n" +
                        "TOOL_CALL: {\"tool\":\"工具名\",\"arguments\":{参数对象}}\n" +
                        "然后在新的一行给出你的正常回复。");
                }

                // 第一次调用：让模型决定是否需要工具
                var firstResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                    provider, model, messages, options, ct);

                var text = firstResponse.Text ?? "";

                // 解析工具调用
                var toolCallMatch = System.Text.RegularExpressions.Regex.Match(text,
                    @"TOOL_CALL:\s*(\{.*\})\s*", System.Text.RegularExpressions.RegexOptions.Singleline);

                if (!toolCallMatch.Success)
                {
                    // 没有工具调用，返回正常回复
                    return null;
                }

                var toolCallJson = toolCallMatch.Groups[1].Value;
                var remainingText = text.Substring(toolCallMatch.Index + toolCallMatch.Length).Trim();

                using var doc = System.Text.Json.JsonDocument.Parse(toolCallJson);
                var root = doc.RootElement;
                var toolName = root.GetProperty("tool").GetString() ?? "";
                var arguments = root.GetProperty("arguments");

                _logger.LogInformation("模型请求调用工具: {ToolName}", toolName);

                // 执行 MCP 工具
                var toolResult = await _mcpServerService.CallToolAsync(
                    new TaskRunner.Contracts.Mcp.McpToolCallRequest
                    {
                        Name = toolName,
                        Arguments = arguments
                    }, ct);

                var resultText = toolResult.Content?.ToString() ?? "工具执行完成，无返回内容";

                // 将工具结果注入消息列表
                messages.Add(new ChatMessage(ChatRole.Assistant,
                    $"我将调用工具 {toolName} 来帮助你。"));
                messages.Add(new ChatMessage(ChatRole.Tool, resultText));

                // 第二次调用：生成最终回复
                var finalResponse = await _aiClientService.GetChatResponseWithAutoStartAsync(
                    provider, model, messages, options, ct);

                var finalText = finalResponse.Text ?? "";
                if (!string.IsNullOrWhiteSpace(remainingText) && string.IsNullOrWhiteSpace(finalText))
                {
                    finalText = remainingText;
                }

                return new OpenAiChatResponse
                {
                    Id = $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = $"{provider.Id}/{model}",
                    Choices =
                    [
                        new OpenAiChoice
                        {
                            Index = 0,
                            Message = new OpenAiMessage
                            {
                                Role = "assistant",
                                Content = finalText
                            },
                            FinishReason = "stop"
                        }
                    ],
                    Usage = new OpenAiUsage
                    {
                        PromptTokens = (int)(firstResponse.Usage?.InputTokenCount ?? 0 + finalResponse.Usage?.InputTokenCount ?? 0),
                        CompletionTokens = (int)(firstResponse.Usage?.OutputTokenCount ?? 0 + finalResponse.Usage?.OutputTokenCount ?? 0),
                        TotalTokens = (int)((firstResponse.Usage?.TotalTokenCount ?? 0) + (finalResponse.Usage?.TotalTokenCount ?? 0))
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工具调用处理失败");
                return null;
            }
        }

        private static (string ProviderId, string ModelId) ParseModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return ("", "");

            // 支持格式: "provider/model" 或 "model"
            if (model.Contains('/'))
            {
                var parts = model.Split('/', 2);
                return (parts[0], parts[1]);
            }

            // 也支持 openclaw 的格式: "ollama/biancang:latest"
            return ("", model);
        }

        private static bool IsLocalProvider(AiProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(provider?.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        private static ChatRole ParseRole(string? role)
        {
            return role?.ToLowerInvariant() switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                "user" => ChatRole.User,
                "tool" => ChatRole.Tool,
                _ => ChatRole.User
            };
        }
    }

    #region OpenAI 兼容 DTO

    public class OpenAiChatRequest
    {
        public string? Model { get; set; }
        public List<OpenAiMessage> Messages { get; set; } = new();
        public bool? Stream { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTokens { get; set; }
        public List<OpenAiTool>? Tools { get; set; }
    }

    public class OpenAiMessage
    {
        public string Role { get; set; } = "user";
        public string? Content { get; set; }
        public List<OpenAiToolCall>? ToolCalls { get; set; }
        public string? ToolCallId { get; set; }
    }

    public class OpenAiTool
    {
        public string Type { get; set; } = "function";
        public OpenAiFunction Function { get; set; } = new();
    }

    public class OpenAiFunction
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public Dictionary<string, object>? Parameters { get; set; }
    }

    public class OpenAiToolCall
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "function";
        public OpenAiToolCallFunction Function { get; set; } = new();
    }

    public class OpenAiToolCallFunction
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    public class OpenAiChatResponse
    {
        public string Id { get; set; } = "";
        public string Object { get; set; } = "chat.completion";
        public long Created { get; set; }
        public string Model { get; set; } = "";
        public List<OpenAiChoice> Choices { get; set; } = new();
        public OpenAiUsage Usage { get; set; } = new();
    }

    public class OpenAiChoice
    {
        public int Index { get; set; }
        public OpenAiMessage Message { get; set; } = new();
        public string? FinishReason { get; set; }
    }

    public class OpenAiUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    public class OpenAiChatStreamChunk
    {
        public string Id { get; set; } = "";
        public string Object { get; set; } = "chat.completion.chunk";
        public long Created { get; set; }
        public string Model { get; set; } = "";
        public List<OpenAiStreamChoice> Choices { get; set; } = new();
    }

    public class OpenAiStreamChoice
    {
        public int Index { get; set; }
        public OpenAiDelta Delta { get; set; } = new();
        public string? FinishReason { get; set; }
    }

    public class OpenAiDelta
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    public class OpenAiModel
    {
        public string Id { get; set; } = "";
        public string Object { get; set; } = "model";
        public long Created { get; set; }
        public string OwnedBy { get; set; } = "";
    }

    #endregion
}
