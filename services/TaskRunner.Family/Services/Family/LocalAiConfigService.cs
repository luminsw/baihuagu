using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskRunner.Contracts.OpenClaw;
using TaskRunner.Helpers;

namespace TaskRunner.Services;

public interface ILocalAiConfigService
{
    Task<OpenClawLocalAiConfigDto> GetLocalAiConfigAsync();
    Task<bool> SaveLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request);
    Task<List<OpenClawLocalModelDto>> ScanLocalModelsAsync(string provider);
    Task<LocalAiServiceStatusDto> DetectAndStartLocalAiAsync(string provider);
    Task<bool> SyncLocalModelsToOpenClawAsync(string provider);
}

public class LocalAiConfigService(
    IHttpClientFactory httpClientFactory,
    OpenClawConfigService openClawConfigService,
    ILogger<LocalAiConfigService> logger) : ILocalAiConfigService
{
    public Task<OpenClawLocalAiConfigDto> GetLocalAiConfigAsync()
        => openClawConfigService.GetLocalAiConfigAsync();

    public Task<bool> SaveLocalAiConfigAsync(SaveOpenClawLocalAiConfigRequest request)
        => openClawConfigService.SaveLocalAiConfigAsync(request);

    #region Scan Local Models

    public async Task<List<OpenClawLocalModelDto>> ScanLocalModelsAsync(string provider)
    {
        var config = await GetLocalAiConfigAsync();

        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanOllamaModelsAsync(config.Ollama);
        }
        if (provider.Equals("lmstudio", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanLmStudioModelsAsync(config.LmStudio);
        }
        if (provider.Equals("llamacpp", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanLlamaCppModelsAsync(config.LlamaCpp);
        }

        return new List<OpenClawLocalModelDto>();
    }

    private async Task<List<OpenClawLocalModelDto>> ScanOllamaModelsAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
            return new List<OpenClawLocalModelDto>();

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{config.BaseUrl.TrimEnd('/')}/api/tags");
            if (!response.IsSuccessStatusCode) return new List<OpenClawLocalModelDto>();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new List<OpenClawLocalModelDto>();
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in models.EnumerateArray())
                {
                    var id = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "扫描 Ollama 模型失败");
            return new List<OpenClawLocalModelDto>();
        }
    }

    private async Task<List<OpenClawLocalModelDto>> ScanLmStudioModelsAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
            return new List<OpenClawLocalModelDto>();

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{config.BaseUrl.TrimEnd('/')}/v1/models");
            if (!response.IsSuccessStatusCode) return new List<OpenClawLocalModelDto>();

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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "扫描 LM Studio 模型失败");
            return new List<OpenClawLocalModelDto>();
        }
    }

    private async Task<List<OpenClawLocalModelDto>> ScanLlamaCppModelsAsync(OpenClawLlamaCppConfigDto? config)
    {
        if (config == null || !config.Enabled)
            return new List<OpenClawLocalModelDto>();

        // 先检测服务是否运行
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{config.BaseUrl.TrimEnd('/')}/v1/models");
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
        catch (Exception ex) { logger.LogDebug(ex, "探测 llama.cpp 运行模型失败"); }

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

    #endregion

    #region Detect and Start Local AI

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
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync(checkUrl);
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
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = startCmd,
                Arguments = startArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                result.Message = $"启动 {displayName} 失败：无法创建进程";
                return result;
            }

            // 等待服务启动
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                try
                {
                    using var client = httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(3);
                    var response = await client.GetAsync(checkUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        result.IsRunning = true;
                        result.StartSuccess = true;
                        result.Message = $"{displayName} 启动成功";
                        return result;
                    }
                }
                catch (Exception ex) { logger.LogDebug(ex, "探测 {DisplayName} 启动状态失败", displayName); }
            }

            result.Message = $"{displayName} 已尝试启动，但服务未在预期时间内就绪，请检查日志";
            logger.LogWarning("{DisplayName} 启动后未就绪", displayName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动 {DisplayName} 失败", displayName);
            result.Message = $"启动 {displayName} 失败: {ex.Message}";
        }

        return result;
    }

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
            using var httpClient = httpClientFactory.CreateClient();
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

            logger.LogInformation("正在启动 llama.cpp: {Cmd}", shellCmd);
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
                    using var httpClient = httpClientFactory.CreateClient();
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
                catch (Exception ex) { logger.LogDebug(ex, "探测 llama.cpp 启动状态失败"); }
            }

            result.Message = "llama.cpp 已尝试启动，但服务未在预期时间内就绪，请检查日志";
            logger.LogWarning("llama.cpp 启动后未就绪");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动 llama.cpp 失败");
            result.Message = $"启动 llama.cpp 失败: {ex.Message}";
        }

        return result;
    }

    #endregion

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
            logger.LogWarning("Ollama 未配置或未启用，无法同步模型");
            return false;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync($"{config.BaseUrl.TrimEnd('/')}/api/tags");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Ollama /api/tags 请求失败: {StatusCode}", response.StatusCode);
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
            }.ToJsonString(JsonHelper.Compact);

            return await openClawConfigService.RunOpenClawConfigSetAsync("models.providers.ollama", providerJson);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "同步 Ollama 模型到 OpenClaw 失败");
            return false;
        }
    }

    private async Task<bool> SyncLmStudioModelsToOpenClawAsync(OpenClawLocalProviderConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            logger.LogWarning("LM Studio 未配置或未启用，无法同步模型");
            return false;
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var baseUrl = config.BaseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = baseUrl[..^3];
            }
            var response = await httpClient.GetAsync($"{baseUrl}/v1/models");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LM Studio /v1/models 请求失败: {StatusCode}", response.StatusCode);
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
            }.ToJsonString(JsonHelper.Compact);

            return await openClawConfigService.RunOpenClawConfigSetAsync("models.providers.lmstudio", providerJson);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "同步 LM Studio 模型到 OpenClaw 失败");
            return false;
        }
    }

    private async Task<bool> SyncLlamaCppModelsToOpenClawAsync(OpenClawLlamaCppConfigDto? config)
    {
        if (config == null || !config.Enabled || string.IsNullOrWhiteSpace(config.ModelPath))
        {
            logger.LogWarning("llama.cpp 未配置或未启用，无法同步模型");
            return false;
        }

        var providerJson = OpenClawConfigService.BuildLlamaCppProviderJson(config).ToJsonString(JsonHelper.Compact);
        return await openClawConfigService.RunOpenClawConfigSetAsync("models.providers.llamacpp", providerJson);
    }

    #endregion
}
