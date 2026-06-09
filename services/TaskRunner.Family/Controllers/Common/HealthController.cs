using Microsoft.AspNetCore.Mvc;
using TaskRunner.Contracts.Health;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// 健康检查控制器
    /// </summary>
    [ApiController]
    [Route("api/health")]
    public class HealthController : ControllerBase
    {
        private readonly Services.SystemHealthService _healthService;
        private readonly Services.AiSettingsService _aiSettings;
        private readonly Services.LocalAiAutoStarter _localAiAutoStarter;
        private readonly Services.ILocalAiConfigService _localAiConfig;
        private readonly ILogger<HealthController> _logger;

        public HealthController(
            Services.SystemHealthService healthService,
            Services.AiSettingsService aiSettings,
            Services.LocalAiAutoStarter localAiAutoStarter,
            Services.ILocalAiConfigService localAiConfig,
            ILogger<HealthController> logger)
        {
            _healthService = healthService;
            _aiSettings = aiSettings;
            _localAiAutoStarter = localAiAutoStarter;
            _localAiConfig = localAiConfig;
            _logger = logger;
        }

        /// <summary>
        /// 获取系统健康报告（完整自检）
        /// </summary>
        [HttpGet("full")]
        public async Task<ActionResult<SystemHealthReportDto>> GetFullHealth(CancellationToken cancellationToken)
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(25));
                var report = await _healthService.GetHealthReportAsync(budget.Token);
                return Ok(report);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("健康检查在时限内未完成（可能机器较慢），建议使用 /api/health/simple");
                return StatusCode(StatusCodes.Status504GatewayTimeout, new
                {
                    error = "健康检查超时",
                    message = "完整自检超过 25 秒未完成。请稍后重试，或使用 GET /api/health/simple、GET /health。"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "健康检查失败");
                return StatusCode(500, new { error = "健康检查失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 简单健康检查（快速响应）
        /// </summary>
        [HttpGet("simple")]
        public ActionResult<dynamic> GetSimpleHealth()
        {
            return new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow.ToString("o"),
                message = "Task Runner Service is running"
            };
        }

        /// <summary>
        /// 检查特定组件
        /// </summary>
        [HttpGet("check/{component}")]
        public async Task<ActionResult<dynamic>> CheckComponent(string component)
        {
            try
            {
                var report = await _healthService.GetHealthReportAsync();
                var componentStatus = report.Components.FirstOrDefault(c => 
                    c.Name.ToLower() == component.ToLower());

                if (componentStatus == null)
                {
                    return NotFound(new { 
                        error = $"组件不存在: {component}",
                        available = string.Join(", ", report.Components.Select(c => c.Name))
                    });
                }

                return Ok(new
                {
                    component = componentStatus.Name,
                    status = componentStatus.Status,
                    version = componentStatus.Version,
                    message = componentStatus.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "组件检查失败");
                return StatusCode(500, new { error = "组件检查失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取所有可用组件列表
        /// </summary>
        [HttpGet("components")]
        public async Task<ActionResult<List<string>>> GetComponents()
        {
            try
            {
                var report = await _healthService.GetHealthReportAsync();
                return Ok(report.Components.Select(c => c.Name).ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取组件列表失败");
                return StatusCode(500, new { error = "获取组件列表失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取服务启动监控信息
        /// </summary>
        [HttpGet("startup")]
        public ActionResult<dynamic> GetStartupInfo()
        {
            try
            {
                var monitor = StartupMonitor.Instance;
                return Ok(new
                {
                    startTime = monitor.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    uptime = (DateTime.Now - monitor.StartTime).ToString(@"hh\:mm\:ss"),
                    restartCount = monitor.RestartCount,
                    pid = Environment.ProcessId,
                    recentRestarts = monitor.RestartHistory
                        .Where(t => (DateTime.Now - t).TotalMinutes < 10)
                        .Select(t => t.ToString("yyyy-MM-dd HH:mm:ss"))
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启动信息失败");
                return StatusCode(500, new { error = "获取启动信息失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取服务器操作系统信息
        /// </summary>
        [HttpGet("os")]
        public ActionResult<dynamic> GetOperatingSystem()
        {
            try
            {
                var os = Environment.OSVersion;
                var platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows);
                var isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Linux);
                var isMac = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.OSX);

                return Ok(new
                {
                    OsName = isWindows ? "Windows" : isLinux ? "Linux" : isMac ? "macOS" : "Unknown",
                    IsWindows = isWindows,
                    IsLinux = isLinux,
                    IsMacOS = isMac,
                    UserName = Environment.UserName,
                    HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    AiRequestTimeoutMinutes = _aiSettings.AiRequestTimeoutMinutes
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取操作系统信息失败");
                return StatusCode(500, new { error = "获取操作系统信息失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 一键修复系统问题
        /// </summary>
        [HttpPost("fix")]
        public async Task<ActionResult<HealthFixResultDto>> FixIssues(CancellationToken cancellationToken)
        {
            var result = new HealthFixResultDto();
            var fixes = new List<HealthFixItemDto>();

            try
            {
                // 先获取当前健康报告
                var report = await _healthService.GetHealthReportAsync(cancellationToken);

                foreach (var component in report.Components)
                {
                    if (component.Status == "healthy")
                    {
                        fixes.Add(new HealthFixItemDto
                        {
                            Component = component.Name,
                            Status = "skipped",
                            Message = "状态正常，无需修复"
                        });
                        continue;
                    }

                    switch (component.Name)
                    {
                        case "Ollama":
                            // 尝试启动 Ollama
                            try
                            {
                                var config = await _localAiConfig.GetLocalAiConfigAsync();
                                var ollamaUrl = config.Ollama?.BaseUrl ?? "http://localhost:11434";
                                var started = await _localAiAutoStarter.TryEnsureRunningAsync("ollama", ollamaUrl);
                                fixes.Add(new HealthFixItemDto
                                {
                                    Component = component.Name,
                                    Status = started ? "fixed" : "failed",
                                    Message = started ? "Ollama 服务已启动" : "Ollama 启动失败，请手动安装并启动"
                                });
                            }
                            catch (Exception ex)
                            {
                                fixes.Add(new HealthFixItemDto
                                {
                                    Component = component.Name,
                                    Status = "failed",
                                    Message = $"启动失败: {ex.Message}"
                                });
                            }
                            break;

                        case "Git":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = "请安装 Git: sudo apt-get install git (Linux) 或访问 git-scm.com (Windows/macOS)"
                            });
                            break;

                        case "Python":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = "请安装 Python: sudo apt-get install python3 (Linux) 或访问 python.org"
                            });
                            break;

                        case "Node.js":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = "请安装 Node.js: 访问 nodejs.org 下载安装包"
                            });
                            break;

                        case "PIP":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = "PIP 通常随 Python 一起安装。如缺失请运行: python3 -m ensurepip"
                            });
                            break;

                        case "API Key":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = "请前往 WebUI → AI设置 页面配置 API Key"
                            });
                            break;

                        case "知识库":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = "请前往 WebUI → 知识库 页面配置 Obsidian 知识库路径"
                            });
                            break;

                        case "Obsidian":
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "manual_required",
                                Message = "请启动 Obsidian 桌面客户端"
                            });
                            break;

                        default:
                            fixes.Add(new HealthFixItemDto
                            {
                                Component = component.Name,
                                Status = "skipped",
                                Message = "暂不支持自动修复"
                            });
                            break;
                    }
                }

                // 重新检测
                var newReport = await _healthService.GetHealthReportAsync(cancellationToken);
                result.Success = newReport.Status != "critical";
                result.Message = $"修复完成。健康度: {newReport.HealthScore}%。需手动处理: {fixes.Count(f => f.Status == "manual_required")} 项";
                result.Fixes = fixes;
                result.NewReport = newReport;

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "一键修复失败");
                return StatusCode(500, new HealthFixResultDto
                {
                    Success = false,
                    Message = $"修复失败: {ex.Message}",
                    Fixes = fixes
                });
            }
        }

        /// <summary>
        /// 一键配置 OpenClaw 环境
        /// 调用 openclaw doctor --fix 自动修复配置问题
        /// </summary>
        [HttpPost("setup-openclaw")]
        public async Task<ActionResult<dynamic>> SetupOpenClaw(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("开始一键配置 OpenClaw 环境...");

                // 1. 检查 openclaw 是否已安装
                var checkPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "openclaw",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var checkProcess = System.Diagnostics.Process.Start(checkPsi);
                if (checkProcess == null)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "OpenClaw 未安装。请运行: npm install -g openclaw"
                    });
                }

                await checkProcess.WaitForExitAsync(cancellationToken);
                if (checkProcess.ExitCode != 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "OpenClaw 安装异常，无法执行配置"
                    });
                }

                var version = await checkProcess.StandardOutput.ReadToEndAsync(cancellationToken);

                // 2. 后台运行 openclaw doctor --fix（不阻塞等待完成）
                var doctorPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "openclaw",
                    Arguments = "doctor --fix",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var doctorProcess = System.Diagnostics.Process.Start(doctorPsi);
                if (doctorProcess == null)
                {
                    return StatusCode(500, new { success = false, message = "无法启动 openclaw doctor" });
                }

                // 异步读取输出，不等待进程退出（doctor 可能需要较长时间）
                var doctorTask = Task.Run(async () =>
                {
                    var stdout = await doctorProcess.StandardOutput.ReadToEndAsync();
                    var stderr = await doctorProcess.StandardError.ReadToEndAsync();
                    await doctorProcess.WaitForExitAsync();
                    return (stdout, stderr, doctorProcess.ExitCode);
                }, cancellationToken);

                // 3. 获取模型列表（快速操作）
                var modelsPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "openclaw",
                    Arguments = "models list --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var modelsJson = "";
                using var modelsProcess = System.Diagnostics.Process.Start(modelsPsi);
                if (modelsProcess != null)
                {
                    using var modelsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    modelsCts.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        await modelsProcess.WaitForExitAsync(modelsCts.Token);
                        modelsJson = await modelsProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                    }
                    catch { /* ignore timeout */ }
                }

                // 尝试等待 doctor 最多 15 秒获取即时结果
                var doctorCompleted = doctorTask.Wait(TimeSpan.FromSeconds(15));
                var (doctorStdout, doctorStderr, exitCode) = doctorCompleted
                    ? doctorTask.Result
                    : ("", "", -1);

                return Ok(new
                {
                    success = exitCode == 0 || !doctorCompleted,
                    version = version.Trim(),
                    doctorCompleted,
                    doctorExitCode = exitCode,
                    doctorOutput = doctorStdout.Trim(),
                    doctorErrors = doctorStderr.Trim(),
                    modelsJson = modelsJson.Trim(),
                    message = doctorCompleted
                        ? (exitCode == 0 ? "OpenClaw 配置修复完成" : "OpenClaw doctor 返回非零退出码")
                        : "OpenClaw 配置修复已在后台启动（可能需要 30-60 秒完成）"
                });
            }
            catch (OperationCanceledException)
            {
                return StatusCode(504, new { success = false, message = "OpenClaw 配置超时（超过 60 秒）" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenClaw 配置失败");
                return StatusCode(500, new { success = false, message = $"配置失败: {ex.Message}" });
            }
        }
    }
}
