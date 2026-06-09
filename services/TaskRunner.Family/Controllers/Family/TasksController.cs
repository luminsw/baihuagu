using TaskRunner.Core.Shared;
using TaskRunner.Services;
using System.Text.Json;
using TaskRunner.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using TaskRunner.Models;
using TaskRunner.Contracts.Scene;
using TaskRunner.Contracts.Tasks;
using TaskRunner.Contracts.Vaults;

namespace TaskRunner.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB 限制，防止 DoS
    public class TasksController : ControllerBase
    {
        private readonly Services.TaskManager _taskManager;
        private readonly Services.AiSettingsService _aiSettings;
        private readonly Services.VaultSettingsService _vaultSettings;
        private readonly Services.AtomNoteSplitter _atomNoteSplitter;
        private readonly Services.AiClientService _aiClientService;
        private readonly Services.LocalAiAutoStarter _localAiAutoStarter;
        private readonly Services.LocalModelDeploymentService _localDeployment;
        private readonly Services.IOpenClawTaskService _openClawTaskService;
        private readonly DefaultPromptProvider _scenePromptService;
        private readonly Services.AnkiCardGenerator _cardGenerator;
        private readonly Services.VaultNoteIndexer _vaultNoteIndexer;
        private readonly ILogger<TasksController> _logger;
        private readonly IHostApplicationLifetime _appLifetime;

        public TasksController(
            Services.TaskManager taskManager,
            Services.AiSettingsService aiSettings,
            Services.VaultSettingsService vaultSettings,
            Services.AtomNoteSplitter atomNoteSplitter,
            Services.AiClientService aiClientService,
            Services.LocalAiAutoStarter localAiAutoStarter,
            Services.LocalModelDeploymentService localDeployment,
            Services.IOpenClawTaskService openClawTaskService,
            DefaultPromptProvider scenePromptService,
            Services.AnkiCardGenerator cardGenerator,
            Services.VaultNoteIndexer vaultNoteIndexer,
            ILogger<TasksController> logger,
            IHostApplicationLifetime appLifetime)
        {
            _taskManager = taskManager;
            _aiSettings = aiSettings;
            _vaultSettings = vaultSettings;
            _atomNoteSplitter = atomNoteSplitter;
            _aiClientService = aiClientService;
            _localAiAutoStarter = localAiAutoStarter;
            _localDeployment = localDeployment;
            _openClawTaskService = openClawTaskService;
            _scenePromptService = scenePromptService;
            _cardGenerator = cardGenerator;
            _vaultNoteIndexer = vaultNoteIndexer;
            _logger = logger;
            _appLifetime = appLifetime;
        }

        private AiProviderConfig? ResolveProvider(string modelName)
        {
            var providers = _aiSettings.GetAiProviders();
            if (string.IsNullOrWhiteSpace(modelName))
                return providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault();

            return providers.FirstOrDefault(p =>
                p.Models.Any(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
                ?? providers.FirstOrDefault(p => p.IsMain)
                ?? providers.FirstOrDefault();
        }

        private static bool IsLocalProvider(AiProviderConfig? provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        /// <summary>
        /// 查找知识库，支持重试以应对可能的写入延迟
        /// </summary>
        private async Task<VaultConfig?> FindVaultWithRetryAsync(string vaultId, int retryCount = 1, int delayMs = 500)
        {
            for (int i = 0; i <= retryCount; i++)
            {
                var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
                if (vault != null) return vault;
                if (i < retryCount)
                {
                    _logger.LogWarning("知识库查找重试 {Attempt}/{Max}: VaultId={VaultId}", i + 1, retryCount, vaultId);
                    await Task.Delay(delayMs);
                }
            }
            return null;
        }

        [HttpGet]
        public ActionResult GetTasks([FromQuery] string? status = null, [FromQuery] int limit = 50)
        {
            var tasks = _taskManager.GetAllTasks();
            
            if (!string.IsNullOrEmpty(status))
            {
                tasks = tasks.Where(t => t.Status.ToString().Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return Ok(new { tasks = tasks.Take(limit), total = tasks.Count });
        }

        [HttpDelete("{taskId}")]
        public ActionResult DeleteTask(string taskId)
        {
            if (_taskManager.DeleteTask(taskId))
            {
                return Ok(new { success = true });
            }
            return NotFound();
        }

        [HttpPost("{taskId}/cancel")]
        public async Task<IActionResult> CancelTask(string taskId)
        {
            var task = _taskManager.GetTask(taskId);
            if (task == null)
            {
                return NotFound(new { error = "任务不存在" });
            }

            if (task.Status != RunnerTaskStatus.Running && task.Status != RunnerTaskStatus.Pending)
            {
                return BadRequest(new { error = "只能取消运行中或待执行的任务" });
            }

            var cancelled = await _taskManager.CancelTaskAsync(taskId);
            if (!cancelled)
            {
                return BadRequest(new { error = "取消任务失败" });
            }

            // OpenClaw 任务需要额外杀掉进程
            if (task.Type == "openclaw" && task.Parameters?.TryGetValue("openclawId", out var openClawIdStr) == true
                && int.TryParse(openClawIdStr, out var openClawId))
            {
                _ = Task.Run(async () => await _openClawTaskService.CancelTaskAsync(openClawId));
            }

            var providerId = task.Parameters?.GetValueOrDefault("providerId");
            var model = task.Parameters?.GetValueOrDefault("model");
            if (!string.IsNullOrEmpty(providerId) && !string.IsNullOrEmpty(model))
            {
                var provider = _aiSettings.GetAiProviders().FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
                if (IsLocalProvider(provider))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogInformation("任务取消后自动卸载本地模型: {Provider} {Model}", providerId, model);
                            await _localDeployment.UnloadModelAsync(providerId, model);
                        }
                        catch { }
                    });
                }
            }

            return Ok(new { success = true, message = "任务已取消" });
        }

        /// <summary>
        /// 重试失败/超时的 AI 查询任务，可指定新的超时时间
        /// </summary>
        [HttpPost("{taskId}/retry")]
        public async Task<ActionResult<AiTaskResponse>> RetryAiTask(string taskId, [FromBody] RetryAiTaskRequest? retryRequest = null)
        {
            var task = _taskManager.GetTask(taskId);
            if (task == null)
            {
                return NotFound(new { error = "任务不存在" });
            }
            if (task.Status != RunnerTaskStatus.Timeout && task.Status != RunnerTaskStatus.Failed)
            {
                return BadRequest(new { error = "只能重试失败或超时的任务" });
            }
            if (task.Type != "ai_query")
            {
                return BadRequest(new { error = "目前仅支持重试 AI 查询任务" });
            }

            // 从原任务参数中提取信息
            var query = task.Parameters?.GetValueOrDefault("query") ?? "";
            var saveToVault = task.Parameters?.GetValueOrDefault("saveToVault") == "True";
            var model = retryRequest?.Model ?? task.Parameters?.GetValueOrDefault("model") ?? "";
            var vaultId = task.Parameters?.GetValueOrDefault("vaultId") ?? "";
            var industry = task.Parameters?.GetValueOrDefault("industry") ?? "";
            var timeoutMinutes = retryRequest?.TimeoutMinutes > 0 ? retryRequest.TimeoutMinutes : _aiSettings.AiRequestTimeoutMinutes;

            _logger.LogInformation("[RetryDebug] taskId={TaskId}, rawModel={RawModel}, industry={Industry}, vaultId={VaultId}, retryRequest.Model={RetryModel}, timeout={Timeout}",
                taskId, model, industry, vaultId, retryRequest?.Model, timeoutMinutes);

            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest(new { error = "原任务缺少查询内容" });
            }

            string modelName;
            if (!string.IsNullOrWhiteSpace(model))
            {
                modelName = model;
            }
            else
            {
                modelName = _aiSettings.AiModel;
            }
            _logger.LogInformation("[RetryDebug] resolved modelName={ModelName}, settings.AiModel={SettingsModel}", modelName, _aiSettings.AiModel);

            var retryProvider = ResolveProvider(modelName);
            _logger.LogInformation("[RetryDebug] resolved provider={ProviderId}", retryProvider?.Id ?? "(null)");
            var retryVault = !string.IsNullOrWhiteSpace(vaultId)
                ? _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)
                : null;
            var retryVaultName = retryVault?.Name ?? "";
            
            // 如果原任务需要保存到知识库，但知识库已不存在，提前报错
            if (saveToVault && retryVault == null)
            {
                _logger.LogWarning("[RetryDebug] 重试任务失败：原知识库已不存在，vaultId={VaultId}", vaultId);
                return BadRequest(new { error = "原任务对应的知识库已不存在，无法重试保存到知识库。请从AI生成页新建任务。" });
            }
            
            var retryParameters = new Dictionary<string, string>
            {
                ["query"] = query,
                ["saveToVault"] = saveToVault.ToString(),
                ["model"] = modelName,
                ["vaultId"] = vaultId,
                ["vaultName"] = retryVaultName,
                ["retriedFrom"] = taskId
            };
            if (retryProvider != null)
            {
                retryParameters["providerId"] = retryProvider.Id;
            }
            if (!string.IsNullOrWhiteSpace(industry))
            {
                retryParameters["industry"] = industry;
            }

            var retryScene = ResolveScene(industry, vaultId);
            _logger.LogInformation("[RetryDebug] resolved scene={Scene} from industry={Industry}, vaultId={VaultId}", retryScene?.ToString() ?? "(null)", industry, vaultId);

            // 创建新任务
            var newTaskId = _taskManager.CreateTask("ai_query", retryParameters);

            _ = Task.Run(async () =>
            {
                using var cts = _taskManager.CreateTaskCts(newTaskId, TimeSpan.FromMinutes(timeoutMinutes));
                try
                {
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Running);
                    await _taskManager.UpdateProgress(newTaskId, 1, 3, "准备调用 AI（重试）...");

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var requestTime = DateTime.Now;
                    await _taskManager.UpdateProgress(newTaskId, 2, 3, $"调用 AI 模型：{modelName}（超时 {timeoutMinutes} 分钟）...");
                    _logger.LogInformation("[RetryDebug] about to CallAiApiAsync with model={Model}, scene={Scene}", modelName, retryScene?.ToString() ?? "(null)");
                    var aiResult = await CallAiApiAsync(query, modelName, cts.Token, scene: retryScene, industry: industry);
                    stopwatch.Stop();

                    var sourceInfo = $"> 📌 **来源**: AI 生成（重试）  \n" +
                        $"> 🤖 **模型**: {aiResult.Model}  \n" +
                        $"> 🏢 **提供商**: {aiResult.ProviderName}  \n" +
                        $"> ⏰ **时间**: {requestTime:yyyy-MM-dd HH:mm:ss}  \n" +
                        $"> ⏱️ **耗时**: {stopwatch.ElapsedMilliseconds}ms  \n\n";

                    var content = sourceInfo + aiResult.Content;
                    var title = query.Length > 50 ? query.Substring(0, 50) + "..." : query;

                    string? notePath = null;
                    if (saveToVault)
                    {
                        // 使用之前已验证过的 retryVault，避免再次查找失败
                        var vaultPath = retryVault?.Path;
                        if (string.IsNullOrEmpty(vaultPath))
                        {
                            await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed, "必须指定有效的知识库");
                            return;
                        }

                        var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                        var aiDir = System.IO.Path.Combine(notesRoot, "AI 生成");
                        System.IO.Directory.CreateDirectory(aiDir);

                        var fileName = $"{title}.md";
                        var fullPath = System.IO.Path.Combine(aiDir, fileName);
                        await System.IO.File.WriteAllTextAsync(fullPath, content);
                        notePath = $"AI 生成/{Path.GetFileNameWithoutExtension(fileName)}";

                        // 自动为该笔记生成 Anki 记忆卡片
                        try
                        {
                            var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                            var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                            {
                                ["notePath"] = notePath,
                                ["vaultId"] = vaultId
                            });
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);
                                    var result = await _cardGenerator.GenerateFromNote(notePath, cardsPath: cardsRoot, notesBasePath: notesRoot);
                                    await _taskManager.UpdateStatus(taskId, result.Success ? RunnerTaskStatus.Success : RunnerTaskStatus.Failed,
                                        data: new { message = result.Message, cardCount = result.CardCount });
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[Retry AI Task] 卡片生成失败");
                                    await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: ex.Message);
                                }
                            });
                            _logger.LogInformation("[Retry AI Task] 笔记已保存，已创建卡片生成任务 {TaskId}：{Path}", taskId, notePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Retry AI Task] 自动触发卡片生成失败");
                        }
                    }

                    await _taskManager.UpdateProgress(newTaskId, 3, 3, "任务完成");
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Success, data: new
                    {
                        notes = new[] { new { title = title, path = notePath ?? "" } },
                        requests = new[]
                        {
                            new
                            {
                                providerId = aiResult.ProviderId,
                                providerName = aiResult.ProviderName,
                                model = aiResult.Model,
                                endpoint = aiResult.Endpoint,
                                elapsedMs = stopwatch.ElapsedMilliseconds,
                                timestamp = requestTime
                            }
                        },
                        query = query,
                        totalElapsedMs = stopwatch.ElapsedMilliseconds,
                        retriedFrom = taskId
                    });
                }
                catch (OperationCanceledException)
                {
                    var currentTask = _taskManager.GetTask(newTaskId);
                    if (currentTask?.Status == RunnerTaskStatus.Cancelled)
                    {
                        _logger.LogInformation("AI 重试任务被用户取消：{TaskId}", newTaskId);
                    }
                    else
                    {
                        _logger.LogWarning("AI 重试任务超时：{TaskId}", newTaskId);
                        await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Timeout,
                            $"AI 调用超时（{timeoutMinutes} 分钟）| 模型: {modelName}");
                    }
                }
                catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("index"))
                {
                    // OpenAI SDK 在解析阿里云内容审核响应时（choices为空）会崩溃
                    _logger.LogWarning(ex, "AI 重试任务触发内容审核：{TaskId}", newTaskId);
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed,
                        "AI 内容审核未通过：输入内容可能包含敏感信息，请修改后重试。");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI 重试任务失败：{TaskId}", newTaskId);
                    await _taskManager.UpdateStatus(newTaskId, RunnerTaskStatus.Failed, ex.Message);
                }
                finally
                {
                    _taskManager.RemoveTaskCts(newTaskId);
                }
            });

            return Ok(new AiTaskResponse
            {
                Success = true,
                Message = "重试任务已创建",
                TaskId = newTaskId
            });
        }

        [HttpPost("ai-query")]
        public async Task<ActionResult<AiTaskResponse>> CreateAiTask([FromBody] AiTaskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest(new { error = "查询内容不能为空" });
            }

            try
            {
                // 优先使用用户指定的模型，否则使用配置的主模型
                string modelName;
                if (!string.IsNullOrWhiteSpace(request.Model))
                {
                    modelName = request.Model.Trim();
                }
                else
                {
                    modelName = _aiSettings.AiModel;
                }

                var provider = ResolveProvider(modelName);
                _logger.LogInformation("创建 AI 任务: Query={Query}, VaultId={VaultId}, SaveToVault={SaveToVault}, AutoSplit={AutoSplit}",
                    request.Query, request.VaultId ?? "(null)", request.SaveToVault, request.AutoSplit);

                var vault = !string.IsNullOrWhiteSpace(request.VaultId)
                    ? _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)
                    : null;
                var vaultName = vault?.Name ?? "";
                
                // 如果要保存到知识库，但知识库不存在，提前报错
                if (request.SaveToVault && vault == null)
                {
                    _logger.LogWarning("创建 AI 任务失败：知识库不存在，vaultId={VaultId}", request.VaultId ?? "(null)");
                    return BadRequest(new { error = "指定的知识库不存在，请重新选择。" });
                }
                
                var parameters = new Dictionary<string, string>
                {
                    ["query"] = request.Query,
                    ["saveToVault"] = request.SaveToVault.ToString(),
                    ["model"] = modelName,
                    ["vaultId"] = request.VaultId ?? "",
                    ["vaultName"] = vaultName
                };
                if (provider != null)
                {
                    parameters["providerId"] = provider.Id;
                }
                if (!string.IsNullOrWhiteSpace(request.Industry))
                {
                    parameters["industry"] = request.Industry;
                }

                var scene = ResolveScene(request.Industry, request.VaultId);
                var taskId = _taskManager.CreateTask("ai_query", parameters);
                _logger.LogInformation("[CreateDebug] CreateAiTask: model={Model}, industry={Industry}, vaultId={VaultId}, scene={Scene}",
                    modelName, request.Industry, request.VaultId, scene?.ToString() ?? "(null)");

                _ = Task.Run(async () =>
                {
                    using var cts = _taskManager.CreateTaskCts(taskId, TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes));
                    try
                    {
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);
                        await _taskManager.UpdateProgress(taskId, 1, 3, "准备调用 AI...");

                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var requestTime = DateTime.Now;
                        await _taskManager.UpdateProgress(taskId, 2, 3, $"调用 AI 模型：{modelName}...");

                        var aiResult = await CallAiApiAsync(request.Query, modelName, cts.Token, request.SystemPrompt, scene, request.Industry);
                        stopwatch.Stop();

                        var sourceInfo = $"> 📌 **来源**: AI 生成  \n" +
                            $"> 🤖 **模型**: {aiResult.Model}  \n" +
                            $"> 🏢 **提供商**: {aiResult.ProviderName}  \n" +
                            $"> ⏰ **时间**: {requestTime:yyyy-MM-dd HH:mm:ss}  \n" +
                            $"> ⏱️ **耗时**: {stopwatch.ElapsedMilliseconds}ms  \n\n";
                        
                        var content = sourceInfo + aiResult.Content;
                        var title = request.Query.Length > 50 ? request.Query.Substring(0, 50) + "..." : request.Query;
                        
                        string? notePath = null;
                        string? fullPath = null;
                        // AI 生成笔记统一写入 notes/ 子目录，便于后续 /vault/read/{path} 读取
                        if (request.SaveToVault)
                        {
                            var vault = await FindVaultWithRetryAsync(request.VaultId ?? "");
                            _logger.LogInformation("AI 任务查找知识库: VaultId={VaultId}, Found={Found}, Path={Path}",
                                request.VaultId ?? "(null)", vault != null, vault?.Path ?? "(null)");
                            var vaultPath = vault?.Path;
                            if (string.IsNullOrEmpty(vaultPath))
                            {
                                _logger.LogError("AI 任务找不到知识库: VaultId={VaultId}, 可用知识库 IDs={AvailableVaultIds}",
                                    request.VaultId ?? "(null)", string.Join(", ", _vaultSettings.GetVaults().Select(v => v.Id)));
                                await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, "必须指定有效的知识库");
                                return;
                            }

                            var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                            var aiDir = System.IO.Path.Combine(notesRoot, "AI 生成");
                            System.IO.Directory.CreateDirectory(aiDir);

                            var fileName = $"{title}.md";
                            fullPath = System.IO.Path.Combine(aiDir, fileName);
                            await System.IO.File.WriteAllTextAsync(fullPath, content);
                            notePath = $"AI 生成/{Path.GetFileNameWithoutExtension(fileName)}";

                            // 自动为该笔记生成 Anki 记忆卡片
                            try
                            {
                                var cardsRoot = System.IO.Path.Combine(vaultPath, "cards");
                                var taskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                                {
                                    ["notePath"] = notePath,
                                    ["vaultId"] = request.VaultId ?? ""
                                });
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);
                                        var result = await _cardGenerator.GenerateFromNote(notePath, cardsPath: cardsRoot, notesBasePath: notesRoot);
                                        await _taskManager.UpdateStatus(taskId, result.Success ? RunnerTaskStatus.Success : RunnerTaskStatus.Failed,
                                            data: new { message = result.Message, cardCount = result.CardCount });
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[AI Task] 卡片生成失败");
                                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: ex.Message);
                                    }
                                });
                                _logger.LogInformation("[AI Task] 笔记已保存，已创建卡片生成任务 {TaskId}：{Path}", taskId, notePath);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[AI Task] 自动触发卡片生成失败");
                            }
                        }

                        await _taskManager.UpdateProgress(taskId, 3, 3, "任务完成");
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: new
                        {
                            notes = new[] { new { title = title, path = notePath ?? "" } },
                            requests = new[]
                            {
                                new
                                {
                                    providerId = aiResult.ProviderId,
                                    providerName = aiResult.ProviderName,
                                    model = aiResult.Model,
                                    endpoint = aiResult.Endpoint,
                                    elapsedMs = stopwatch.ElapsedMilliseconds,
                                    timestamp = requestTime
                                }
                            },
                            query = request.Query,
                            totalElapsedMs = stopwatch.ElapsedMilliseconds
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        var currentTask = _taskManager.GetTask(taskId);
                        if (currentTask?.Status == RunnerTaskStatus.Cancelled)
                        {
                            _logger.LogInformation("AI 查询任务被用户取消：{TaskId}", taskId);
                        }
                        else
                        {
                            _logger.LogWarning("AI 查询任务超时：{TaskId}", taskId);
                            var timeoutMin = _aiSettings.AiRequestTimeoutMinutes;
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Timeout,
                                $"AI 调用超时（{timeoutMin} 分钟）| 模型: {modelName} | 提示词: {TruncateForError(request.Query, 100)}");
                        }
                    }
                    catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("index"))
                    {
                        // OpenAI SDK 在解析阿里云内容审核响应时（choices为空）会崩溃
                        _logger.LogWarning(ex, "AI 查询任务触发内容审核：{TaskId}", taskId);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed,
                            "AI 内容审核未通过：输入内容可能包含敏感信息，请修改后重试。");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AI 查询任务失败：{TaskId}", taskId);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, ex.Message);
                    }
                    finally
                    {
                        _taskManager.RemoveTaskCts(taskId);
                    }
                });

                return Ok(new AiTaskResponse
                {
                    Success = true,
                    Message = "任务已创建",
                    TaskId = taskId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 AI 任务失败");
                return Ok(new AiTaskResponse
                {
                    Success = false,
                    Message = $"创建失败：{ex.Message}"
                });
            }
        }

        [HttpPost("vault-generation")]
        public async Task<ActionResult<VaultGenerationResponse>> CreateVaultGenerationTask([FromBody] VaultGenerationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Industry) || string.IsNullOrWhiteSpace(request.Keyword))
            {
                return BadRequest(new { error = "行业和关键词不能为空" });
            }

            var noteCount = request.NoteCount;
            if (noteCount < 1 || noteCount > 50)
                noteCount = 30;

            try
            {
                string modelName;
                if (!string.IsNullOrWhiteSpace(request.Model))
                {
                    modelName = request.Model.Trim();
                }
                else
                {
                    modelName = _aiSettings.AiModel;
                }

                var provider = ResolveProvider(modelName);
                if (provider == null)
                {
                    return BadRequest(new { error = "未找到可用的 AI 提供商，请检查模型配置" });
                }

                _logger.LogInformation("创建 AI 知识库生成任务: Industry={Industry}, Keyword={Keyword}, Model={Model}, NoteCount={NoteCount}",
                    request.Industry, request.Keyword, modelName, noteCount);

                var parameters = new Dictionary<string, string>
                {
                    ["industry"] = request.Industry,
                    ["keyword"] = request.Keyword,
                    ["model"] = modelName,
                    ["noteCount"] = noteCount.ToString(),
                    ["providerId"] = provider.Id,
                };

                var taskId = _taskManager.CreateTask("ai_vault_generation", parameters);

                _ = Task.Run(async () =>
                {
                    using var cts = _taskManager.CreateTaskCts(taskId, TimeSpan.FromMinutes(_aiSettings.AiRequestTimeoutMinutes * 4));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.ApplicationStopping, cts.Token);

                    var totalSteps = 4 + noteCount;
                    var currentStep = 0;

                    try
                    {
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Running);

                        var options = Services.AiClientService.BuildChatOptions(temperature: 0.7f, maxOutputTokens: 4000);

                        // Step 1: 生成知识库名称
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, "生成知识库名称...");
                        var vaultName = await GenerateVaultNameAsync(provider, modelName, request.Industry, request.Keyword, options, linkedCts.Token);
                        _logger.LogInformation("[AiVaultGeneration] 任务 {TaskId} 生成名称: {VaultName}", taskId, vaultName);

                        // Step 2: 生成 system prompt
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, "生成系统提示词...");
                        var systemPrompt = await GenerateSystemPromptAsync(provider, modelName, request.Industry, options, linkedCts.Token);
                        _logger.LogInformation("[AiVaultGeneration] 任务 {TaskId} 生成 system prompt, 长度={Length}", taskId, systemPrompt.Length);

                        // Step 3: 生成笔记列表
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, $"生成笔记大纲 ({noteCount} 条)...");
                        var outline = await GenerateNoteListAsync(provider, modelName, vaultName, request.Industry, request.Keyword, systemPrompt, noteCount, options, linkedCts.Token);
                        _logger.LogInformation("[AiVaultGeneration] 任务 {TaskId} 生成大纲: {Count} 条", taskId, outline.Count);

                        if (outline.Count == 0)
                        {
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, "笔记大纲生成失败，返回空列表");
                            return;
                        }

                        // Step 4: 创建知识库
                        currentStep++;
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, $"创建知识库: {vaultName}...");
                        var vault = _vaultSettings.AddVault(vaultName, "", request.Industry);
                        var vaultId = vault.Id;
                        var vaultPath = vault.Path;
                        _logger.LogInformation("[AiVaultGeneration] 任务 {TaskId} 创建知识库: {VaultId}", taskId, vaultId);

                        // Ensure notes directory exists
                        var notesRoot = System.IO.Path.Combine(vaultPath, "notes");
                        System.IO.Directory.CreateDirectory(notesRoot);

                        // Step 5+: 逐条生成笔记内容
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        var generatedNotes = new List<(string title, string path)>();

                        for (int i = 0; i < outline.Count; i++)
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();
                            currentStep++;
                            var item = outline[i];
                            await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, $"生成笔记 ({i + 1}/{outline.Count}): {item.title}");

                            try
                            {
                                var content = await GenerateNoteContentAsync(
                                    provider, modelName, item.title, item.category, vaultName, systemPrompt, options, linkedCts.Token);

                                var safeTitle = item.title.Replace("\\", "_").Replace("/", "_").Replace(":", "_")
                                    .Replace("*", "_").Replace("?", "_").Replace("\"", "_").Replace("<", "_")
                                    .Replace(">", "_").Replace("|", "_");
                                var categoryDir = System.IO.Path.Combine(notesRoot, item.category);
                                System.IO.Directory.CreateDirectory(categoryDir);
                                var noteFilePath = System.IO.Path.Combine(categoryDir, $"{safeTitle}.md");
                                await System.IO.File.WriteAllTextAsync(noteFilePath, content, linkedCts.Token);
                                var notePath = $"{item.category}/{safeTitle}";
                                generatedNotes.Add((item.title, notePath));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[AiVaultGeneration] 笔记 \"{Title}\" 生成失败，跳过", item.title);
                            }
                        }

                        stopwatch.Stop();

                        // 重建 FTS5 索引
                        await _taskManager.UpdateProgress(taskId, currentStep, totalSteps, "重建搜索索引...");
                        await _vaultNoteIndexer.IndexVaultAsync(vaultId, vaultPath, linkedCts.Token);

                        await _taskManager.UpdateProgress(taskId, totalSteps, totalSteps, "任务完成");
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: new
                        {
                            vaultId = vaultId,
                            vaultName = vaultName,
                            industry = request.Industry,
                            noteCount = generatedNotes.Count,
                            notes = generatedNotes.Select(n => new { title = n.title, path = n.path }).ToArray(),
                            model = modelName,
                            providerName = provider?.Name ?? "",
                            totalElapsedMs = stopwatch.ElapsedMilliseconds
                        });

                        _logger.LogInformation("[AiVaultGeneration] 任务 {TaskId} 完成: {VaultName}, {NoteCount} 条笔记, 耗时 {ElapsedMs}ms",
                            taskId, vaultName, generatedNotes.Count, stopwatch.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException)
                    {
                        var currentTask = _taskManager.GetTask(taskId);
                        if (currentTask?.Status == RunnerTaskStatus.Cancelled)
                        {
                            _logger.LogInformation("AI 知识库生成任务被用户取消：{TaskId}", taskId);
                        }
                        else
                        {
                            _logger.LogWarning("AI 知识库生成任务超时：{TaskId}", taskId);
                            var timeoutMin = _aiSettings.AiRequestTimeoutMinutes * 4;
                            await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Timeout,
                                $"AI 知识库生成超时（{timeoutMin} 分钟）| 模型: {modelName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "AI 知识库生成任务失败：{TaskId}", taskId);
                        await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, ex.Message);
                    }
                    finally
                    {
                        _taskManager.RemoveTaskCts(taskId);
                    }
                });

                return Ok(new VaultGenerationResponse
                {
                    Success = true,
                    Message = "任务已创建",
                    TaskId = taskId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 AI 知识库生成任务失败");
                return Ok(new VaultGenerationResponse
                {
                    Success = false,
                    Message = $"创建失败：{ex.Message}"
                });
            }
        }

        private async Task<string> GenerateVaultNameAsync(
            AiProviderConfig provider, string model, string industry, string keyword,
            ChatOptions options, CancellationToken ct)
        {
            var prompt = $"你是知识库命名专家。请为\"{industry}\"领域的\"{keyword}\"生成一个简短、准确、有吸引力的中文知识库名称（2-8个字）。只返回名称本身，不要有任何解释、标点或书名号。";
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "你只输出名称，不要任何额外内容。"),
                new(ChatRole.User, prompt)
            };
            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_name");
            var name = (response.Text ?? "").Trim()
                .Replace("\"", "").Replace("'", "").Replace("「", "").Replace("」", "")
                .Replace("《", "").Replace("》", "").Replace("\n", "").Replace("\r", "");
            if (name.Length > 20) name = name.Substring(0, 20);
            if (string.IsNullOrWhiteSpace(name)) name = $"{industry}知识库";
            return name;
        }

        private async Task<string> GenerateSystemPromptAsync(
            AiProviderConfig provider, string model, string industry,
            ChatOptions options, CancellationToken ct)
        {
            var prompt = $"""
                你是一位专业的系统提示词工程师。你的任务是为"{industry}"行业生成一个系统提示词，该提示词将用于指导 AI 生成该领域的「原子笔记」。

                原子笔记必须严格遵循以下原则：
                1. 一个笔记 = 一个核心概念，聚焦单一主题，绝不展开多个主题
                2. 内容高度结构化，拒绝冗长描述和背景铺垫
                3. 每篇笔记必须包含：核心定义（1-3句话）、关键要点（3-5条）、关联概念（1-2个）、记忆锚点（口诀/歌诀/类比）、典型场景/案例
                4. 使用 Markdown 格式输出
                5. 语言专业、准确、客观，使用行业标准术语

                请直接返回生成的系统提示词内容，不要有任何额外说明。
                """;
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "你只输出提示词内容，不要任何额外内容。"),
                new(ChatRole.User, prompt)
            };
            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_prompt");
            var promptText = (response.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(promptText))
                promptText = $"你是{industry}领域专家，请用专业、严谨、结构化的方式回答问题。";
            return promptText;
        }

        private async Task<List<NoteOutlineItem>> GenerateNoteListAsync(
            AiProviderConfig provider, string model, string vaultName, string industry, string keyword,
            string systemPrompt, int noteCount, ChatOptions options, CancellationToken ct)
        {
            var prompt = $"{systemPrompt}\n\n请为知识库\"{vaultName}\"（{industry}-{keyword}）生成 {noteCount} 条笔记的大纲。每条笔记包含：title（标题，简洁专业）、category（分类，2-4字）。\n\n要求：\n1. 覆盖{keyword}的核心知识点，由浅入深\n2. 标题要具体，避免过于笼统\n3. 分类要合理，同一知识库内分类不宜超过5个\n4. 必须严格返回 JSON 数组格式，不要加 markdown 代码块标记\n\n格式示例：\n[{{\"title\": \"示例标题\", \"category\": \"示例分类\"}}]";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "你是一个严格的 JSON 生成器，只输出合法的 JSON 数组，不添加任何额外文字或 markdown 标记。"),
                new(ChatRole.User, prompt)
            };

            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_outline");
            var raw = response.Text ?? "";

            // 尝试从代码块中提取 JSON
            var jsonStr = raw;
            var codeBlock = System.Text.RegularExpressions.Regex.Match(raw, @"```(?:json)?\s*([\s\S]*?)```");
            if (codeBlock.Success) jsonStr = codeBlock.Groups[1].Value;

            try
            {
                var outline = JsonSerializer.Deserialize<List<NoteOutlineItem>>(jsonStr, JsonHelper.CaseInsensitive);
                if (outline == null || outline.Count == 0) throw new Exception("解析为空");
                return outline.Take(noteCount).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiVaultGeneration] 大纲 JSON 解析失败，尝试 fallback 解析");
                // Fallback: 从文本中逐行提取 title 和 category
                var fallback = new List<NoteOutlineItem>();
                var lines = raw.Split('\n').Where(l => l.Contains("\"title\"")).ToList();
                foreach (var line in lines)
                {
                    var titleMatch = System.Text.RegularExpressions.Regex.Match(line, @"""title""\s*:\s*""([^""]+)""");
                    var catMatch = System.Text.RegularExpressions.Regex.Match(line, @"""category""\s*:\s*""([^""]+)""");
                    if (titleMatch.Success)
                    {
                        fallback.Add(new NoteOutlineItem
                        {
                            title = titleMatch.Groups[1].Value,
                            category = catMatch.Success ? catMatch.Groups[1].Value : "其他"
                        });
                    }
                }
                return fallback.Take(noteCount).ToList();
            }
        }

        private async Task<string> GenerateNoteContentAsync(
            AiProviderConfig provider, string model, string title, string category, string vaultName,
            string systemPrompt, ChatOptions options, CancellationToken ct)
        {
            var prompt = $"""
                {systemPrompt}

                请严格遵循「原子笔记」原则生成该笔记的 Markdown 内容：
                知识库：{vaultName}
                分类：{category}
                标题：{title}

                1. **聚焦单一主题**：只讨论"{title}"这一个核心概念，不展开关联概念
                2. **高度结构化**：必须包含以下部分（按顺序）：
                   - 核心定义（1-3句话精确定义）
                   - 关键要点（3-5条最核心的知识点，用列表）
                   - 关联概念（1-2个直接关联的其他概念，仅名称）
                   - 记忆锚点（1个简短的口诀、歌诀或类比，帮助记忆）
                   - 典型场景/案例（1个真实或典型的应用示例）
                3. **无冗余**：不讨论历史沿革、文化背景、个人经验
                4. **语言风格**：专业、清晰、客观、中立

                请直接返回 Markdown 格式的笔记内容，不要有任何额外说明。
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, prompt)
            };

            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(provider, model, messages, options, ct, operation: "vault_gen_content");
            return response.Text ?? "";
        }

        private class NoteOutlineItem
        {
            public string title { get; set; } = "";
            public string category { get; set; } = "";
        }

        /// <summary>
        /// 根据行业名称或知识库 ID 解析对应的场景
        /// </summary>
        private AppScene? ResolveScene(string? industry, string? vaultId)
        {
            // 优先使用显式传入的行业
            var target = !string.IsNullOrWhiteSpace(industry) ? industry.Trim() : null;
            _logger.LogInformation("[SceneDebug] ResolveScene input: industry={Industry}, vaultId={VaultId}, initialTarget={Target}", industry, vaultId, target);

            // 其次从知识库的 Industry 字段推导
            if (target == null && !string.IsNullOrWhiteSpace(vaultId))
            {
                var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
                target = vault?.Industry;
                _logger.LogInformation("[SceneDebug] looked up vault: name={VaultName}, industry={VaultIndustry}", vault?.Name, vault?.Industry);
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                _logger.LogInformation("[SceneDebug] ResolveScene result: (null) - empty target");
                return null;
            }

            AppScene? result = target switch
            {
                "开发" or "计算机" or "技术" => AppScene.Computer,
                "通用" => AppScene.General,
                "中医" or "中药" or "笔记" => AppScene.Tcm,
                _ => null // 自定义行业暂无内置模板，回退到全局默认
            };
            _logger.LogInformation("[SceneDebug] ResolveScene result: {Result} for target='{Target}'", result?.ToString() ?? "(null)", target);
            return result;
        }

        private async Task<AiCallResult> CallAiApiAsync(string query, string model, CancellationToken cancellationToken, string? customSystemPrompt = null, AppScene? scene = null, string? industry = null)
        {
            var providers = _aiSettings.GetAiProviders();

            // 根据模型名称找到对应的 provider（优先匹配模型名，否则回退到主 provider）
            var provider = providers.FirstOrDefault(p =>
                p.Models.Any(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase)))
                ?? providers.FirstOrDefault(p => p.IsMain)
                ?? providers.FirstOrDefault();

            if (provider == null)
                throw new Exception("未找到可用的AI提供商");

            var apiEndpoint = provider.AiBaseUrl.TrimEnd('/') + "/chat/completions";
            _logger.LogInformation("AI 请求路由到 provider [{ProviderId}] {ProviderName}，模型：{Model}，行业：{Industry}，端点：{Endpoint}",
                provider.Id, provider.Name, model, industry ?? "(未指定)", apiEndpoint);

            // 使用自定义提示词 > 行业提示词 > 场景提示词 > 默认中医提示词
            // 注意：场景(Scene)只用于菜单分类，不允许影响生成笔记；行业(Industry)决定提示词
            string systemPrompt;
            if (!string.IsNullOrWhiteSpace(customSystemPrompt))
            {
                systemPrompt = customSystemPrompt;
                _logger.LogInformation("[SceneDebug] system prompt source: custom");
            }
            else if (!string.IsNullOrWhiteSpace(industry))
            {
                // 优先根据行业名称查找模板（支持自定义场景配置）
                var template = _scenePromptService.GetTemplateByName(industry);
                systemPrompt = template.ChatSystemPrompt;
                _logger.LogInformation("[SceneDebug] system prompt source: industry={Industry}, template={TemplateName}", industry, template.DisplayName);
            }
            else if (scene.HasValue)
            {
                systemPrompt = _scenePromptService.GetTemplate(scene.Value).ChatSystemPrompt;
                _logger.LogInformation("[SceneDebug] system prompt source: scene={Scene}", scene.Value);
            }
            else
            {
                // 默认使用中医提示词（与Cloud版本保持一致）
                systemPrompt = _scenePromptService.GetTemplateByName("笔记").ChatSystemPrompt;
                _logger.LogInformation("[SceneDebug] system prompt source: default(笔记)");
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, query)
            };
            var options = Services.AiClientService.BuildChatOptions();

            ChatResponse response;
            try
            {
                response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                    provider, model, messages, options, cancellationToken);
            }
            catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("index"))
            {
                // OpenAI SDK 在解析阿里云内容审核响应时（choices为空）会崩溃
                _logger.LogWarning(ex, "AI 返回内容审核失败响应（choices为空），可能是敏感内容触发阿里云拦截");
                throw new Exception("AI 内容审核未通过：输入内容可能包含敏感信息，请修改后重试。", ex);
            }
            var content = response.Text;

            return new AiCallResult
            {
                Content = content ?? throw new Exception("AI 返回内容为空。有可能是当前所用的 AI 模型不支持该问题，建议换一个 AI 提供商或模型再试试。"),
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                Model = model,
                Endpoint = apiEndpoint
            };
        }

        /// <summary>
        /// AI API 调用结果，包含内容和请求详情
        /// </summary>
        private class AiCallResult
        {
            public string Content { get; set; } = "";
            public string ProviderId { get; set; } = "";
            public string ProviderName { get; set; } = "";
            public string Model { get; set; } = "";
            public string Endpoint { get; set; } = "";
        }

        /// <summary>
        /// 获取任务历史列表
        /// </summary>
        [HttpGet("history")]
        public ActionResult<TaskHistoryResponse> GetTaskHistory([FromQuery] int limit = 100, [FromQuery] int offset = 0)
        {
            try
            {
                var tasks = _taskManager.GetAllTasks(limit, offset);
                var total = _taskManager.GetTaskCount();
                
                return Ok(new TaskHistoryResponse
                {
                    Success = true,
                    Tasks = tasks.Select(t => new TaskHistoryItem
                    {
                        TaskId = t.Id,
                        TaskType = t.Type,
                        Status = t.Status.ToString(),
                        Progress = (int)t.Progress.Percentage,
                        ProgressMessage = t.Progress.Message,
                        CreatedAt = t.CreatedAt,
                        UpdatedAt = t.UpdatedAt
                    }).ToList(),
                    Total = total
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务历史失败");
                return StatusCode(500, new { error = "获取任务历史失败" });
            }
        }

        /// <summary>
        /// 清理指定时间之前的任务
        /// </summary>
        [HttpPost("cleanup")]
        public ActionResult<CleanupResponse> CleanupTasks([FromBody] CleanupRequest request)
        {
            try
            {
                int count;
                if (request.OlderThanDays > 0)
                {
                    var retention = TimeSpan.FromDays(request.OlderThanDays);
                    count = _taskManager.CleanupOldTasks(retention);
                }
                else
                {
                    // 默认清理所有已完成的任务
                    count = _taskManager.CleanupAllCompletedTasks();
                }
                
                _logger.LogInformation("已清理 {Count} 个任务", count);
                return Ok(new CleanupResponse { Success = true, DeletedCount = count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理任务失败");
                return StatusCode(500, new { error = "清理任务失败" });
            }
        }

        /// <summary>
        /// 清空所有任务历史（包括进行中的任务）
        /// </summary>
        [HttpDelete("all")]
        public ActionResult DeleteAllTasks()
        {
            try
            {
                _taskManager.DeleteAllTasks();
                return Ok(new { success = true, message = "所有任务已清空" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空所有任务失败");
                return StatusCode(500, new { error = "清空任务失败" });
            }
        }

        /// <summary>
        /// 获取任务统计信息
        /// </summary>
        [HttpGet("stats")]
        public ActionResult<TaskStatsResponse> GetTaskStats()
        {
            try
            {
                var total = _taskManager.GetTaskCount();
                var pending = _taskManager.GetTasksByStatus("Pending", 1000).Count;
                var running = _taskManager.GetTasksByStatus("Running", 1000).Count;
                var completed = _taskManager.GetTasksByStatus("Success", 1000).Count;
                var failed = _taskManager.GetTasksByStatus("Failed", 1000).Count;
                
                return Ok(new TaskStatsResponse
                {
                    Success = true,
                    Total = total,
                    Pending = pending,
                    Running = running,
                    Completed = completed,
                    Failed = failed
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务统计失败");
                return StatusCode(500, new { error = "获取任务统计失败" });
            }
        }

        /// <summary>
        /// 截断字符串用于错误信息
        /// </summary>
        private static string TruncateForError(string? text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }
    }

}
