using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskRunner.Contracts.LocalModels;
using TaskRunner.Helpers;
using TaskRunner.Models;

namespace TaskRunner.Services
{
    /// <summary>
    /// Ollama 本地模型服务：处理 Ollama 的检测、部署、运行、模型管理
    /// </summary>
    public class OllamaService(
        ILogger<OllamaService> logger,
        IHttpClientFactory httpClientFactory,
        LocalModelSettingsService localModelSettings)
    {
        #region Tool Info

        public async Task<string?> GetVersionAsync(CancellationToken ct = default)
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

        public long GetModelsDirFreeSpace()
        {
            try
            {
                var customDir = localModelSettings.LocalModelDownloadDirectory;
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

        public string GetDefaultModelsPath()
        {
            var custom = localModelSettings.LocalModelDownloadDirectory;
            if (!string.IsNullOrEmpty(custom))
                return custom;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".ollama", "models");
        }

        #endregion

        #region Deploy

        public async Task PullModelAsync(DeployTaskStatusDto task, string modelName, CancellationToken ct)
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

            var customDir = localModelSettings.LocalModelDownloadDirectory;
            if (!string.IsNullOrEmpty(customDir))
            {
                psi.EnvironmentVariables["OLLAMA_MODELS"] = customDir;
            }

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("无法启动 ollama pull 进程");

            var stdoutReader = Task.Run(async () =>
            {
                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(ct);
                    if (line == null) break;
                    ParsePullOutput(task, line);
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

        private void ParsePullOutput(DeployTaskStatusDto task, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            task.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {line.Trim()}");

            var progressMatch = Regex.Match(line, @"(\d+)%");
            if (progressMatch.Success && int.TryParse(progressMatch.Groups[1].Value, out var percent))
            {
                task.ProgressPercent = Math.Min(100, Math.Max(0, percent));
            }

            if (line.Contains("pulling manifest", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "下载清单";
            else if (line.Contains("pulling", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "下载模型文件";
            else if (line.Contains("verifying", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "验证文件";
            else if (line.Contains("writing", StringComparison.OrdinalIgnoreCase))
                task.CurrentStep = "写入模型";
        }

        public async Task<bool> VerifyModelAsync(string modelName, CancellationToken ct = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = "list",
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

        #endregion

        #region Running Models

        public async Task<List<RunningModelDto>> GetRunningModelsAsync(CancellationToken ct = default)
        {
            var list = new List<RunningModelDto>();
            using var client = httpClientFactory.CreateClient();
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
                    ModelName = JsonHelper.GetString(item, "name"),
                    DisplayName = JsonHelper.GetString(item, "name"),
                    Status = "running",
                    SizeBytes = JsonHelper.GetLong(item, "size"),
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
                    dto.Family = JsonHelper.GetString(details, "family");
                }

                list.Add(dto);
            }

            return list;
        }

        #endregion

        #region Available Models

        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var list = new List<string>();
            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync("http://localhost:11434/api/tags", ct);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("models", out var modelsArray)) return list;

                foreach (var item in modelsArray.EnumerateArray())
                {
                    var name = JsonHelper.GetString(item, "name");
                    if (!string.IsNullOrEmpty(name))
                        list.Add(name);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "获取 Ollama 可用模型列表失败");
            }
            return list;
        }

        #endregion

        #region Downloaded Models

        public async Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(List<RunningModelDto> runningModels, CancellationToken ct = default)
        {
            var list = new List<DownloadedModelDto>();
            try
            {
                using var client = httpClientFactory.CreateClient();
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
                    var name = JsonHelper.GetString(item, "name");
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
                        Digest = JsonHelper.GetString(item, "digest"),
                        IsRunning = runningSet.Contains(name),
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "获取 Ollama 已下载模型列表失败");
            }
            return list;
        }

        #endregion

        #region Model Lifecycle

        public async Task<bool> DeleteModelAsync(string modelName, CancellationToken ct = default)
        {
            try
            {
                using var client = httpClientFactory.CreateClient();
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
                    logger.LogInformation("Ollama 模型 {ModelName} 已删除", modelName);
                    return true;
                }

                var error = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("删除 Ollama 模型 {ModelName} 失败: {StatusCode} {Error}", modelName, response.StatusCode, error);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "删除 Ollama 模型 {ModelName} 异常", modelName);
                return false;
            }
        }

        public async Task<ModelDetailsDto?> GetModelDetailsAsync(string modelName, CancellationToken ct = default)
        {
            try
            {
                using var client = httpClientFactory.CreateClient();
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
                logger.LogDebug(ex, "获取 Ollama 模型 {ModelName} 详情失败", modelName);
                return null;
            }
        }

        public async Task<bool> LoadModelAsync(string modelName, int keepAliveMinutes, CancellationToken ct = default)
        {
            try
            {
                using var client = httpClientFactory.CreateClient();
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
                logger.LogError(ex, "加载 Ollama 模型失败: {Model}", modelName);
                return false;
            }
        }

        public async Task<bool> UnloadModelAsync(string modelName, CancellationToken ct = default)
        {
            try
            {
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
                logger.LogDebug(ex, "ollama stop CLI 失败，回退到 API 方式");
            }

            try
            {
                using var client = httpClientFactory.CreateClient();
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
                logger.LogError(ex, "卸载 Ollama 模型失败: {Model}", modelName);
                return false;
            }
        }

        #endregion

        #region Json Helpers



        #endregion
    }
}
