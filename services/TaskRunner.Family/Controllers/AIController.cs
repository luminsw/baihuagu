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
    public class AIController : ControllerBase
    {
        private readonly Services.SettingsService _settings;
        private readonly Services.AiClientService _aiClientService;
        private readonly Services.LocalModelDeploymentService _localDeployment;
        private readonly Services.RagService _ragService;
        private readonly DefaultPromptProvider _scenePromptService;
        private readonly Services.AiFunctionService _aiFunctionService;
        private readonly Services.ChatMemoryService _chatMemoryService;
        private readonly Services.AnkiCardGenerator _cardGenerator;
        private readonly Services.TaskManager _taskManager;
        private readonly ILogger<AIController> _logger;

        public AIController(
            Services.SettingsService settings,
            Services.AiClientService aiClientService,
            Services.LocalModelDeploymentService localDeployment,
            Services.RagService ragService,
            DefaultPromptProvider scenePromptService,
            Services.AiFunctionService aiFunctionService,
            Services.ChatMemoryService chatMemoryService,
            Services.AnkiCardGenerator cardGenerator,
            Services.TaskManager taskManager,
            ILogger<AIController> logger)
        {
            _settings = settings;
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
            var list = _settings.GetAiProviders()
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

        /// <summary>
        /// AI 问答 - 生成一篇笔记
        /// </summary>
        [HttpPost("ask")]
        public async Task<ActionResult<AiNoteResponse>> Ask([FromBody] AskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest(new { error = "问题不能为空" });
            }

            try
            {
                _logger.LogInformation("收到 AI 查询：{Query}", request.Query);

                var (provider, model) = ResolveProviderAndModel(request.ProviderId, request.Model);

                // RAG 增强：检索知识库上下文
                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, GetSystemPrompt(provider.Id)),
                    new(ChatRole.User, request.Query)
                };
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = await CallAiApiAsync(messages, model, provider.Id, enableTools: request.EnableTools ?? false);
                stopwatch.Stop();

                var note = ParseNote(result, request.Query);
                
                // 添加来源信息到笔记开头
                var sourceInfo = $"> 📌 **来源**: AI 生成  \n" +
                    $"> 🤖 **模型**: {model}  \n" +
                    $"> 🏢 **提供商**: {provider.Name}  \n" +
                    $"> ⏰ **时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  \n" +
                    $"> ⏱️ **耗时**: {stopwatch.ElapsedMilliseconds}ms  \n\n";
                
                note.Content = sourceInfo + note.Content;

                // 如果选择保存到知识库
                string? vaultPath = null;
                if (!string.IsNullOrWhiteSpace(request.VaultId))
                {
                    vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)?.Path;
                }
                if (string.IsNullOrWhiteSpace(vaultPath) && !string.IsNullOrWhiteSpace(request.VaultPath))
                {
                    vaultPath = request.VaultPath;
                }
                if (request.SaveToVault && !string.IsNullOrEmpty(vaultPath))
                {
                    await SaveNote(note, vaultPath);

                    // 自动为该笔记生成 Anki 记忆卡片
                    try
                    {
                        var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                        var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                        var vault = _settings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId);
                        var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                        {
                            ["notePath"] = note.FilePath,
                            ["vaultId"] = request.VaultId ?? "",
                            ["vaultName"] = vault?.Name ?? ""
                        });
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _taskManager.UpdateStatus(taskId, Services.TaskStatus.Running);
                                var result = await _cardGenerator.GenerateFromNote(note.FilePath, cardsPath: cardsRoot, notesBasePath: notesRoot);
                                await _taskManager.UpdateStatus(taskId, result.Success ? Services.TaskStatus.Success : Services.TaskStatus.Failed,
                                    data: new { message = result.Message, cardCount = result.CardCount });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[AI Ask] 卡片生成失败");
                                await _taskManager.UpdateStatus(taskId, Services.TaskStatus.Failed, error: ex.Message);
                            }
                        });
                        _logger.LogInformation("[AI Ask] 笔记已保存，已创建卡片生成任务 {TaskId}：{Path}", taskId, note.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AI Ask] 自动触发卡片生成失败");
                    }
                }

                return Ok(new AiNoteResponse
                {
                    Success = true,
                    Message = "生成成功",
                    Title = note.Title,
                    Content = note.Content,
                    NoteId = GenerateNoteId(note.Title)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 查询失败");
                return Ok(new AiNoteResponse
                {
                    Success = false,
                    Message = $"生成失败：{ex.Message}"
                });
            }
        }

        /// <summary>
        /// AI 生成缺失的 wikilink 目标笔记，并纠正引用该链接的其他笔记
        /// </summary>
        [HttpPost("generate-missing-note")]
        public async Task<ActionResult<GenerateMissingNoteResponse>> GenerateMissingNote([FromBody] GenerateMissingNoteRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.LinkPath))
                return BadRequest(new { error = "链接路径不能为空" });

            try
            {
                _logger.LogInformation("生成缺失笔记：{LinkPath}", request.LinkPath);

                var (provider, model) = ResolveProviderAndModel(request.ProviderId, request.Model);

                // 解析知识库
                var vault = !string.IsNullOrWhiteSpace(request.VaultId)
                    ? _settings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)
                    : _settings.GetActiveVault();
                var vaultPath = vault?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                    return BadRequest(new { error = "未找到有效的知识库" });

                var notesPath = System.IO.Path.Combine(vaultPath, "notes");

                // 提取链接中的标题（去掉分类路径部分）
                var linkTitle = System.IO.Path.GetFileName(request.LinkPath);

                // 1. AI 生成笔记内容
                var industry = vault?.Industry ?? "";
                var template = _scenePromptService.GetTemplateByName(industry);
                var systemPrompt = template.ChatSystemPrompt;
                var userPrompt = $"请生成一篇关于「{linkTitle}」的笔记，要求：\n" +
                    $"- 使用 Markdown 格式\n" +
                    $"- 内容准确、专业、有深度\n" +
                    $"- 如果是相关内容，请引用经典原文\n" +
                    $"- 使用 [[wikilink]] 链接到相关概念\n" +
                    $"- 在开头用一级标题标注笔记名称";

                var messages = new List<ChatMessage>
                {
                    new(ChatRole.System, systemPrompt),
                    new(ChatRole.User, userPrompt)
                };
                messages = await _ragService.EnrichMessagesWithVaultContextAsync(messages);

                var aiResult = await CallAiApiAsync(messages, model, provider.Id, enableTools: request.EnableTools ?? false);

                // 2. 解析 AI 返回，提取标题和内容
                var title = linkTitle;
                var firstLine = aiResult.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.TrimStart('#').Trim();
                if (!string.IsNullOrEmpty(firstLine))
                    title = firstLine;

                var content = $"> 📌 **来源**: AI 生成（补充缺失链接）  \n" +
                    $"> 🤖 **模型**: {model}  \n" +
                    $"> ⏰ **时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  \n\n" +
                    aiResult;

                // 3. 确定保存路径：如果链接已包含分类则直接用，否则搜索知识库推断分类
                var notePath = request.LinkPath;
                if (!request.LinkPath.Contains('/') || !System.IO.Directory.Exists(System.IO.Path.Combine(notesPath, System.IO.Path.GetDirectoryName(request.LinkPath)!)))
                {
                    // 链接没有分类或分类目录不存在，尝试推断
                    var inferredCategory = InferCategoryFromReferences(linkTitle, notesPath, template);
                    if (!string.IsNullOrEmpty(inferredCategory))
                        notePath = $"{inferredCategory}/{linkTitle}";
                }

                // 4. 保存笔记
                var fullPath = System.IO.Path.Combine(notesPath, notePath + ".md");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
                await System.IO.File.WriteAllTextAsync(fullPath, content);
                _logger.LogInformation("缺失笔记已生成并保存：{Path}", notePath);

                // 自动为该笔记生成 Anki 记忆卡片
                try
                {
                    var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                    var vaultForCard = _settings.GetVaults().FirstOrDefault(v => v.Path == vaultPath);
                    var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                    {
                        ["notePath"] = notePath,
                        ["vaultId"] = vaultForCard?.Id ?? "",
                        ["vaultName"] = vaultForCard?.Name ?? ""
                    });
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _taskManager.UpdateStatus(taskId, Services.TaskStatus.Running);
                            var result = await _cardGenerator.GenerateFromNote(notePath, cardsPath: cardsRoot, notesBasePath: notesPath);
                            await _taskManager.UpdateStatus(taskId, result.Success ? Services.TaskStatus.Success : Services.TaskStatus.Failed,
                                data: new { message = result.Message, cardCount = result.CardCount });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[GenerateMissingNote] 卡片生成失败");
                            await _taskManager.UpdateStatus(taskId, Services.TaskStatus.Failed, error: ex.Message);
                        }
                    });
                    _logger.LogInformation("[GenerateMissingNote] 笔记已保存，已创建卡片生成任务 {TaskId}：{Path}", taskId, notePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GenerateMissingNote] 自动触发卡片生成失败");
                }

                // 5. 纠正引用该链接的其他笔记（将无分类链接替换为有分类链接）
                var fixedLinks = FixWikiLinksInVault(linkTitle, notePath, notesPath);

                return Ok(new GenerateMissingNoteResponse
                {
                    Success = true,
                    Message = $"笔记已生成并保存到 {notePath}",
                    NotePath = notePath,
                    Title = title,
                    Content = content,
                    FixedLinks = fixedLinks
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成缺失笔记失败");
                return Ok(new GenerateMissingNoteResponse
                {
                    Success = false,
                    Message = $"生成失败：{ex.Message}"
                });
            }
        }

        /// <summary>
        /// 从知识库中引用该标题的其他笔记推断分类
        /// </summary>
        private string? InferCategoryFromReferences(string title, string notesPath, DefaultPromptProvider.PromptTemplate template)
        {
            // 搜索知识库中包含 [[title]] 链接的笔记，从它们的路径推断分类
            try
            {
                if (!System.IO.Directory.Exists(notesPath))
                    return null;

                var linkPattern1 = $"[[{title}]]";
                var linkPattern2 = $"[[{title}|";
                var categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in System.IO.Directory.EnumerateFiles(notesPath, "*.md", System.IO.SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(file);
                        if (content.Contains(linkPattern1, StringComparison.OrdinalIgnoreCase) ||
                            content.Contains(linkPattern2, StringComparison.OrdinalIgnoreCase))
                        {
                            // 从文件路径提取分类
                            var relPath = System.IO.Path.GetRelativePath(notesPath, file);
                            var dir = System.IO.Path.GetDirectoryName(relPath);
                            if (!string.IsNullOrEmpty(dir))
                            {
                                var topCategory = dir.Split('/')[0];
                                categories.TryGetValue(topCategory, out var count);
                                categories[topCategory] = count + 1;
                            }
                        }
                    }
                    catch { /* 跳过无法读取的文件 */ }
                }

                // 返回出现最多的分类
                if (categories.Count > 0)
                    return categories.OrderByDescending(c => c.Value).First().Key;

                // 如果没有引用，使用场景默认分类
                var defaultCategories = template.DefaultCategories;
                if (defaultCategories != null && defaultCategories.Count > 0)
                    return defaultCategories[0];
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "推断分类失败");
            }

            return null;
        }

        /// <summary>
        /// 纠正知识库中引用该标题的 wikilink：将 [[title]] 替换为 [[category/title]]
        /// </summary>
        private int FixWikiLinksInVault(string title, string correctPath, string notesPath)
        {
            var fixedCount = 0;
            try
            {
                if (!System.IO.Directory.Exists(notesPath))
                    return 0;

                // 只有当 correctPath 包含分类时才需要纠正
                if (!correctPath.Contains('/'))
                    return 0;

                var oldLink = $"[[{title}]]";
                var newLink = $"[[{correctPath}]]";
                // 也处理带 alias 的链接 [[title|alias]]
                var oldLinkWithAliasPattern = $"[[{title}|";

                foreach (var file in System.IO.Directory.EnumerateFiles(notesPath, "*.md", System.IO.SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var content = System.IO.File.ReadAllText(file);
                        var modified = false;

                        // 替换 [[title]] -> [[category/title]]
                        if (content.Contains(oldLink, StringComparison.OrdinalIgnoreCase))
                        {
                            content = content.Replace(oldLink, newLink, StringComparison.OrdinalIgnoreCase);
                            modified = true;
                        }

                        // 替换 [[title|alias]] -> [[category/title|alias]]
                        if (content.Contains(oldLinkWithAliasPattern, StringComparison.OrdinalIgnoreCase))
                        {
                            content = System.Text.RegularExpressions.Regex.Replace(content,
                                $@"\[\[{System.Text.RegularExpressions.Regex.Escape(title)}\|",
                                $"[[{correctPath}|",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            modified = true;
                        }

                        if (modified)
                        {
                            System.IO.File.WriteAllText(file, content);
                            fixedCount++;
                            _logger.LogInformation("纠正链接：{File} 中 [[{Title}]] -> [[{Path}]]",
                                System.IO.Path.GetRelativePath(notesPath, file), title, correctPath);
                        }
                    }
                    catch { /* 跳过无法处理的文件 */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "纠正链接失败");
            }

            return fixedCount;
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
            var providers = _settings.GetAiProviders();
            var provider = string.IsNullOrEmpty(providerId)
                ? providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
                : providers.FirstOrDefault(p => p.Id == providerId);

            if (provider == null)
                throw new Exception("未找到可用的AI提供商");

            var apiEndpoint = provider.AiBaseUrl.TrimEnd('/') + "/chat/completions";
            _logger.LogInformation("调用 AI API: {Endpoint}, 提供商：{Provider}, 模型：{Model}, tools={Tools}", apiEndpoint, provider.Name, model, enableTools);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_settings.AiRequestTimeoutMinutes));
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
            var activeVault = _settings.GetActiveVault();
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

        private List<ChatMessage> BuildMessagesWithHistory(List<ChatHistoryItem>? history, string providerId, string model, string? sessionId = null)
        {
            // 同步版本：仅做 Token 预算截断（摘要压缩和语义检索在异步版本中）
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, GetSystemPrompt(providerId))
            };

            if (history != null)
            {
                // Token 预算截断
                var trimmed = _chatMemoryService.TrimByTokenBudget(history, model);

                foreach (var item in trimmed)
                {
                    var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? ChatRole.Assistant
                        : ChatRole.User;
                    messages.Add(new ChatMessage(role, item.Content));
                }
            }

            return messages;
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
            var providers = _settings.GetAiProviders();
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

        /// <summary>
        /// AI 聊天 - 对话模式
        /// </summary>
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

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_settings.AiRequestTimeoutMinutes));
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

    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string? ProviderId { get; set; }
        public string? Model { get; set; }
        public List<ChatHistoryItem>? History { get; set; }
        /// <summary>
        /// 会话 ID（用于记忆系统的摘要缓存和语义检索）
        /// </summary>
        public string? SessionId { get; set; }
        /// <summary>
        /// 是否启用 Function Calling（工具调用）。默认 true（聊天场景通常需要搜索知识库等工具）
        /// </summary>
        public bool? EnableTools { get; set; }
    }

    public class ChatHistoryItem
    {
        public string Role { get; set; } = "user"; // "user" or "assistant"
        public string Content { get; set; } = string.Empty;
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

    public class GenerateMissingNoteResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string? NotePath { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public int FixedLinks { get; set; }
    }


}
