using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskRunner.Contracts.LocalModels;
using TaskRunner.Contracts.OpenClaw;
using TaskRunner.Helpers;
using TaskRunner.Models;

namespace TaskRunner.Services
{
    /// <summary>
    /// llama.cpp 本地模型服务：处理 llama.cpp 的检测、运行、模型管理
    /// </summary>
    public class LlamaCppService(
        ILogger<LlamaCppService> logger,
        IHttpClientFactory httpClientFactory,
        ILocalAiConfigService localAiConfig)
    {
        #region Tool Info

        public async Task<(bool Installed, string? Version, bool Running, string? ModelPath)> GetToolInfoAsync(CancellationToken ct = default)
        {
            var config = await localAiConfig.GetLocalAiConfigAsync();
            var llamaCpp = config.LlamaCpp;

            if (llamaCpp == null)
                return (false, null, false, null);

            var binaryExists = !string.IsNullOrWhiteSpace(llamaCpp.BinaryPath) && File.Exists(llamaCpp.BinaryPath);
            if (!binaryExists)
                return (false, null, false, null);

            var modelExists = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath) && File.Exists(llamaCpp.ModelPath);

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
            catch (Exception ex) { logger.LogDebug(ex, "操作失败"); }

            var running = false;
            if (llamaCpp.Enabled && modelExists)
            {
                try
                {
                    using var client = httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(2);
                    var response = await client.GetAsync($"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models", ct);
                    running = response.IsSuccessStatusCode;
                }
                catch (Exception ex) { logger.LogDebug(ex, "检测服务运行状态失败"); }
            }

            var modelDir = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath)
                ? Path.GetDirectoryName(llamaCpp.ModelPath)
                : null;

            return (true, version ?? "unknown", running, modelDir);
        }

        #endregion

        #region Running Models

        public async Task<List<RunningModelDto>> GetRunningModelsAsync(CancellationToken ct = default)
        {
            var list = new List<RunningModelDto>();
            var config = await localAiConfig.GetLocalAiConfigAsync();
            var llamaCpp = config.LlamaCpp;

            if (llamaCpp == null || !llamaCpp.Enabled || string.IsNullOrWhiteSpace(llamaCpp.ModelPath))
                return list;

            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(3);
                var response = await client.GetAsync($"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models", ct);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataArray)) return list;

                var modelFileName = Path.GetFileNameWithoutExtension(llamaCpp.ModelPath);
                var modelFileSize = 0L;
                try { modelFileSize = new FileInfo(llamaCpp.ModelPath).Length; }
                catch (Exception ex) { logger.LogDebug(ex, "文件系统操作失败"); }

                foreach (var item in dataArray.EnumerateArray())
                {
                    var modelId = JsonHelper.GetString(item, "id");
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
                logger.LogDebug(ex, "获取 llama.cpp 运行中模型失败");
            }

            return list;
        }

        #endregion

        #region Available Models

        public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
        {
            var list = new List<string>();
            var config = await localAiConfig.GetLocalAiConfigAsync();
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
                logger.LogDebug(ex, "扫描 llama.cpp 模型目录失败");
            }

            return list;
        }

        #endregion

        #region Downloaded Models

        public async Task<List<DownloadedModelDto>> GetDownloadedModelsAsync(List<RunningModelDto> runningModels, CancellationToken ct = default)
        {
            var list = new List<DownloadedModelDto>();
            var runningSet = new HashSet<string>(runningModels
                .Where(r => r.ToolId.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.ModelName), StringComparer.OrdinalIgnoreCase);

            try
            {
                var config = await localAiConfig.GetLocalAiConfigAsync();
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
                    catch (Exception ex) { logger.LogDebug(ex, "文件系统操作失败"); }

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
                logger.LogDebug(ex, "扫描 llama.cpp 已下载模型目录失败");
            }

            return list;
        }

        #endregion

        #region Model Lifecycle

        public async Task<ModelDetailsDto?> GetModelDetailsAsync(string modelName, CancellationToken ct = default)
        {
            try
            {
                var config = await localAiConfig.GetLocalAiConfigAsync();
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
                logger.LogDebug(ex, "获取 llama.cpp 模型 {ModelName} 详情失败", modelName);
            }
            return null;
        }

        public async Task<bool> LoadModelAsync(string modelName, CancellationToken ct = default)
        {
            var config = await localAiConfig.GetLocalAiConfigAsync();
            var llamaCpp = config.LlamaCpp;

            if (llamaCpp == null || !llamaCpp.Enabled)
            {
                logger.LogWarning("llama.cpp 未启用");
                return false;
            }

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
                logger.LogWarning("llama.cpp 模型文件未找到: {ModelName}", modelName);
                return false;
            }

            var currentModelFile = !string.IsNullOrWhiteSpace(llamaCpp.ModelPath)
                ? Path.GetFileNameWithoutExtension(llamaCpp.ModelPath)
                : null;
            if (currentModelFile?.Equals(modelName, StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    using var client = httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync($"{llamaCpp.BaseUrl.TrimEnd('/')}/v1/models", ct);
                    if (response.IsSuccessStatusCode) return true;
                }
                catch (Exception ex) { logger.LogDebug(ex, "检测服务运行状态失败"); }
            }

            await UnloadModelAsync(ct);

            llamaCpp.ModelPath = targetModelPath;
            await localAiConfig.SaveLocalAiConfigAsync(new SaveOpenClawLocalAiConfigRequest
            {
                LlamaCpp = llamaCpp
            });

            var status = await localAiConfig.DetectAndStartLocalAiAsync("llamacpp");
            return status.IsRunning;
        }

        public async Task<bool> UnloadModelAsync(CancellationToken ct = default)
        {
            try
            {
                var processes = Process.GetProcessesByName("llama-server");
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.Kill();
                        await proc.WaitForExitAsync(ct);
                    }
                    catch (Exception ex) { logger.LogDebug(ex, "操作失败"); }
                }

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
                catch (Exception ex) { logger.LogDebug(ex, "操作失败"); }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "卸载 llama.cpp 模型失败");
                return false;
            }
        }

        #endregion

        #region Service Control

        public async Task<LocalAiServiceStatusDto> StartAsync(CancellationToken ct = default)
        {
            return await localAiConfig.DetectAndStartLocalAiAsync("llamacpp");
        }

        public async Task<bool> StopAsync(CancellationToken ct = default)
        {
            return await UnloadModelAsync(ct);
        }

        #endregion

        #region Json Helpers



        #endregion
    }
}
