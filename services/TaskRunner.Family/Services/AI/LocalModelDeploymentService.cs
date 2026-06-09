using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
        private readonly SettingsService _settingsService;
        private readonly IOpenClawTaskService _openClawTaskService;
        private readonly IMemoryCache _cache;

        // 内存中的部署任务状态
        private static readonly ConcurrentDictionary<string, DeployTaskStatusDto> _tasks = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCancellations = new();

        // 缓存配置（首次访问时缓存，点击刷新或操作后失效才更新）
        private const string ToolsCacheKey = "local_tools";
        private const string RunningModelsCacheKey = "running_models";

        public LocalModelDeploymentService(
            ILogger<LocalModelDeploymentService> logger,
            IHttpClientFactory httpClientFactory,
            LocalAiAutoStarter autoStarter,
            AiConfigService aiConfigService,
            SettingsService settingsService,
            IOpenClawTaskService openClawTaskService,
            IMemoryCache cache)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _autoStarter = autoStarter;
            _aiConfigService = aiConfigService;
            _settingsService = settingsService;
            _openClawTaskService = openClawTaskService;
            _cache = cache;
        }

        #region Task Management

        /// <summary>
        /// 获取任务状态
        /// </summary>
        public DeployTaskStatusDto? GetRunnerTaskStatus(string taskId)
        {
            return _tasks.TryGetValue(taskId, out var status) ? status : null;
        }

        /// <summary>
        /// 取消部署任务
        /// </summary>
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

        /// <summary>
        /// 清理已完成的任务（防止内存无限增长）
        /// </summary>
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

        /// <summary>
        /// 启动模型部署
        /// </summary>
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

            // 在后台执行部署
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

        /// <summary>
        /// 部署到 Ollama
        /// </summary>
        private async Task DeployToOllamaAsync(DeployTaskStatusDto task, ModelEntry model, CancellationToken ct)
        {
            task.Status = "running";

            // 1. 检查 Ollama 是否安装
            task.CurrentStep = "检查 Ollama 安装";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 Ollama 安装...");
            var ollamaVersion = await GetOllamaVersionAsync(ct);
            if (string.IsNullOrEmpty(ollamaVersion))
            {
                throw new InvalidOperationException(
                    "Ollama 未安装。请访问 https://ollama.com 下载安装。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] Ollama 版本: {ollamaVersion}");

            // 2. 确保 Ollama 服务运行
            task.CurrentStep = "启动 Ollama 服务";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 Ollama 服务状态...");
            var running = await _autoStarter.TryEnsureRunningAsync("ollama", "http://localhost:11434/v1");
            if (!running)
            {
                throw new InvalidOperationException("Ollama 服务启动失败，请手动启动后重试。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] Ollama 服务已就绪");

            // 3. 检查可用磁盘空间
            var requiredBytes = (long)(model.SizeGiB * 1.2 * 1024 * 1024 * 1024); // 20% 余量
            var availableBytes = GetOllamaModelsDirFreeSpace();
            if (availableBytes > 0 && availableBytes < requiredBytes)
            {
                throw new InvalidOperationException(
                    $"磁盘空间不足。模型需要约 {model.SizeGiB * 1.2:F1} GB，可用空间仅 {availableBytes / (1024.0 * 1024 * 1024):F1} GB。");
            }

            // 4. 执行 ollama pull
            task.CurrentStep = "下载模型";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 开始下载: ollama pull {model.OllamaModelName}");

            await RunOllamaPullAsync(task, model.OllamaModelName, ct);

            // 5. 验证部署
            task.CurrentStep = "验证部署";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 验证模型...");
            var verified = await VerifyOllamaModelAsync(model.OllamaModelName, ct);
            if (!verified)
            {
                throw new InvalidOperationException("模型下载完成但验证失败，请检查 Ollama 日志。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 模型验证通过");

            // 6. 自动配置 AI Provider
            task.CurrentStep = "配置 AI Provider";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 添加到 AI 服务商配置...");
            ConfigureOllamaProvider(model);
            task.AutoConfiguredProvider = true;
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] AI Provider 配置完成");

            // 完成
            task.Status = "completed";
            task.ProgressPercent = 100;
            task.CurrentStep = "部署完成";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] ✅ 部署成功！模型已可用。");
        }

        /// <summary>
        /// 部署到 LM Studio：使用 lms get 下载模型
        /// </summary>
        private async Task DeployToLmStudioAsync(DeployTaskStatusDto task, ModelEntry model, CancellationToken ct)
        {
            task.Status = "running";

            // 1. 检查 LM Studio CLI
            task.CurrentStep = "检查 LM Studio";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 LM Studio 安装...");
            var lmsVersion = await GetLmStudioVersionAsync(ct);
            if (string.IsNullOrEmpty(lmsVersion))
            {
                throw new InvalidOperationException(
                    "LM Studio CLI (lms) 未安装。请访问 https://lmstudio.ai 下载安装，并确保 lms 命令在 PATH 中。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] LM Studio CLI: {lmsVersion}");

            // 2. 检查 LM Studio 服务
            task.CurrentStep = "启动 LM Studio 服务";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 检查 LM Studio 服务状态...");
            var running = await _autoStarter.TryEnsureRunningAsync("lmstudio", "http://localhost:1234/v1");
            if (!running)
            {
                throw new InvalidOperationException("LM Studio 服务启动失败，请手动启动后重试。");
            }
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] LM Studio 服务已就绪");

            // 3. 检查可用磁盘空间
            var requiredBytes = (long)(model.SizeGiB * 1.2 * 1024 * 1024 * 1024);
            var availableBytes = GetLmStudioModelsDirFreeSpace();
            if (availableBytes > 0 && availableBytes < requiredBytes)
            {
                throw new InvalidOperationException(
                    $"磁盘空间不足。模型需要约 {model.SizeGiB * 1.2:F1} GB，可用空间仅 {availableBytes / (1024.0 * 1024 * 1024):F1} GB。");
            }

            // 4. 确定搜索名称和下载源
            var searchName = model.LmStudioSearchName ?? model.Id;
            var preferredSource = _settingsService.PreferredDownloadSource;
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 搜索名称: {searchName}, 下载源偏好: {preferredSource}");

            // 5. 执行 lms get 下载
            task.CurrentStep = "下载模型";
            var downloadTarget = ResolveLmStudioDownloadTarget(model, preferredSource);
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 开始下载: lms get {downloadTarget} --gguf -y");
            await RunLmsGetAsync(task, downloadTarget, ct);

            // 6. 验证部署（扫描本地目录确认模型已下载）
            task.CurrentStep = "验证部署";
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 验证模型...");
            var verified = await VerifyLmStudioModelAsync(searchName, ct);
            if (!verified)
            {
                task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] ⚠️ 无法自动验证模型是否下载成功，请检查 LM Studio 界面。");
            }
            else
            {
                task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 模型验证通过");
            }

            // 7. 自动配置 AI Provider
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

        #region Ollama Operations

        private async Task<string?> GetOllamaVersionAsync(CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process == null) return null;

                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) return null;

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                var version = Regex.Match(output, @"ollama version is ([\d.]+)")?.Groups[1].Value;
                if (string.IsNullOrEmpty(version))
                    version = Regex.Match(output, @"([\d.]+)")?.Groups[1].Value;
                return version;
            }
            catch { return null; }
        }

        private async Task RunOllamaPullAsync(DeployTaskStatusDto task, string modelName, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ollama",
                Arguments = $"pull {modelName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // 设置下载目录环境变量（如果用户配置了）
            var customDir = _settingsService.LocalModelDownloadDirectory;
            if (!string.IsNullOrEmpty(customDir))
            {
                psi.EnvironmentVariables["OLLAMA_MODELS"] = customDir;
            }

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("无法启动 ollama pull 进程");

            // 异步读取输出
            var stdoutReader = Task.Run(async () =>
            {
                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line == null) break;
                    ParseOllamaPullOutput(task, line);
                }
            }, ct);

            var stderrReader = Task.Run(async () =>
            {
                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(ct);
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {line.Trim()}");
                    }
                }
            }, ct);

            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutReader, stderrReader);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ollama pull 退出码 {process.ExitCode}");
            }
        }

        /// <summary>
        /// 解析 ollama pull 的输出，提取进度信息
        /// </summary>
        private void ParseOllamaPullOutput(DeployTaskStatusDto task, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // 记录日志
            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {line.Trim()}");

            // 解析进度条格式，例如：
            // pulling 6e9c80...  45% ▕███████████████████                             ▏ 2.1 GB/4.7 GB  50 MB/s  1m30s
            var progressMatch = Regex.Match(line, @"(\d+)%");
            if (progressMatch.Success && int.TryParse(progressMatch.Groups[1].Value, out var percent))
            {
                task.ProgressPercent = Math.Min(100, Math.Max(0, percent));
            }

            // 更新当前步骤描述
            if (line.Contains("pulling manifest", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "下载清单";
            else if (line.Contains("pulling", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "下载模型文件";
            else if (line.Contains("verifying", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "验证文件";
            else if (line.Contains("writing", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "写入模型";
        }

        private async Task<bool> VerifyOllamaModelAsync(string modelName, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = $"list",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process == null) return false;

                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) return false;

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                var modelBase = modelName.Split(':')[0];
                return output.Contains(modelBase, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private long GetOllamaModelsDirFreeSpace()
        {
            try
            {
                var customDir = _settingsService.LocalModelDownloadDirectory;
                var dir = string.IsNullOrEmpty(customDir)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models")
                    : customDir;

                if (!Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir) ?? dir;

                var drive = new DriveInfo(Path.GetPathRoot(dir) ?? dir);
                return drive.IsReady ? drive.AvailableFreeSpace : 0;
            }
            catch { return 0; }
        }

        #endregion

        #region LM Studio Operations

        private async Task<string?> GetLmStudioVersionAsync(CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process == null) return null;

                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) return null;

                var output = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
                var versionMatch = Regex.Match(output, @"(\d+\.\d+\.?\d*)");
                if (versionMatch.Success)
                    return versionMatch.Groups[1].Value;

                return string.IsNullOrEmpty(output) ? null : output;
            }
            catch { return null; }
        }

        private long GetLmStudioModelsDirFreeSpace()
        {
            try
            {
                var dir = GetDefaultLmStudioModelsPath();
                if (!Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir) ?? dir;

                var drive = new DriveInfo(Path.GetPathRoot(dir) ?? dir);
                return drive.IsReady ? drive.AvailableFreeSpace : 0;
            }
            catch { return 0; }
        }

        private async Task RunLmsGetAsync(DeployTaskStatusDto task, string searchName, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "lms",
                Arguments = $"get {searchName} --gguf -y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("无法启动 lms get 进程");

            var stdoutReader = Task.Run(async () =>
            {
                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line == null) break;
                    ParseLmsGetOutput(task, line);
                }
            }, ct);

            var stderrReader = Task.Run(async () =>
            {
                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(ct);
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {line.Trim()}");
                    }
                }
            }, ct);

            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutReader, stderrReader);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"lms get 退出码 {process.ExitCode}。可能模型名称未找到或 LM Studio GUI 未运行。");
            }
        }

        private void ParseLmsGetOutput(DeployTaskStatusDto task, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {line.Trim()}");

            // lms get 可能输出进度信息，尝试解析百分比
            var progressMatch = Regex.Match(line, @"(\d+)%");
            if (progressMatch.Success && int.TryParse(progressMatch.Groups[1].Value, out var percent))
            {
                task.ProgressPercent = Math.Min(100, Math.Max(0, percent));
            }

            if (line.Contains("downloading", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "下载模型文件";
            else if (line.Contains("downloaded", StringComparison.OrdinalIgnoreCase) || line.Contains("finished", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "下载完成";
            else if (line.Contains("searching", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "搜索模型";
        }

        private async Task<bool> VerifyLmStudioModelAsync(string searchName, CancellationToken ct)
        {
            try
            {
                // 方法1: lms ls --json（最权威）
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = "ls --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync(ct);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(output);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                var searchLower = searchName.Replace("-", "").Replace("_", "").ToLowerInvariant();
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    var key = GetJsonString(item, "modelKey");
                                    if (string.IsNullOrEmpty(key)) key = GetJsonString(item, "name");
                                    if (string.IsNullOrEmpty(key)) key = GetJsonString(item, "id");
                                    if (string.IsNullOrEmpty(key)) continue;

                                    // 精确匹配或包含匹配
                                    if (key.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                                        return true;

                                    var keyLower = key.Replace("-", "").Replace("_", "").Replace("/", "").ToLowerInvariant();
                                    if (keyLower.Contains(searchLower) || searchLower.Contains(keyLower))
                                        return true;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 方法2: 扫描本地目录作为 fallback
            try
            {
                var modelsPath = GetDefaultLmStudioModelsPath();
                if (!Directory.Exists(modelsPath))
                    return false;

                var searchLower = searchName.Replace("-", "").Replace("_", "").ToLowerInvariant();
                var ggufFiles = Directory.EnumerateFiles(modelsPath, "*.gguf", SearchOption.AllDirectories);
                foreach (var file in ggufFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file).Replace("-", "").Replace("_", "").ToLowerInvariant();
                    if (fileName.Contains(searchLower) || searchLower.Contains(fileName))
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 根据下载源偏好，解析 LM Studio 的下载目标（搜索名称或 HuggingFace/ModelScope URL）
        /// </summary>
        private static string ResolveLmStudioDownloadTarget(ModelEntry model, string preferredSource)
        {
            var searchName = model.LmStudioSearchName ?? model.Id;

            // auto / lmstudio：使用 LM Studio Hub 搜索
            if (preferredSource.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || preferredSource.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
            {
                return searchName;
            }

            // 如果模型有 HuggingFace 信息，尝试构造直接下载 URL
            if (!string.IsNullOrEmpty(model.HuggingFaceRepo) && !string.IsNullOrEmpty(model.GgufFilename))
            {
                if (preferredSource.Equals("huggingface", StringComparison.OrdinalIgnoreCase))
                    return $"https://huggingface.co/{model.HuggingFaceRepo}/resolve/main/{model.GgufFilename}";

                if (preferredSource.Equals("hf-mirror", StringComparison.OrdinalIgnoreCase))
                    return $"https://hf-mirror.com/{model.HuggingFaceRepo}/resolve/main/{model.GgufFilename}";

                if (preferredSource.Equals("modelscope", StringComparison.OrdinalIgnoreCase))
                    return $"https://modelscope.cn/models/{model.HuggingFaceRepo}/resolve/master/{model.GgufFilename}";
            }

            // 无法构造精确 URL 时回退到搜索名称
            return searchName;
        }

        #endregion

        #region Provider Auto-Configuration

        /// <summary>
        /// 将模型添加到 Ollama AI Provider 配置中
        /// </summary>
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
                // 保留用户自定义的名称，不覆盖
                providerName = existing.Name;
                models = existing.GetModelOptions().Select(m => new AiModelConfig
                {
                    Name = m.Name,
                    IsPaid = m.IsPaid,
                    IsMain = false // 保留现有的主模型
                }).ToList();
                isMain = existing.IsMain;

                // 如果模型已存在，不重复添加
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
                // 创建新的 provider
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
                isMain = false; // 不自动设为主 provider，避免覆盖用户配置
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

            // Ollama 通常不需要 API Key，传空字符串表示不修改
            _aiConfigService.SaveProvider(setting, "");

            _logger.LogInformation("已自动配置 Ollama Provider，新增模型: {Model}", model.OllamaModelName);
        }

        /// <summary>
        /// 将模型添加到 LM Studio AI Provider 配置中
        /// </summary>
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
                // 保留用户自定义的名称，不覆盖
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

        #endregion

        #region Tool Detection

        /// <summary>
        /// 获取已安装的本地 AI 工具信息
        /// </summary>
        public async Task<List<LocalToolInfoDto>> GetLocalToolsAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && _cache.TryGetValue(ToolsCacheKey, out List<LocalToolInfoDto>? cached) && cached != null)
            {
                _logger.LogDebug("本地工具状态命中缓存");
                return cached;
            }

            var tools = new List<LocalToolInfoDto>();

            // Ollama
            var ollamaVersion = await GetOllamaVersionAsync(ct);
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
                catch { }
            }

            tools.Add(new LocalToolInfoDto
            {
                Id = "ollama",
                Name = "Ollama",
                IsInstalled = !string.IsNullOrEmpty(ollamaVersion),
                Version = ollamaVersion,
                IsRunning = ollamaRunning,
                DefaultModelPath = GetDefaultOllamaModelsPath(),
                InstallGuideUrl = "https://ollama.com/download"
            });

            // LM Studio
            var lmsVersion = await GetLmStudioVersionAsync(ct);
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
                catch { }
            }

            tools.Add(new LocalToolInfoDto
            {
                Id = "lmstudio",
                Name = "LM Studio",
                IsInstalled = !string.IsNullOrEmpty(lmsVersion),
                Version = lmsVersion,
                IsRunning = lmstudioRunning,
                DefaultModelPath = GetDefaultLmStudioModelsPath(),
                InstallGuideUrl = "https://lmstudio.ai/download"
            });

            // llama.cpp
            var (llamaCppInstalled, llamaCppVersion, llamaCppRunning, llamaCppModelPath) = await GetLlamaCppToolInfoAsync(ct);
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

        private string GetDefaultOllamaModelsPath()
        {
            var custom = _settingsService.LocalModelDownloadDirectory;
            if (!string.IsNullOrEmpty(custom))
                return custom;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".ollama", "models");
        }

        private string GetDefaultLmStudioModelsPath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".lmstudio", "models");
        }

        #region Running Model Management

        /// <summary>
        /// 获取所有运行中的模型（Ollama + LM Studio）
        /// </summary>
        public async Task<List<RunningModelDto>> GetRunningModelsAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!forceRefresh && _cache.TryGetValue(RunningModelsCacheKey, out List<RunningModelDto>? cached) && cached != null)
            {
                _logger.LogDebug("运行中模型命中缓存");
                return cached;
            }

            var results = new List<RunningModelDto>();

            // Ollama
            try
            {
                var ollamaModels = await GetOllamaRunningModelsAsync(ct);
                results.AddRange(ollamaModels);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 Ollama 运行中模型失败");
            }

            // LM Studio
            try
            {
                var lmModels = await GetLmStudioRunningModelsAsync(ct);
                results.AddRange(lmModels);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 LM Studio 运行中模型失败");
            }

            // llama.cpp
            try
            {
                var llamaModels = await GetLlamaCppRunningModelsAsync(ct);
                results.AddRange(llamaModels);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 llama.cpp 运行中模型失败");
            }

            _cache.Set(RunningModelsCacheKey, results);
            return results;
        }

        private async Task<List<RunningModelDto>> GetOllamaRunningModelsAsync(CancellationToken ct)
        {
            var list = new List<RunningModelDto>();
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var response = await client.GetAsync("http://localhost:11434/api/ps", ct);
            if (!response.IsSuccessStatusCode) return list;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var modelsArray)) return list;

            foreach (var item in modelsArray.EnumerateArray())
            {
                var dto = new RunningModelDto
                {
                    ToolId = "ollama",
                    ToolName = "Ollama",
                    ModelName = GetJsonString(item, "name"),
                    DisplayName = GetJsonString(item, "name"),
                    Status = "running",
                    SizeBytes = GetJsonLong(item, "size"),
                };

                if (item.TryGetProperty("size_vram", out var vramProp) && vramProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                    dto.VramBytes = vramProp.GetInt64();

                if (item.TryGetProperty("expires_at", out var expProp) && expProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    if (DateTime.TryParse(expProp.GetString(), out var expDt))
                        dto.ExpiresAt = expDt;

                if (item.TryGetProperty("context_length", out var ctxProp) && ctxProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                    dto.ContextLength = ctxProp.GetInt32();

                if (item.TryGetProperty("details", out var details))
                {
                    dto.Family = GetJsonString(details, "family");
                }

                list.Add(dto);
            }

            return list;
        }

        private async Task<List<RunningModelDto>> GetLmStudioRunningModelsAsync(CancellationToken ct)
        {
            var list = new List<RunningModelDto>();

            // 优先尝试 lms ps --json 获取更丰富的元数据（需要 LM Studio GUI 运行）
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = "ps --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync(ct);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(output);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    var modelId = GetJsonString(item, "modelKey");
                                    if (string.IsNullOrEmpty(modelId))
                                        modelId = GetJsonString(item, "identifier");
                                    if (string.IsNullOrEmpty(modelId))
                                        modelId = GetJsonString(item, "id");

                                    if (string.IsNullOrEmpty(modelId)) continue;

                                    var dto = new RunningModelDto
                                    {
                                        ToolId = "lmstudio",
                                        ToolName = "LM Studio",
                                        ModelName = modelId,
                                        DisplayName = modelId,
                                        Status = "running",
                                    };

                                    if (item.TryGetProperty("loadedContextLength", out var ctxProp) && ctxProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        dto.ContextLength = ctxProp.GetInt32();

                                    list.Add(dto);
                                }
                                return list;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "lms ps --json 失败，回退到 HTTP API");
            }

            // 回退到 HTTP API（信息较基础）
            try
            {
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
                var response = await client.GetAsync("http://localhost:1234/v1/models", ct);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray)) return list;

                foreach (var item in dataArray.EnumerateArray())
                {
                    var modelId = GetJsonString(item, "id");
                    var dto = new RunningModelDto
                    {
                        ToolId = "lmstudio",
                        ToolName = "LM Studio",
                        ModelName = modelId,
                        DisplayName = modelId,
                        Status = "running",
                    };
                    list.Add(dto);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 LM Studio 运行中模型失败");
            }

            return list;
        }

        /// <summary>
        /// 获取指定工具中已下载/可用的模型列表
        /// </summary>
        public async Task<List<string>> GetAvailableModelsAsync(string toolId, CancellationToken ct = default)
        {
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                return await GetOllamaAvailableModelsAsync(ct);

            if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                return await GetLmStudioAvailableModelsAsync(ct);

            if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                return await GetLlamaCppAvailableModelsAsync(ct);

            return new List<string>();
        }

        private async Task<List<string>> GetOllamaAvailableModelsAsync(CancellationToken ct)
        {
            var list = new List<string>();
            try
            {
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync("http://localhost:11434/api/tags", ct);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("models", out var modelsArray)) return list;

                foreach (var item in modelsArray.EnumerateArray())
                {
                    var name = GetJsonString(item, "name");
                    if (!string.IsNullOrEmpty(name))
                        list.Add(name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 Ollama 可用模型列表失败");
            }
            return list;
        }

        private async Task<List<string>> GetLmStudioAvailableModelsAsync(CancellationToken ct)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 方法1: lms ls --json（最权威，支持所有格式和路径）
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = "ls --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync(ct);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(output);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    var key = GetJsonString(item, "modelKey");
                                    if (string.IsNullOrEmpty(key))
                                        key = GetJsonString(item, "name");
                                    if (string.IsNullOrEmpty(key))
                                        key = GetJsonString(item, "id");
                                    if (!string.IsNullOrEmpty(key) && seen.Add(key))
                                        list.Add(key);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "lms ls --json 失败");
            }

            // 方法2: 扫描本地模型目录作为 fallback（lms 不可用时）
            if (list.Count == 0)
            {
                try
                {
                    var modelsPath = GetDefaultLmStudioModelsPath();
                    if (Directory.Exists(modelsPath))
                    {
                        var ggufFiles = Directory.EnumerateFiles(modelsPath, "*.gguf", SearchOption.AllDirectories);
                        foreach (var file in ggufFiles)
                        {
                            var name = Path.GetFileNameWithoutExtension(file);
                            if (seen.Add(name))
                                list.Add(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "扫描 LM Studio 模型目录失败");
                }
            }

            // 方法3: 回退到 HTTP API（lms 和目录扫描都不可用时，仅返回已加载模型）
            if (list.Count == 0)
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync("http://localhost:1234/v1/models", ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync(ct);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("data", out var dataArray))
                        {
                            foreach (var item in dataArray.EnumerateArray())
                            {
                                var id = GetJsonString(item, "id");
                                if (!string.IsNullOrEmpty(id) && seen.Add(id))
                                    list.Add(id);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "获取 LM Studio 可用模型列表失败");
                }
            }

            return list;
        }

        #region Downloaded Model Management

        /// <summary>
        /// 获取所有已下载模型（聚合所有工具）
        /// </summary>
        public async Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(CancellationToken ct = default)
        {
            var runningModels = await GetRunningModelsAsync(forceRefresh: true, ct);
            var results = new List<DownloadedModelDto>();

            results.AddRange(await GetOllamaDownloadedModelsAsync(runningModels, ct));
            results.AddRange(await GetLmStudioDownloadedModelsAsync(runningModels, ct));
            results.AddRange(await GetLlamaCppDownloadedModelsAsync(runningModels, ct));

            return results;
        }

        private async Task<List<DownloadedModelDto>> GetOllamaDownloadedModelsAsync(List<RunningModelDto> runningModels, CancellationToken ct)
        {
            var list = new List<DownloadedModelDto>();
            try
            {
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync("http://localhost:11434/api/tags", ct);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("models", out var modelsArray)) return list;

                var runningSet = new HashSet<string>(runningModels
                    .Where(r => r.ToolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.ModelName), StringComparer.OrdinalIgnoreCase);

                foreach (var item in modelsArray.EnumerateArray())
                {
                    var name = GetJsonString(item, "name");
                    if (string.IsNullOrEmpty(name)) continue;

                    long size = 0;
                    if (item.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                        size = sizeProp.GetInt64();

                    DateTime? modifiedAt = null;
                    if (item.TryGetProperty("modified_at", out var modProp) && modProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        if (DateTime.TryParse(modProp.GetString(), out var md))
                            modifiedAt = md;

                    list.Add(new DownloadedModelDto
                    {
                        Name = name,
                        ToolId = "ollama",
                        ToolName = "Ollama",
                        SizeBytes = size,
                        ModifiedAt = modifiedAt,
                        Digest = GetJsonString(item, "digest"),
                        IsRunning = runningSet.Contains(name),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 Ollama 已下载模型列表失败");
            }
            return list;
        }

        private async Task<List<DownloadedModelDto>> GetLmStudioDownloadedModelsAsync(List<RunningModelDto> runningModels, CancellationToken ct)
        {
            var list = new List<DownloadedModelDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runningSet = new HashSet<string>(runningModels
                .Where(r => r.ToolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.ModelName), StringComparer.OrdinalIgnoreCase);

            // 方法1: lms ls --json（最权威，支持所有格式和路径）
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = "ls --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync(ct);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(output);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    var key = GetJsonString(item, "modelKey");
                                    if (string.IsNullOrEmpty(key)) key = GetJsonString(item, "name");
                                    if (string.IsNullOrEmpty(key)) key = GetJsonString(item, "id");
                                    if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

                                    long size = 0;
                                    if (item.TryGetProperty("sizeBytes", out var sz) && sz.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        size = sz.GetInt64();

                                    DateTime? modifiedAt = null;
                                    if (item.TryGetProperty("modifiedAt", out var modProp) && modProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                        if (DateTime.TryParse(modProp.GetString(), out var md))
                                            modifiedAt = md;
                                    if (modifiedAt == null && item.TryGetProperty("downloadedAt", out var dlProp) && dlProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                        if (DateTime.TryParse(dlProp.GetString(), out var dd))
                                            modifiedAt = dd;

                                    list.Add(new DownloadedModelDto
                                    {
                                        Name = key,
                                        ToolId = "lmstudio",
                                        ToolName = "LM Studio",
                                        SizeBytes = size,
                                        ModifiedAt = modifiedAt,
                                        IsRunning = runningSet.Contains(key),
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "lms ls --json 获取已下载模型失败");
            }

            // 方法2: 扫描本地模型目录作为 fallback（lms 不可用时）
            if (list.Count == 0)
            {
                try
                {
                    var modelsPath = GetDefaultLmStudioModelsPath();
                    if (Directory.Exists(modelsPath))
                    {
                        var ggufFiles = Directory.EnumerateFiles(modelsPath, "*.gguf", SearchOption.AllDirectories);
                        foreach (var file in ggufFiles)
                        {
                            var name = Path.GetFileNameWithoutExtension(file);
                            if (!seen.Add(name)) continue;

                            long size = 0;
                            DateTime? modifiedAt = null;
                            try
                            {
                                var fi = new FileInfo(file);
                                size = fi.Length;
                                modifiedAt = fi.LastWriteTime;
                            }
                            catch { }

                            list.Add(new DownloadedModelDto
                            {
                                Name = name,
                                ToolId = "lmstudio",
                                ToolName = "LM Studio",
                                SizeBytes = size,
                                ModifiedAt = modifiedAt,
                                IsRunning = runningSet.Contains(name),
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "扫描 LM Studio 已下载模型目录失败");
                }
            }

            return list;
        }

        private async Task<List<DownloadedModelDto>> GetLlamaCppDownloadedModelsAsync(List<RunningModelDto> runningModels, CancellationToken ct)
        {
            var list = new List<DownloadedModelDto>();
            var runningSet = new HashSet<string>(runningModels
                .Where(r => r.ToolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.ModelName), StringComparer.OrdinalIgnoreCase);

            try
            {
                var config = await _openClawTaskService.GetLocalAiConfigAsync();
                var llamaCpp = config.LlamaCpp;
                if (llamaCpp == null || !llamaCpp.Enabled)
                    return list;

                var searchDir = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath)
                    ? Path.GetDirectoryName(llamaCpp.ModelPath)
                    : null;

                if (string.IsNullOrWhiteSpace(searchDir) || !Directory.Exists(searchDir))
                    return list;

                var ggufFiles = Directory.EnumerateFiles(searchDir, "*.gguf", SearchOption.TopDirectoryOnly);
                foreach (var file in ggufFiles)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    long size = 0;
                    DateTime? modifiedAt = null;
                    try
                    {
                        var fi = new FileInfo(file);
                        size = fi.Length;
                        modifiedAt = fi.LastWriteTime;
                    }
                    catch { }

                    list.Add(new DownloadedModelDto
                    {
                        Name = name,
                        ToolId = "llamacpp",
                        ToolName = "llama.cpp",
                        SizeBytes = size,
                        ModifiedAt = modifiedAt,
                        IsRunning = runningSet.Contains(name),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "扫描 llama.cpp 已下载模型目录失败");
            }

            return list;
        }

        /// <summary>
        /// 删除本地模型
        /// </summary>
        public async Task<bool> DeleteModelAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                return await DeleteOllamaModelAsync(modelName, ct);
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

        private async Task<bool> DeleteOllamaModelAsync(string modelName, CancellationToken ct)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
                var body = new Dictionary<string, object> { ["name"] = modelName };
                var json = System.Text.Json.JsonSerializer.Serialize(body);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Delete, "http://localhost:11434/api/delete")
                    {
                        Content = content
                    }, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Ollama 模型 {ModelName} 已删除", modelName);
                    // 从 AI Provider 配置中移除该模型
                    RemoveModelFromProviderConfig("ollama", modelName);
                    return true;
                }

                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("删除 Ollama 模型 {ModelName} 失败: {StatusCode} {Error}", modelName, response.StatusCode, error);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除 Ollama 模型 {ModelName} 异常", modelName);
                return false;
            }
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

                var setting = new TaskRunner.Data.Entities.AiProviderSetting
                {
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    BaseUrl = provider.AiBaseUrl,
                    IsMain = provider.IsMain,
                    ModelsJson = System.Text.Json.JsonSerializer.Serialize(filtered),
                    IsEnabled = true,
                };

                _aiConfigService.SaveProvider(setting, plainApiKey: null);
                _settingsService.ClearAiProvidersCache();
                _logger.LogInformation("已从 AI Provider {ProviderId} 的配置中移除模型 {ModelName}", providerId, modelName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 AI Provider 配置中移除模型 {ModelName} 失败", modelName);
            }
        }

        /// <summary>
        /// 获取模型详情
        /// </summary>
        public async Task<ModelDetailsDto?> GetModelDetailsAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                return await GetOllamaModelDetailsAsync(modelName, ct);
            }

            if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
            {
                return await GetLmStudioModelDetailsAsync(modelName, ct);
            }

            if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
            {
                return await GetLlamaCppModelDetailsAsync(modelName, ct);
            }

            return null;
        }

        private async Task<ModelDetailsDto?> GetOllamaModelDetailsAsync(string modelName, CancellationToken ct)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
                var body = new Dictionary<string, object> { ["name"] = modelName };
                var json = System.Text.Json.JsonSerializer.Serialize(body);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync("http://localhost:11434/api/show", content, ct);
                if (!response.IsSuccessStatusCode) return null;

                var respJson = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(respJson);
                var root = doc.RootElement;

                var dto = new ModelDetailsDto
                {
                    Name = modelName,
                    ToolId = "ollama",
                };

                if (root.TryGetProperty("modelfile", out var mf) && mf.ValueKind == System.Text.Json.JsonValueKind.String)
                    dto.Modelfile = mf.GetString();

                if (root.TryGetProperty("parameters", out var pa) && pa.ValueKind == System.Text.Json.JsonValueKind.String)
                    dto.Parameters = pa.GetString();

                if (root.TryGetProperty("template", out var tp) && tp.ValueKind == System.Text.Json.JsonValueKind.String)
                    dto.Template = tp.GetString();

                if (root.TryGetProperty("license", out var lic) && lic.ValueKind == System.Text.Json.JsonValueKind.String)
                    dto.License = lic.GetString();

                dto.DetailsJson = respJson;
                return dto;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 Ollama 模型 {ModelName} 详情失败", modelName);
                return null;
            }
        }

        private async Task<ModelDetailsDto?> GetLmStudioModelDetailsAsync(string modelName, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = "ls --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync(ct);
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(output);
                            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    var key = GetJsonString(item, "modelKey");
                                    if (string.IsNullOrEmpty(key)) key = GetJsonString(item, "name");
                                    if (string.IsNullOrEmpty(key)) key = GetJsonString(item, "id");
                                    if (!key.Equals(modelName, StringComparison.OrdinalIgnoreCase)) continue;

                                    var dto = new ModelDetailsDto
                                    {
                                        Name = modelName,
                                        ToolId = "lmstudio",
                                    };

                                    var displayName = GetJsonString(item, "displayName");
                                    var publisher = GetJsonString(item, "publisher");
                                    var path = GetJsonString(item, "path");
                                    var paramsStr = GetJsonString(item, "paramsString");
                                    var architecture = GetJsonString(item, "architecture");

                                    var details = new List<string>();
                                    if (!string.IsNullOrEmpty(displayName)) details.Add($"Display Name: {displayName}");
                                    if (!string.IsNullOrEmpty(publisher)) details.Add($"Publisher: {publisher}");
                                    if (!string.IsNullOrEmpty(path)) details.Add($"Path: {path}");
                                    if (!string.IsNullOrEmpty(paramsStr)) details.Add($"Parameters: {paramsStr}");
                                    if (!string.IsNullOrEmpty(architecture)) details.Add($"Architecture: {architecture}");

                                    if (item.TryGetProperty("quantization", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Object)
                                    {
                                        var qName = GetJsonString(q, "name");
                                        if (q.TryGetProperty("bits", out var bits) && bits.ValueKind == System.Text.Json.JsonValueKind.Number)
                                            details.Add($"Quantization: {qName} ({bits.GetInt32()}-bit)");
                                        else if (!string.IsNullOrEmpty(qName))
                                            details.Add($"Quantization: {qName}");
                                    }

                                    if (item.TryGetProperty("maxContextLength", out var ctx) && ctx.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        details.Add($"Max Context: {ctx.GetInt32()}");

                                    long sizeBytes = 0;
                                    if (item.TryGetProperty("sizeBytes", out var sz) && sz.ValueKind == System.Text.Json.JsonValueKind.Number)
                                        sizeBytes = sz.GetInt64();
                                    if (sizeBytes > 0)
                                        details.Add($"Size: {sizeBytes / (1024.0 * 1024 * 1024):F2} GB");

                                    dto.Parameters = string.Join("\n", details);
                                    dto.DetailsJson = output;
                                    return dto;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 LM Studio 模型 {ModelName} 详情失败", modelName);
            }

            return await GetFileBasedModelDetailsAsync("lmstudio", modelName, GetDefaultLmStudioModelsPath(), ct);
        }

        private async Task<ModelDetailsDto?> GetLlamaCppModelDetailsAsync(string modelName, CancellationToken ct)
        {
            try
            {
                var config = await _openClawTaskService.GetLocalAiConfigAsync();
                var llamaCpp = config.LlamaCpp;
                if (llamaCpp != null && !string.IsNullOrWhiteSpace(llamaCpp.ModelPath))
                {
                    var fileName = Path.GetFileNameWithoutExtension(llamaCpp.ModelPath);
                    if (fileName.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                    {
                        var fi = new FileInfo(llamaCpp.ModelPath);
                        var details = new List<string>
                        {
                            $"Path: {llamaCpp.ModelPath}",
                            $"Size: {fi.Length / (1024.0 * 1024 * 1024):F2} GB",
                            $"Modified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}",
                            $"Binary: {llamaCpp.BinaryPath}",
                            $"Port: {llamaCpp.Port}",
                            $"GPU Layers: {llamaCpp.NGpuLayers}",
                            $"Context Size: {llamaCpp.ContextSize}",
                        };

                        return new ModelDetailsDto
                        {
                            Name = modelName,
                            ToolId = "llamacpp",
                            Parameters = string.Join("\n", details),
                        };
                    }
                }

                var searchDir = llamaCpp?.ModelPath != null ? Path.GetDirectoryName(llamaCpp.ModelPath) : null;
                if (!string.IsNullOrWhiteSpace(searchDir) && Directory.Exists(searchDir))
                {
                    foreach (var file in Directory.EnumerateFiles(searchDir, "*.gguf", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            var fi = new FileInfo(file);
                            return new ModelDetailsDto
                            {
                                Name = modelName,
                                ToolId = "llamacpp",
                                Parameters = $"Path: {file}\nSize: {fi.Length / (1024.0 * 1024 * 1024):F2} GB\nModified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}",
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 llama.cpp 模型 {ModelName} 详情失败", modelName);
            }
            return null;
        }

        private async Task<ModelDetailsDto?> GetFileBasedModelDetailsAsync(string toolId, string modelName, string searchDir, CancellationToken ct)
        {
            try
            {
                if (!Directory.Exists(searchDir)) return null;

                foreach (var file in Directory.EnumerateFiles(searchDir, "*.gguf", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                    {
                        var fi = new FileInfo(file);
                        return new ModelDetailsDto
                        {
                            Name = modelName,
                            ToolId = toolId,
                            Parameters = $"Path: {file}\nSize: {fi.Length / (1024.0 * 1024 * 1024):F2} GB\nModified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}",
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "从文件系统获取 {ToolId} 模型 {ModelName} 详情失败", toolId, modelName);
            }
            return null;
        }

        #endregion

        /// <summary>
        /// 加载模型到内存（自动确保本地服务已运行）
        /// </summary>
        public async Task<bool> LoadModelAsync(string toolId, string modelName, int keepAliveMinutes, CancellationToken ct = default)
        {
            bool result;
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
            {
                var running = await _autoStarter.TryEnsureRunningAsync("ollama", "http://localhost:11434/v1");
                if (!running)
                    throw new InvalidOperationException("Ollama 服务未运行且自动启动失败，请手动启动后重试。");
                result = await LoadOllamaModelAsync(modelName, keepAliveMinutes, ct);
            }
            else if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
            {
                var running = await _autoStarter.TryEnsureRunningAsync("lmstudio", "http://localhost:1234/v1");
                if (!running)
                    throw new InvalidOperationException("LM Studio 服务未运行且自动启动失败，请手动启动后重试。");
                result = await LoadLmStudioModelAsync(modelName, ct);
            }
            else if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
            {
                result = await LoadLlamaCppModelAsync(modelName, ct);
            }
            else
            {
                return false;
            }

            if (result) InvalidateCaches();
            return result;
        }

        private async Task<bool> LoadOllamaModelAsync(string modelName, int keepAliveMinutes, CancellationToken ct)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);
                var keepAlive = keepAliveMinutes < 0 ? "-1" : $"{keepAliveMinutes}m";
                var body = new Dictionary<string, object>
                {
                    ["model"] = modelName,
                    ["prompt"] = "",
                    ["stream"] = false,
                    ["keep_alive"] = keepAlive
                };
                var json = System.Text.Json.JsonSerializer.Serialize(body);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync("http://localhost:11434/api/generate", content, ct);
                if (!response.IsSuccessStatusCode) return false;

                var respJson = await response.Content.ReadAsStringAsync(ct);
                return respJson.Contains("\"done_reason\":\"load\"") || respJson.Contains("\"done\":true");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载 Ollama 模型失败: {Model}", modelName);
                return false;
            }
        }

        private async Task<bool> LoadLmStudioModelAsync(string modelName, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = $"load {modelName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process == null) return false;
                await process.WaitForExitAsync(ct);
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载 LM Studio 模型失败: {Model}", modelName);
                return false;
            }
        }

        /// <summary>
        /// 卸载模型释放内存
        /// </summary>
        public async Task<bool> UnloadModelAsync(string toolId, string modelName, CancellationToken ct = default)
        {
            bool result;
            if (toolId.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                result = await UnloadOllamaModelAsync(modelName, ct);
            else if (toolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                result = await UnloadLmStudioModelAsync(modelName, ct);
            else if (toolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                result = await UnloadLlamaCppModelAsync(ct);
            else
                return false;

            if (result) InvalidateCaches();
            return result;
        }

        private async Task<bool> UnloadOllamaModelAsync(string modelName, CancellationToken ct)
        {
            try
            {
                // 方法1: ollama stop CLI
                var psi = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = $"stop {modelName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0) return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ollama stop CLI 失败，回退到 API 方式");
            }

            try
            {
                // 方法2: API 方式设置 keep_alive=0 立即卸载
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
                var body = new Dictionary<string, object>
                {
                    ["model"] = modelName,
                    ["prompt"] = "",
                    ["stream"] = false,
                    ["keep_alive"] = 0
                };
                var json = System.Text.Json.JsonSerializer.Serialize(body);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/generate")
                {
                    Content = content
                };
                request.Headers.ConnectionClose = true;
                var response = await client.SendAsync(request, ct);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "卸载 Ollama 模型失败: {Model}", modelName);
                return false;
            }
        }

        private async Task<bool> UnloadLmStudioModelAsync(string modelName, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "lms",
                    Arguments = $"unload {modelName}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process == null) return false;
                await process.WaitForExitAsync(ct);
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "卸载 LM Studio 模型失败: {Model}", modelName);
                return false;
            }
        }

        #region LlamaCpp Helpers

        private async Task<(bool Installed, string? Version, bool Running, string? ModelPath)> GetLlamaCppToolInfoAsync(CancellationToken ct)
        {
            var config = await _openClawTaskService.GetLocalAiConfigAsync();
            var llamaCpp = config.LlamaCpp;

            if (llamaCpp == null)
                return (false, null, false, null);

            // 只要配置了有效二进制路径，就认为已安装（可以进入配置界面）
            // enabled 状态只影响服务是否运行，不影响"是否可配置"
            var binaryExists = !string.IsNullOrWhiteSpace(llamaCpp.BinaryPath) && File.Exists(llamaCpp.BinaryPath);
            if (!binaryExists)
                return (false, null, false, null);

            // 模型路径不再作为"是否安装"的必要条件（用户可能还没选模型）
            var modelExists = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath) && File.Exists(llamaCpp.ModelPath);

            // 尝试获取版本
            string? version = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = llamaCpp.BinaryPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode == 0)
                    {
                        var output = await process.StandardOutput.ReadToEndAsync(ct);
                        var match = Regex.Match(output, @"(\d+\.\d+\.?\d*)");
                        version = match.Success ? match.Groups[1].Value : output.Trim().Split('\n')[0].Trim();
                    }
                }
            }
            catch { }

            // 检测是否运行（只有 enabled 且模型存在时才可能运行）
            var running = false;
            if (llamaCpp.Enabled && modelExists)
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);
                    var response = await client.GetAsync($"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models", ct);
                    running = response.IsSuccessStatusCode;
                }
                catch { }
            }

            var modelDir = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath)
                ? Path.GetDirectoryName(llamaCpp.ModelPath)
                : null;

            return (true, version ?? "unknown", running, modelDir);
        }

        private async Task<List<RunningModelDto>> GetLlamaCppRunningModelsAsync(CancellationToken ct)
        {
            var list = new List<RunningModelDto>();
            var config = await _openClawTaskService.GetLocalAiConfigAsync();
            var llamaCpp = config.LlamaCpp;

            if (llamaCpp == null || !llamaCpp.Enabled || string.IsNullOrWhiteSpace(llamaCpp.ModelPath))
                return list;

            try
            {
                using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
                var response = await client.GetAsync($"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models", ct);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray)) return list;

                var modelFileName = Path.GetFileNameWithoutExtension(llamaCpp.ModelPath);
                var modelFileSize = 0L;
                try { modelFileSize = new FileInfo(llamaCpp.ModelPath).Length; }
                catch { }

                foreach (var item in dataArray.EnumerateArray())
                {
                    var modelId = GetJsonString(item, "id");
                    if (string.IsNullOrEmpty(modelId)) continue;

                    list.Add(new RunningModelDto
                    {
                        ToolId = "llamacpp",
                        ToolName = "llama.cpp",
                        ModelName = modelId,
                        DisplayName = modelFileName,
                        Status = "running",
                        SizeBytes = modelFileSize,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "获取 llama.cpp 运行中模型失败");
            }

            return list;
        }

        private async Task<List<string>> GetLlamaCppAvailableModelsAsync(CancellationToken ct)
        {
            var list = new List<string>();
            var config = await _openClawTaskService.GetLocalAiConfigAsync();
            var llamaCpp = config.LlamaCpp;

            if (llamaCpp == null || !llamaCpp.Enabled)
                return list;

            var searchDir = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath)
                ? Path.GetDirectoryName(llamaCpp.ModelPath)
                : null;

            if (string.IsNullOrWhiteSpace(searchDir) || !Directory.Exists(searchDir))
                return list;

            try
            {
                var ggufFiles = Directory.EnumerateFiles(searchDir, "*.gguf", SearchOption.TopDirectoryOnly);
                foreach (var file in ggufFiles)
                {
                    list.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "扫描 llama.cpp 模型目录失败");
            }

            return list;
        }

        private async Task<bool> LoadLlamaCppModelAsync(string modelName, CancellationToken ct)
        {
            var config = await _openClawTaskService.GetLocalAiConfigAsync();
            var llamaCpp = config.LlamaCpp;

            if (llamaCpp == null || !llamaCpp.Enabled)
            {
                _logger.LogWarning("llama.cpp 未启用");
                return false;
            }

            // 查找模型文件
            var searchDir = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath)
                ? Path.GetDirectoryName(llamaCpp.ModelPath)
                : null;

            string? targetModelPath = null;
            if (!string.IsNullOrWhiteSpace(searchDir) && Directory.Exists(searchDir))
            {
                targetModelPath = Directory.EnumerateFiles(searchDir, "*.gguf", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(modelName, StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrWhiteSpace(targetModelPath) || !File.Exists(targetModelPath))
            {
                _logger.LogWarning("llama.cpp 模型文件未找到: {ModelName}", modelName);
                return false;
            }

            // 如果当前运行的就是同一个模型，直接返回成功
            var currentModelFile = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath)
                ? Path.GetFileNameWithoutExtension(llamaCpp.ModelPath)
                : null;
            if (currentModelFile?.Equals(modelName, StringComparison.OrdinalIgnoreCase) == true)
            {
                // 检查服务是否已运行
                try
                {
                    using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync($"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models", ct);
                    if (response.IsSuccessStatusCode) return true;
                }
                catch { }
            }

            // 先停止当前运行的 llama-server
            await UnloadLlamaCppModelAsync(ct);

            // 更新配置中的模型路径
            llamaCpp.ModelPath = targetModelPath;
            await _openClawTaskService.SaveLocalAiConfigAsync(new SaveOpenClawLocalAiConfigRequest
            {
                LlamaCpp = llamaCpp
            });

            // 启动 llama.cpp
            var status = await _openClawTaskService.DetectAndStartLocalAiAsync("llamacpp");
            return status.IsRunning;
        }

        private async Task<bool> UnloadLlamaCppModelAsync(CancellationToken ct)
        {
            try
            {
                // 查找并终止 llama-server 进程
                var processes = Process.GetProcessesByName("llama-server");
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.Kill();
                        await proc.WaitForExitAsync(ct);
                    }
                    catch { /* ignore */ }
                }

                // 也尝试通过进程命令行查找
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pkill",
                        Arguments = "-f llama-server",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var pkill = Process.Start(psi);
                    if (pkill != null)
                    {
                        await pkill.WaitForExitAsync(ct);
                    }
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "卸载 llama.cpp 模型失败");
                return false;
            }
        }

        /// <summary>
        /// 启动 llama.cpp 服务（使用当前配置）
        /// </summary>
        public async Task<LocalAiServiceStatusDto> StartLlamaCppAsync(CancellationToken ct = default)
        {
            var result = await _openClawTaskService.DetectAndStartLocalAiAsync("llamacpp");
            if (result.IsRunning) InvalidateCaches();
            return result;
        }

        /// <summary>
        /// 停止 llama.cpp 服务
        /// </summary>
        public async Task<bool> StopLlamaCppAsync(CancellationToken ct = default)
        {
            var result = await UnloadLlamaCppModelAsync(ct);
            if (result) InvalidateCaches();
            return result;
        }

        /// <summary>
        /// 清除运行中模型缓存
        /// </summary>
        private void InvalidateCaches()
        {
            _cache.Remove(RunningModelsCacheKey);
            _cache.Remove(ToolsCacheKey);
            _logger.LogDebug("本地模型缓存已清除");
        }

        #endregion

        private static string GetJsonString(System.Text.Json.JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String)
                return prop.GetString() ?? "";
            return "";
        }

        private static long GetJsonLong(System.Text.Json.JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                if (prop.TryGetInt64(out var val)) return val;
                if (prop.TryGetInt32(out var intVal)) return intVal;
            }
            return 0;
        }

        #endregion

        /// <summary>
        /// 获取可用下载源列表
        /// </summary>
        public List<DownloadSourceDto> GetDownloadSources()
        {
            var sources = new List<DownloadSourceDto>
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

            return sources;
        }

        #endregion
    }
}
