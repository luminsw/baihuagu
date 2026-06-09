using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using TaskRunner.Contracts.LocalModels;
using TaskRunner.Contracts.OpenClaw;
using TaskRunner.Data.Entities;
using TaskRunner.Models;

namespace TaskRunner.Services
{
    /// <summary>
    /// 本地模型部署服务：协调模型下载、部署和 AI Provider 自动配置
    /// </summary>
    public class LocalModelDeploymentService
    {
        private readonly ILogger<LocalModelDeploymentService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly LocalAiAutoStarter _autoStarter;
        private readonly AiConfigService _aiConfigService;
        private readonly LocalModelSettingsService _localModelSettings;
        private readonly AiSettingsService _aiSettings;
        private readonly IMemoryCache _cache;

        private readonly OllamaService _ollama;
        private readonly LmStudioService _lmStudio;
        private readonly LmStudioDownloadService _lmStudioDownload;
        private readonly LlamaCppService _llamaCpp;

        // 内存中的部署任务状态
        private static readonly ConcurrentDictionary<string, DeployTaskStatusDto> _tasks = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCancellations = new();

        // 缓存配置
        private const string ToolsCacheKey = "local_tools";
        private const string RunningModelsCacheKey = "running_models";

        public LocalModelDeploymentService(
            ILogger<LocalModelDeploymentService> logger,
            IHttpClientFactory httpClientFactory,
            LocalAiAutoStarter autoStarter,
            AiConfigService aiConfigService,
            LocalModelSettingsService localModelSettings,
            AiSettingsService aiSettings,
            IMemoryCache cache,
            OllamaService ollama,
            LmStudioService lmStudio,
            LmStudioDownloadService lmStudioDownload,
            LlamaCppService llamaCpp)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _autoStarter = autoStarter;
            _aiConfigService = aiConfigService;
            _localModelSettings = localModelSettings;
            _aiSettings = aiSettings;
            _cache = cache;
            _ollama = ollama;
            _lmStudio = lmStudio;
            _lmStudioDownload = lmStudioDownload;
            _llamaCpp = llamaCpp;
        }

        #region Task Management

        public DeployTaskStatusDto? GetRunnerTaskStatus(string taskId)
        {
            return _tasks.TryGetValue(taskId, out var status) ? status : null;
        }

        public bool CancelTask(string taskId)
        {
            if (_taskCancellations.TryRemove(taskId, out var cts))
            {
                cts.Cancel();
                if (_tasks.TryGetValue(taskId, out var task))
                {
                    task.Status = "failed";
                    task.ErrorMessage = "用户取消";
                    task.CompletedAt = DateTime.Now;
                }
                return true;
            }
            return false;
        }

        public void CleanupOldTasks(TimeSpan maxAge)
        {
            var cutoff = DateTime.Now - maxAge;
            var keysToRemove = _tasks
                .Where(kv => kv.Value.Status is "completed" or "failed" && kv.Value.CompletedAt < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _tasks.TryRemove(key, out _);
                _taskCancellations.TryRemove(key, out _);
            }
        }

        #endregion

        #region Deploy

        public async Task<DeployLocalModelResult> DeployAsync(DeployLocalModelRequest request)
        {
            var model = ModelDatabase.GetById(request.ModelId);
            if (model == null)
            {
                return new DeployLocalModelResult
                {
                    Success = false,
                    Message = $"未找到模型: {request.ModelId}"
                };
            }

            var taskId = Guid.NewGuid().ToString("N")[..12];
            var cts = new CancellationTokenSource();
            _taskCancellations[taskId] = cts;

            var taskStatus = new DeployTaskStatusDto
            {
                TaskId = taskId,
                ModelId = model.Id,
                ModelName = model.Name,
                Status = "pending",
                ProgressPercent = 0,
                CurrentStep = "准备部署",
                CreatedAt = DateTime.Now,
                Logs = new List<string> { $"[{DateTime.Now:HH:mm:ss}] 开始部署: {model.Name} ({model.OllamaModelName})" }
            };
            _tasks[taskId] = taskStatus;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (request.TargetTool.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                    {
                        await DeployToOllamaAsync(taskStatus, model, cts.Token);
                    }
                    else if (request.TargetTool.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                    {
                        await DeployToLmStudioAsync(taskStatus, model, cts.Token);
                    }
                    else
                    {
                        throw new NotSupportedException($"不支持的部署工具: {request.TargetTool}");
                    }
                }
                catch (OperationCanceledException)
                {
                    taskStatus.Status = "failed";
                    taskStatus.ErrorMessage = "部署已取消";
                    taskStatus.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 部署已取消");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "模型部署失败: {ModelId}", model.Id);
                    taskStatus.Status = "failed";
                    taskStatus.ErrorMessage = ex.Message;
                    taskStatus.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 错误: {ex.Message}");
                }
                finally
                {
                    taskStatus.CompletedAt = DateTime.Now;
                    _taskCancellations.TryRemove(taskId, out _);
                }
            }, cts.Token);

            return new DeployLocalModelResult
            {
                Success = true,
                TaskId = taskId,
                Message = "部署任务已启动"
            };
        }

        private async Task DeployToOllamaAsync(DeployTaskStatusDto task, ModelEntry model, CancellationToken ct)
        {
            task.Status = "running";

            task.CurrentStep = "检查 Ollama 安装";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 Ollama 安装...");
            var ollamaVersion = await _ollama.GetVersionAsync(ct);
            if (string.IsNullOrEmpty(ollamaVersion))
            {
                throw new InvalidOperationException(
                    "Ollama 未安装。请访问 https://ollama.com 下载安装。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] Ollama 版本: {ollamaVersion}");

            task.CurrentStep = "启动 Ollama 服务";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 Ollama 服务状态...");
            var running = await _autoStarter.TryEnsureRunningAsync("ollama", "http://localhost:11434/v1");
            if (!running)
            {
                throw new InvalidOperationException("Ollama 服务启动失败，请手动启动后重试。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] Ollama 服务已就绪");

            var requiredBytes = (long)(model.SizeGiB * 1.2 * 1024 * 1024 * 1024);
            var availableBytes = _ollama.GetModelsDirFreeSpace();
            if (availableBytes > 0 && availableBytes < requiredBytes)
            {
                throw new InvalidOperationException(
                    $"磁盘空间不足。模型需要约 {model.SizeGiB * 1.2:F1} GB，可用空间仅 {availableBytes / (1024.0 * 1024 * 1024):F1} GB。");
            }

            task.CurrentStep = "下载模型";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 开始下载: ollama pull {model.OllamaModelName}");
            await _ollama.PullModelAsync(task, model.OllamaModelName, ct);

            task.CurrentStep = "验证部署";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 验证模型...");
            var verified = await _ollama.VerifyModelAsync(model.OllamaModelName, ct);
            if (!verified)
            {
                throw new InvalidOperationException("模型下载完成但验证失败，请检查 Ollama 日志。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 模型验证通过");

            task.CurrentStep = "配置 AI Provider";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 添加到 AI 服务商配置...");
            ConfigureOllamaProvider(model);
            task.AutoConfiguredProvider = true;
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] AI Provider 配置完成");

            task.Status = "completed";
            task.ProgressPercent = 100;
            task.CurrentStep = "部署完成";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] ✅ 部署成功！模型已可用。");
        }

        private async Task DeployToLmStudioAsync(DeployTaskStatusDto task, ModelEntry model, CancellationToken ct)
        {
            task.Status = "running";

            task.CurrentStep = "检查 LM Studio";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 LM Studio 安装...");
            var lmsVersion = await _lmStudio.GetVersionAsync(ct);
            if (string.IsNullOrEmpty(lmsVersion))
            {
                throw new InvalidOperationException(
                    "LM Studio CLI (lms) 未安装。请访问 https://lmstudio.ai 下载安装，并确保 lms 命令在 PATH 中。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] LM Studio CLI: {lmsVersion}");

            task.CurrentStep = "启动 LM Studio 服务";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 LM Studio 服务状态...");
            var running = await _autoStarter.TryEnsureRunningAsync("lmstudio", "http://localhost:1234/v1");
            if (!running)
            {
                throw new InvalidOperationException("LM Studio 服务启动失败，请手动启动后重试。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] LM Studio 服务已就绪");

            var requiredBytes = (long)(model.SizeGiB * 1.2 * 1024 * 1024 * 1024);
            var availableBytes = _lmStudio.GetModelsDirFreeSpace();
            if (availableBytes > 0 && availableBytes < requiredBytes)
            {
                throw new InvalidOperationException(
                    $"磁盘空间不足。模型需要约 {model.SizeGiB * 1.2:F1} GB，可用空间仅 {availableBytes / (1024.0 * 1024 * 1024):F1} GB。");
            }

            var searchName = model.LmStudioSearchName ?? model.Id;
            var preferredSource = _localModelSettings.PreferredDownloadSource;
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 搜索名称: {searchName}, 下载源偏好: {preferredSource}");

            task.CurrentStep = "下载模型";
            await _lmStudioDownload.PullModelAsync(task, model, preferredSource, ct);

            task.CurrentStep = "验证部署";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 验证模型...");
            var verified = await _lmStudioDownload.VerifyModelAsync(searchName, ct);
            if (!verified)
            {
                task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] ⚠️ 无法自动验证模型是否下载成功，请检查 LM Studio 界面。");
            }
            else
            {
                task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 模型验证通过");
            }

            task.CurrentStep = "配置 AI Provider";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 添加到 AI 服务商配置...");
            ConfigureLmStudioProvider(model);
            task.AutoConfiguredProvider = true;
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] AI Provider 配置完成");

            task.Status = "completed";
            task.ProgressPercent = 100;
            task.CurrentStep = "部署完成";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] ✅ 部署成功！模型已可用。");
        }

        #endregion

        #region Provider Auto-Configuration

        private void ConfigureOllamaProvider(ModelEntry model)
        {
            const string providerId = "ollama";
            const string defaultProviderName = "本地 Ollama";
            const string baseUrl = "http://localhost:11434/v1";

            var existing = _aiConfigService.GetProvider(providerId);
            List<AiModelConfig> models;
            bool isMain;
            string providerName;

            if (existing != null)
            {
                providerName = existing.Name;
                models = existing.GetModelOptions().Select(m => new AiModelConfig
                {
                    Name = m.Name,
                    IsPaid = m.IsPaid,
                    IsMain = false
                }).ToList();
                isMain = existing.IsMain;

                if (!models.Any(m => m.Name.Equals(model.OllamaModelName, StringComparison.OrdinalIgnoreCase)))
                {
                    models.Add(new AiModelConfig
                    {
                        Name = model.OllamaModelName,
                        IsPaid = false,
                        IsMain = models.Count == 0
                    });
                }
            }
            else
            {
                providerName = defaultProviderName;
                models = new List<AiModelConfig>
                {
                    new()
                    {
                        Name = model.OllamaModelName,
                        IsPaid = false,
                        IsMain = true
                    }
                };
                isMain = false;
            }

            var setting = new AiProviderSetting
            {
                ProviderId = providerId,
                ProviderName = providerName,
                BaseUrl = baseUrl,
                IsMain = isMain,
                ModelsJson = AiConfigService.SerializeModels(models),
                SortOrder = 0,
                IsEnabled = true
            };

            _aiConfigService.SaveProvider(setting, "");
            _logger.LogInformation("已自动配置 Ollama Provider，新增模型: {Model}", model.OllamaModelName);
        }

        private void ConfigureLmStudioProvider(ModelEntry model)
        {
            const string providerId = "lmstudio";
            const string defaultProviderName = "本地 LM Studio";
            const string baseUrl = "http://localhost:1234/v1";

            var existing = _aiConfigService.GetProvider(providerId);
            List<AiModelConfig> models;
            bool isMain;
            string providerName;

            if (existing != null)
            {
                providerName = existing.Name;
                models = existing.GetModelOptions().Select(m => new AiModelConfig
                {
                    Name = m.Name,
                    IsPaid = m.IsPaid,
                    IsMain = false
                }).ToList();
                isMain = existing.IsMain;

                if (!models.Any(m => m.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    models.Add(new AiModelConfig
                    {
                        Name = model.Name,
                        IsPaid = false,
                        IsMain = models.Count == 0
                    });
                }
            }
            else
            {
                providerName = defaultProviderName;
                models = new List<AiModelConfig>
                {
                    new()
                    {
                        Name = model.Name,
                        IsPaid = false,
                        IsMain = true
                    }
                };
                isMain = false;
            }

            var setting = new AiProviderSetting
            {
                ProviderId = providerId,
                ProviderName = providerName,
                BaseUrl = baseUrl,
                IsMain = isMain,
                ModelsJson = AiConfigService.SerializeModels(models),
                SortOrder = 0,
                IsEnabled = true
            };

            _aiConfigService.SaveProvider(setting, "");
            _logger.LogInformation("已自动配置 LM Studio Provider，新增模型: {Model}", model.Name);
        }

        private void RemoveModelFromProviderConfig(string toolId, string modelName)
        {
            try
            {
                var providerId = toolId.ToLowerInvariant();
                var provider = _aiConfigService.GetProvider(providerId);
                if (provider == null) return;

                var modelOptions = provider.GetModelOptions();
                var originalCount = modelOptions.Count;
                var filtered = modelOptions
                    .Where(m => !m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (filtered.Count == originalCount) return;

                var setting = new AiProviderSetting
                {
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    BaseUrl = provider.AiBaseUrl,
                    IsMain = provider.IsMain,
                    ModelsJson = System.Text.Json.JsonSerializer.Serialize(filtered),
                    IsEnabled = true,
                };

                _aiConfigService.SaveProvider(setting, plainApiKey: null);
                _aiSettings.ClearAiProvidersCache();
                _logger.LogInformation("已从 AI Provider {ProviderId} 的配置中移除模型 {ModelName}", providerId, modelName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 AI Provider 配置中移除模型 {ModelName} 失败", modelName);
            }
        }

        #endregion

        #region Tool Detection

        public async Task<List<LocalToolInfoDto>> GetLocalToolsAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && _cache.TryGetValue(ToolsCacheKey, out List<LocalToolInfoDto>? cached) && cached != null)
            {
                _logger.LogDebug("本地工具状态命中缓存");
                return cached;
            }

            var tools = new List<LocalToolInfoDto>();

            // Ollama
            var ollamaVersion = await _ollama.GetVersionAsync(ct);
            var ollamaRunning = false;
            if (!string.IsNullOrEmpty(ollamaVersion))
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(2);
                    var response = await client.GetAsync("http://localhost:11434/", ct);
                    ollamaRunning = response.IsSuccessStatusCode || (int)response.StatusCode < 500;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "检测服务运行状态失败"); }
            }

            tools.Add(new LocalToolInfoDto
            {
                Id = "ollama",
                Name = "Ollama",
                IsInstalled = !string.IsNullOrEmpty(ollamaVersion),
                Version = ollamaVersion,
                IsRunning = ollamaRunning,
                DefaultModelPath = _ollama.GetDefaultModelsPath(),
                InstallGuideUrl = "https://ollama.com/download"
            });

            // LM Studio
            var lmsVersion = await _lmStudio.GetVersionAsync(ct);
            var lmstudioRunning = false;
            if (!string.IsNullOrEmpty(lmsVersion))
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(2);
                    var response = await client.GetAsync("http://localhost:1234/v1/models", ct);
                    lmstudioRunning = response.IsSuccessStatusCode;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "检测服务运行状态失败"); }
            }

            tools.Add(new LocalToolInfoDto
            {
                Id = "lmstudio",
                Name = "LM Studio",
                IsInstalled = !string.IsNullOrEmpty(lmsVersion),
                Version = lmsVersion,
                IsRunning = lmstudioRunning,
                DefaultModelPath = LmStudioDownloadService.GetDefaultModelsPath(),
                InstallGuideUrl = "https://lmstudio.ai/download"
            });

            // llama.cpp
            var (llamaCppInstalled, llamaCppVersion, llamaCppRunning, llamaCppModelPath) = await _llamaCpp.GetToolInfoAsync(ct);
            tools.Add(new LocalToolInfoDto
            {
                Id = "llamacpp",
                Name = "llama.cpp",
                IsInstalled = llamaCppInstalled,
                Version = llamaCppVersion,
                IsRunning = llamaCppRunning,
                DefaultModelPath = llamaCppModelPath,
                InstallGuideUrl = "https://github.com/ggerganov/llama.cpp"
            });

            _cache.Set(ToolsCacheKey, tools, TimeSpan.FromSeconds(10));
            return tools;
        }

        #endregion

        #region Running Model Management

        public async Task<List<RunningModelDto>> GetRunningModelsAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && _cache.TryGetValue(RunningModelsCacheKey, out List<RunningModelDto>? cached) && cached != null)
            {
                _logger.LogDebug("运行中模型命中缓存");
                return cached;
            }

            var results = new List<RunningModelDto>();

            try
            {
                var ollamaModels = await _ollama.GetRunningModelsAsync(ct);
                results.AddRange(ollamaModels);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 Ollama 运行中模型失败");
            }

            try
            {
                var lmModels = await _lmStudio.GetRunningModelsAsync(ct);
                results.AddRange(lmModels);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 LM Studio 运行中模型失败");
            }

            try
            {
                var llamaModels = await _llamaCpp.GetRunningModelsAsync(ct);
                results.AddRange(llamaModels);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 llama.cpp 运行中模型失败");
            }

            _cache.Set(RunningModelsCacheKey, results);
            return results;
        }

        #endregion

        #region Available Models

        public async Task<List<string>> GetAvailableModelsAsync(string toolId, CancellationToken ct = default)
        {
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                return await _ollama.GetAvailableModelsAsync(ct);

            if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                return await _lmStudio.GetAvailableModelsAsync(ct);

            if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                return await _llamaCpp.GetAvailableModelsAsync(ct);

            return new List<string>();
        }

        #endregion

        #region Downloaded Model Management

        public async Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(CancellationToken ct = default)
        {
            var runningModels = await GetRunningModelsAsync(forceRefresh: true, ct);
            var results = new List<DownloadedModelDto>();

            results.AddRange(await _ollama.GetDownloadedModelsAsync(runningModels, ct));
            results.AddRange(await _lmStudio.GetDownloadedModelsAsync(runningModels, ct));
            results.AddRange(await _llamaCpp.GetDownloadedModelsAsync(runningModels, ct));

            return results;
        }

        public async Task<bool> DeleteModelAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                var result = await _ollama.DeleteModelAsync(modelName, ct);
                if (result)
                {
                    RemoveModelFromProviderConfig("ollama", modelName);
                }
                return result;
            }

            if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("LM Studio 模型删除暂不支持，请手动删除模型文件");
                return false;
            }

            if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("llama.cpp 模型删除暂不支持，请手动删除 .gguf 文件");
                return false;
            }

            return false;
        }

        public async Task<ModelDetailsDto?> GetModelDetailsAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                return await _ollama.GetModelDetailsAsync(modelName, ct);

            if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                return await _lmStudio.GetModelDetailsAsync(modelName, ct);

            if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                return await _llamaCpp.GetModelDetailsAsync(modelName, ct);

            return null;
        }

        #endregion

        #region Model Lifecycle

        public async Task<bool> LoadModelAsync(string toolId, string modelName, int keepAliveMinutes, CancellationToken ct = default)
        {
            bool result;
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                var running = await _autoStarter.TryEnsureRunningAsync("ollama", "http://localhost:11434/v1");
                if (!running)
                    throw new InvalidOperationException("Ollama 服务未运行且自动启动失败，请手动启动后重试。");
                result = await _ollama.LoadModelAsync(modelName, keepAliveMinutes, ct);
            }
            else if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
            {
                var running = await _autoStarter.TryEnsureRunningAsync("lmstudio", "http://localhost:1234/v1");
                if (!running)
                    throw new InvalidOperationException("LM Studio 服务未运行且自动启动失败，请手动启动后重试。");
                result = await _lmStudio.LoadModelAsync(modelName, ct);
            }
            else if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
            {
                result = await _llamaCpp.LoadModelAsync(modelName, ct);
            }
            else
            {
                return false;
            }

            if (result) InvalidateCaches();
            return result;
        }

        public async Task<bool> UnloadModelAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            bool result;
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                result = await _ollama.UnloadModelAsync(modelName, ct);
            else if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                result = await _lmStudio.UnloadModelAsync(modelName, ct);
            else if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                result = await _llamaCpp.UnloadModelAsync(ct);
            else
                return false;

            if (result) InvalidateCaches();
            return result;
        }

        #endregion

        #region LlamaCpp Service Control

        public async Task<LocalAiServiceStatusDto> StartLlamaCppAsync(CancellationToken ct = default)
        {
            var result = await _llamaCpp.StartAsync(ct);
            if (result.IsRunning) InvalidateCaches();
            return result;
        }

        public async Task<bool> StopLlamaCppAsync(CancellationToken ct = default)
        {
            var result = await _llamaCpp.StopAsync(ct);
            if (result) InvalidateCaches();
            return result;
        }

        #endregion

        #region Download Sources

        public List<DownloadSourceDto> GetDownloadSources()
        {
            return new List<DownloadSourceDto>
            {
                new()
                {
                    Id = "ollama",
                    Name = "Ollama Library",
                    BaseUrl = "https://ollama.com/library",
                    IsChinaMirror = false,
                    IsAvailable = true
                },
                new()
                {
                    Id = "huggingface",
                    Name = "Hugging Face",
                    BaseUrl = "https://huggingface.co",
                    IsChinaMirror = false,
                    IsAvailable = true
                },
                new()
                {
                    Id = "hf-mirror",
                    Name = "Hugging Face 镜像 (hf-mirror.com)",
                    BaseUrl = "https://hf-mirror.com",
                    IsChinaMirror = true,
                    IsAvailable = true
                },
                new()
                {
                    Id = "modelscope",
                    Name = "魔搭社区 (ModelScope)",
                    BaseUrl = "https://modelscope.cn",
                    IsChinaMirror = true,
                    IsAvailable = true
                }
            };
        }

        #endregion

        #region Cache Management

        private void InvalidateCaches()
        {
            _cache.Remove(RunningModelsCacheKey);
            _cache.Remove(ToolsCacheKey);
            _logger.LogDebug("本地模型缓存已清除");
        }

        #endregion
    }
}
