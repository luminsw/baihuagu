using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TaskRunner.Contracts.OpenClaw;
using TaskRunner.Data;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services;

public interface IOpenClawTaskService
{
    Task<OpenClawTaskDto> CreateTaskAsync(string prompt);
    Task<OpenClawTaskDto?> GetTaskAsync(int id);
    Task<List<OpenClawTaskDto>> GetTasksAsync(int limit = 100);
    Task<bool> DeleteTaskAsync(int id);
    Task<bool> CancelTaskAsync(int id);
    Task<string?> GetReportContentAsync(int id);
    Task<OpenClawLocalAiConfigDto> GetLocalAiConfigAsync();
    Task<bool> SaveLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request);
    Task<List<OpenClawLocalModelDto>> ScanLocalModelsAsync(string provider);
    Task<LocalAiServiceStatusDto> DetectAndStartLocalAiAsync(string provider);
    Task<OpenClawDefaultModelDto> GetDefaultModelAsync();
    Task<bool> SetDefaultModelAsync(string model);
    Task<ModelProfileListDto> GetModelProfilesAsync();
    Task<bool> SetModelProfileAsync(string profileId);
    Task<bool> SyncLocalModelsToOpenClawAsync(string provider);
}

public class OpenClawTaskService : IOpenClawTaskService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TaskRunner.Services.TaskManager? _taskManager;
    private readonly ILogger<OpenClawTaskService> _logger;
    private readonly string _reportsDir;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> _runningProcesses = new();
    // OpenClaw TaskId -> TaskManager TaskId 映射
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _openClawToTaskManagerMap = new();

    public OpenClawTaskService(IDbContextFactory<AppDbContext> dbFactory, IHttpClientFactory httpClientFactory, TaskRunner.Services.TaskManager? taskManager, ILogger<OpenClawTaskService> logger)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _taskManager = taskManager;
        _logger = logger;
        var dataDir = Environment.GetEnvironmentVariable("TASKRUNNER_DATA_DIR")
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        _reportsDir = Path.Combine(dataDir, "openclaw-reports");
        Directory.CreateDirectory(_reportsDir);
    }

    public async Task<OpenClawTaskDto> CreateTaskAsync(string prompt)
    {
        var task = new OpenClawTask
        {
            TaskId = Guid.NewGuid().ToString("N")[..8],
            Prompt = prompt,
            Status = "running"
        };
        using var db = await _dbFactory.CreateDbContextAsync();
        db.OpenClawTasks.Add(task);
        await db.SaveChangesAsync();

        // 同时注册到 TaskManager，使任务页可见
        string? tmTaskId = null;
        if (_taskManager != null)
        {
            tmTaskId = _taskManager.CreateTask("openclaw", new Dictionary<string, string>
            {
                ["prompt"] = prompt.Length > 100 ? prompt[..100] + "..." : prompt,
                ["openclawId"] = task.Id.ToString()
            });
            _openClawToTaskManagerMap[task.Id] = tmTaskId;
            _ = _taskManager.UpdateStatus(tmTaskId, TaskRunner.Services.TaskStatus.Running);
        }

        _ = RunOpenClawAsync(task.Id, prompt, tmTaskId);

        return ToDto(task);
    }

    private static readonly TimeSpan OpenClawTaskTimeout = TimeSpan.FromMinutes(30);

    private async Task RunOpenClawAsync(int taskId, string prompt, string? tmTaskId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(taskId);
        if (task == null) return;

        try
        {
            var sessionId = $"webui-{task.TaskId}";
            var startInfo = new ProcessStartInfo
            {
                FileName = "openclaw",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            startInfo.ArgumentList.Add("agent");
            startInfo.ArgumentList.Add("--session-id");
            startInfo.ArgumentList.Add(sessionId);
            startInfo.ArgumentList.Add("--message");
            startInfo.ArgumentList.Add(prompt);
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("--timeout");
            startInfo.ArgumentList.Add(((int)OpenClawTaskTimeout.TotalSeconds).ToString());

            _logger.LogInformation("Starting OpenClaw: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("无法启动 openclaw 进程");
            }

            _runningProcesses.TryAdd(taskId, process);

            // 必须同时开始读取 stdout 和 stderr，否则管道缓冲区满会导致死锁
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 整体超时：如果进程在 30 分钟内未退出则强制终止
            using var timeoutCts = new CancellationTokenSource(OpenClawTaskTimeout);
            var processExitTask = process.WaitForExitAsync(timeoutCts.Token);
            await processExitTask;

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogWarning("OpenClaw stderr: {Stderr}", stderr);
            }

            _logger.LogInformation("OpenClaw exited with code {ExitCode}, stdout length {Length}",
                process.ExitCode, stdout.Length);

            if (process.ExitCode != 0)
            {
                task.Status = "failed";
                task.ErrorMessage = $"Exit code {process.ExitCode}: {stderr}";
                task.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return;
            }

            // Parse JSON output
            var reportContent = ParseOpenClawOutput(stdout);
            var reportFileName = $"{task.TaskId}_{DateTime.UtcNow:yyyyMMddHHmmss}.md";
            var reportPath = Path.Combine(_reportsDir, reportFileName);
            await File.WriteAllTextAsync(reportPath, reportContent);

            task.Status = "completed";
            task.ReportPath = reportPath;
            task.Result = reportContent[..Math.Min(reportContent.Length, 2000)];
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation("OpenClaw task {TaskId} completed, report saved to {ReportPath}",
                task.TaskId, reportPath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OpenClaw task {TaskId} timed out after {Timeout}m", task.TaskId, OpenClawTaskTimeout.TotalMinutes);
            task.Status = "failed";
            task.ErrorMessage = $"任务执行超时（超过 {OpenClawTaskTimeout.TotalMinutes} 分钟）";
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenClaw task {TaskId} failed", task.TaskId);
            task.Status = "failed";
            task.ErrorMessage = ex.Message;
            task.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        finally
        {
            _runningProcesses.TryRemove(taskId, out _);
        }
    }

    public async Task<bool> CancelTaskAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        if (task == null || task.Status != "running") return false;

        // 尝试终止进程
        if (_runningProcesses.TryRemove(id, out var process))
        {
            try
            {
                process.Kill(true);
                _logger.LogInformation("OpenClaw task {TaskId} process killed", task.TaskId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenClaw task {TaskId} process kill failed", task.TaskId);
            }
        }

        task.Status = "failed";
        task.ErrorMessage = "用户已取消";
        task.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 同步更新 TaskManager 任务状态
        if (_openClawToTaskManagerMap.TryRemove(id, out var tmTaskId) && _taskManager != null)
        {
            await _taskManager.UpdateStatus(tmTaskId, TaskRunner.Services.TaskStatus.Cancelled, "用户已取消");
        }

        return true;
    }

    private static string ParseOpenClawOutput(string jsonOutput)
    {
        try
        {
            var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# OpenClaw 执行报告");
            sb.AppendLine();

            // OpenClaw may wrap payloads/meta under "result" or at root level
            var result = root;
            if (root.TryGetProperty("result", out var resultProp))
            {
                result = resultProp;
            }

            // Meta info
            if (result.TryGetProperty("meta", out var meta))
            {
                if (meta.TryGetProperty("durationMs", out var durationMs))
                    sb.AppendLine($"- **执行耗时**: {durationMs.GetDouble():F0} ms");
                if (meta.TryGetProperty("agentMeta", out var agentMeta) &&
                    agentMeta.TryGetProperty("model", out var modelProp))
                    sb.AppendLine($"- **模型**: {modelProp.GetString()}");
                sb.AppendLine();
            }

            // Payloads
            if (result.TryGetProperty("payloads", out var payloads))
            {
                foreach (var payload in payloads.EnumerateArray())
                {
                    if (payload.TryGetProperty("type", out var typeProp))
                    {
                        var type = typeProp.GetString();
                        if (type == "text" && payload.TryGetProperty("text", out var textProp))
                        {
                            sb.AppendLine(textProp.GetString());
                            sb.AppendLine();
                        }
                        else if (type == "json" && payload.TryGetProperty("json", out var jsonProp))
                        {
                            sb.AppendLine("```json");
                            sb.AppendLine(jsonProp.GetRawText());
                            sb.AppendLine("```");
                            sb.AppendLine();
                        }
                        else if (type == "artifact" && payload.TryGetProperty("title", out var titleProp))
                        {
                            sb.AppendLine($"### {titleProp.GetString()}");
                            if (payload.TryGetProperty("content", out var contentProp))
                            {
                                sb.AppendLine("```");
                                sb.AppendLine(contentProp.GetString());
                                sb.AppendLine("```");
                            }
                            sb.AppendLine();
                        }
                    }
                    else if (payload.TryGetProperty("text", out var textProp))
                    {
                        // Some payloads don't have "type" field
                        sb.AppendLine(textProp.GetString());
                        sb.AppendLine();
                    }
                }
            }

            // Fallback: if no structured payloads, include raw JSON
            if (sb.Length < 50)
            {
                sb.AppendLine("```json");
                sb.AppendLine(root.GetRawText());
                sb.AppendLine("```");
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            // Not valid JSON, return raw output wrapped in markdown
            return $"# OpenClaw 输出\n\n```\n{jsonOutput}\n```\n";
        }
    }

    public async Task<OpenClawTaskDto?> GetTaskAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        return task == null ? null : ToDto(task);
    }

    public async Task<List<OpenClawTaskDto>> GetTasksAsync(int limit = 100)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var tasks = await db.OpenClawTasks
            .OrderByDescending(t => t.CreatedAt)
            .Take(limit)
            .ToListAsync();
        return tasks.Select(ToDto).ToList();
    }

    public async Task<bool> DeleteTaskAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        if (task == null) return false;

        if (!string.IsNullOrEmpty(task.ReportPath) && File.Exists(task.ReportPath))
        {
            File.Delete(task.ReportPath);
        }

        db.OpenClawTasks.Remove(task);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<string?> GetReportContentAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.OpenClawTasks.FindAsync(id);
        if (task?.ReportPath == null || !File.Exists(task.ReportPath))
            return null;

        return await File.ReadAllTextAsync(task.ReportPath);
    }

    private static OpenClawTaskDto ToDto(OpenClawTask task) => new()
    {
        Id = task.Id,
        TaskId = task.TaskId,
        Prompt = task.Prompt,
        Status = task.Status,
        ReportPath = task.ReportPath,
        Result = task.Result,
        ErrorMessage = task.ErrorMessage,
        CreatedAt = task.CreatedAt,
        CompletedAt = task.CompletedAt
    };

    #region Local AI Config

    private static string GetOpenClawConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(home, ".openclaw", "openclaw.json");
    }

    private static string GetLlamaCppConfigPath()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(home, ".openclaw", "llamacpp-config.json");
    }

    public async Task<OpenClawLocalAiConfigDto> GetLocalAiConfigAsync()
    {
        var path = GetOpenClawConfigPath();
        var result = new OpenClawLocalAiConfigDto();

        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("models", out var models) &&
                models.TryGetProperty("providers", out var providers))
            {
                if (providers.TryGetProperty("ollama", out var ollama))
                {
                    result.Ollama = ParseProviderConfig(ollama);
                }
                if (providers.TryGetProperty("lmstudio", out var lmstudio))
                {
                    result.LmStudio = ParseProviderConfig(lmstudio);
                }
                if (providers.TryGetProperty("llamacpp", out var llamacpp))
                {
                    result.LlamaCpp = ParseLlamaCppConfig(llamacpp);
                }
            }
        }

        // llama.cpp 运行参数存储在单独文件中（避免 openclaw-gateway 覆盖）
        var llamaCppPath = GetLlamaCppConfigPath();
        if (File.Exists(llamaCppPath))
        {
            var json = await File.ReadAllTextAsync(llamaCppPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var cfg = result.LlamaCpp ?? new OpenClawLlamaCppConfigDto();
            if (root.TryGetProperty("enabled", out var enabled))
                cfg.Enabled = enabled.GetBoolean();
            if (root.TryGetProperty("binaryPath", out var binaryPath))
                cfg.BinaryPath = binaryPath.GetString() ?? "";
            if (root.TryGetProperty("modelPath", out var modelPath))
                cfg.ModelPath = modelPath.GetString() ?? "";
            if (root.TryGetProperty("baseUrl", out var baseUrl))
                cfg.BaseUrl = baseUrl.GetString() ?? "http://localhost:8080";
            if (root.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number)
                cfg.Port = port.GetInt32();
            if (root.TryGetProperty("nGpuLayers", out var ngl) && ngl.ValueKind == JsonValueKind.Number)
                cfg.NGpuLayers = ngl.GetInt32();
            if (root.TryGetProperty("contextSize", out var ctx) && ctx.ValueKind == JsonValueKind.Number)
                cfg.ContextSize = ctx.GetInt32();
            if (root.TryGetProperty("extraArgs", out var extraArgs))
                cfg.ExtraArgs = extraArgs.GetString() ?? "";
            if (root.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Number)
                cfg.Threads = threads.GetInt32();
            if (root.TryGetProperty("batchSize", out var batchSize) && batchSize.ValueKind == JsonValueKind.Number)
                cfg.BatchSize = batchSize.GetInt32();
            if (root.TryGetProperty("cacheTypeK", out var cacheTypeK))
                cfg.CacheTypeK = cacheTypeK.GetString() ?? "";
            if (root.TryGetProperty("cacheTypeV", out var cacheTypeV))
                cfg.CacheTypeV = cacheTypeV.GetString() ?? "";
            if (root.TryGetProperty("useContBatching", out var useContBatching) && useContBatching.ValueKind == JsonValueKind.True)
                cfg.UseContBatching = useContBatching.GetBoolean();
            result.LlamaCpp = cfg;
        }

        return result;
    }

    private static OpenClawLlamaCppConfigDto ParseLlamaCppConfig(JsonElement element)
    {
        var config = new OpenClawLlamaCppConfigDto { Enabled = true };
        if (element.TryGetProperty("baseUrl", out var baseUrl))
            config.BaseUrl = baseUrl.GetString() ?? "http://localhost:8080";
        if (element.TryGetProperty("api", out var api))
            config.ApiType = api.GetString() ?? "openai-completions";
        if (element.TryGetProperty("binaryPath", out var binaryPath))
            config.BinaryPath = binaryPath.GetString() ?? "";
        if (element.TryGetProperty("modelPath", out var modelPath))
            config.ModelPath = modelPath.GetString() ?? "";
        if (element.TryGetProperty("port", out var port) && port.ValueKind == JsonValueKind.Number)
            config.Port = port.GetInt32();
        if (element.TryGetProperty("nGpuLayers", out var ngl) && ngl.ValueKind == JsonValueKind.Number)
            config.NGpuLayers = ngl.GetInt32();
        if (element.TryGetProperty("contextSize", out var ctx) && ctx.ValueKind == JsonValueKind.Number)
            config.ContextSize = ctx.GetInt32();
        if (element.TryGetProperty("extraArgs", out var extraArgs))
            config.ExtraArgs = extraArgs.GetString() ?? "";
        if (element.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Number)
            config.Threads = threads.GetInt32();
        if (element.TryGetProperty("batchSize", out var batchSize) && batchSize.ValueKind == JsonValueKind.Number)
            config.BatchSize = batchSize.GetInt32();
        if (element.TryGetProperty("cacheTypeK", out var cacheTypeK))
            config.CacheTypeK = cacheTypeK.GetString() ?? "";
        if (element.TryGetProperty("cacheTypeV", out var cacheTypeV))
            config.CacheTypeV = cacheTypeV.GetString() ?? "";
        if (element.TryGetProperty("useContBatching", out var useContBatching) && useContBatching.ValueKind == JsonValueKind.True)
            config.UseContBatching = useContBatching.GetBoolean();
        return config;
    }

    private static OpenClawLocalProviderConfigDto ParseProviderConfig(JsonElement element)
    {
        var config = new OpenClawLocalProviderConfigDto { Enabled = true };
        if (element.TryGetProperty("baseUrl", out var baseUrl))
            config.BaseUrl = baseUrl.GetString() ?? "";
        if (element.TryGetProperty("api", out var api))
            config.ApiType = api.GetString() ?? "openai-completions";
        if (element.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                var model = new OpenClawLocalModelDto
                {
                    Id = m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = m.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    ApiType = m.TryGetProperty("api", out var mapi) ? mapi.GetString() ?? "openai-completions" : "openai-completions",
                    ContextWindow = m.TryGetProperty("contextWindow", out var ctx) ? ctx.GetInt32() : 128000,
                    MaxTokens = m.TryGetProperty("maxTokens", out var max) ? max.GetInt32() : 4096,
                };
                if (m.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
                {
                    model.Input = input.EnumerateArray().Select(x => x.GetString() ?? "text").ToList();
                }
                config.Models.Add(model);
            }
        }
        return config;
    }

    public async Task<bool> SaveLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request)
    {
        // Ollama: 用 openclaw config set 写入（避免 gateway 覆盖）
        if (request.Ollama != null)
        {
            if (request.Ollama.Enabled && !string.IsNullOrWhiteSpace(request.Ollama.BaseUrl))
            {
                var providerJson = BuildProviderJson(request.Ollama).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                if (!await RunOpenClawConfigSetAsync("models.providers.ollama", providerJson))
                    return false;
            }
            else
            {
                await RunOpenClawConfigUnsetAsync("models.providers.ollama");
            }
        }

        // LM Studio
        if (request.LmStudio != null)
        {
            if (request.LmStudio.Enabled && !string.IsNullOrWhiteSpace(request.LmStudio.BaseUrl))
            {
                var providerJson = BuildProviderJson(request.LmStudio).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                if (!await RunOpenClawConfigSetAsync("models.providers.lmstudio", providerJson))
                    return false;
            }
            else
            {
                await RunOpenClawConfigUnsetAsync("models.providers.lmstudio");
            }
        }

        // llama.cpp 运行参数写入单独文件
        if (request.LlamaCpp != null)
        {
            var llamaCppPath = GetLlamaCppConfigPath();
            var llamaCppConfig = new JsonObject
            {
                ["enabled"] = request.LlamaCpp.Enabled,
                ["binaryPath"] = request.LlamaCpp.BinaryPath,
                ["modelPath"] = request.LlamaCpp.ModelPath,
                ["baseUrl"] = request.LlamaCpp.BaseUrl,
                ["port"] = request.LlamaCpp.Port,
                ["nGpuLayers"] = request.LlamaCpp.NGpuLayers,
                ["contextSize"] = request.LlamaCpp.ContextSize,
                ["apiType"] = request.LlamaCpp.ApiType,
                ["extraArgs"] = request.LlamaCpp.ExtraArgs,
                ["threads"] = request.LlamaCpp.Threads,
                ["batchSize"] = request.LlamaCpp.BatchSize,
                ["cacheTypeK"] = request.LlamaCpp.CacheTypeK,
                ["cacheTypeV"] = request.LlamaCpp.CacheTypeV,
                ["useContBatching"] = request.LlamaCpp.UseContBatching,
            };
            await File.WriteAllTextAsync(llamaCppPath, llamaCppConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            if (request.LlamaCpp.Enabled && !string.IsNullOrWhiteSpace(request.LlamaCpp.ModelPath))
            {
                var providerJson = BuildLlamaCppProviderJson(request.LlamaCpp).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                if (!await RunOpenClawConfigSetAsync("models.providers.llamacpp", providerJson))
                    return false;
            }
            else
            {
                await RunOpenClawConfigUnsetAsync("models.providers.llamacpp");
            }
        }

        return true;
    }

    private async Task<bool> RunOpenClawConfigSetAsync(string path, string jsonValue)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "openclaw",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("config");
            startInfo.ArgumentList.Add("set");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add(jsonValue);
            startInfo.ArgumentList.Add("--strict-json");
            startInfo.ArgumentList.Add("--merge");

            using var process = Process.Start(startInfo);
            if (process == null) return false;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("openclaw config set 失败 ({Path}): {Stderr}", path, stderr);
                return false;
            }
            _logger.LogInformation("openclaw config set 成功: {Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "openclaw config set 异常 ({Path})", path);
            return false;
        }
    }

    private async Task<bool> RunOpenClawConfigUnsetAsync(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "openclaw",
                Arguments = $"config unset {path}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "openclaw config unset 异常 ({Path})", path);
            return false;
        }
    }

    private static JsonObject BuildLlamaCppProviderJson(OpenClawLlamaCppConfigDto config)
    {
        var baseUrl = config.BaseUrl.TrimEnd('/');
        var modelName = Path.GetFileNameWithoutExtension(config.ModelPath);
        // Clean up model name for id
        var modelId = modelName.Replace(".", "-").ToLowerInvariant();

        var modelsArray = new JsonArray
        {
            new JsonObject
            {
                ["id"] = modelId,
                ["name"] = modelName,
                ["api"] = config.ApiType,
                ["input"] = new JsonArray("text"),
                ["contextWindow"] = config.ContextSize,
                ["maxTokens"] = 4096,
            }
        };

        // 只返回 OpenClaw 标准字段（避免 schema 验证失败）
        return new JsonObject
        {
            ["baseUrl"] = baseUrl,
            ["api"] = config.ApiType,
            ["models"] = modelsArray,
        };
    }

    private static JsonObject BuildProviderJson(OpenClawLocalProviderConfigDto config)
    {
        var modelsArray = new JsonArray();
        foreach (var m in config.Models)
        {
            var modelObj = new JsonObject
            {
                ["id"] = m.Id,
                ["name"] = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
                ["api"] = m.ApiType,
                ["input"] = new JsonArray(m.Input.Select(i => JsonValue.Create(i)).ToArray()),
                ["contextWindow"] = m.ContextWindow,
                ["maxTokens"] = m.MaxTokens,
            };
            modelsArray.Add(modelObj);
        }

        return new JsonObject
        {
            ["baseUrl"] = config.BaseUrl.TrimEnd('/'),
            ["api"] = config.ApiType,
            ["models"] = modelsArray,
        };
    }

    public async Task<List<OpenClawLocalModelDto>> ScanLocalModelsAsync(string provider)
    {
        var config = await GetLocalAiConfigAsync();
        var providerConfig = provider.ToLowerInvariant() switch
        {
            "ollama" => config.Ollama,
            "lmstudio" => config.LmStudio,
            "llamacpp" => null, // llama.cpp handled separately
            _ => null
        };

        if (provider.ToLowerInvariant() == "llamacpp")
        {
            return await ScanLlamaCppModelsAsync(config.LlamaCpp);
        }

        if (providerConfig == null || !providerConfig.Enabled || string.IsNullOrWhiteSpace(providerConfig.BaseUrl))
        {
            return new List<OpenClawLocalModelDto>();
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var baseUrl = providerConfig.BaseUrl.TrimEnd('/');
            // 避免 BaseUrl 已包含 /v1 导致重复（如 LM Studio 默认 http://localhost:1234/v1）
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = baseUrl[..^3];
            }
            var response = await httpClient.GetAsync($"{baseUrl}/v1/models");
            if (!response.IsSuccessStatusCode && providerConfig.ApiType == "ollama")
            {
                response = await httpClient.GetAsync($"{providerConfig.BaseUrl.TrimEnd('/')}/api/tags");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new List<OpenClawLocalModelDto>();
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new List<OpenClawLocalModelDto>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                // OpenAI-compatible /v1/models format
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new OpenClawLocalModelDto
                    {
                        Id = id,
                        Name = id,
                        ApiType = providerConfig.ApiType,
                    });
                }
            }
            else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                // LM Studio /v1/models or similar
                foreach (var item in models.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    result.Add(new OpenClawLocalModelDto
                    {
                        Id = id,
                        Name = id,
                        ApiType = providerConfig.ApiType,
                    });
                }
            }
            else if (providerConfig.ApiType == "ollama" && root.ValueKind == JsonValueKind.Object)
            {
                // Ollama /api/tags format
                if (root.TryGetProperty("models", out var ollamaModels) && ollamaModels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ollamaModels.EnumerateArray())
                    {
                        var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(name)) continue;
                        result.Add(new OpenClawLocalModelDto
                        {
                            Id = name,
                            Name = name,
                            ApiType = "ollama",
                        });
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描本地模型失败，Provider: {Provider}", provider);
            return new List<OpenClawLocalModelDto>();
        }
    }

    private async Task<List<OpenClawLocalModelDto>> ScanLlamaCppModelsAsync(OpenClawLlamaCppConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.ModelPath))
        {
            return new List<OpenClawLocalModelDto>();
        }

        // 先检查服务是否已运行
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            var response = await httpClient.GetAsync($"{config.BaseUrl.TrimEnd('/')}/v1/models");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var result = new List<OpenClawLocalModelDto>();
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(id)) continue;
                        result.Add(new OpenClawLocalModelDto
                        {
                            Id = id,
                            Name = id,
                            ApiType = config.ApiType,
                        });
                    }
                }
                return result;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "探测 llama.cpp 运行模型失败"); }

        // 服务未运行，返回配置中的模型（前端会提示用户先启动）
        if (File.Exists(config.ModelPath))
        {
            var modelName = Path.GetFileNameWithoutExtension(config.ModelPath);
            var modelId = modelName.Replace(".", "-").ToLowerInvariant();
            return new List<OpenClawLocalModelDto>
            {
                new OpenClawLocalModelDto
                {
                    Id = modelId,
                    Name = $"{modelName} (需启动服务)",
                    ApiType = config.ApiType,
                    ContextWindow = config.ContextSize,
                }
            };
        }

        return new List<OpenClawLocalModelDto>();
    }

    #region Sync Local Models to OpenClaw

    public async Task<bool> SyncLocalModelsToOpenClawAsync(string provider)
    {
        var config = await GetLocalAiConfigAsync();

        return provider.ToLowerInvariant() switch
        {
            "ollama" => await SyncOllamaModelsToOpenClawAsync(config.Ollama),
            "lmstudio" => await SyncLmStudioModelsToOpenClawAsync(config.LmStudio),
            "llamacpp" => await SyncLlamaCppModelsToOpenClawAsync(config.LlamaCpp),
            _ => false,
        };
    }

    private async Task<bool> SyncOllamaModelsToOpenClawAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            _logger.LogWarning("Ollama 未配置或未启用，无法同步模型");
            return false;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync($"{config.BaseUrl.TrimEnd('/')}/api/tags");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama /api/tags 请求失败: {StatusCode}", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modelsArray = new JsonArray();
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;

                    modelsArray.Add(new JsonObject
                    {
                        ["id"] = name,
                        ["name"] = name,
                        ["api"] = config.ApiType,
                        ["input"] = new JsonArray("text"),
                        ["contextWindow"] = 128000,
                        ["maxTokens"] = 4096,
                    });
                }
            }

            var providerJson = new JsonObject
            {
                ["baseUrl"] = config.BaseUrl.TrimEnd('/'),
                ["api"] = config.ApiType,
                ["models"] = modelsArray,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

            return await RunOpenClawConfigSetAsync("models.providers.ollama", providerJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步 Ollama 模型到 OpenClaw 失败");
            return false;
        }
    }

    private async Task<bool> SyncLmStudioModelsToOpenClawAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            _logger.LogWarning("LM Studio 未配置或未启用，无法同步模型");
            return false;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var baseUrl = config.BaseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = baseUrl[..^3];
            }
            var response = await httpClient.GetAsync($"{baseUrl}/v1/models");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("LM Studio /v1/models 请求失败: {StatusCode}", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var modelsArray = new JsonArray();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(id)) continue;

                    modelsArray.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["name"] = id,
                        ["api"] = config.ApiType,
                        ["input"] = new JsonArray("text"),
                        ["contextWindow"] = 128000,
                        ["maxTokens"] = 4096,
                    });
                }
            }

            var providerJson = new JsonObject
            {
                ["baseUrl"] = config.BaseUrl.TrimEnd('/'),
                ["api"] = config.ApiType,
                ["models"] = modelsArray,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

            return await RunOpenClawConfigSetAsync("models.providers.lmstudio", providerJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步 LM Studio 模型到 OpenClaw 失败");
            return false;
        }
    }

    private async Task<bool> SyncLlamaCppModelsToOpenClawAsync(OpenClawLlamaCppConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.ModelPath))
        {
            _logger.LogWarning("llama.cpp 未配置或未启用，无法同步模型");
            return false;
        }

        var providerJson = BuildLlamaCppProviderJson(config).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return await RunOpenClawConfigSetAsync("models.providers.llamacpp", providerJson);
    }

    #endregion

    public async Task<LocalAiServiceStatusDto> DetectAndStartLocalAiAsync(string provider)
    {
        var result = new LocalAiServiceStatusDto { Provider = provider };

        // llama.cpp 特殊处理
        if (provider.ToLowerInvariant() == "llamacpp")
        {
            return await DetectAndStartLlamaCppAsync();
        }

        var (checkUrl, startCmd, startArgs, displayName) = provider.ToLowerInvariant() switch
        {
            "ollama" => ("http://localhost:11434/api/tags", "ollama", "serve", "Ollama"),
            "lmstudio" => ("http://localhost:1234/v1/models", "lms", "server start", "LM Studio"),
            _ => (null, null, null, provider)
        };

        if (checkUrl == null)
        {
            result.Message = $"不支持的 provider: {provider}";
            return result;
        }

        // 1. 检测服务是否已运行
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            var response = await httpClient.GetAsync(checkUrl);
            if (response.IsSuccessStatusCode)
            {
                result.IsRunning = true;
                result.Message = $"{displayName} 服务正在运行";
                return result;
            }
        }
        catch
        {
            // 未运行，继续尝试启动
        }

        // 2. 尝试启动服务
        result.AttemptedStart = true;
        if (startCmd == null)
        {
            result.Message = $"{displayName} 未运行，且未配置启动命令";
            return result;
        }

        // 检查命令是否存在
        var cmdPath = FindCommandPath(startCmd);
        if (string.IsNullOrEmpty(cmdPath))
        {
            result.Message = $"{displayName} 未运行，且找不到启动命令 '{startCmd}'，请手动安装并启动";
            return result;
        }

        try
        {
            _logger.LogInformation("正在启动 {DisplayName}: {Cmd} {Args}", displayName, cmdPath, startArgs);
            var startInfo = new ProcessStartInfo
            {
                FileName = cmdPath,
                Arguments = startArgs ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // 对于需要后台持续运行的服务，启动后不要等待退出，也不重定向输出避免缓冲区阻塞
            var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Message = $"启动 {displayName} 失败：无法创建进程";
                return result;
            }

            // 给服务一点启动时间
            await Task.Delay(TimeSpan.FromSeconds(3));

            // 轮询检测服务是否就绪（最多 15 秒）
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                try
                {
                    using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(3);
                    var response = await httpClient.GetAsync(checkUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        result.IsRunning = true;
                        result.StartSuccess = true;
                        result.Message = $"{displayName} 启动成功";
                        return result;
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "探测 {DisplayName} 启动状态失败", displayName); }
            }

            // 启动超时
            result.Message = $"{displayName} 已尝试启动，但服务未在预期时间内就绪，请检查日志";
            _logger.LogWarning("{DisplayName} 启动后未就绪", displayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 {DisplayName} 失败", displayName);
            result.Message = $"启动 {displayName} 失败: {ex.Message}";
        }

        return result;
    }

    private static string? FindCommandPath(string command)
    {
        // 先直接尝试
        if (File.Exists(command)) return command;

        // 在 PATH 中查找
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            var fullPath = Path.Combine(dir, command);
            if (File.Exists(fullPath)) return fullPath;
            // Windows .exe
            if (File.Exists(fullPath + ".exe")) return fullPath + ".exe";
        }

        // 常见额外路径
        var extraPaths = new[]
        {
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/opt",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
        };
        foreach (var dir in extraPaths)
        {
            var fullPath = Path.Combine(dir, command);
            if (File.Exists(fullPath)) return fullPath;
            if (File.Exists(fullPath + ".exe")) return fullPath + ".exe";
        }

        return null;
    }

    #endregion

    #region LlamaCpp

    private async Task<LocalAiServiceStatusDto> DetectAndStartLlamaCppAsync()
    {
        var result = new LocalAiServiceStatusDto { Provider = "llamacpp" };
        var config = await GetLocalAiConfigAsync();
        var llamaCpp = config.LlamaCpp;

        if (llamaCpp == null || !llamaCpp.Enabled)
        {
            result.Message = "llama.cpp 未启用";
            return result;
        }

        if (string.IsNullOrWhiteSpace(llamaCpp.BinaryPath) || !File.Exists(llamaCpp.BinaryPath))
        {
            result.Message = $"llama-server 路径无效: {llamaCpp.BinaryPath}";
            return result;
        }

        if (string.IsNullOrWhiteSpace(llamaCpp.ModelPath) || !File.Exists(llamaCpp.ModelPath))
        {
            result.Message = $"模型文件不存在: {llamaCpp.ModelPath}";
            return result;
        }

        var checkUrl = $"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models";

        // 1. 检测服务是否已运行
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            var response = await httpClient.GetAsync(checkUrl);
            if (response.IsSuccessStatusCode)
            {
                result.IsRunning = true;
                result.Message = "llama.cpp 服务正在运行";
                return result;
            }
        }
        catch
        {
            // 未运行，继续尝试启动
        }

        // 2. 尝试启动服务
        result.AttemptedStart = true;
        try
        {
            var oneapiScript = "/opt/intel/oneapi/setvars.sh";
            var hasOneapi = File.Exists(oneapiScript);
            var args = $"-m \"{llamaCpp.ModelPath}\" -ngl {llamaCpp.NGpuLayers} --port {llamaCpp.Port} --host 127.0.0.1 -c {llamaCpp.ContextSize}";

            // 组合预定义参数
            if (llamaCpp.UseFlashAttn) args += " --flash-attn";
            if (llamaCpp.UseMlock) args += " --mlock";
            if (llamaCpp.UseNoMmap) args += " --no-mmap";
            if (llamaCpp.Threads > 0) args += $" -t {llamaCpp.Threads}";
            if (llamaCpp.BatchSize > 0) args += $" -b {llamaCpp.BatchSize}";
            if (!string.IsNullOrWhiteSpace(llamaCpp.CacheTypeK)) args += $" --cache-type-k {llamaCpp.CacheTypeK}";
            if (!string.IsNullOrWhiteSpace(llamaCpp.CacheTypeV)) args += $" --cache-type-v {llamaCpp.CacheTypeV}";
            if (llamaCpp.UseContBatching) args += " --cont-batching";

            if (!string.IsNullOrWhiteSpace(llamaCpp.ExtraArgs))
                args += " " + llamaCpp.ExtraArgs.Trim();
            var shellCmd = hasOneapi
                ? $"source {oneapiScript} > /dev/null 2>&1 && {llamaCpp.BinaryPath} {args}"
                : $"{llamaCpp.BinaryPath} {args}";

            _logger.LogInformation("正在启动 llama.cpp: {Cmd}", shellCmd);
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{shellCmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Message = "启动 llama.cpp 失败：无法创建进程";
                return result;
            }

            // llama.cpp 加载模型需要较长时间
            await Task.Delay(TimeSpan.FromSeconds(5));

            // 轮询检测服务是否就绪（最多 30 秒）
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                try
                {
                    using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(3);
                    var response = await httpClient.GetAsync(checkUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        result.IsRunning = true;
                        result.StartSuccess = true;
                        result.Message = "llama.cpp 启动成功（GPU 加速已启用）";
                        return result;
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "探测 llama.cpp 启动状态失败"); }
            }

            result.Message = "llama.cpp 已尝试启动，但服务未在预期时间内就绪，请检查日志";
            _logger.LogWarning("llama.cpp 启动后未就绪");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 llama.cpp 失败");
            result.Message = $"启动 llama.cpp 失败: {ex.Message}";
        }

        return result;
    }

    #endregion

    #region Default Model

    public async Task<OpenClawDefaultModelDto> GetDefaultModelAsync()
    {
        var result = new OpenClawDefaultModelDto();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "openclaw",
                Arguments = "config get agents.defaults.model.primary",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                {
                    var val = stdout.Trim();
                    if (!val.Contains("Config path not found"))
                        result.CurrentModel = val;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 OpenClaw 默认模型失败");
        }

        // 收集可用模型
        try
        {
            var config = await GetLocalAiConfigAsync();
            if (config.Ollama?.Enabled == true)
            {
                foreach (var m in config.Ollama.Models)
                    result.AvailableModels.Add($"ollama/{m.Id}");
            }
            if (config.LmStudio?.Enabled == true)
            {
                foreach (var m in config.LmStudio.Models)
                    result.AvailableModels.Add($"lmstudio/{m.Id}");
            }
            if (config.LlamaCpp?.Enabled == true)
            {
                var modelName = Path.GetFileNameWithoutExtension(config.LlamaCpp.ModelPath);
                var modelId = modelName.Replace(".", "-").ToLowerInvariant();
                result.AvailableModels.Add($"llamacpp/{modelId}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "收集可用模型失败");
        }

        return result;
    }

    public async Task<bool> SetDefaultModelAsync(string model)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "openclaw",
                Arguments = $"config set agents.defaults.model.primary \"{model.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("设置默认模型失败: {Stderr}", stderr);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置 OpenClaw 默认模型失败");
            return false;
        }
    }

    // 内置模型配置文件
    private static readonly List<ModelProfileDto> BuiltInProfiles = new()
    {
        new()
        {
            Id = "fast",
            Name = "快速",
            Description = "671MB 超轻量模型，响应极快，适合简单问答和日常查询",
            Model = "ollama/qwen2.5:0.5b",
            Provider = "ollama",
            SizeInfo = "671MB",
            SpeedLabel = "⚡ 极快"
        },
        new()
        {
            Id = "balanced",
            Name = "平衡",
            Description = "4.7GB 量化模型，在知识库内容上表现均衡，推荐日常使用",
            Model = "ollama/biancang:latest",
            Provider = "ollama",
            SizeInfo = "4.7GB Q4_K_M",
            SpeedLabel = "🚀 快"
        },
        new()
        {
            Id = "powerful",
            Name = "强力",
            Description = "27B 大参数模型，推理能力强，适合复杂辨证分析和深度问答",
            Model = "ollama/qwen3.6:27b",
            Provider = "ollama",
            SizeInfo = "~17GB",
            SpeedLabel = "🐢 较慢"
        }
    };

    public async Task<ModelProfileListDto> GetModelProfilesAsync()
    {
        var result = new ModelProfileListDto
        {
            Profiles = BuiltInProfiles
        };

        try
        {
            var defaultModel = await GetDefaultModelAsync();
            if (!string.IsNullOrWhiteSpace(defaultModel.CurrentModel))
            {
                var profile = BuiltInProfiles.FirstOrDefault(p =>
                    p.Model.Equals(defaultModel.CurrentModel, StringComparison.OrdinalIgnoreCase));
                result.CurrentProfile = profile?.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取当前 profile 失败");
        }

        return result;
    }

    public async Task<bool> SetModelProfileAsync(string profileId)
    {
        var profile = BuiltInProfiles.FirstOrDefault(p =>
            p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            _logger.LogWarning("未知模型配置: {ProfileId}", profileId);
            return false;
        }

        return await SetDefaultModelAsync(profile.Model);
    }

    #endregion
}
