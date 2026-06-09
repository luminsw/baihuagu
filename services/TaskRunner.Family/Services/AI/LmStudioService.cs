using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskRunner.Contracts.LocalModels;
using TaskRunner.Helpers;
using TaskRunner.Models;

namespace TaskRunner.Services
{
    /// <summary>
    /// LM Studio 本地模型服务：处理 LM Studio 的检测、部署、运行、模型管理
    /// </summary>
    public class LmStudioService(
        ILogger<LmStudioService> logger,
        IHttpClientFactory httpClientFactory)
    {
        #region Tool Info

        public async Task<string?> GetVersionAsync(CancellationToken ct = default)
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

        public long GetModelsDirFreeSpace()
        {
            try
            {
                var dir = LmStudioDownloadService.GetDefaultModelsPath();
                if (!Directory.Exists(dir))
                    dir = Path.GetDirectoryName(dir) ?? dir;

                var drive = new DriveInfo(Path.GetPathRoot(dir) ?? dir);
                return drive.IsReady ? drive.AvailableFreeSpace : 0;
            }
            catch { return 0; }
        }


        #endregion

        #region Deploy

        #endregion

        #region Running Models

        public async Task<List<RunningModelDto>> GetRunningModelsAsync(CancellationToken ct = default)
        {
            var list = new List<RunningModelDto>();

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
                                    var modelId = JsonHelper.GetString(item, "modelKey");
                                    if (string.IsNullOrEmpty(modelId))
                                        modelId = JsonHelper.GetString(item, "identifier");
                                    if (string.IsNullOrEmpty(modelId))
                                        modelId = JsonHelper.GetString(item, "id");

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
                logger.LogDebug(ex, "lms ps --json 失败，回退到 HTTP API");
            }

            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(3);
                var response = await client.GetAsync("http://localhost:1234/v1/models", ct);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray)) return list;

                foreach (var item in dataArray.EnumerateArray())
                {
                    var modelId = JsonHelper.GetString(item, "id");
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
                logger.LogDebug(ex, "获取 LM Studio 运行中模型失败");
            }

            return list;
        }

        #endregion

        #region Available Models

        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                                    var key = JsonHelper.GetString(item, "modelKey");
                                    if (string.IsNullOrEmpty(key))
                                        key = JsonHelper.GetString(item, "name");
                                    if (string.IsNullOrEmpty(key))
                                        key = JsonHelper.GetString(item, "id");
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
                logger.LogDebug(ex, "lms ls --json 失败");
            }

            if (list.Count == 0)
            {
                try
                {
                    var modelsPath = LmStudioDownloadService.GetDefaultModelsPath();
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
                    logger.LogDebug(ex, "扫描 LM Studio 模型目录失败");
                }
            }

            if (list.Count == 0)
            {
                try
                {
                    using var client = httpClientFactory.CreateClient();
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
                                var id = JsonHelper.GetString(item, "id");
                                if (!string.IsNullOrEmpty(id) && seen.Add(id))
                                    list.Add(id);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "获取 LM Studio 可用模型列表失败");
                }
            }

            return list;
        }

        #endregion

        #region Downloaded Models

        public async Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(List<RunningModelDto> runningModels, CancellationToken ct = default)
        {
            var list = new List<DownloadedModelDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runningSet = new HashSet<string>(runningModels
                .Where(r => r.ToolId.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.ModelName), StringComparer.OrdinalIgnoreCase);

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
                                    var key = JsonHelper.GetString(item, "modelKey");
                                    if (string.IsNullOrEmpty(key)) key = JsonHelper.GetString(item, "name");
                                    if (string.IsNullOrEmpty(key)) key = JsonHelper.GetString(item, "id");
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
                logger.LogDebug(ex, "lms ls --json 获取已下载模型失败");
            }

            if (list.Count == 0)
            {
                try
                {
                    var modelsPath = LmStudioDownloadService.GetDefaultModelsPath();
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
                            catch (Exception ex) { logger.LogDebug(ex, "文件系统操作失败"); }

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
                    logger.LogDebug(ex, "扫描 LM Studio 已下载模型目录失败");
                }
            }

            return list;
        }

        #endregion

        #region Model Lifecycle

        public async Task<ModelDetailsDto?> GetModelDetailsAsync(string modelName, CancellationToken ct = default)
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
                                    var key = JsonHelper.GetString(item, "modelKey");
                                    if (string.IsNullOrEmpty(key)) key = JsonHelper.GetString(item, "name");
                                    if (string.IsNullOrEmpty(key)) key = JsonHelper.GetString(item, "id");
                                    if (!key.Equals(modelName, StringComparison.OrdinalIgnoreCase)) continue;

                                    var dto = new ModelDetailsDto
                                    {
                                        Name = modelName,
                                        ToolId = "lmstudio",
                                    };

                                    var displayName = JsonHelper.GetString(item, "displayName");
                                    var publisher = JsonHelper.GetString(item, "publisher");
                                    var path = JsonHelper.GetString(item, "path");
                                    var paramsStr = JsonHelper.GetString(item, "paramsString");
                                    var architecture = JsonHelper.GetString(item, "architecture");

                                    var details = new List<string>();
                                    if (!string.IsNullOrEmpty(displayName)) details.Add($"Display Name: {displayName}");
                                    if (!string.IsNullOrEmpty(publisher)) details.Add($"Publisher: {publisher}");
                                    if (!string.IsNullOrEmpty(path)) details.Add($"Path: {path}");
                                    if (!string.IsNullOrEmpty(paramsStr)) details.Add($"Parameters: {paramsStr}");
                                    if (!string.IsNullOrEmpty(architecture)) details.Add($"Architecture: {architecture}");

                                    if (item.TryGetProperty("quantization", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Object)
                                    {
                                        var qName = JsonHelper.GetString(q, "name");
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
                logger.LogDebug(ex, "获取 LM Studio 模型 {ModelName} 详情失败", modelName);
            }

            return await GetFileBasedModelDetailsAsync(modelName, LmStudioDownloadService.GetDefaultModelsPath(), ct);
        }

        public async Task<bool> LoadModelAsync(string modelName, CancellationToken ct = default)
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
                logger.LogError(ex, "加载 LM Studio 模型失败: {Model}", modelName);
                return false;
            }
        }

        public async Task<bool> UnloadModelAsync(string modelName, CancellationToken ct = default)
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
                logger.LogError(ex, "卸载 LM Studio 模型失败: {Model}", modelName);
                return false;
            }
        }

        private async Task<ModelDetailsDto?> GetFileBasedModelDetailsAsync(string modelName, string searchDir, CancellationToken ct)
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
                            ToolId = "lmstudio",
                            Parameters = $"Path: {file}\nSize: {fi.Length / (1024.0 * 1024 * 1024):F2} GB\nModified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}",
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "从文件系统获取 LM Studio 模型 {ModelName} 详情失败", modelName);
            }
            return null;
        }

        #endregion


    }
}
