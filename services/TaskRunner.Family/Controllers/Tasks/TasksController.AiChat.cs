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
    public partial class TasksController : ControllerBase
    {
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
    }
}
