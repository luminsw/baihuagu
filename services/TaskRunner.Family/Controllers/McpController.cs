using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Contracts.Mcp;
using TaskRunner.Services;

namespace TaskRunner.Controllers;

/// <summary>
/// MCP (Model Context Protocol) Server 端点
/// 暴露标准 JSON-RPC 接口，供 Claude/Cursor/VS Code 等客户端连接
/// </summary>
[ApiController]
[Route("mcp")]
public class McpController : ControllerBase
{
    private readonly McpServerService _mcpService;
    private readonly ILogger<McpController> _logger;

    public McpController(McpServerService mcpService, ILogger<McpController> logger)
    {
        _mcpService = mcpService;
        _logger = logger;
    }

    /// <summary>
    /// MCP JSON-RPC 主端点（POST /mcp）
    /// 支持 initialize, tools/list, tools/call, notifications/initialized
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> HandleMcpRequest([FromBody] McpJsonRpcRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return BadRpcResponse(request.Id, -32600, "Missing method");
        }

        _logger.LogDebug("MCP 请求: method={Method}, id={Id}", request.Method, request.Id);

        try
        {
            switch (request.Method)
            {
                case "initialize":
                    return HandleInitialize(request);

                case "notifications/initialized":
                    // 初始化完成通知，无需响应
                    _logger.LogInformation("MCP 客户端初始化完成");
                    return NoContent();

                case "tools/list":
                    return HandleListTools(request);

                case "tools/call":
                    return await HandleCallToolAsync(request);

                case "prompts/list":
                    return HandleListPrompts(request);

                case "prompts/get":
                    return HandleGetPrompt(request);

                case "resources/list":
                    return HandleListResources(request);

                case "resources/read":
                    return HandleReadResource(request);

                default:
                    return BadRpcResponse(request.Id, -32601, $"Method not found: {request.Method}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP 请求处理失败: {Method}", request.Method);
            return BadRpcResponse(request.Id, -32603, $"Internal error: {ex.Message}");
        }
    }

    /// <summary>
    /// MCP SSE (Server-Sent Events) 端点
    /// 用于支持流式通信（当前仅返回 tools/list 作为 SSE 连接建立确认）
    /// </summary>
    [HttpGet]
    public IActionResult HandleMcpSse()
    {
        // 对于简单的 Streamable HTTP 模式，GET 请求可以返回端点信息
        // 完整的 SSE 实现需要保持连接，这里返回基本的信息
        return Ok(new
        {
            protocol = "mcp",
            version = "2024-11-05",
            endpoints = new
            {
                jsonrpc = "/mcp",
                health = "/health"
            },
            message = "TaskRunner MCP Server is running. Use POST /mcp with JSON-RPC 2.0 payloads."
        });
    }

    private IActionResult HandleInitialize(McpJsonRpcRequest request)
    {
        McpInitializeRequest initRequest;
        try
        {
            initRequest = request.Params.HasValue
                ? JsonSerializer.Deserialize<McpInitializeRequest>(request.Params.Value.GetRawText())
                  ?? new McpInitializeRequest()
                : new McpInitializeRequest();
        }
        catch
        {
            initRequest = new McpInitializeRequest();
        }

        var result = _mcpService.Initialize(initRequest);
        return Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = result
        });
    }

    private IActionResult HandleListTools(McpJsonRpcRequest request)
    {
        var result = _mcpService.ListTools();
        return Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = result
        });
    }

    private async Task<IActionResult> HandleCallToolAsync(McpJsonRpcRequest request)
    {
        McpToolCallRequest callRequest;
        try
        {
            callRequest = request.Params.HasValue
                ? JsonSerializer.Deserialize<McpToolCallRequest>(request.Params.Value.GetRawText())
                  ?? new McpToolCallRequest()
                : new McpToolCallRequest();
        }
        catch (Exception ex)
        {
            return BadRpcResponse(request.Id, -32602, $"Invalid params: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(callRequest.Name))
        {
            return BadRpcResponse(request.Id, -32602, "Missing tool name");
        }

        var result = await _mcpService.CallToolAsync(callRequest, HttpContext.RequestAborted);
        return Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = result
        });
    }

    private IActionResult HandleListPrompts(McpJsonRpcRequest request)
    {
        var result = _mcpService.ListPrompts();
        return Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = result
        });
    }

    private IActionResult HandleGetPrompt(McpJsonRpcRequest request)
    {
        McpPromptGetRequest promptRequest;
        try
        {
            promptRequest = request.Params.HasValue
                ? JsonSerializer.Deserialize<McpPromptGetRequest>(request.Params.Value.GetRawText())
                  ?? new McpPromptGetRequest()
                : new McpPromptGetRequest();
        }
        catch (Exception ex)
        {
            return BadRpcResponse(request.Id, -32602, $"Invalid params: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(promptRequest.Name))
        {
            return BadRpcResponse(request.Id, -32602, "Missing prompt name");
        }

        try
        {
            var result = _mcpService.GetPrompt(promptRequest);
            return Ok(new McpJsonRpcResponse
            {
                Id = request.Id,
                Result = result
            });
        }
        catch (Exception ex)
        {
            return BadRpcResponse(request.Id, -32602, ex.Message);
        }
    }

    private IActionResult HandleListResources(McpJsonRpcRequest request)
    {
        var result = _mcpService.ListResources();
        return Ok(new McpJsonRpcResponse
        {
            Id = request.Id,
            Result = result
        });
    }

    private IActionResult HandleReadResource(McpJsonRpcRequest request)
    {
        McpResourceReadRequest readRequest;
        try
        {
            readRequest = request.Params.HasValue
                ? JsonSerializer.Deserialize<McpResourceReadRequest>(request.Params.Value.GetRawText())
                  ?? new McpResourceReadRequest()
                : new McpResourceReadRequest();
        }
        catch (Exception ex)
        {
            return BadRpcResponse(request.Id, -32602, $"Invalid params: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(readRequest.Uri))
        {
            return BadRpcResponse(request.Id, -32602, "Missing resource URI");
        }

        try
        {
            var result = _mcpService.ReadResource(readRequest);
            return Ok(new McpJsonRpcResponse
            {
                Id = request.Id,
                Result = result
            });
        }
        catch (Exception ex)
        {
            return BadRpcResponse(request.Id, -32602, ex.Message);
        }
    }

    private static IActionResult BadRpcResponse(object? id, int code, string message)
    {
        return new ObjectResult(new McpJsonRpcResponse
        {
            Id = id,
            Error = new McpJsonRpcError
            {
                Code = code,
                Message = message
            }
        })
        {
            StatusCode = 200 // JSON-RPC 错误也用 200 返回，错误信息在 body 中
        };
    }
}
