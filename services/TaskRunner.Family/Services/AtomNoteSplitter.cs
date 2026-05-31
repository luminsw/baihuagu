using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using TaskRunner.Models;
using TaskRunner.Contracts.Scene;

namespace TaskRunner.Services
{
    public class AtomNoteSplitter
    {
        private static readonly object _suppHistoryLock = new();
        private static List<string>? _supplementHistory;
        private string SupplementHistoryFile => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "supplement.history.json");
        private const int MaxSupplementHistoryEntries = 1000;
        private readonly AiClientService _aiClientService;
        private readonly TaskManager _taskManager;
        private readonly SettingsService _settings;
        private readonly LocalAiAutoStarter _localAiAutoStarter;
        private readonly DefaultPromptProvider _scenePromptService;
        private readonly AnkiCardGenerator _cardGenerator;
        private readonly ILogger<AtomNoteSplitter> _logger;

        public AtomNoteSplitter(
            AiClientService aiClientService,
            TaskManager taskManager,
            SettingsService settings,
            LocalAiAutoStarter localAiAutoStarter,
            DefaultPromptProvider scenePromptService,
            AnkiCardGenerator cardGenerator,
            ILogger<AtomNoteSplitter> logger)
        {
            _aiClientService = aiClientService;
            _taskManager = taskManager;
            _settings = settings;
            _localAiAutoStarter = localAiAutoStarter;
            _scenePromptService = scenePromptService;
            _cardGenerator = cardGenerator;
            _logger = logger;
        }

        private static string ComputeTargetsHash(List<string> targets)
        {
            targets.Sort(StringComparer.OrdinalIgnoreCase);
            var concat = string.Join("|", targets);
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(concat);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private static bool IsTransient(Exception ex)
        {
            if (ex is HttpRequestException) return true;
            if (ex is TaskCanceledException) return true; // timeout
            if (ex is System.ClientModel.ClientResultException) return true;
            if (ex.InnerException != null) return IsTransient(ex.InnerException);
            return false;
        }

        private static bool IsConnectionFailure(Exception ex)
        {
            var message = ex.Message + (ex.InnerException?.Message ?? "");
            return message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("积极拒绝", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
                || message.Contains("无法连接", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
                || ex.InnerException is System.Net.Sockets.SocketException;
        }

        private static Dictionary<string, string> BuildTitleToPathMap(List<Note> notes, Dictionary<Note, string> notePathMap)
        {
            return notes
                .GroupBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => notePathMap[g.Single()], StringComparer.OrdinalIgnoreCase);
        }

        private async Task<string> SendRequestWithRetryAsync(string providerId, string model, string userPrompt, bool isSupplement, CancellationToken cancellationToken, TaskRunner.Contracts.Scene.AppScene? scene = null)
        {
            _logger.LogDebug("发送 AI 请求到 provider {ProviderId} model {Model} (补充={IsSupplement})", providerId, model, isSupplement);

            var maxAttempts = _settings.AiRequestMaxAttempts;
            var initialBackoff = _settings.AiRequestInitialBackoffMs;
            var maxBackoff = _settings.AiRequestMaxBackoffMs;
            var rand = new Random();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None, cancellationToken);
                    cts.CancelAfter(TimeSpan.FromMinutes(_settings.AiRequestTimeoutMinutes));

                    var chatClient = _aiClientService.CreateChatClient(providerId, model);
                    var messages = new List<ChatMessage>
                    {
                        new(ChatRole.System, GetSystemPrompt(scene)),
                        new(ChatRole.User, userPrompt)
                    };
                    var options = AiClientService.BuildChatOptions();

                    var response = await chatClient.GetResponseAsync(messages, options, cts.Token);
                    var aiContent = response.Text;
                    _logger.LogDebug("解析到 AI 内容长度：{Len}", aiContent?.Length ?? 0);
                    return aiContent ?? throw new Exception("AI 返回内容为空");
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
                {
                    // 首次失败且是连接类错误时，尝试自动启动本地 AI 服务
                    if (attempt == 1 && IsConnectionFailure(ex))
                    {
                        try
                        {
                            var provider = _settings.GetAiProviders().FirstOrDefault(p => p.Id == providerId);
                            if (provider != null)
                            {
                                await _localAiAutoStarter.TryEnsureRunningAsync(provider.Id, provider.AiBaseUrl);
                            }
                        }
                        catch { /* 自动启动失败不影响原有重试逻辑 */ }
                    }

                    var backoff = Math.Min(maxBackoff, initialBackoff * (int)Math.Pow(2, attempt - 1));
                    var delayMs = backoff + rand.Next(0, Math.Min(500, backoff));
                    _logger.LogWarning(ex, "调用 AI 第 {Attempt} 次失败，{Delay}ms 后重试...", attempt, delayMs);
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw;
                }
            }

            throw new Exception("AI 请求在多次重试后仍然失败");
        }

        private static string NormalizeRelPath(string relPath)
        {
            if (string.IsNullOrWhiteSpace(relPath)) return string.Empty;
            // 使用正斜杠，去掉前后斜杠，删除多余空格
            var p = relPath.Replace('\\', '/').Trim();
            while (p.StartsWith('/')) p = p.Substring(1);
            while (p.EndsWith('/')) p = p.Substring(0, p.Length - 1);
            // 规范化连续的斜杠
            while (p.Contains("//")) p = p.Replace("//", "/");
            return p;
        }

        public async Task SplitAtomNotes(
            string taskId,
            string title,
            string content,
            bool useChain,
            IReadOnlyList<string>? aiProviderIds = null,
            IReadOnlyDictionary<string, List<string>>? aiModels = null,
            string? vaultPath = null,
            CancellationToken cancellationToken = default,
            TaskRunner.Contracts.Scene.AppScene? scene = null)
        {
            try
            {
                await _taskManager.UpdateProgress(taskId, 0, 1, "准备 AI 配置...");
                _logger.LogInformation("SplitAtomNotes 开始: taskId={TaskId}, vaultPath={VaultPath}, useChain={UseChain}, scene={Scene}", taskId, vaultPath ?? "(null)", useChain, scene?.ToString() ?? "默认");

                var orderedProviders = ResolveProviders(aiProviderIds);
                if (orderedProviders.Count == 0)
                    throw new Exception("未配置可用的 AI 提供方（请检查 appsettings.json 中 Ai 数组）");

                var steps = BuildSteps(orderedProviders, aiModels);

                // 链式调用：全局首步拆分，之后可选补充（跨提供方、跨模型）
                List<Note> allNotes = new List<Note>();
                string originalContent = content;
                var modelErrors = new List<string>();
                int totalSteps = steps.Count + 2; // 模型执行 + 解析 + 保存

                // 记录每次 AI 请求的详情
                var requestRecords = new List<object>();
                var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < steps.Count; i++)
                {
                    var (provider, model, isSplitStep) = steps[i];
                    var displayName = string.IsNullOrWhiteSpace(provider.Name) ? provider.Id : provider.Name;

                    var phase = isSplitStep ? "拆分笔记" : "补充完善";
                    await _taskManager.UpdateProgress(taskId, i + 1, totalSteps,
                        $"步骤 {i + 1}/{totalSteps}：{phase}（{displayName} / {model}）...");

                    _logger.LogInformation("调用 {Provider} 模型 {Index}/{Total}: {Model} (模式：{Mode})",
                        provider.Id, i + 1, steps.Count, model, isSplitStep ? "拆分" : "补充");

                    if (string.IsNullOrEmpty(_settings.GetApiKeyForProvider(provider.Id)))
                        _logger.LogWarning(
                            "提供方 {ProviderId} 未配置 API Key",
                            provider.Id);

                    try
                    {
                        string result;
                        var stepStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        if (isSplitStep)
                        {
                            result = await CallAiApiAsync(title, originalContent, model, provider.Id, isSupplement: false, cancellationToken: cancellationToken, scene: scene);

                            var firstRoundNotes = ParseResult(result);
                            allNotes.AddRange(firstRoundNotes);

                            _logger.LogInformation("首轮拆分得到 {Count} 条笔记", firstRoundNotes.Count);
                        }
                        else if (useChain)
                        {
                            result = await CallAiApiAsync(title, originalContent, model, provider.Id, isSupplement: true, existingNotes: allNotes, cancellationToken: cancellationToken, scene: scene);

                            var supplementNotes = ParseResult(result);

                            var existingPaths = allNotes.Select(n => n.Path).ToHashSet();
                            foreach (var note in supplementNotes)
                            {
                                if (!existingPaths.Contains(note.Path))
                                {
                                    allNotes.Add(note);
                                    existingPaths.Add(note.Path);
                                    _logger.LogInformation("补充新笔记：{Path}", note.Path);
                                }
                            }
                        }
                        stepStopwatch.Stop();
                        requestRecords.Add(new
                        {
                            providerId = provider.Id,
                            providerName = displayName,
                            model,
                            phase = isSplitStep ? "拆分" : "补充",
                            elapsedMs = stepStopwatch.ElapsedMilliseconds,
                            success = true,
                            timestamp = DateTime.Now
                        });
                    }
                    catch (Exception ex)
                    {
                        var mode = isSplitStep ? "拆分" : "补充";
                        var err = $"{displayName} / {model} ({mode}) 失败：{ex.Message}";
                        modelErrors.Add(err);
                        _logger.LogWarning(ex, err);
                        requestRecords.Add(new
                        {
                            providerId = provider.Id,
                            providerName = displayName,
                            model,
                            phase = mode,
                            elapsedMs = 0,
                            success = false,
                            error = ex.Message,
                            timestamp = DateTime.Now
                        });
                        continue;
                    }
                }

                if (allNotes.Count == 0)
                {
                    var errorText = modelErrors.Count > 0
                        ? $"所有模型均失败：{string.Join(" | ", modelErrors.Take(3))}"
                        : "所有模型均未产出可用笔记";
                    throw new Exception(errorText);
                }

                await _taskManager.UpdateProgress(taskId, steps.Count + 1, totalSteps, "解析结果...");

                await _taskManager.UpdateProgress(taskId, steps.Count + 2, totalSteps, $"保存 {allNotes.Count} 条笔记...");

                // 拆分完成后：修复 Obsidian wikilink 在 AI 拆分后丢失分类目录的问题。
                // 规则：若 wikilink 形如 [[桂枝汤]]（target 不包含 '/'），且 target 与某条生成笔记的 title 唯一匹配，
                // 则补全为 [[分类/标题]]（保留 #header 与 |alias）。
                
                // 预计算并缓存所有笔记的规范化路径
                var notePathMap = new Dictionary<Note, string>();
                foreach (var note in allNotes)
                {
                    notePathMap[note] = NormalizeRelPath(note.Path);
                }
                var existingNormalizedPaths = notePathMap.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                
                // 构建 title->path 映射
                var titleToPath = BuildTitleToPathMap(allNotes, notePathMap);

                // 在保存前，检查是否有 AI 引用但未生成/本地不存在的 wikilink 目标，若有则循环向 AI 请求补全，直到补齐或达到上限
                var notesPath = string.IsNullOrEmpty(vaultPath) ? _settings.NotesPath : Path.Combine(vaultPath, "notes");

                // 限制补充轮次上限，防止 AI 每轮返回新笔记但持续引入新 wikilink 导致无限循环
                const int maxSupplementRounds = 5;
                int round = 0;
                while (round < maxSupplementRounds)
                {
                    round++;

                    // 重新构建 title->path 映射（基于当前已知笔记）
                    titleToPath = BuildTitleToPathMap(allNotes, notePathMap);

                    // 找到所有被引用的链接目标（解析 [[...]]），并映射为相对路径（若能解析）
                    var referenced = ExtractWikiLinkTargets(allNotes);
                    var resolvedReferencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var unresolvedRaw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rawLink in referenced)
                    {
                        var resolved = ResolveLinkToPath(rawLink, titleToPath);
                        if (!string.IsNullOrEmpty(resolved)) resolvedReferencedPaths.Add(NormalizeRelPath(resolved));
                        else unresolvedRaw.Add(rawLink);
                    }

                    // 汇总需要补充的目标：未在内存 note 列表中且本地文件不存在
                    var needToFill = new List<string>();
                    foreach (var p in resolvedReferencedPaths)
                    {
                        if (existingNormalizedPaths.Contains(p)) continue;
                        var full = string.IsNullOrEmpty(notesPath) ? null : Path.Combine(notesPath, p + ".md");
                        if (full == null || !File.Exists(full))
                        {
                            needToFill.Add(p);
                        }
                    }

                    // 把无法解析为带目录的 raw 链接也作为补充项（交给 AI 帮忙补齐分类）
                    foreach (var raw in unresolvedRaw)
                    {
                        if (!needToFill.Contains(raw, StringComparer.OrdinalIgnoreCase))
                            needToFill.Add(raw);
                    }

                    if (needToFill.Count == 0)
                        break; // 没有缺失，退出循环

                    // 把补充次数计入总进度，确保前端进度条能显示真实进度
                    totalSteps += 1;
                    await _taskManager.UpdateProgress(taskId, steps.Count + 1 + round, totalSteps, $"第 {round} 轮补充：{needToFill.Count} 个缺失目标...");
                    _logger.LogInformation("发现 {Count} 个缺失的 wikilink 目标，开始第 {Round} 轮补充", needToFill.Count, round);

                    try
                    {
                        // 本地幂等检查：如果相同目标集合已在历史中，跳过重复请求
                        var hash = ComputeTargetsHash(needToFill);
                        var shouldSkip = false;
                        lock (_suppHistoryLock)
                        {
                            if (_supplementHistory == null)
                            {
                                if (File.Exists(SupplementHistoryFile))
                                {
                                    try { _supplementHistory = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SupplementHistoryFile)) ?? new List<string>(); } catch (Exception ex) { _logger.LogDebug(ex, "读取补充历史文件失败"); _supplementHistory = new List<string>(); }
                                }
                                else
                                {
                                    _supplementHistory = new List<string>();
                                }
                            }
                            if (_supplementHistory.Contains(hash))
                                shouldSkip = true;
                        }

                        if (shouldSkip)
                        {
                            _logger.LogInformation("检测到补充目标集合已在本地历史中，跳过本轮补充（轮次 {Round}）", round);
                            try { await _taskManager.NotifySupplementEventAsync(taskId, "SupplementSkipped", new { round, targets = needToFill.Count }); } catch { /* SignalR 推送失败不影响主流程 */ }
                            break;
                        }

                        // 通知前端：补充开始
                        try { await _taskManager.NotifySupplementEventAsync(taskId, "SupplementStarted", new { round, targets = needToFill }); } catch { /* SignalR 推送失败不影响主流程 */ }

                        // 向 AI 请求针对这些缺失目标的笔记（明确要求只输出 JSON 数组）
                        var suppStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var suppProvider = steps.Last().Provider;
                        var suppModel = steps.Last().Model;
                        var suppDisplayName = string.IsNullOrWhiteSpace(suppProvider.Name) ? suppProvider.Id : suppProvider.Name;
                        List<Note> newNotes = new();
                        try
                        {
                            var supplementResult = await CallAiApiAsync(title, originalContent, suppModel, suppProvider.Id, isSupplement: true, existingNotes: allNotes, missingTargets: needToFill, cancellationToken: cancellationToken, scene: scene);
                            suppStopwatch.Stop();
                            newNotes = ParseResult(supplementResult);
                            requestRecords.Add(new
                            {
                                providerId = suppProvider.Id,
                                providerName = suppDisplayName,
                                model = suppModel,
                                phase = $"循环补充(轮{round})",
                                elapsedMs = suppStopwatch.ElapsedMilliseconds,
                                success = true,
                                timestamp = DateTime.Now
                            });
                        }
                        catch (Exception ex)
                        {
                            suppStopwatch.Stop();
                            _logger.LogWarning(ex, "循环补充 AI 调用失败（第 {Round} 轮）", round);
                            requestRecords.Add(new
                            {
                                providerId = suppProvider.Id,
                                providerName = suppDisplayName,
                                model = suppModel,
                                phase = $"循环补充(轮{round})",
                                elapsedMs = 0,
                                success = false,
                                error = ex.Message,
                                timestamp = DateTime.Now
                            });
                            break;
                        }

                        // 记录历史（哈希）以做本地幂等
                        lock (_suppHistoryLock)
                        {
                            if (_supplementHistory == null)
                                _supplementHistory = new List<string>();
                            if (!_supplementHistory.Contains(hash))
                            {
                                _supplementHistory.Add(hash);
                                if (_supplementHistory.Count > MaxSupplementHistoryEntries)
                                {
                                    _supplementHistory.RemoveRange(0, _supplementHistory.Count - MaxSupplementHistoryEntries);
                                }
                                try { File.WriteAllText(SupplementHistoryFile, System.Text.Json.JsonSerializer.Serialize(_supplementHistory)); } catch (Exception ex) { _logger.LogWarning(ex, "写入补充历史失败"); }
                            }
                        }

                        var beforeCount = allNotes.Count;
                        foreach (var n in newNotes)
                        {
                            var normalizedPath = NormalizeRelPath(n.Path);
                            n.Path = normalizedPath;
                            if (!existingNormalizedPaths.Contains(normalizedPath))
                            {
                                allNotes.Add(n);
                                notePathMap[n] = normalizedPath;
                                existingNormalizedPaths.Add(normalizedPath);
                                _logger.LogInformation("补充新笔记（循环）: {Path}", n.Path);
                            }
                        }

                        var added = allNotes.Count - beforeCount;
                        // 更新当前轮次进度并通知前端
                        await _taskManager.UpdateProgress(taskId, steps.Count + 1 + round, totalSteps, $"第 {round} 轮补充完成，新增 {added} 条笔记");

                        // 通知前端：补充完成
                        try { await _taskManager.NotifySupplementEventAsync(taskId, "SupplementFinished", new { round, added = added }); } catch { /* SignalR 推送失败不影响主流程 */ }

                        if (allNotes.Count == beforeCount)
                        {
                            // AI 没有产出新筆记，避免死循环
                            _logger.LogWarning("AI 在补充轮次未返回新笔记，终止补充循环");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "补充轮次失败（第 {Round} 轮）", round);
                        break;
                    }
                }

                // 在保存前，统一规范所有 note.Path 的格式（用 '/' 作为分隔，去除首尾斜杠），
                // 并在内容中使用与保存一致的路径格式来重写 wikilink
                for (int i = 0; i < allNotes.Count; i++)
                {
                    // 规范化路径用于保存和链接重写
                    allNotes[i].Path = NormalizeRelPath(allNotes[i].Path);
                    var original = allNotes[i].Content;
                    allNotes[i].Content = WikiLinkRewriter.RewriteMissingCategoryLinks(original, titleToPath);
                }

                _logger.LogInformation("SplitAtomNotes 准备保存 {Count} 条笔记到: {NotesPath}", allNotes.Count, notesPath ?? "(null)");
                var savedNotes = await SaveNotes(allNotes, notesPath);

                // 确保返回的笔记列表与 AI 查询格式一致，便于 WebUI 统一显示
                var notesList = savedNotes.Select(n => new { title = n.Title, path = n.Path }).ToList();
                
                if (savedNotes.Count == 0)
                {
                    _logger.LogWarning("任务 {TaskId} 没有成功保存任何笔记", taskId);
                    await _taskManager.UpdateStatus(taskId, TaskStatus.Failed, "没有成功保存任何笔记，请检查知识库路径配置");
                    return;
                }

                totalStopwatch.Stop();

                await _taskManager.UpdateStatus(taskId, TaskStatus.Success, null, new
                {
                    count = savedNotes.Count,
                    notes = notesList,
                    requests = requestRecords,
                    totalElapsedMs = totalStopwatch.ElapsedMilliseconds
                });

                _logger.LogInformation("任务 {TaskId} 完成，共生成 {Count} 条笔记", taskId, savedNotes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务 {TaskId} 失败", taskId);
                await _taskManager.UpdateStatus(taskId, TaskStatus.Failed, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 按请求顺序解析提供方；空列表时使用主 AI；无效 Id 跳过。
        /// </summary>
        private List<AiProviderConfig> ResolveProviders(IReadOnlyList<string>? aiProviderIds)
        {
            var rawIds = aiProviderIds?
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList() ?? new List<string>();

            if (rawIds.Count == 0)
            {
                var main = _settings.GetMainAiProvider();
                return main != null ? new List<AiProviderConfig> { main } : new List<AiProviderConfig>();
            }

            var ordered = new List<AiProviderConfig>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in rawIds)
            {
                if (!seen.Add(id))
                    continue;
                var p = _settings.GetAiProvider(id);
                if (p != null)
                    ordered.Add(p);
                else
                    _logger.LogWarning("忽略未知的 AI 提供方 Id：{Id}", id);
            }

            return ordered;
        }

        private static List<(AiProviderConfig Provider, string Model, bool IsSplit)> BuildSteps(
            IReadOnlyList<AiProviderConfig> orderedProviders,
            IReadOnlyDictionary<string, List<string>>? aiModels = null)
        {
            var steps = new List<(AiProviderConfig Provider, string Model, bool IsSplit)>();
            bool splitAssigned = false;

            foreach (var p in orderedProviders)
            {
                var models = new List<string>();
                
                // 如果前端指定了该 provider 的模型列表，使用指定模型
                if (aiModels != null && aiModels.TryGetValue(p.Id, out var specifiedModels) && specifiedModels != null && specifiedModels.Count > 0)
                {
                    models.AddRange(specifiedModels);
                }
                else
                {
                    // 使用主模型（第一个）
                    var mainModel = p.GetMainModel();
                    if (string.IsNullOrEmpty(mainModel))
                        mainModel = "Qwen/Qwen2.5-14B-Instruct";
                    models.Add(mainModel);
                }

                // 为每个选中的模型创建一个步骤
                foreach (var model in models)
                {
                    var isSplit = !splitAssigned;
                    steps.Add((p, model, isSplit));
                    if (isSplit)
                        splitAssigned = true;
                }
            }

            return steps;
        }

        private async Task<string> CallAiApiAsync(
            string title,
            string content,
            string model,
            string providerId,
            bool isSupplement = false,
            List<Note>? existingNotes = null,
            CancellationToken cancellationToken = default,
            TaskRunner.Contracts.Scene.AppScene? scene = null)
        {
            string userPrompt = BuildUserPrompt(title, content, isSupplement, existingNotes);
            _logger.LogDebug("调用 AI API: provider={ProviderId}, model={Model}, 模式：{Mode}", providerId, model, isSupplement ? "补充" : "拆分");
            return await SendRequestWithRetryAsync(providerId, model, userPrompt, isSupplement, cancellationToken, scene);
        }

        // 新增重载，支持传入需要补充的缺失目标（relative path or raw link）
        private async Task<string> CallAiApiAsync(
            string title,
            string content,
            string model,
            string providerId,
            bool isSupplement,
            List<Note>? existingNotes,
            List<string>? missingTargets,
            CancellationToken cancellationToken = default,
            TaskRunner.Contracts.Scene.AppScene? scene = null)
        {
            if (missingTargets == null || missingTargets.Count == 0)
                return await CallAiApiAsync(title, content, model, providerId, isSupplement, existingNotes, cancellationToken, scene);

            var targetList = string.Join('\n', missingTargets.Select(t => $"- {t}"));
            var extra = $"\n\n【需要补充的缺失目标】\n{targetList}\n\n请只输出这些目标对应的笔记（JSON 数组），不要输出其他内容或解释。";

            string userPrompt = BuildUserPrompt(title, content, isSupplement, existingNotes) + extra;
            _logger.LogDebug("调用 AI 补充接口，目标数：{Count}", missingTargets.Count);
            return await SendRequestWithRetryAsync(providerId, model, userPrompt, isSupplement: true, cancellationToken: cancellationToken, scene);
        }

        private string BuildUserPrompt(string title, string content, bool isSupplement, List<Note>? existingNotes)
        {
            var template = _scenePromptService.GetTemplate();

            if (isSupplement && existingNotes != null && existingNotes.Count > 0)
            {
                var existingPaths = string.Join("\n", existingNotes.Select(n => $"- {n.Path} ({n.Title})"));
                return template.SupplementUserPrompt
                    .Replace("{title}", title)
                    .Replace("{existingNotes}", existingPaths)
                    .Replace("{content}", content);
            }
            else
            {
                return template.SplitUserPrompt
                    .Replace("{title}", title)
                    .Replace("{content}", content);
            }
        }

        private List<Note> ParseResult(string aiContent)
        {
            try
            {
                var normalized = ExtractJsonPayload(aiContent);
                // 使用大小写不敏感的配置（AI 返回小写字段名）
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                _logger.LogDebug("准备解析 JSON 片段，长度：{Len}", normalized.Length);
                var notes = JsonSerializer.Deserialize<List<Note>>(normalized, options) ?? new List<Note>();
                _logger.LogInformation("解析到 {Count} 条笔记", notes.Count);
                foreach (var note in notes)
                {
                    _logger.LogDebug("解析笔记：Path={Path}, Title={Title}", note.Path, note.Title);
                }
                return notes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析 AI 返回失败：{Content}", aiContent.Substring(0, Math.Min(500, aiContent.Length)));
                throw new Exception($"JSON 解析失败：{ex.Message}");
            }
        }

        // 提取所有 [[...]] 形式的 wikilink 内容（不包含 [[ ]]）
        private static List<string> ExtractWikiLinkTargets(List<Note> notes)
        {
            var pattern = new Regex(@"\[\[([^\]|#]+)", RegexOptions.Compiled);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in notes)
            {
                if (string.IsNullOrEmpty(n.Content)) continue;
                foreach (Match m in pattern.Matches(n.Content))
                {
                    var v = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(v)) set.Add(v);
                }
            }
            return set.ToList();
        }

        // 将一个原始 wikilink 目标解析为路径（若已包含 '/ ' 则直接返回），或尝试用 titleToPath 映射
        private static string? ResolveLinkToPath(string rawLink, Dictionary<string, string> titleToPath)
        {
            if (string.IsNullOrWhiteSpace(rawLink)) return null;
            var s = rawLink.Trim();
            if (s.Contains('/')) return s; // 已包含分类
            if (titleToPath != null && titleToPath.TryGetValue(s, out var p)) return p;
            return null;
        }

        private string ExtractJsonPayload(string text)
        {
            var raw = (text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(raw))
            {
                throw new Exception("AI 返回内容为空");
            }

            // 兼容 ```json ... ``` / ``` ... ```
            if (raw.StartsWith("```", StringComparison.Ordinal))
            {
                var lines = raw.Split('\n').ToList();
                if (lines.Count > 0 && lines[0].StartsWith("```", StringComparison.Ordinal))
                {
                    lines.RemoveAt(0);
                }
                if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                raw = string.Join('\n', lines).Trim();
            }

            // 若有多余说明文字，提取首个 JSON 数组片段
            var start = raw.IndexOf('[');
            if (start < 0)
            {
                throw new Exception("未找到 JSON 数组起始符 '['");
            }

            int depth = 0;
            int end = -1;
            for (int i = start; i < raw.Length; i++)
            {
                var ch = raw[i];
                if (ch == '[') depth++;
                else if (ch == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                throw new Exception("JSON 数组结束符 ']' 不完整");
            }

            var jsonPayload = raw.Substring(start, end - start + 1).Trim();
            
            // 修复 AI 返回的 JSON 中未转义的换行符和制表符
            // 这些字符在 JSON 字符串中必须转义为 \n 和 \t
            jsonPayload = FixUnescapedCharsInJson(jsonPayload);
            
            return jsonPayload;
        }

        /// <summary>
        /// 修复 JSON 字符串中未正确转义的控制字符
        /// AI 有时会在 content 字段中直接输出换行符而不是 \n，导致 JSON 解析失败
        /// </summary>
        private string FixUnescapedCharsInJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            
            var result = new StringBuilder();
            bool inString = false;
            bool escapeNext = false;
            
            foreach (char c in json)
            {
                if (escapeNext)
                {
                    // 当前字符是被转义的，直接添加
                    result.Append(c);
                    escapeNext = false;
                    continue;
                }
                
                if (c == '\\')
                {
                    result.Append(c);
                    escapeNext = true;
                    continue;
                }
                
                if (c == '"' && !escapeNext)
                {
                    inString = !inString;
                    result.Append(c);
                    continue;
                }
                
                if (inString)
                {
                    // 在字符串内部，需要转义控制字符
                    switch (c)
                    {
                        case '\n': // 0x0A
                            result.Append("\\n");
                            break;
                        case '\r': // 0x0D
                            result.Append("\\r");
                            break;
                        case '\t': // 0x09
                            result.Append("\\t");
                            break;
                        case '\b': // 0x08
                            result.Append("\\b");
                            break;
                        case '\f': // 0x0C
                            result.Append("\\f");
                            break;
                        default:
                            if (c < 0x20)
                            {
                                // 其他控制字符使用 Unicode 转义
                                result.Append($"\\u{(int)c:X4}");
                            }
                            else
                            {
                                result.Append(c);
                            }
                            break;
                    }
                }
                else
                {
                    // 在字符串外部，直接添加
                    result.Append(c);
                }
            }
            
            return result.ToString();
        }

        private async Task<List<Note>> SaveNotes(List<Note> notes, string? overrideNotesPath = null)
        {
            var savedNotes = new List<Note>();
            var notesPath = overrideNotesPath ?? _settings.NotesPath;  // 使用 notes 子目录
            if (string.IsNullOrEmpty(notesPath)) return savedNotes;

            Directory.CreateDirectory(notesPath);

            // 首先创建所有必要的目录结构
            var directoriesToCreate = new HashSet<string>();
            foreach (var note in notes)
            {
                var fullPath = Path.Combine(notesPath, note.Path + ".md");
                var directory = Path.GetDirectoryName(fullPath)!;
                directoriesToCreate.Add(directory);
            }
            
            foreach (var directory in directoriesToCreate)
            {
                Directory.CreateDirectory(directory);
            }

            // 并行写入文件
            var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
            var savedNotesSync = new System.Collections.Concurrent.ConcurrentBag<Note>();
            
            await Parallel.ForEachAsync(notes, options, async (note, ct) =>
            {
                try
                {
                    var fullPath = Path.Combine(notesPath, note.Path + ".md");
                    await File.WriteAllTextAsync(fullPath, note.Content, ct);
                    savedNotesSync.Add(note);
                    _logger.LogInformation("保存笔记：{Path}", note.Path);

                    // 自动为该笔记生成 Anki 记忆卡片
                    try
                    {
                        var vaultPath = Path.GetDirectoryName(notesPath);
                        var cardsRoot = string.IsNullOrEmpty(vaultPath) ? "" : System.IO.Path.Combine(vaultPath, "cards");
                        var vaultId = _settings.GetVaults().FirstOrDefault(v => v.Path == vaultPath)?.Id ?? "";
                        var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                        {
                            ["notePath"] = note.Path,
                            ["vaultId"] = vaultId
                        });
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _taskManager.UpdateStatus(taskId, TaskStatus.Running);
                                var result = await _cardGenerator.GenerateFromNote(note.Path, cardsPath: cardsRoot, notesBasePath: notesPath);
                                await _taskManager.UpdateStatus(taskId, result.Success ? TaskStatus.Success : TaskStatus.Failed,
                                    data: new { message = result.Message, cardCount = result.CardCount });
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[Split] 卡片生成失败：{Path}", note.Path);
                                await _taskManager.UpdateStatus(taskId, TaskStatus.Failed, error: ex.Message);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Split] 自动触发卡片生成失败：{Path}", note.Path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "保存笔记失败：{Path}", note.Path);
                }
            });
            
            savedNotes.AddRange(savedNotesSync);
            return savedNotes;
        }

        private string GetSystemPrompt(TaskRunner.Contracts.Scene.AppScene? scene = null)
        {
            return _scenePromptService.GetTemplate(scene).SplitSystemPrompt;
        }

        public class Note
        {
            public string Path { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Summary { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
        }
    }
}
