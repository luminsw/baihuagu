using Microsoft.AspNetCore.Mvc;
using TaskRunner.Contracts.LocalModels;
using TaskRunner.Contracts.OpenClaw;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// 本地模型部署 API：硬件检测、模型推荐、部署管理
    /// </summary>
    [ApiController]
    [Route("api/local-models")]
    public class LocalModelDeploymentController : ControllerBase
    {
        private readonly HardwareInfoService _hardwareInfoService;
        private readonly ModelRecommendationEngine _recommendationEngine;
        private readonly LocalModelDeploymentService _deploymentService;
        private readonly LocalModelSettingsService _localModelSettings;
        private readonly OllamaLibraryClient? _ollamaLibrary;
        private readonly ILogger<LocalModelDeploymentController> _logger;

        public LocalModelDeploymentController(
            HardwareInfoService hardwareInfoService,
            ModelRecommendationEngine recommendationEngine,
            LocalModelDeploymentService deploymentService,
            LocalModelSettingsService localModelSettings,
            OllamaLibraryClient? ollamaLibrary,
            ILogger<LocalModelDeploymentController> logger)
        {
            _hardwareInfoService = hardwareInfoService;
            _recommendationEngine = recommendationEngine;
            _deploymentService = deploymentService;
            _localModelSettings = localModelSettings;
            _ollamaLibrary = ollamaLibrary;
            _logger = logger;
        }

        /// <summary>
        /// 手动刷新 Ollama Library 模型列表
        /// </summary>
        [HttpPost("refresh-library")]
        public async Task<ActionResult> RefreshLibrary()
        {
            if (_ollamaLibrary == null)
                return BadRequest(new { error = "Ollama Library 客户端未启用" });

            try
            {
                await _ollamaLibrary.RefreshAsync(HttpContext.RequestAborted);
                return Ok(new { success = true, message = "模型库已刷新" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新 Ollama Library 失败");
                return StatusCode(500, new { error = "刷新失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取当前硬件信息
        /// </summary>
        [HttpGet("hardware")]
        public ActionResult<HardwareInfoDto> GetHardware([FromQuery] bool forceRefresh = false)
        {
            try
            {
                var info = forceRefresh
                    ? _hardwareInfoService.RefreshHardwareInfo()
                    : _hardwareInfoService.GetHardwareInfo();
                return Ok(info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取硬件信息失败");
                return StatusCode(500, new { error = "获取硬件信息失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取基于当前硬件的推荐模型（包含已下载状态）
        /// </summary>
        [HttpGet("recommend")]
        public async Task<ActionResult<List<RecommendedModelDto>>> GetRecommendations([FromQuery] string? scenario = null, [FromQuery] bool forceRefresh = false)
        {
            try
            {
                var hardware = _hardwareInfoService.GetHardwareInfo();
                var recommendations = forceRefresh
                    ? _recommendationEngine.RefreshRecommendations(hardware, scenario)
                    : _recommendationEngine.GetRecommendations(hardware, scenario);

                // 查询已下载模型列表，填充下载状态
                var ollamaModels = await _deploymentService.GetAvailableModelsAsync("ollama");
                var lmStudioModels = await _deploymentService.GetAvailableModelsAsync("lmstudio");
                var llamaCppModels = await _deploymentService.GetAvailableModelsAsync("llamacpp");

                foreach (var r in recommendations)
                {
                    r.IsDownloadedOllama = ollamaModels.Any(m =>
                        m.Equals(r.OllamaModelName, StringComparison.OrdinalIgnoreCase) ||
                        m.Split(':')[0].Equals(r.OllamaModelName.Split(':')[0], StringComparison.OrdinalIgnoreCase));

                    r.IsDownloadedLmStudio = !string.IsNullOrEmpty(r.LmStudioSearchName) && lmStudioModels.Any(m =>
                        m.Contains(r.LmStudioSearchName, StringComparison.OrdinalIgnoreCase) ||
                        r.LmStudioSearchName.Contains(m, StringComparison.OrdinalIgnoreCase));

                    r.IsDownloadedLlamaCpp = llamaCppModels.Any(m =>
                        !string.IsNullOrEmpty(r.LmStudioSearchName) &&
                        (m.Contains(r.LmStudioSearchName, StringComparison.OrdinalIgnoreCase) ||
                         r.LmStudioSearchName.Contains(m, StringComparison.OrdinalIgnoreCase)));
                }

                return Ok(recommendations);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取模型推荐失败");
                return StatusCode(500, new { error = "获取模型推荐失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 启动模型部署
        /// </summary>
        [HttpPost("deploy")]
        public async Task<ActionResult<DeployLocalModelResult>> Deploy([FromBody] DeployLocalModelRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ModelId))
                    return BadRequest(new { error = "ModelId 不能为空" });

                var result = await _deploymentService.DeployAsync(request);
                if (!result.Success)
                    return BadRequest(new { error = result.Message });

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动部署失败");
                return StatusCode(500, new { error = "启动部署失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 查询部署任务状态
        /// </summary>
        [HttpGet("deploy/{taskId}")]
        public ActionResult<DeployTaskStatusDto> GetDeployStatus(string taskId)
        {
            var status = _deploymentService.GetRunnerTaskStatus(taskId);
            if (status == null)
                return NotFound(new { error = "任务不存在", taskId });

            return Ok(status);
        }

        /// <summary>
        /// 取消部署任务
        /// </summary>
        [HttpPost("deploy/{taskId}/cancel")]
        public ActionResult CancelDeploy(string taskId)
        {
            var cancelled = _deploymentService.CancelTask(taskId);
            if (!cancelled)
                return NotFound(new { error = "任务不存在或已完成", taskId });

            return Ok(new { success = true, message = "任务已取消" });
        }

        /// <summary>
        /// 获取已安装的本地 AI 工具
        /// </summary>
        [HttpGet("tools")]
        public async Task<ActionResult<List<LocalToolInfoDto>>> GetTools([FromQuery] bool forceRefresh = false)
        {
            try
            {
                var tools = await _deploymentService.GetLocalToolsAsync(forceRefresh);
                return Ok(tools);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取本地工具信息失败");
                return StatusCode(500, new { error = "获取本地工具信息失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取可用下载源
        /// </summary>
        [HttpGet("sources")]
        public ActionResult<List<DownloadSourceDto>> GetSources()
        {
            return Ok(_deploymentService.GetDownloadSources());
        }

        /// <summary>
        /// 获取下载目录配置
        /// </summary>
        [HttpGet("config")]
        public ActionResult<DownloadDirectoryConfigDto> GetConfig()
        {
            var dto = new DownloadDirectoryConfigDto
            {
                DownloadDirectory = _localModelSettings.LocalModelDownloadDirectory,
                PreferredSource = _localModelSettings.PreferredDownloadSource,
                UseChinaMirror = _localModelSettings.UseChinaMirror,
                PlatformDefaultDirectory = GetPlatformDefaultDirectory(),
            };
            return Ok(dto);
        }

        /// <summary>
        /// 保存下载目录配置
        /// </summary>
        [HttpPost("config")]
        public ActionResult SaveConfig([FromBody] DownloadDirectoryConfigDto config)
        {
            try
            {
                if (!string.IsNullOrEmpty(config.DownloadDirectory))
                {
                    _localModelSettings.LocalModelDownloadDirectory = config.DownloadDirectory;
                }

                if (!string.IsNullOrEmpty(config.PreferredSource))
                {
                    _localModelSettings.PreferredDownloadSource = config.PreferredSource;
                }

                _localModelSettings.UseChinaMirror = config.UseChinaMirror;

                return Ok(new { success = true, message = "配置已保存" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存下载目录配置失败");
                return StatusCode(500, new { error = "保存失败", message = ex.Message });
            }
        }

        #region Running Model Management

        /// <summary>
        /// 获取运行中的模型列表
        /// </summary>
        [HttpGet("running")]
        public async Task<ActionResult<List<RunningModelDto>>> GetRunningModels([FromQuery] bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            try
            {
                var models = await _deploymentService.GetRunningModelsAsync(forceRefresh, cancellationToken);
                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取运行中模型失败");
                return StatusCode(500, new { error = "获取运行中模型失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取指定工具中可用的模型列表（已下载）
        /// </summary>
        [HttpGet("available")]
        public async Task<ActionResult<List<string>>> GetAvailableModels([FromQuery] string toolId, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(toolId))
                    return BadRequest(new { error = "toolId 不能为空" });

                var models = await _deploymentService.GetAvailableModelsAsync(toolId, cancellationToken);
                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取可用模型列表失败");
                return StatusCode(500, new { error = "获取可用模型列表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 加载模型到内存
        /// </summary>
        [HttpPost("running/load")]
        public async Task<ActionResult<dynamic>> LoadModel([FromBody] LoadModelRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                    return BadRequest(new { error = "ToolId 和 ModelName 不能为空" });

                var success = await _deploymentService.LoadModelAsync(request.ToolId, request.ModelName, request.KeepAliveMinutes, cancellationToken);
                if (success)
                    return Ok(new { success = true, message = $"模型 {request.ModelName} 已加载" });

                return StatusCode(500, new { error = "加载失败", message = "请检查工具是否运行" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载模型失败");
                return StatusCode(500, new { error = "加载失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 卸载模型释放内存
        /// </summary>
        [HttpPost("running/unload")]
        public async Task<ActionResult<dynamic>> UnloadModel([FromBody] UnloadModelRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                    return BadRequest(new { error = "ToolId 和 ModelName 不能为空" });

                var success = await _deploymentService.UnloadModelAsync(request.ToolId, request.ModelName, cancellationToken);
                if (success)
                    return Ok(new { success = true, message = $"模型 {request.ModelName} 已卸载" });

                return StatusCode(500, new { error = "卸载失败", message = "请检查工具是否运行" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "卸载模型失败");
                return StatusCode(500, new { error = "卸载失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 启动 llama.cpp 服务
        /// </summary>
        [HttpPost("llamacpp/start")]
        public async Task<ActionResult<LocalAiServiceStatusDto>> StartLlamaCpp(CancellationToken cancellationToken)
        {
            try
            {
                var status = await _deploymentService.StartLlamaCppAsync(cancellationToken);
                if (status.IsRunning)
                    return Ok(status);
                return StatusCode(500, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动 llama.cpp 失败");
                return StatusCode(500, new LocalAiServiceStatusDto { Provider = "llamacpp", Message = ex.Message });
            }
        }

        /// <summary>
        /// 停止 llama.cpp 服务
        /// </summary>
        [HttpPost("llamacpp/stop")]
        public async Task<ActionResult> StopLlamaCpp(CancellationToken cancellationToken)
        {
            try
            {
                var success = await _deploymentService.StopLlamaCppAsync(cancellationToken);
                if (success)
                    return Ok(new { success = true, message = "llama.cpp 已停止" });
                return StatusCode(500, new { error = "停止失败", message = "请检查进程是否存活" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止 llama.cpp 失败");
                return StatusCode(500, new { error = "停止失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取所有已下载模型（聚合所有工具）
        /// </summary>
        [HttpGet("downloaded")]
        public async Task<ActionResult<List<DownloadedModelDto>>> GetDownloadedModels(CancellationToken cancellationToken)
        {
            try
            {
                var models = await _deploymentService.GetDownloadedModelsAsync(cancellationToken);
                return Ok(models);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取已下载模型列表失败");
                return StatusCode(500, new { error = "获取已下载模型列表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 删除本地模型
        /// </summary>
        [HttpPost("delete")]
        public async Task<ActionResult> DeleteModel([FromBody] DeleteModelRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                    return BadRequest(new { error = "ToolId 和 ModelName 不能为空" });

                var success = await _deploymentService.DeleteModelAsync(request.ToolId, request.ModelName, cancellationToken);
                if (success)
                    return Ok(new { success = true, message = $"模型 {request.ModelName} 已删除" });

                return StatusCode(500, new { error = "删除失败", message = "请检查工具是否运行或模型是否存在" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除模型失败");
                return StatusCode(500, new { error = "删除失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取模型详情
        /// </summary>
        [HttpPost("details")]
        public async Task<ActionResult<ModelDetailsDto?>> GetModelDetails([FromBody] ModelDetailsRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ToolId) || string.IsNullOrWhiteSpace(request.ModelName))
                    return BadRequest(new { error = "ToolId 和 ModelName 不能为空" });

                var details = await _deploymentService.GetModelDetailsAsync(request.ToolId, request.ModelName, cancellationToken);
                if (details != null)
                    return Ok(details);

                return NotFound(new { error = "模型详情未找到", message = "该工具暂不支持查看模型详情" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取模型详情失败");
                return StatusCode(500, new { error = "获取模型详情失败", message = ex.Message });
            }
        }

        #endregion

        private static string GetPlatformDefaultDirectory()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".ollama", "models");
        }
    }
}
