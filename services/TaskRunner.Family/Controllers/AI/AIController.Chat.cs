using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using TaskRunner.Contracts.Ai;
using TaskRunner.Models;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    public partial class AIController
    {
        [HttpPost("chat")]
        public async Task<ActionResult<TaskRunner.Contracts.Ai.ChatResponse>> Chat([FromBody] ChatRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { error = "消息不能为空" });
            }

            try
            {
                _logger.LogInformation("收到 AI 聊天：{Message}", request.Message);

                var (provider, model) = ResolveProviderAndModel(request.ProviderId, request.Model);

                // 构建消息列表（使用三层记忆系统）
                var messages = await BuildMessagesWithMemoryAsync(
                    request.History, provider.Id, model, request.Message, request.SessionId, HttpContext.RequestAborted);
                // RAG 增强
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await CallAiApiAsync(messages, model, provider.Id, enableTools: request.EnableTools ?? true, ct: HttpContext.RequestAborted);
                stopwatch.Stop();

                var sourceInfo = $"> 📌 **来源**: AI 对话  \n" +
                    $"> 🤖 **模型**: {model}  \n" +
                    $"> 🏢 **提供商**: {provider.Name}  \n" +
                    $"> ⏰ **时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  \n" +
                    $"> ⏱️ **耗时**: {stopwatch.ElapsedMilliseconds}ms  \n\n";

                return Ok(new TaskRunner.Contracts.Ai.ChatResponse
                {
                    Success = true,
                    Message = "回复成功",
                    Reply = sourceInfo + result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 聊天失败");
                return Ok(new TaskRunner.Contracts.Ai.ChatResponse
                {
                    Success = false,
                    Message = $"聊天失败：{ex.Message}"
                });
            }
        }

        /// <summary>
        /// 流式 AI 对话（SSE）
        /// </summary>
        [HttpPost("chat/stream")]
        public async Task ChatStream([FromBody] ChatRequest request)
        {
            var httpResponse = HttpContext.Response;
            httpResponse.ContentType = "text/event-stream";
            httpResponse.Headers["Cache-Control"] = "no-cache";
            httpResponse.Headers["X-Accel-Buffering"] = "no";

            async Task SendSse(string eventType, string data)
            {
                await httpResponse.WriteAsync($"event: {eventType}\ndata: {data}\n\n");
                await httpResponse.Body.FlushAsync();
            }

            AiProviderConfig provider = null!;
            string model = "";

            try
            {
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    await SendSse("error", "消息不能为空");
                    return;
                }

                (provider, model) = ResolveProviderAndModel(request.ProviderId, request.Model);

                // 对于本地 provider，先尝试确保服务已运行
                var ready = await _aiClientService.EnsureProviderReadyAsync(provider);
                if (!ready && IsLocalProvider(provider))
                {
                    await SendSse("error", $"本地 AI 服务 {provider.Name} 未运行且自动启动失败，请手动启动后重试。");
                    return;
                }

                // 发送元信息
                await SendSse("meta", System.Text.Json.JsonSerializer.Serialize(new { provider = provider.Name, model }));

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, timeoutCts.Token);

                // 构建消息列表（使用三层记忆系统：Token预算 + 摘要压缩 + 语义检索）
                var messages = await BuildMessagesWithMemoryAsync(
                    request.History, provider.Id, model, request.Message, request.SessionId, linkedCts.Token);

                // RAG 增强（流式模式下发送检索提示）
                var (enrichedMessages, wasEnriched) = await _ragService.TryEnrichForStreamingAsync(messages, linkedCts.Token);
                if (wasEnriched)
                {
                    await SendSse("delta", System.Text.Json.JsonSerializer.Serialize(new { content = "🔍 正在检索知识库...\n\n" }));
                }
                messages = enrichedMessages;

                var options = Services.AiClientService.BuildChatOptions();
                var enableTools = request.EnableTools ?? true;

                // 收集完整 AI 回复用于记忆存储
                var fullResponse = new System.Text.StringBuilder();

                if (enableTools)
                {
                    var tools = _aiFunctionService.GetAllTools();
                    await foreach (var update in _aiClientService.GetStreamingResponseWithToolsAsync(
                        provider, model, messages, options, tools,
                        onToolExecuting: fc =>
                        {
                            // 发送 tool_call 事件通知前端
                            _ = SendSse("tool_call", System.Text.Json.JsonSerializer.Serialize(new
                            {
                                name = fc.Name,
                                arguments = fc.Arguments
                            }));
                        },
                        linkedCts.Token))
                    {
                        var text = update.Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            fullResponse.Append(text);
                            await SendSse("delta", System.Text.Json.JsonSerializer.Serialize(new { content = text }));
                        }
                    }
                }
                else
                {
                    var client = _aiClientService.CreateChatClient(provider, model);
                    await foreach (var update in client.GetStreamingResponseAsync(messages, options, linkedCts.Token))
                    {
                        var text = update.Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            fullResponse.Append(text);
                            await SendSse("delta", System.Text.Json.JsonSerializer.Serialize(new { content = text }));
                        }
                    }
                }

                await SendSse("done", "");

                // 异步存储本轮对话到记忆系统（不阻塞响应）
                if (!string.IsNullOrEmpty(request.SessionId))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var round = (request.History?.Count ?? 0) / 2 + 1;
                            await _chatMemoryService.StoreMemoryAsync(
                                request.SessionId, round, request.Message,
                                fullResponse.ToString(), CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "存储对话记忆失败（不影响主流程）");
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                await SendSse("error", "AI 调用超时或已被取消");

                // 如果是本地 provider，主动卸载模型以释放 GPU/CPU 资源
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
                _logger.LogError(ex, "AI 流式聊天失败");
                await SendSse("error", $"聊天失败：{ex.Message}");
            }
        }
    }



    public class AskRequest
        {
            public string Query { get; set; } = string.Empty;
            public bool SaveToVault { get; set; } = true;
            public string? VaultPath { get; set; }
            public string? VaultId { get; set; }
            public string? ProviderId { get; set; }
            public string? Model { get; set; }
            /// <summary>
            /// 是否启用 Function Calling（工具调用）。默认 false（生成笔记场景通常不需要工具）
            /// </summary>
            public bool? EnableTools { get; set; }
        }

    public class GenerateMissingNoteRequest
    {
        /// <summary>
        /// 缺失笔记的链接路径（如 "桂枝汤" 或 "方剂/桂枝汤"）
        /// </summary>
        public string LinkPath { get; set; } = string.Empty;

        public string? VaultId { get; set; }
        public string? ProviderId { get; set; }
        public string? Model { get; set; }
        /// <summary>
        /// 是否启用 Function Calling（工具调用）。默认 false
        /// </summary>
        public bool? EnableTools { get; set; }
    }
}
