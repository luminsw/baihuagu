using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using TaskRunner.Controllers;
using TaskRunner.Data;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services;

/// <summary>
/// 对话记忆服务：三层记忆系统
/// 1. Token 预算截断：根据模型上下文窗口动态截断历史
/// 2. 摘要压缩：将早期对话压缩为摘要，保留语义
/// 3. 语义检索：通过向量检索与当前问题相关的历史记忆
/// </summary>
public class ChatMemoryService
{
    private readonly AiClientService _aiClientService;
    private readonly EmbeddingService _embeddingService;
    private readonly DefaultPromptProvider _scenePromptService;
    private readonly IDbContextFactory<AIDbContext> _dbFactory;
    private readonly ILogger<ChatMemoryService> _logger;

    // 触发摘要压缩的阈值：超过此轮数时，将早期对话压缩为摘要
    private const int SummaryThreshold = 10;

    // 摘要后保留的最近完整对话轮数
    private const int RecentRoundsToKeep = 5;

    // 语义检索返回的最大记忆条数
    private const int MaxRetrievedMemories = 3;

    // 每个会话最多保留的记忆条数
    private const int MaxMemoryEntriesPerSession = 200;

    public ChatMemoryService(
        AiClientService aiClientService,
        EmbeddingService embeddingService,
        DefaultPromptProvider scenePromptService,
        IDbContextFactory<AIDbContext> dbFactory,
        ILogger<ChatMemoryService> logger)
    {
        _aiClientService = aiClientService;
        _embeddingService = embeddingService;
        _scenePromptService = scenePromptService;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    #region Layer 1: Token 预算截断

    /// <summary>
    /// 根据模型名称估算上下文窗口大小（token 数）
    /// </summary>
    public int GetContextWindowTokens(string model)
    {
        if (string.IsNullOrEmpty(model))
            return 8_000;

        var m = model.ToLowerInvariant();

        // 常见模型的上下文窗口
        if (m.Contains("128k")) return 128_000;
        if (m.Contains("32k")) return 32_000;
        if (m.Contains("gpt-4o") || m.Contains("gpt4o")) return 128_000;
        if (m.Contains("gpt-4") || m.Contains("gpt4")) return 8_192;
        if (m.Contains("claude-3.5") || m.Contains("claude3.5")) return 200_000;
        if (m.Contains("claude-3")) return 200_000;
        if (m.Contains("qwen2.5-72b") || m.Contains("qwen2_5-72b")) return 32_768;
        if (m.Contains("qwen2.5-14b") || m.Contains("qwen2_5-14b")) return 32_768;
        if (m.Contains("qwen2.5-7b") || m.Contains("qwen2_5-7b")) return 32_768;
        if (m.Contains("deepseek-r1") || m.Contains("deepseek_v3")) return 128_000;
        if (m.Contains("deepseek")) return 64_000;
        if (m.Contains("llama3.1") || m.Contains("llama3_1")) return 128_000;
        if (m.Contains("llama3")) return 8_192;
        if (m.Contains("glm-4")) return 128_000;

        // 默认保守值
        return 8_000;
    }

    /// <summary>
    /// 粗估文本的 token 数
    /// 中文约 1.5 字/token，英文约 4 字符/token，混合取中间值
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // 统计中文字符数
        int cjkCount = 0;
        int totalChars = 0;
        foreach (char c in text)
        {
            if (c > 0x4E00 && c < 0x9FFF) // CJK Unified Ideographs
                cjkCount++;
            totalChars++;
        }

        // 中文: ~1.5 字/token, 非中文: ~4 字符/token
        var cjkTokens = cjkCount / 1.5;
        var otherTokens = (totalChars - cjkCount) / 4.0;
        return (int)Math.Ceiling(cjkTokens + otherTokens);
    }

    /// <summary>
    /// 按 Token 预算截断历史消息（从最新往前保留）
    /// </summary>
    public List<ChatHistoryItem> TrimByTokenBudget(List<ChatHistoryItem> history, string model, int reserveForOutput = 2000, int reserveForSystem = 1000)
    {
        if (history == null || history.Count == 0)
            return history;

        var budget = GetContextWindowTokens(model) - reserveForOutput - reserveForSystem;
        if (budget < 1000) budget = 1000; // 最低保底

        var result = new List<ChatHistoryItem>();
        var usedTokens = 0;

        // 从最新消息往前加
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var cost = EstimateTokens(history[i].Content);
            if (usedTokens + cost > budget)
                break;
            usedTokens += cost;
            result.Insert(0, history[i]);
        }

        _logger.LogDebug("Token 预算截断：模型 {Model} 预算 {Budget}，保留 {Kept}/{Total} 条历史，使用 {Used} tokens",
            model, budget, result.Count, history.Count, usedTokens);

        return result;
    }

    #endregion

    #region Layer 2: 摘要压缩

    /// <summary>
    /// 对历史消息进行摘要压缩：超过阈值时，将早期对话压缩为摘要
    /// </summary>
    public async Task<MemoryContext> BuildMemoryContextAsync(
        List<ChatHistoryItem> history,
        string model,
        string providerId,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var result = new MemoryContext();

        if (history == null || history.Count == 0)
            return result;

        // 先按 Token 预算截断
        var trimmed = TrimByTokenBudget(history, model);

        // 判断是否需要摘要压缩
        var roundCount = trimmed.Count / 2; // 粗估轮数
        if (roundCount <= SummaryThreshold)
        {
            // 不需要压缩，直接返回截断后的历史
            result.RecentHistory = trimmed;
            return result;
        }

        // 分割：早期部分压缩为摘要，最近部分保留完整
        var splitIndex = Math.Max(0, trimmed.Count - RecentRoundsToKeep * 2);
        var earlyHistory = trimmed.Take(splitIndex).ToList();
        var recentHistory = trimmed.Skip(splitIndex).ToList();

        // 获取或生成早期对话的摘要
        result.Summary = await GetOrCreateSummaryAsync(earlyHistory, model, providerId, sessionId, ct);
        result.RecentHistory = recentHistory;

        _logger.LogInformation("记忆压缩：{EarlyCount} 条早期对话压缩为摘要，保留 {RecentCount} 条最近对话",
            earlyHistory.Count, recentHistory.Count);

        return result;
    }

    /// <summary>
    /// 获取或创建早期对话的摘要
    /// </summary>
    private async Task<string?> GetOrCreateSummaryAsync(
        List<ChatHistoryItem> earlyHistory,
        string model,
        string providerId,
        string? sessionId,
        CancellationToken ct)
    {
        if (earlyHistory.Count == 0)
            return null;

        // 尝试从数据库读取已缓存的摘要
        if (!string.IsNullOrEmpty(sessionId))
        {
            var cached = await GetCachedSummaryAsync(sessionId);
            if (cached != null)
            {
                _logger.LogDebug("使用缓存的对话摘要（会话 {SessionId}）", sessionId);
                return cached;
            }
        }

        // 调用 AI 生成摘要
        try
        {
            var summary = await SummarizeHistoryAsync(earlyHistory, model, providerId, ct);

            // 缓存摘要到数据库
            if (!string.IsNullOrEmpty(sessionId) && summary != null)
            {
                await CacheSummaryAsync(sessionId, summary);
            }

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成对话摘要失败，回退到直接使用早期历史");
            return null;
        }
    }

    /// <summary>
    /// 调用 AI 将早期对话压缩为摘要
    /// </summary>
    private async Task<string?> SummarizeHistoryAsync(
        List<ChatHistoryItem> earlyHistory,
        string model,
        string providerId,
        CancellationToken ct)
    {
        var historyText = string.Join("\n", earlyHistory.Select(h =>
            $"{(h.Role == "user" ? "用户" : "AI")}: {TruncateForSummary(h.Content, 500)}"));

        var prompt = $"请将以下对话历史压缩为一段简洁的摘要（200字以内），保留关键事实、决策和结论，省略寒暄和重复：\n\n{historyText}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是一个对话摘要助手，输出简洁准确的中文摘要。"),
            new(ChatRole.User, prompt)
        };

        var chatClient = _aiClientService.CreateChatClient(providerId, model);
        var options = AiClientService.BuildChatOptions(temperature: 0.3f, maxOutputTokens: 500);

        var response = await chatClient.GetResponseAsync(messages, options, ct);
        return response.Text;
    }

    private static string TruncateForSummary(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;
        return text.Substring(0, maxChars) + "...";
    }

    private async Task<string?> GetCachedSummaryAsync(string sessionId)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            // 查找该会话最新的摘要（Round=0 表示摘要）
            var entry = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round == 0)
                .OrderByDescending(e => e.CreatedAt)
                .FirstOrDefaultAsync();
            return entry?.UserSummary;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取缓存摘要失败");
            return null;
        }
    }

    private async Task CacheSummaryAsync(string sessionId, string summary)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            // 删除旧摘要
            var old = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round == 0)
                .ToListAsync();
            if (old.Count > 0)
                db.Set<ChatMemoryEntry>().RemoveRange(old);

            db.Set<ChatMemoryEntry>().Add(new ChatMemoryEntry
            {
                SessionId = sessionId,
                Round = 0,
                UserSummary = summary,
                AssistantSummary = "",
                UserContent = "",
                AssistantContent = "",
                EstimatedTokens = EstimateTokens(summary),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "缓存摘要失败");
        }
    }

    #endregion

    #region Layer 3: 语义检索记忆

    /// <summary>
    /// 存储一轮对话到记忆库（含向量索引）
    /// </summary>
    public async Task StoreMemoryAsync(
        string sessionId,
        int round,
        string userContent,
        string assistantContent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        try
        {
            var userSummary = TruncateForSummary(userContent, 200);
            var assistantSummary = TruncateForSummary(assistantContent, 200);
            var totalTokens = EstimateTokens(userContent) + EstimateTokens(assistantContent);

            // 生成向量（用于语义检索）
            string? vectorJson = null;
            int dimensions = 0;
            var textToEmbed = $"用户: {userSummary}\nAI: {assistantSummary}";
            var embedding = await _embeddingService.GetEmbeddingAsync(textToEmbed);
            if (embedding != null)
            {
                vectorJson = JsonSerializer.Serialize(embedding);
                dimensions = embedding.Count;
            }

            using var db = await _dbFactory.CreateDbContextAsync();

            // 清理该会话过旧的记忆
            var existing = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round > 0)
                .OrderByDescending(e => e.Round)
                .ToListAsync();

            if (existing.Count >= MaxMemoryEntriesPerSession)
            {
                var toRemove = existing.Skip(MaxMemoryEntriesPerSession).ToList();
                db.Set<ChatMemoryEntry>().RemoveRange(toRemove);
            }

            db.Set<ChatMemoryEntry>().Add(new ChatMemoryEntry
            {
                SessionId = sessionId,
                Round = round,
                UserSummary = userSummary,
                AssistantSummary = assistantSummary,
                UserContent = userContent,
                AssistantContent = assistantContent,
                VectorJson = vectorJson,
                Dimensions = dimensions,
                EstimatedTokens = totalTokens,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
            _logger.LogDebug("存储对话记忆：会话 {SessionId}，轮次 {Round}", sessionId, round);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "存储对话记忆失败（不影响主流程）");
        }
    }

    /// <summary>
    /// 语义检索与当前问题相关的历史记忆
    /// </summary>
    public async Task<List<RetrievedMemory>> RetrieveRelevantMemoriesAsync(
        string sessionId,
        string query,
        int topK = MaxRetrievedMemories,
        CancellationToken ct = default)
    {
        var result = new List<RetrievedMemory>();

        if (string.IsNullOrEmpty(sessionId) || !_embeddingService.IsSemanticSearchEnabled())
            return result;

        try
        {
            var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);
            if (queryEmbedding == null)
                return result;

            using var db = await _dbFactory.CreateDbContextAsync();
            var memories = await db.Set<ChatMemoryEntry>()
                .Where(e => e.SessionId == sessionId && e.Round > 0 && e.VectorJson != null)
                .OrderByDescending(e => e.Round)
                .Take(50) // 只对最近 50 条做相似度计算
                .ToListAsync();

            var scored = new List<(ChatMemoryEntry entry, double score)>();
            foreach (var mem in memories)
            {
                try
                {
                    var vector = JsonSerializer.Deserialize<List<double>>(mem.VectorJson!);
                    if (vector != null && vector.Count > 0)
                    {
                        var similarity = CosineSimilarity(queryEmbedding, vector);
                        scored.Add((mem, similarity));
                    }
                }
                catch { /* 跳过无效向量 */ }
            }

            // 取相似度最高的 topK 条
            foreach (var (entry, score) in scored.OrderByDescending(x => x.score).Take(topK))
            {
                if (score < 0.5) continue; // 相似度阈值
                result.Add(new RetrievedMemory
                {
                    UserContent = entry.UserContent,
                    AssistantContent = entry.AssistantContent,
                    Similarity = score,
                    Round = entry.Round
                });
            }

            _logger.LogDebug("语义检索记忆：查询 '{Query}'，找到 {Count} 条相关记忆", 
                TruncateForSummary(query, 50), result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "语义检索记忆失败");
        }

        return result;
    }

    private static double CosineSimilarity(List<double> a, List<double> b)
    {
        if (a.Count != b.Count || a.Count == 0)
            return 0;

        double dotProduct = 0, magnitudeA = 0, magnitudeB = 0;
        for (int i = 0; i < a.Count; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    #endregion

    /// <summary>
    /// 构建最终发送给 AI 的消息列表（整合三层记忆）
    /// </summary>
    public async Task<List<ChatMessage>> BuildMessagesWithMemoryAsync(
        List<ChatHistoryItem>? history,
        string providerId,
        string model,
        string currentMessage,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>();

        // System Prompt
        var template = _scenePromptService.GetTemplate();
        messages.Add(new ChatMessage(ChatRole.System, template.ChatSystemPrompt));

        if (history == null || history.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.User, currentMessage));
            return messages;
        }

        // Layer 2: 摘要压缩 + Layer 1: Token 预算截断
        var memoryContext = await BuildMemoryContextAsync(history, model, providerId, sessionId, ct);

        // 注入摘要（如果有）
        if (!string.IsNullOrEmpty(memoryContext.Summary))
        {
            messages.Add(new ChatMessage(ChatRole.System,
                $"【对话历史摘要】\n{memoryContext.Summary}"));
        }

        // Layer 3: 语义检索相关记忆
        if (!string.IsNullOrEmpty(sessionId) && _embeddingService.IsSemanticSearchEnabled())
        {
            var relevantMemories = await RetrieveRelevantMemoriesAsync(sessionId, currentMessage, ct: ct);
            if (relevantMemories.Count > 0)
            {
                var memoryText = string.Join("\n---\n", relevantMemories.Select(m =>
                    $"用户: {TruncateForSummary(m.UserContent, 300)}\nAI: {TruncateForSummary(m.AssistantContent, 300)}"));
                messages.Add(new ChatMessage(ChatRole.System,
                    $"【相关历史记忆】\n{memoryText}"));
            }
        }

        // 注入最近完整对话
        foreach (var item in memoryContext.RecentHistory)
        {
            var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            messages.Add(new ChatMessage(role, item.Content));
        }

        // 当前用户消息
        messages.Add(new ChatMessage(ChatRole.User, currentMessage));

        return messages;
    }
}

/// <summary>
/// 记忆上下文：摘要 + 最近完整对话
/// </summary>
public class MemoryContext
{
    /// <summary>
    /// 早期对话的压缩摘要
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// 最近的完整对话历史（已按 Token 预算截断）
    /// </summary>
    public List<ChatHistoryItem> RecentHistory { get; set; } = new();
}

/// <summary>
/// 语义检索到的记忆条目
/// </summary>
public class RetrievedMemory
{
    public string UserContent { get; set; } = "";
    public string AssistantContent { get; set; } = "";
    public double Similarity { get; set; }
    public int Round { get; set; }
}
