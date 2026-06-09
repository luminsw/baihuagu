using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using OpenAI;
using System.ClientModel;
using TaskRunner.Data;
using TaskRunner.Data.Entities;
using TaskRunner.Models;

namespace TaskRunner.Services
{
    /// <summary>
    /// 统一的 AI 客户端服务：基于 Microsoft.Extensions.AI 抽象层，
    /// 为任意 OpenAI 兼容提供商创建 IChatClient 和 IEmbeddingGenerator。
    /// </summary>
    public class AiClientService
    {
        private readonly SettingsService _settings;
        private readonly LocalAiAutoStarter _autoStarter;
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly AiMetricsService _metrics;
        private readonly IDistributedCache _cache;
        private readonly AnthropicAiClient _anthropicClient;
        private readonly ILogger<AiClientService> _logger;

        public AiClientService(
            SettingsService settings,
            LocalAiAutoStarter autoStarter,
            IDbContextFactory<AppDbContext> dbFactory,
            AiMetricsService metrics,
            IDistributedCache cache,
            AnthropicAiClient anthropicClient,
            ILogger<AiClientService> logger)
        {
            _settings = settings;
            _autoStarter = autoStarter;
            _dbFactory = dbFactory;
            _metrics = metrics;
            _cache = cache;
            _anthropicClient = anthropicClient;
            _logger = logger;
        }

        /// <summary>
        /// 为指定提供商+模型创建 IChatClient
        /// </summary>
        public IChatClient CreateChatClient(string providerId, string model)
        {
            return CreateChatClient(providerId, model, tools: null);
        }

        /// <summary>
        /// 为指定提供商+模型创建 IChatClient（支持 Function Calling）
        /// </summary>
        public IChatClient CreateChatClient(string providerId, string model, IList<AITool>? tools)
        {
            var provider = _settings.GetAiProvider(providerId)
                ?? throw new Exception($"未找到 AI 提供商：{providerId}");

            var apiKey = _settings.GetAiApiKey(providerId);
            if (string.IsNullOrWhiteSpace(apiKey))
                _logger.LogWarning("提供商 {ProviderId} 未配置 API Key，将以无鉴权方式请求", providerId);

            var endpoint = new Uri(provider.AiBaseUrl.TrimEnd('/') );
            var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };

            // 无 API Key 时使用占位符（Ollama/LMStudio 等本地服务不需要 Key）
            var credential = string.IsNullOrWhiteSpace(apiKey)
                ? new ApiKeyCredential("placeholder")
                : new ApiKeyCredential(apiKey);

            var openaiClient = new OpenAIClient(credential, clientOptions);
            var client = openaiClient.GetChatClient(model).AsIChatClient();
            return client;
        }

        /// <summary>
        /// 为指定提供商+模型创建 IChatClient（通过 AiProviderConfig 对象）
        /// </summary>
        public IChatClient CreateChatClient(AiProviderConfig provider, string model)
        {
            return CreateChatClient(provider.Id, model);
        }

        /// <summary>
        /// 为指定提供商+模型创建 IChatClient（通过 AiProviderConfig 对象，支持 Function Calling）
        /// </summary>
        public IChatClient CreateChatClient(AiProviderConfig provider, string model, IList<AITool>? tools)
        {
            return CreateChatClient(provider.Id, model, tools);
        }

        /// <summary>
        /// 创建带分布式缓存的 IChatClient（用于非流式请求）
        /// </summary>
        private IChatClient CreateChatClientWithCache(string providerId, string model)
        {
            var client = CreateChatClient(providerId, model);
            return client.AsBuilder()
                .UseDistributedCache(_cache)
                .Build();
        }

        /// <summary>
        /// 创建带分布式缓存的 IChatClient（用于非流式请求）
        /// </summary>
        private IChatClient CreateChatClientWithCache(AiProviderConfig provider, string model)
        {
            return CreateChatClientWithCache(provider.Id, model);
        }

        /// <summary>
        /// 创建 Embedding 生成器（回退到旧配置）
        /// </summary>
        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
        {
            var url = _settings.SemanticEmbeddingUrl;
            var model = _settings.SemanticEmbeddingModel;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
                throw new Exception("未配置 Embedding URL 或模型");

            // Embedding 可能使用与 Chat 不同的提供商
            // 尝试从 AI 提供商中匹配 Embedding URL
            var providers = _settings.GetAiProviders();
            var matchedProvider = providers.FirstOrDefault(p =>
                p.AiBaseUrl.TrimEnd('/').Equals(url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

            string apiKey;
            if (matchedProvider != null)
            {
                apiKey = _settings.GetAiApiKey(matchedProvider.Id);
            }
            else
            {
                // 使用主提供商的 API Key
                apiKey = _settings.AiApiKey;
            }

            return CreateEmbeddingGenerator(url, model, apiKey);
        }

        /// <summary>
        /// 为指定 URL+模型+Key 创建 Embedding 生成器
        /// </summary>
        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string url, string model, string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(model))
                throw new Exception("未配置 Embedding URL 或模型");

            var endpoint = new Uri(url.TrimEnd('/'));
            var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };

            var credential = string.IsNullOrWhiteSpace(apiKey)
                ? new ApiKeyCredential("placeholder")
                : new ApiKeyCredential(apiKey);

            var openaiClient = new OpenAIClient(credential, clientOptions);
            return openaiClient.GetEmbeddingClient(model).AsIEmbeddingGenerator();
        }

        /// <summary>
        /// 如果 provider 是本地服务，尝试确保其已运行
        /// </summary>
        public async Task<bool> EnsureProviderReadyAsync(AiProviderConfig provider)
        {
            if (!IsLocalProvider(provider))
                return true;

            return await _autoStarter.TryEnsureRunningAsync(provider.Id, provider.AiBaseUrl);
        }

        /// <summary>
        /// 调用 GetResponseAsync，如果连接失败则尝试自动启动本地服务后重试一次
        /// </summary>
        /// <param name="provider">AI 提供商配置</param>
        /// <param name="model">模型名称</param>
        /// <param name="messages">聊天消息列表</param>
        /// <param name="options">聊天选项</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="operation">操作类型，用于指标统计：chat, benchmark, task, split, index, openclaw 等</param>
        public async Task<ChatResponse> GetChatResponseWithAutoStartAsync(
            AiProviderConfig provider, string model,
            IList<ChatMessage> messages, ChatOptions options, CancellationToken ct,
            string operation = "chat")
        {
            var sw = Stopwatch.StartNew();
            ChatResponse? response = null;
            try
            {
                var client = CreateChatClientWithCache(provider, model);
                try
                {
                    response = await client.GetResponseAsync(messages, options, ct);
                }
                catch (Exception ex) when (IsConnectionFailure(ex) && IsLocalProvider(provider))
                {
                    _logger.LogWarning("本地 AI 服务连接失败，尝试自动启动: {Provider}", provider.Id);
                    var started = await _autoStarter.TryEnsureRunningAsync(provider.Id, provider.AiBaseUrl);
                    if (!started)
                        throw new Exception($"本地 AI 服务 {provider.Name} 未运行且自动启动失败，请手动启动后重试。");

                    client = CreateChatClientWithCache(provider, model);
                    response = await client.GetResponseAsync(messages, options, ct);
                }

                // Anthropic fallback：若返回为空，尝试 Anthropic 协议
                if (IsEmptyResponse(response) && !string.IsNullOrWhiteSpace(provider.AnthropicBaseUrl))
                {
                    _logger.LogWarning("AI ({Provider}/{Model}) OpenAI 响应为空，尝试 Anthropic fallback", provider.Name, model);
                    var apiKey = _settings.GetAiApiKey(provider.Id);
                    response = await _anthropicClient.GetChatResponseAsync(
                        provider.AnthropicBaseUrl, apiKey, model, messages, options, ct);
                    _logger.LogInformation("AI ({Provider}/{Model}) Anthropic fallback 成功，内容长度={Length}",
                        provider.Name, model, response.Text?.Length ?? 0);
                }

                sw.Stop();
                await RecordMetricAsync(provider, model, operation, sw.ElapsedMilliseconds, response, true, null);
                return response;
            }
            catch (Exception ex)
            {
                sw.Stop();
                await RecordMetricAsync(provider, model, operation, sw.ElapsedMilliseconds, response, false, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 获取支持 Function Calling 的流式响应
        /// 先通过非流式请求检测 function call，执行后再流式输出最终回复
        /// </summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseWithToolsAsync(
            AiProviderConfig provider, string model,
            IList<ChatMessage> messages, ChatOptions options,
            IList<AITool> tools,
            Action<FunctionCallContent>? onToolExecuting = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var client = CreateChatClient(provider, model);
            var toolOptions = new ChatOptions
            {
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                TopP = options.TopP,
                Tools = tools
            };

            // 第一轮：非流式请求，检测 function call
            var response = await client.GetResponseAsync(messages, toolOptions, ct);

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            // Qwen 等模型在启用工具调用时可能返回空内容（不调用工具也不回复）
            // 自动回退：禁用 tools 后重试一次
            if (functionCalls.Count == 0 && string.IsNullOrWhiteSpace(response.Text))
            {
                _logger.LogWarning("AI ({Provider}/{Model}) 流式响应为空，尝试禁用工具调用后重试", provider.Name, model);
                var retryOptions = new ChatOptions
                {
                    Temperature = options.Temperature,
                    MaxOutputTokens = options.MaxOutputTokens,
                    TopP = options.TopP
                };
                response = await client.GetResponseAsync(messages, retryOptions, ct);
                functionCalls = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();
                _logger.LogInformation("AI ({Provider}/{Model}) 流式重试结果：HasContent={HasContent}, HasFunctionCall={HasFunctionCall}",
                    provider.Name, model, !string.IsNullOrWhiteSpace(response.Text), functionCalls.Count > 0);
            }

            if (functionCalls.Count > 0)
            {
                // 通知调用方有哪些 tool call
                foreach (var fc in functionCalls)
                {
                    onToolExecuting?.Invoke(fc);
                }

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
                            var result = await aiFunc.InvokeAsync(new AIFunctionArguments(fc.Arguments), ct);
                            messages.Add(new ChatMessage(ChatRole.Tool,
                                new[] { new FunctionResultContent(fc.CallId, result?.ToString() ?? "") }));
                        }
                        catch (Exception ex)
                        {
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

                // 重新获取流式响应（不带 tools，避免无限循环）
                var finalOptions = new ChatOptions
                {
                    Temperature = options.Temperature,
                    MaxOutputTokens = options.MaxOutputTokens,
                    TopP = options.TopP
                };

                await foreach (var update in client.GetStreamingResponseAsync(messages, finalOptions, ct))
                {
                    yield return update;
                }
            }
            else
            {
                // 没有 function call，将非流式响应模拟为流式输出
                var text = response.Text ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text);
                }
            }
        }

        private static bool IsEmptyResponse(ChatResponse? response)
        {
            if (response == null) return true;
            var text = response.Text ?? "";
            return string.IsNullOrWhiteSpace(text);
        }

        private static bool IsLocalProvider(AiProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(provider?.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        private static bool IsConnectionFailure(Exception ex)
        {
            var message = ex.Message + (ex.InnerException?.Message ?? "");
            return message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("积极拒绝", StringComparison.OrdinalIgnoreCase)
                || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("无法连接", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no connection could be made", StringComparison.OrdinalIgnoreCase)
                || ex.InnerException is System.Net.Sockets.SocketException;
        }

        /// <summary>
        /// 构建标准 ChatOptions
        /// </summary>
        public static ChatOptions BuildChatOptions(float temperature = 0.7f, int maxOutputTokens = 2000, float topP = 0.95f)
        {
            return new ChatOptions
            {
                Temperature = temperature,
                MaxOutputTokens = maxOutputTokens,
                TopP = topP,
            };
        }

        /// <summary>
        /// 记录 AI 调用指标到数据库
        /// </summary>
        private async Task RecordMetricAsync(
            AiProviderConfig provider, string model, string operation,
            long latencyMs, ChatResponse? response, bool isSuccess, string? errorMessage)
        {
            try
            {
                int? inputTokens = null;
                int? outputTokens = null;
                int? totalTokens = null;
                double? tps = null;

                if (response?.Usage is { } usage)
                {
                    inputTokens = (int?)usage.InputTokenCount;
                    outputTokens = (int?)usage.OutputTokenCount;
                    totalTokens = (int?)usage.TotalTokenCount;
                    if (latencyMs > 0 && outputTokens > 0)
                        tps = outputTokens.Value / (latencyMs / 1000.0);
                }

                // 1. 记录到 .NET Metrics（通过 OpenTelemetry OTLP 推送到 OpenObserve）
                _metrics.RecordAiRequest(
                    provider.Id, model, operation,
                    latencyMs, isSuccess,
                    inputTokens, outputTokens, tps);

                // 2. 记录到 SQLite（保留本地历史查询能力）
                using var db = await _dbFactory.CreateDbContextAsync();
                db.AiUsageMetrics.Add(new AiUsageMetric
                {
                    CalledAt = DateTime.UtcNow,
                    ProviderId = provider.Id,
                    ProviderName = provider.Name ?? provider.Id,
                    ModelId = model,
                    Operation = operation,
                    LatencyMs = latencyMs,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    TokensPerSecond = tps,
                    IsSuccess = isSuccess,
                    ErrorMessage = errorMessage,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "记录 AI 调用指标失败（不影响主流程）");
            }
        }
    }
}
