using Microsoft.AspNetCore.Mvc;
using TaskRunner.Contracts.OpenClaw;
using TaskRunner.Services;

namespace TaskRunner.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OpenClawController : ControllerBase
{
    private readonly IOpenClawTaskService _taskService;
    private readonly ILocalAiConfigService _localAiConfig;
    private readonly IOpenClawModelProfileService _modelProfile;

    public OpenClawController(IOpenClawTaskService taskService, ILocalAiConfigService localAiConfig, IOpenClawModelProfileService modelProfile)
    {
        _taskService = taskService;
        _localAiConfig = localAiConfig;
        _modelProfile = modelProfile;
    }

    [HttpPost("tasks")]
    public async Task<ActionResult<OpenClawTaskDto>> CreateTask([FromBody] CreateOpenClawTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return BadRequest(new { error = "Prompt 不能为空" });
        }

        var task = await _taskService.CreateTaskAsync(request.Prompt.Trim());
        return Ok(task);
    }

    [HttpGet("tasks")]
    public async Task<ActionResult<List<OpenClawTaskDto>>> GetTasks([FromQuery] int limit = 100)
    {
        var tasks = await _taskService.GetTasksAsync(limit);
        return Ok(tasks);
    }

    [HttpGet("tasks/{id:int}")]
    public async Task<ActionResult<OpenClawTaskDto>> GetTask(int id)
    {
        var task = await _taskService.GetTaskAsync(id);
        if (task == null)
        {
            return NotFound(new { error = "任务不存在" });
        }
        return Ok(task);
    }

    [HttpGet("tasks/{id:int}/report")]
    public async Task<ActionResult<string>> GetReport(int id)
    {
        var content = await _taskService.GetReportContentAsync(id);
        if (content == null)
        {
            return NotFound(new { error = "报告不存在或尚未生成" });
        }
        return Ok(content);
    }

    [HttpDelete("tasks/{id:int}")]
    public async Task<IActionResult> DeleteTask(int id)
    {
        var result = await _taskService.DeleteTaskAsync(id);
        if (!result)
        {
            return NotFound(new { error = "任务不存在" });
        }
        return NoContent();
    }

    [HttpPost("tasks/{id:int}/cancel")]
    public async Task<IActionResult> CancelTask(int id)
    {
        var result = await _taskService.CancelTaskAsync(id);
        if (!result)
        {
            return BadRequest(new { error = "任务不存在或已结束" });
        }
        return Ok(new { success = true, message = "任务已取消" });
    }

    [HttpGet("local-ai-config")]
    public async Task<ActionResult<OpenClawLocalAiConfigDto>> GetLocalAiConfig()
    {
        var config = await _localAiConfig.GetLocalAiConfigAsync();
        return Ok(config);
    }

    [HttpPost("local-ai-config")]
    public async Task<IActionResult> SaveLocalAiConfig([FromBody] SaveOpenClawLocalAiConfigRequest request)
    {
        var success = await _localAiConfig.SaveLocalAiConfigAsync(request);
        if (!success)
        {
            return BadRequest(new { error = "保存配置失败" });
        }
        return Ok(new { success = true });
    }

    [HttpGet("local-ai-models")]
    public async Task<ActionResult<List<OpenClawLocalModelDto>>> ScanLocalModels([FromQuery] string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return BadRequest(new { error = "provider 参数不能为空" });
        }
        var models = await _localAiConfig.ScanLocalModelsAsync(provider);
        return Ok(models);
    }

    [HttpPost("local-ai-detect")]
    public async Task<ActionResult<LocalAiServiceStatusDto>> DetectAndStartLocalAi([FromBody] DetectLocalAiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            return BadRequest(new { error = "provider 不能为空" });
        }
        var result = await _localAiConfig.DetectAndStartLocalAiAsync(request.Provider);
        return Ok(result);
    }

    [HttpGet("default-model")]
    public async Task<ActionResult<OpenClawDefaultModelDto>> GetDefaultModel()
    {
        var result = await _modelProfile.GetDefaultModelAsync();
        return Ok(result);
    }

    [HttpPost("default-model")]
    public async Task<IActionResult> SetDefaultModel([FromBody] SetOpenClawDefaultModelRequest request)
    {
        var success = await _modelProfile.SetDefaultModelAsync(request.Model);
        if (!success)
        {
            return BadRequest(new { error = "设置默认模型失败" });
        }
        return Ok(new { success = true });
    }

    [HttpPost("sync-local-models")]
    public async Task<IActionResult> SyncLocalModels([FromBody] SyncLocalModelsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            return BadRequest(new { error = "provider 不能为空" });
        }
        var success = await _localAiConfig.SyncLocalModelsToOpenClawAsync(request.Provider);
        if (!success)
        {
            return BadRequest(new { error = $"同步 {request.Provider} 模型到 OpenClaw 失败" });
        }
        return Ok(new { success = true, message = $"{request.Provider} 模型已同步到 OpenClaw" });
    }

    [HttpGet("model-profiles")]
    public async Task<ActionResult<ModelProfileListDto>> GetModelProfiles()
    {
        var result = await _modelProfile.GetModelProfilesAsync();
        return Ok(result);
    }

    [HttpPost("model-profiles")]
    public async Task<IActionResult> SetModelProfile([FromBody] SetModelProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Profile))
        {
            return BadRequest(new { error = "profile 不能为空" });
        }
        var success = await _modelProfile.SetModelProfileAsync(request.Profile);
        if (!success)
        {
            return BadRequest(new { error = $"设置模型配置 {request.Profile} 失败" });
        }
        return Ok(new { success = true, message = $"已切换到 {request.Profile} 配置" });
    }
}
