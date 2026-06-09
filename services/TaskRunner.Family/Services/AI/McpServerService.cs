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
public class McpServerService
{
    private readonly TaskManager _taskManager;
    private readonly IOpenClawTaskService _openClawTaskService;
    private readonly SystemHealthService _healthService;
    private readonly AiClientService _aiClientService;
    private readonly SettingsService _settingsService;
    private readonly ILogger<McpServerService> _logger;

    // 工具注册表
    private readonly Dictionary<string, McpTool> _tools = new();
    private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<McpToolCallResult>>> _handlers = new();

    public McpServerService(
        TaskManager taskManager,
        IOpenClawTaskService openClawTaskService,
        SystemHealthService healthService,
        AiClientService aiClientService,
        SettingsService settingsService,
        ILogger<McpServerService> logger)
    {
        _taskManager = taskManager;
        _openClawTaskService = openClawTaskService;
        _healthService = healthService;
        _aiClientService = aiClientService;
        _settingsService = settingsService;
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
            var vaults = _settingsService.GetVaults();
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

        var vault = _settingsService.GetVaults().FirstOrDefault(v => v.Id == vaultId);
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

    #region Tool Handlers

    private async Task<McpToolCallResult> HandleQueryAiAsync(JsonElement? args, CancellationToken ct)
    {
        if (args == null) return ErrorResult("缺少参数");
        var query = GetString(args.Value, "query");
        if (string.IsNullOrWhiteSpace(query)) return ErrorResult("query 不能为空");

        var model = GetString(args.Value, "model");
        var systemPrompt = GetString(args.Value, "system_prompt");

        try
        {
            // 确定模型和 provider
            var providers = _settingsService.GetAiProviders();
            AiProviderConfig? provider = null;
            string modelName = "";

            // 处理 provider/model 格式（如 ollama/biancang:latest）
            if (!string.IsNullOrWhiteSpace(model) && model.Contains('/'))
            {
                var parts = model.Split('/', 2);
                var providerHint = parts[0].ToLowerInvariant();
                modelName = parts[1];

                // 按 provider id 匹配
                provider = providers.FirstOrDefault(p =>
                    p.Id.Equals(providerHint, StringComparison.OrdinalIgnoreCase));

                // 按 URL 特征匹配
                if (provider is null)
                {
                    provider = providers.FirstOrDefault(p =>
                        p.AiBaseUrl.Contains(providerHint, StringComparison.OrdinalIgnoreCase));
                }

                // 为 ollama 创建临时 provider
                if (provider is null && providerHint == "ollama")
                {
                    provider = new AiProviderConfig
                    {
                        Id = "ollama",
                        Name = "Ollama",
                        AiBaseUrl = "http://localhost:11434",
                        Models = new List<AiModelConfig>
                        {
                            new() { Name = modelName, IsMain = true }
                        }
                    };
                }
            }

            if (provider is null)
            {
                // 尝试根据模型名匹配 provider
                provider = providers.FirstOrDefault(p =>
                    p.Models.Any(m => m.Name.Equals(model ?? "", StringComparison.OrdinalIgnoreCase)));
            }

            if (provider is null)
            {
                provider = providers.FirstOrDefault(p => p.IsMain) ?? providers.FirstOrDefault()
                    ?? throw new Exception("未找到可用的 AI 提供商");
            }

            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = !string.IsNullOrWhiteSpace(model)
                    ? model.Trim()
                    : provider.GetMainModel()
                      ?? "Qwen/Qwen2.5-14B-Instruct";
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, string.IsNullOrWhiteSpace(systemPrompt)
                    ? "你是一位知识库专家助手。"
                    : systemPrompt),
                new(ChatRole.User, query)
            };

            var options = AiClientService.BuildChatOptions();
            var response = await _aiClientService.GetChatResponseWithAutoStartAsync(
                provider, modelName, messages, options, ct);

            var content = response.Text ?? "（AI 返回空内容）";
            return TextResult(content);
        }
        catch (Exception ex)
        {
            return ErrorResult($"AI 查询失败: {ex.Message}");
        }
    }

    private Task<McpToolCallResult> HandleCreateAiQueryTaskAsync(JsonElement? args, CancellationToken ct)
    {
        if (args == null) return Task.FromResult(ErrorResult("缺少参数"));
        var query = GetString(args.Value, "query");
        if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(ErrorResult("query 不能为空"));

        var model = GetString(args.Value, "model");
        var saveToVault = GetBool(args.Value, "save_to_vault", false);
        var vaultId = GetString(args.Value, "vault_id");

        var taskId = _taskManager.CreateTask("ai_query", new Dictionary<string, string>
        {
            ["query"] = query,
            ["model"] = model ?? "",
            ["saveToVault"] = saveToVault.ToString(),
            ["vaultId"] = vaultId ?? ""
        });

        // Note: 实际执行需要 TasksController 中相同的逻辑。MCP 只负责创建任务。
        // 如果需要立即执行，这里需要注入更多依赖并复制 TasksController 的执行逻辑。
        // 为简化，MCP 仅创建任务，用户用 get_task_status 查询。
        return Task.FromResult(TextResult($"AI 查询任务已创建，taskId: {taskId}\n注意：任务已创建但未自动执行。请通过 WebUI 触发执行，或联系开发者支持 MCP 自动执行。"));
    }

    private async Task<McpToolCallResult> HandleCreateOpenClawTaskAsync(JsonElement? args, CancellationToken ct)
    {
        if (args == null) return ErrorResult("缺少参数");
        var prompt = GetString(args.Value, "prompt");
        if (string.IsNullOrWhiteSpace(prompt)) return ErrorResult("prompt 不能为空");

        try
        {
            var task = await _openClawTaskService.CreateTaskAsync(prompt.Trim());
            return TextResult($"OpenClaw 任务已创建\nTask ID: {task.TaskId}\n数据库 ID: {task.Id}\nStatus: {task.Status}\n创建时间: {task.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            return ErrorResult($"创建 OpenClaw 任务失败: {ex.Message}");
        }
    }

    private Task<McpToolCallResult> HandleGetTaskStatusAsync(JsonElement? args, CancellationToken ct)
    {
        if (args == null) return Task.FromResult(ErrorResult("缺少参数"));
        var taskId = GetString(args.Value, "task_id");
        if (string.IsNullOrWhiteSpace(taskId)) return Task.FromResult(ErrorResult("task_id 不能为空"));

        var task = _taskManager.GetTask(taskId);
        if (task == null) return Task.FromResult(ErrorResult($"任务不存在: {taskId}"));

        var result = new JsonObject
        {
            ["taskId"] = task.Id,
            ["type"] = task.Type,
            ["status"] = task.Status.ToString(),
            ["progress"] = new JsonObject
            {
                ["current"] = task.Progress.Current,
                ["total"] = task.Progress.Total,
                ["percentage"] = task.Progress.Percentage,
                ["message"] = task.Progress.Message
            },
            ["createdAt"] = task.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            ["updatedAt"] = task.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        if (task.Result != null)
        {
            result["result"] = new JsonObject
            {
                ["success"] = task.Result.Success,
                ["error"] = task.Result.Error,
            };
            if (task.Result.Data != null)
            {
                try
                {
                    var resultObj = result["result"] as JsonObject;
                    if (resultObj != null)
                        resultObj["data"] = JsonSerializer.Serialize(task.Result.Data);
                }
                catch { }
            }
        }

        return Task.FromResult(TextResult(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    private Task<McpToolCallResult> HandleListTasksAsync(JsonElement? args, CancellationToken ct)
    {
        var limit = GetInt(args, "limit", 20);
        var status = GetString(args, "status");

        List<TaskRunner.Core.Shared.TaskInfo> tasks;
        if (!string.IsNullOrWhiteSpace(status))
        {
            tasks = _taskManager.GetTasksByStatus(status, limit);
        }
        else
        {
            tasks = _taskManager.GetAllTasks(limit, 0);
        }

        var array = new JsonArray();
        foreach (var t in tasks)
        {
            array.Add(new JsonObject
            {
                ["taskId"] = t.Id,
                ["type"] = t.Type,
                ["status"] = t.Status.ToString(),
                ["progressMessage"] = t.Progress.Message,
                ["progressPercent"] = t.Progress.Percentage,
                ["createdAt"] = t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }

        var result = new JsonObject { ["tasks"] = array, ["count"] = array.Count };
        return Task.FromResult(TextResult(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    private async Task<McpToolCallResult> HandleGetSystemHealthAsync(JsonElement? args, CancellationToken ct)
    {
        try
        {
            var report = await _healthService.GetHealthReportAsync(ct);
            var result = new JsonObject
            {
                ["status"] = report.Status,
                ["healthScore"] = report.HealthScore,
                ["totalWallClockMs"] = report.TotalWallClockMs,
                ["timestamp"] = report.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                ["components"] = new JsonArray(report.Components.Select(c => new JsonObject
                {
                    ["name"] = c.Name,
                    ["status"] = c.Status,
                    ["message"] = c.Message,
                    ["version"] = c.Version,
                    ["checkDurationMs"] = c.CheckDurationMs
                }).ToArray())
            };
            return TextResult(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            return ErrorResult($"健康检查失败: {ex.Message}");
        }
    }

    private async Task<McpToolCallResult> HandleListLocalAiModelsAsync(JsonElement? args, CancellationToken ct)
    {
        var provider = GetString(args, "provider");
        var allModels = new List<OpenClawLocalModelDto>();

        var providers = string.IsNullOrWhiteSpace(provider)
            ? new[] { "ollama", "lmstudio", "llamacpp" }
            : new[] { provider.ToLowerInvariant() };

        foreach (var p in providers)
        {
            try
            {
                var models = await _openClawTaskService.ScanLocalModelsAsync(p);
                foreach (var m in models)
                {
                    m.Id = $"{p}/{m.Id}";
                }
                allModels.AddRange(models);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "扫描 {Provider} 模型失败", p);
            }
        }

        var array = new JsonArray(allModels.Select(m => new JsonObject
        {
            ["id"] = m.Id,
            ["name"] = m.Name,
            ["apiType"] = m.ApiType,
            ["contextWindow"] = m.ContextWindow,
            ["maxTokens"] = m.MaxTokens,
        }).ToArray());

        var result = new JsonObject { ["models"] = array, ["count"] = array.Count };
        return TextResult(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<McpToolCallResult> HandleListOpenClawTasksAsync(JsonElement? args, CancellationToken ct)
    {
        var limit = GetInt(args, "limit", 20);
        try
        {
            var tasks = await _openClawTaskService.GetTasksAsync(limit);
            var array = new JsonArray(tasks.Select(t => new JsonObject
            {
                ["id"] = t.Id,
                ["taskId"] = t.TaskId,
                ["prompt"] = t.Prompt.Length > 100 ? t.Prompt[..100] + "..." : t.Prompt,
                ["status"] = t.Status,
                ["createdAt"] = t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                ["completedAt"] = t.CompletedAt?.ToString("yyyy-MM-dd HH:mm:ss")
            }).ToArray());

            var result = new JsonObject { ["tasks"] = array, ["count"] = array.Count };
            return TextResult(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            return ErrorResult($"获取 OpenClaw 任务列表失败: {ex.Message}");
        }
    }

    private async Task<McpToolCallResult> HandleGetOpenClawTaskReportAsync(JsonElement? args, CancellationToken ct)
    {
        if (args == null) return ErrorResult("缺少参数");
        var taskId = GetInt(args.Value, "task_id", 0);
        if (taskId <= 0) return ErrorResult("task_id 必须是正整数");

        try
        {
            var report = await _openClawTaskService.GetReportContentAsync(taskId);
            if (report == null) return ErrorResult("报告不存在或尚未生成");
            return TextResult(report);
        }
        catch (Exception ex)
        {
            return ErrorResult($"获取报告失败: {ex.Message}");
        }
    }

    private Task<McpToolCallResult> HandleListVaultsAsync(JsonElement? args, CancellationToken ct)
    {
        var vaults = _settingsService.GetVaults();
        var array = new JsonArray(vaults.Select(v => new JsonObject
        {
            ["id"] = v.Id,
            ["name"] = v.Name,
            ["path"] = v.Path,
        }).ToArray());

        var result = new JsonObject { ["vaults"] = array, ["count"] = array.Count };
        return Task.FromResult(TextResult(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    private Task<McpToolCallResult> HandleReadVaultNoteAsync(JsonElement? args, CancellationToken ct)
    {
        if (args == null) return Task.FromResult(ErrorResult("缺少参数"));
        var vaultId = GetString(args.Value, "vault_id");
        var path = GetString(args.Value, "path");
        if (string.IsNullOrWhiteSpace(vaultId)) return Task.FromResult(ErrorResult("vault_id 不能为空"));
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult(ErrorResult("path 不能为空"));

        var vault = _settingsService.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        if (vault == null) return Task.FromResult(ErrorResult($"知识库不存在: {vaultId}"));
        if (string.IsNullOrWhiteSpace(vault.Path) || !Directory.Exists(vault.Path))
            return Task.FromResult(ErrorResult($"知识库路径无效: {vault.Path}"));

        try
        {
            var cleanPath = path.TrimEnd('/', '\\');
            if (cleanPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                cleanPath = cleanPath[..^3];
            if (cleanPath.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
                cleanPath = cleanPath.Substring("notes/".Length);

            var notesPath = Path.Combine(vault.Path, "notes");
            var filePath = Path.Combine(notesPath, cleanPath + ".md");

            if (!File.Exists(filePath))
                return Task.FromResult(ErrorResult($"笔记不存在: {path}"));

            var content = File.ReadAllText(filePath);
            return Task.FromResult(TextResult(content));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"读取笔记失败: {ex.Message}"));
        }
    }

    private Task<McpToolCallResult> HandleSearchVaultAsync(JsonElement? args, CancellationToken ct)
    {
        if (args == null) return Task.FromResult(ErrorResult("缺少参数"));
        var vaultId = GetString(args.Value, "vault_id");
        var query = GetString(args.Value, "query");
        var limit = GetInt(args, "limit", 20);
        if (string.IsNullOrWhiteSpace(vaultId)) return Task.FromResult(ErrorResult("vault_id 不能为空"));
        if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(ErrorResult("query 不能为空"));

        var vault = _settingsService.GetVaults().FirstOrDefault(v => v.Id == vaultId);
        if (vault == null) return Task.FromResult(ErrorResult($"知识库不存在: {vaultId}"));
        if (string.IsNullOrWhiteSpace(vault.Path) || !Directory.Exists(vault.Path))
            return Task.FromResult(ErrorResult($"知识库路径无效: {vault.Path}"));

        try
        {
            var queryLower = query.ToLowerInvariant();
            var files = Directory.GetFiles(vault.Path, "*.md", SearchOption.AllDirectories);
            var results = new List<JsonObject>();

            foreach (var file in files)
            {
                try
                {
                    var fileName = Path.GetFileName(file);
                    if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;

                    var content = File.ReadAllText(file);
                    var title = Path.GetFileNameWithoutExtension(file);
                    var relPath = file.Substring(vault.Path.Length).Replace('\\', '/').TrimStart('/');
                    if (relPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        relPath = relPath[..^3];
                    if (relPath.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
                        relPath = relPath.Substring("notes/".Length);

                    var titleMatch = title.ToLowerInvariant().Contains(queryLower);
                    var contentMatch = content.ToLowerInvariant().Contains(queryLower);

                    if (titleMatch || contentMatch)
                    {
                        var preview = "";
                        var index = content.ToLowerInvariant().IndexOf(queryLower);
                        if (index >= 0)
                        {
                            var start = Math.Max(0, index - 50);
                            var length = Math.Min(200, content.Length - start);
                            preview = content.Substring(start, length).Replace("\n", " ").Replace("#", "").Replace("*", "");
                            if (start > 0) preview = "..." + preview;
                            if (start + length < content.Length) preview = preview + "...";
                        }

                        results.Add(new JsonObject
                        {
                            ["title"] = title,
                            ["path"] = relPath,
                            ["preview"] = preview.Trim(),
                            ["score"] = titleMatch ? 10 : 5
                        });
                    }
                }
                catch { /* 忽略单个文件读取错误 */ }
            }

            results = results.OrderByDescending(r => (int)r["score"]).Take(limit).ToList();
            var array = new JsonArray(results.ToArray());
            var result = new JsonObject { ["results"] = array, ["count"] = array.Count, ["query"] = query };
            return Task.FromResult(TextResult(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"搜索失败: {ex.Message}"));
        }
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
