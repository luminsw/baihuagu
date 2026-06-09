using TaskRunner.Core.Shared;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TaskRunner.Contracts.Mcp;
using TaskRunner.Contracts.OpenClaw;
using TaskRunner.Models;

namespace TaskRunner.Services;

/// <summary>
/// MCP (Model Context Protocol) Server 服务：暴露 JSON-RPC 工具接口
/// 使 Claude/Cursor/VS Code 等客户端可通过标准 MCP 协议调用 TaskRunner 功能
/// </summary>
public partial class McpServerService
{
    private readonly TaskManager _taskManager;
    private readonly IOpenClawTaskService _openClawTaskService;
    private readonly ILocalAiConfigService _localAiConfig;
    private readonly SystemHealthService _healthService;
    private readonly AiClientService _aiClientService;
    private readonly VaultSettingsService _vaultSettings;
    private readonly AiSettingsService _aiSettings;
    private readonly ILogger<McpServerService> _logger;

    // 工具注册表
    private readonly Dictionary<string, McpTool> _tools = new();
    private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<McpToolCallResult>>> _handlers = new();

    public McpServerService(
        TaskManager taskManager,
        IOpenClawTaskService openClawTaskService,
        ILocalAiConfigService localAiConfig,
        SystemHealthService healthService,
        AiClientService aiClientService,
        VaultSettingsService vaultSettings,
        AiSettingsService aiSettings,
        ILogger<McpServerService> logger)
    {
        _taskManager = taskManager;
        _openClawTaskService = openClawTaskService;
        _localAiConfig = localAiConfig;
        _healthService = healthService;
        _aiClientService = aiClientService;
        _vaultSettings = vaultSettings;
        _aiSettings = aiSettings;
        _logger = logger;

        RegisterTools();
    }

    #region Tool Registration

    private void RegisterTools()
    {
        // 1. query_ai - 同步 AI 查询
        _tools["query_ai"] = new McpTool
        {
            Name = "query_ai",
            Description = "直接调用 AI 模型进行查询并返回结果。支持指定模型，否则使用默认模型。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["query"] = new() { Type = "string", Description = "要查询的内容" },
                    ["model"] = new() { Type = "string", Description = "指定使用的 AI 模型，如 ollama/biancang:latest、Qwen/Qwen2.5-14B-Instruct。留空使用默认模型。" },
                    ["system_prompt"] = new() { Type = "string", Description = "可选的 system prompt，覆盖默认的经方家助手角色" },
                },
                Required = new List<string> { "query" }
            }
        };
        _handlers["query_ai"] = HandleQueryAiAsync;

        // 2. create_ai_query_task - 创建 AI 查询后台任务
        _tools["create_ai_query_task"] = new McpTool
        {
            Name = "create_ai_query_task",
            Description = "创建一个异步 AI 查询后台任务，返回 taskId 供后续轮询。适合需要长时间运行的查询。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["query"] = new() { Type = "string", Description = "要查询的内容" },
                    ["model"] = new() { Type = "string", Description = "指定使用的 AI 模型，留空使用默认模型" },
                    ["save_to_vault"] = new() { Type = "boolean", Description = "是否将结果保存到知识库", Default = false },
                    ["vault_id"] = new() { Type = "string", Description = "目标知识库 ID（save_to_vault 为 true 时必填）" },
                },
                Required = new List<string> { "query" }
            }
        };
        _handlers["create_ai_query_task"] = HandleCreateAiQueryTaskAsync;

        // 3. create_openclaw_task - 创建 OpenClaw 任务
        _tools["create_openclaw_task"] = new McpTool
        {
            Name = "create_openclaw_task",
            Description = "使用 OpenClaw 运行一个 AI 任务（通过 openclaw agent）。适合复杂的知识库分析任务。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["prompt"] = new() { Type = "string", Description = "OpenClaw 任务提示词" },
                },
                Required = new List<string> { "prompt" }
            }
        };
        _handlers["create_openclaw_task"] = HandleCreateOpenClawTaskAsync;

        // 4. get_task_status - 获取后台任务状态
        _tools["get_task_status"] = new McpTool
        {
            Name = "get_task_status",
            Description = "获取指定后台任务（ai_query、split_atom_notes、openclaw 等）的当前状态和结果。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["task_id"] = new() { Type = "string", Description = "任务 ID" },
                },
                Required = new List<string> { "task_id" }
            }
        };
        _handlers["get_task_status"] = HandleGetTaskStatusAsync;

        // 5. list_tasks - 列出后台任务
        _tools["list_tasks"] = new McpTool
        {
            Name = "list_tasks",
            Description = "列出最近创建的后台任务，包括 AI 查询、笔记拆分、OpenClaw 任务等。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["limit"] = new() { Type = "integer", Description = "返回数量上限", Default = 20 },
                    ["status"] = new() { Type = "string", Description = "按状态过滤：Pending、Running、Success、Failed、Timeout" },
                },
                Required = null
            }
        };
        _handlers["list_tasks"] = HandleListTasksAsync;

        // 6. get_system_health - 系统健康检查
        _tools["get_system_health"] = new McpTool
        {
            Name = "get_system_health",
            Description = "获取 TaskRunner 系统健康状态报告，包括 Git、Obsidian、Ollama、Python、Node.js、API Key、知识库等组件的状态。",
            InputSchema = new McpJsonSchema
            {
                Properties = new(),
                Required = null
            }
        };
        _handlers["get_system_health"] = HandleGetSystemHealthAsync;

        // 7. list_local_ai_models - 列出本地 AI 模型
        _tools["list_local_ai_models"] = new McpTool
        {
            Name = "list_local_ai_models",
            Description = "扫描并列出本地 AI 服务（Ollama、LM Studio、llama.cpp）上的可用模型。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["provider"] = new() { Type = "string", Description = "指定 provider：ollama、lmstudio、llamacpp。留空扫描所有已配置的 provider。" },
                },
                Required = null
            }
        };
        _handlers["list_local_ai_models"] = HandleListLocalAiModelsAsync;

        // 8. list_openclaw_tasks - 列出 OpenClaw 任务
        _tools["list_openclaw_tasks"] = new McpTool
        {
            Name = "list_openclaw_tasks",
            Description = "列出 OpenClaw 任务历史。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["limit"] = new() { Type = "integer", Description = "返回数量上限", Default = 20 },
                },
                Required = null
            }
        };
        _handlers["list_openclaw_tasks"] = HandleListOpenClawTasksAsync;

        // 9. get_openclaw_task_report - 获取 OpenClaw 任务报告
        _tools["get_openclaw_task_report"] = new McpTool
        {
            Name = "get_openclaw_task_report",
            Description = "获取指定 OpenClaw 任务的完整报告内容。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["task_id"] = new() { Type = "integer", Description = "OpenClaw 任务的数据库 ID（不是 TaskId 字符串）" },
                },
                Required = new List<string> { "task_id" }
            }
        };
        _handlers["get_openclaw_task_report"] = HandleGetOpenClawTaskReportAsync;

        // 10. list_vaults
        _tools["list_vaults"] = new McpTool
        {
            Name = "list_vaults",
            Description = "列出所有已配置的知识库（Vault）。",
            InputSchema = new McpJsonSchema
            {
                Properties = new(),
                Required = null
            }
        };
        _handlers["list_vaults"] = HandleListVaultsAsync;

        // 11. read_vault_note
        _tools["read_vault_note"] = new McpTool
        {
            Name = "read_vault_note",
            Description = "读取知识库中的指定笔记内容。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["vault_id"] = new() { Type = "string", Description = "知识库 ID" },
                    ["path"] = new() { Type = "string", Description = "笔记路径（不含 .md 后缀），如 症状/头痛" },
                },
                Required = new List<string> { "vault_id", "path" }
            }
        };
        _handlers["read_vault_note"] = HandleReadVaultNoteAsync;

        // 12. search_vault
        _tools["search_vault"] = new McpTool
        {
            Name = "search_vault",
            Description = "在知识库中搜索关键词，返回匹配的笔记列表。",
            InputSchema = new McpJsonSchema
            {
                Properties = new Dictionary<string, McpJsonSchemaProperty>
                {
                    ["vault_id"] = new() { Type = "string", Description = "知识库 ID" },
                    ["query"] = new() { Type = "string", Description = "搜索关键词" },
                    ["limit"] = new() { Type = "integer", Description = "返回数量上限", Default = 20 },
                },
                Required = new List<string> { "vault_id", "query" }
            }
        };
        _handlers["search_vault"] = HandleSearchVaultAsync;
    }

    #endregion

    #region Public API

    public McpInitializeResult Initialize(McpInitializeRequest request)
    {
        _logger.LogInformation("MCP 客户端连接: {ClientName} v{Version}, 协议版本: {Protocol}",
            request.ClientInfo.Name, request.ClientInfo.Version, request.ProtocolVersion);

        return new McpInitializeResult
        {
            ProtocolVersion = request.ProtocolVersion,
            Capabilities = new McpServerCapabilities
            {
                Tools = new McpToolsCapability { ListChanged = false },
                Prompts = new McpPromptsCapability { ListChanged = false },
                Resources = new McpResourcesCapability { Subscribe = false, ListChanged = false }
            },
            ServerInfo = new McpImplementationInfo
            {
                Name = "taskrunner-mcp",
                Version = "1.1.0"
            }
        };
    }

    public McpToolListResult ListTools()
    {
        return new McpToolListResult
        {
            Tools = _tools.Values.ToList()
        };
    }

    public async Task<McpToolCallResult> CallToolAsync(McpToolCallRequest request, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(request.Name, out var handler))
        {
            return ErrorResult($"未知工具: {request.Name}");
        }

        try
        {
            return await handler(request.Arguments, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP 工具调用失败: {ToolName}", request.Name);
            return ErrorResult($"工具调用失败: {ex.Message}");
        }
    }

    public McpPromptListResult ListPrompts()
    {
        return new McpPromptListResult
        {
            Prompts = new List<McpPrompt>
            {
                new()
                {
                    Name = "diagnose_symptoms",
                    Description = "症状诊断：根据症状描述进行辨证分析",
                    Arguments = new List<McpPromptArgument>
                    {
                        new() { Name = "symptoms", Description = "患者症状描述", Required = true },
                        new() { Name = "duration", Description = "症状持续时间", Required = false },
                    }
                },
                new()
                {
                    Name = "analyze_formula",
                    Description = "方剂分析：分析经方的组成、功效、适应症和方解",
                    Arguments = new List<McpPromptArgument>
                    {
                        new() { Name = "formula", Description = "方剂名称，如桂枝汤、麻黄汤", Required = true },
                    }
                },
                new()
                {
                    Name = "study_classic",
                    Description = "经典学习：解读《伤寒论》《金匮要略》条文",
                    Arguments = new List<McpPromptArgument>
                    {
                        new() { Name = "text", Description = "经典条文内容", Required = true },
                        new() { Name = "source", Description = "出处，如伤寒论、金匮要略", Required = false },
                    }
                },
                new()
                {
                    Name = "case_analysis",
                    Description = "医案分析：分析医案的辨证思路、处方用药和疗效",
                    Arguments = new List<McpPromptArgument>
                    {
                        new() { Name = "case_text", Description = "医案内容", Required = true },
                    }
                }
            }
        };
    }

    public McpPromptGetResult GetPrompt(McpPromptGetRequest request)
    {
        var symptoms = request.Arguments?.GetValueOrDefault("symptoms") ?? request.Arguments?.GetValueOrDefault("case_text") ?? "";
        var formula = request.Arguments?.GetValueOrDefault("formula") ?? "";
        var text = request.Arguments?.GetValueOrDefault("text") ?? "";
        var source = request.Arguments?.GetValueOrDefault("source") ?? "";
        var duration = request.Arguments?.GetValueOrDefault("duration") ?? "";

        return request.Name switch
        {
            "diagnose_symptoms" => new McpPromptGetResult
            {
                Description = "症状诊断",
                Messages = new List<McpPromptMessage>
                {
                    new()
                    {
                        Role = "system",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = "你是一位知识库专家。请根据患者症状进行六经辨证，分析病机、给出治法建议，并推荐可能的经方。分析时请引用原文依据。"
                        }
                    },
                    new()
                    {
                        Role = "user",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = string.IsNullOrWhiteSpace(duration)
                                ? $"患者症状：{symptoms}"
                                : $"患者症状：{symptoms}\n持续时间：{duration}"
                        }
                    }
                }
            },
            "analyze_formula" => new McpPromptGetResult
            {
                Description = "方剂分析",
                Messages = new List<McpPromptMessage>
                {
                    new()
                    {
                        Role = "system",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = "你是一位知识库专家。请详细分析以下方剂的组成、配伍意义、功效主治、适用证候，并说明其在《伤寒论》或《金匮要略》中的出处和方解。"
                        }
                    },
                    new()
                    {
                        Role = "user",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = $"请分析方剂：{formula}"
                        }
                    }
                }
            },
            "study_classic" => new McpPromptGetResult
            {
                Description = "经典学习",
                Messages = new List<McpPromptMessage>
                {
                    new()
                    {
                        Role = "system",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = "你是一位经典研究专家。请解读以下经典条文，包括字词解释、病机分析、临床意义，以及相关的方证对应关系。"
                        }
                    },
                    new()
                    {
                        Role = "user",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = string.IsNullOrWhiteSpace(source)
                                ? $"请解读以下条文：\n{text}"
                                : $"请解读《{source}》条文：\n{text}"
                        }
                    }
                }
            },
            "case_analysis" => new McpPromptGetResult
            {
                Description = "医案分析",
                Messages = new List<McpPromptMessage>
                {
                    new()
                    {
                        Role = "system",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = "你是一位经验丰富的临床医师。请分析以下医案，梳理辨证思路、处方用药依据，并评价疗效。如有可改进之处也请指出。"
                        }
                    },
                    new()
                    {
                        Role = "user",
                        Content = new McpPromptMessageContent
                        {
                            Type = "text",
                            Text = $"请分析以下医案：\n{symptoms}"
                        }
                    }
                }
            },
            _ => throw new Exception($"未知提示词: {request.Name}")
        };
    }

    public McpResourceListResult ListResources()
    {
        var resources = new List<McpResource>();
        try
        {
            var vaults = _vaultSettings.GetVaults();
            foreach (var vault in vaults)
            {
                if (string.IsNullOrWhiteSpace(vault.Path) || !Directory.Exists(vault.Path))
                    continue;

                var notesPath = Path.Combine(vault.Path, "notes");
                if (!Directory.Exists(notesPath))
                    continue;

                var files = Directory.GetFiles(notesPath, "*.md", SearchOption.AllDirectories);
                foreach (var file in files.Take(50)) // 限制数量
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var relPath = file.Substring(notesPath.Length).Replace('\\', '/').TrimStart('/');
                    var uri = $"vault://{vault.Id}/{relPath}";
                    resources.Add(new McpResource
                    {
                        Uri = uri,
                        Name = fileName,
                        Description = $"知识库 '{vault.Name}' 中的笔记",
                        MimeType = "text/markdown"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "扫描知识库资源失败");
        }

        return new McpResourceListResult { Resources = resources };
    }

    public McpResourceReadResult ReadResource(McpResourceReadRequest request)
    {
        var uri = request.Uri;
        if (!uri.StartsWith("vault://"))
            throw new Exception($"不支持的资源 URI: {uri}");

        var parts = uri[8..].Split('/', 2);
        if (parts.Length < 2)
            throw new Exception($"无效的 URI: {uri}");

        var vaultId = parts[0];
        var path = parts[1];

        var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        if (vault == null || string.IsNullOrWhiteSpace(vault.Path))
            throw new Exception($"知识库不存在: {vaultId}");

        var notesPath = Path.Combine(vault.Path, "notes");
        var filePath = Path.Combine(notesPath, path);

        // 安全检查
        var fullPath = Path.GetFullPath(filePath);
        var safeBase = Path.GetFullPath(notesPath);
        if (!fullPath.StartsWith(safeBase))
            throw new Exception("非法路径");

        if (!File.Exists(filePath))
            throw new Exception($"资源不存在: {path}");

        var content = File.ReadAllText(filePath);
        return new McpResourceReadResult
        {
            Contents = new List<McpResourceContent>
            {
                new()
                {
                    Uri = uri,
                    MimeType = "text/markdown",
                    Text = content
                }
            }
        };
    }

    #endregion


    #region Helpers

    private static McpToolCallResult TextResult(string text)
    {
        return new McpToolCallResult
        {
            Content = new List<McpToolCallContent>
            {
                new() { Type = "text", Text = text }
            }
        };
    }

    private static McpToolCallResult ErrorResult(string message)
    {
        return new McpToolCallResult
        {
            IsError = true,
            Content = new List<McpToolCallContent>
            {
                new() { Type = "text", Text = $"❌ {message}" }
            }
        };
    }

    private static string? GetString(JsonElement? args, string key)
    {
        if (args == null || args.Value.ValueKind != JsonValueKind.Object) return null;
        if (args.Value.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int GetInt(JsonElement? args, string key, int defaultValue)
    {
        if (args == null || args.Value.ValueKind != JsonValueKind.Object) return defaultValue;
        if (args.Value.TryGetProperty(key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
                return val;
            // 尝试从字符串解析
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }
        return defaultValue;
    }

    private static bool GetBool(JsonElement? args, string key, bool defaultValue)
    {
        if (args == null || args.Value.ValueKind != JsonValueKind.Object) return defaultValue;
        if (args.Value.TryGetProperty(key, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
                return parsed;
        }
        return defaultValue;
    }

    #endregion
}
