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
        private async Task<ActionResult<AiTaskResponse>> HandleRetryAiTaskAsync(string taskId, RetryAiTaskRequest? retryRequest)
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
                            var cardTaskId = _taskManager.CreateTask("anki_card_generate", new Dictionary<string, string>
                            {
                                ["notePath"] = notePath,
                                ["vaultId"] = vaultId
                            });
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _taskManager.UpdateStatus(cardTaskId, RunnerTaskStatus.Running);
                                    var result = await _cardGenerator.GenerateFromNote(notePath, cardsPath: cardsRoot, notesBasePath: notesRoot);
                                    await _taskManager.UpdateStatus(cardTaskId, result.Success ? RunnerTaskStatus.Success : RunnerTaskStatus.Failed,
                                        data: new { message = result.Message, cardCount = result.CardCount });
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[Retry AI Task] 卡片生成失败");
                                    await _taskManager.UpdateStatus(cardTaskId, RunnerTaskStatus.Failed, error: ex.Message);
                                }
                            });
                            _logger.LogInformation("[Retry AI Task] 笔记已保存，已创建卡片生成任务 {TaskId}：{Path}", cardTaskId, notePath);
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
    }
}
