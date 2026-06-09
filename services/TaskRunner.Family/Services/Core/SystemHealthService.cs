using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using TaskRunner.Core.Shared.Security;
using ComponentStatus = TaskRunner.Contracts.Health.ComponentStatusDto;
using SystemHealthReport = TaskRunner.Contracts.Health.SystemHealthReportDto;

namespace TaskRunner.Services
{
    /// <summary>
    /// 系统健康检查服务：检测所需组件和依赖
    /// </summary>
    public class SystemHealthService
    {
        private readonly ILogger<SystemHealthService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AiConfigService _aiConfigService;
        private readonly SettingsService _settingsService;
        private readonly AiMetricsService _metrics;

        public SystemHealthService(
            IHttpClientFactory httpClientFactory,
            ILogger<SystemHealthService> logger,
            AiConfigService aiConfigService,
            SettingsService settingsService,
            AiMetricsService metrics)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _aiConfigService = aiConfigService;
            _settingsService = settingsService;
            _metrics = metrics;
        }

        /// <summary>
        /// 获取系统健康状态（各子项并行，整体受 cancellationToken 约束）。
        /// </summary>
        public async Task<SystemHealthReport> GetHealthReportAsync(CancellationToken cancellationToken = default)
        {
            var report = new SystemHealthReport
            {
                Timestamp = DateTime.UtcNow,
                Components = new List<ComponentStatus>()
            };

            cancellationToken.ThrowIfCancellationRequested();

            var wallClock = Stopwatch.StartNew();
            var results = await Task.WhenAll(
                WithCheckDurationAsync(() => CheckGitAsync(cancellationToken)),
                WithCheckDurationAsync(() => CheckObsidianAsync(cancellationToken)),
                WithCheckDurationAsync(() => CheckOllamaAsync(cancellationToken)),
                WithCheckDurationAsync(() => CheckPythonAsync(cancellationToken)),
                WithCheckDurationAsync(() => CheckNodeAsync(cancellationToken)),
                WithCheckDurationAsync(() => CheckPipAsync(cancellationToken)),
                WithCheckDurationAsync(() => CheckApiKeyAsync(cancellationToken)),
                WithCheckDurationAsync(() => CheckVaultPathAsync(cancellationToken)));
            wallClock.Stop();
            report.TotalWallClockMs = wallClock.ElapsedMilliseconds;

            report.Components.AddRange(results);

            var total = report.Components.Count;
            var healthy = report.Components.Count(c => c.Status == "healthy");
            var warning = report.Components.Count(c => c.Status == "warning");
            var critical = report.Components.Count(c => c.Status == "critical");

            report.HealthScore = total > 0 ? (healthy * 100 + warning * 50) / total : 0;
            report.Status = critical > 0 ? "critical" : (warning > 0 ? "warning" : "healthy");

            // 记录健康检查指标到 .NET Metrics（OpenTelemetry -> OpenObserve）
            foreach (var component in report.Components)
            {
                _metrics.RecordHealthCheck(
                    component.Name, component.Status,
                    component.CheckDurationMs);
            }
            _metrics.RecordHealthCheck(
                "_total", report.Status,
                report.TotalWallClockMs,
                wallClockMs: report.TotalWallClockMs,
                score: report.HealthScore);

            _logger.LogInformation("健康检查完成：{Status}，健康度：{Score}%", report.Status, report.HealthScore);

            return report;
        }

        private static async Task<ComponentStatus> WithCheckDurationAsync(Func<Task<ComponentStatus>> check)
        {
            var sw = Stopwatch.StartNew();
            var result = await check().ConfigureAwait(false);
            sw.Stop();
            result.CheckDurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        private static void TryKill(Process? process)
        {
            if (process is null || process.HasExited) return;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                /* ignore */
            }
        }

        /// <summary>在 timeoutMs 内等待退出；若外层 cancellationToken 取消则向上抛出。</summary>
        private static async Task<(bool exitedOk, int exitCode, string stdout)> WaitForProcessAsync(
            Process process,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeoutMs);
            try
            {
                await process.WaitForExitAsync(linked.Token);
                var stdout = await process.StandardOutput.ReadToEndAsync();
                _ = await process.StandardError.ReadToEndAsync();
                return (true, process.ExitCode, stdout);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
                return (false, -1, "");
            }
        }

        private async Task<ComponentStatus> CheckGitAsync(CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null)
                    return new ComponentStatus { Name = "Git", Status = "critical", Message = "Git 未安装" };

                var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                if (!ok)
                    return new ComponentStatus { Name = "Git", Status = "critical", Message = "Git 检测超时" };
                if (exitCode != 0)
                    return new ComponentStatus { Name = "Git", Status = "critical", Message = "Git 检测失败" };

                return new ComponentStatus
                {
                    Name = "Git",
                    Status = "healthy",
                    Version = ExtractVersion(output),
                    Message = "Git 已安装"
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Git 检测失败");
                return new ComponentStatus { Name = "Git", Status = "critical", Message = "Git 检测异常" };
            }
        }

        private Task<ComponentStatus> CheckObsidianAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var obsidianRunning = Process.GetProcessesByName("Obsidian").Length > 0;

                if (obsidianRunning)
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "Obsidian",
                        Status = "healthy",
                        Message = "Obsidian 桌面客户端运行中"
                    });
                }

                // Linux 上 Obsidian 桌面客户端不是必须的，FTS5 全文搜索已替代 CLI 搜索功能
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "Obsidian",
                        Status = "healthy",
                        Message = "Obsidian 桌面客户端未运行（Linux 上 FTS5 搜索已替代 CLI 功能）"
                    });
                }

                return Task.FromResult(new ComponentStatus
                {
                    Name = "Obsidian",
                    Status = "warning",
                    Message = "Obsidian 桌面客户端未运行（CLI 功能需要桌面客户端）"
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Obsidian 检测失败");
                return Task.FromResult(new ComponentStatus
                {
                    Name = "Obsidian",
                    Status = "warning",
                    Message = "Obsidian 检测异常"
                });
            }
        }

        /// <summary>
        /// 启动时初始化 Obsidian（仅调用一次）
        /// 如果 CLI 已安装但 Obsidian 未运行，则启动 Obsidian
        /// </summary>
        public async Task InitializeObsidianAsync()
        {
            try
            {
                var obsidianRunning = Process.GetProcessesByName("Obsidian").Length > 0;
                if (obsidianRunning)
                {
                    _logger.LogInformation("Obsidian 已在运行，跳过启动");
                    return;
                }

                // 过去用 `obsidian help` 做“CLI 可用性探测”，在 Windows 上可能表现为“打开又关闭”，
                // 反而干扰用户观察。这里不再探测，直接尝试启动桌面端；失败则记录并降级为文件扫描。
                var obsidianExe = ObsidianExecutableResolver.Resolve();
                _logger.LogInformation("Obsidian 未运行，尝试启动：{Exe}", obsidianExe);

                var startInfo = new ProcessStartInfo
                {
                    FileName = obsidianExe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.EnvironmentVariables["ELECTRON_DISABLE_AUTO_UPDATE"] = "1";
                startInfo.EnvironmentVariables["OBSIDIAN_DISABLE_AUTO_UPDATE"] = "1";

                Process.Start(startInfo);
                _logger.LogInformation("Obsidian 启动成功");

                await Task.Delay(3000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动 Obsidian 失败");
            }
        }

        private async Task<ComponentStatus> CheckOllamaAsync(CancellationToken cancellationToken)
        {
            try
            {
                // 只检测 Ollama 服务是否在运行，不尝试启动进程
                var ollamaRunning = await IsOllamaServiceUpAsync(cancellationToken);

                if (!ollamaRunning)
                {
                    return new ComponentStatus
                    {
                        Name = "Ollama",
                        Status = "warning",
                        Message = "Ollama 服务未运行（可选，用于本地 AI 模型）"
                    };
                }

                return new ComponentStatus
                {
                    Name = "Ollama",
                    Status = "healthy",
                    Message = "Ollama 服务正在运行"
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ollama 检测失败");
                return new ComponentStatus
                {
                    Name = "Ollama",
                    Status = "warning",
                    Message = "Ollama 检测异常"
                };
            }
        }

        private async Task<(bool ExitedOk, int ExitCode, string Stdout)> RunSimpleCommandAsync(
            string fileName,
            string arguments,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{File} 启动失败", fileName);
                return (false, -1, "");
            }

            if (process is null)
                return (false, -1, "");

            var (ok, exitCode, stdout) = await WaitForProcessAsync(process, timeoutMs, cancellationToken);
            if (!ok)
                return (false, exitCode, stdout);
            return (true, exitCode, stdout);
        }

        private async Task<(bool ExitedOk, int ExitCode, string Stdout)> RunWslAsync(
            IReadOnlyList<string> argumentList,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in argumentList)
                    psi.ArgumentList.Add(a);

                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "wsl.exe 启动失败（可能未安装 WSL）");
                return (false, -1, "");
            }

            if (process is null)
                return (false, -1, "");

            return await WaitForProcessAsync(process, timeoutMs, cancellationToken);
        }

        /// <summary>
        /// Windows 上对本机环回与 WSL 内 11434 并行探测，避免串行 HTTP+curl 多等几秒。
        /// </summary>
        private async Task<bool> IsOllamaServiceUpAsync(CancellationToken cancellationToken)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return await ProbeOllamaHttpFromWindowsAsync(cancellationToken).ConfigureAwait(false);

            var winTask = ProbeOllamaHttpFromWindowsAsync(cancellationToken);
            var wslTask = ProbeOllamaHttpViaWslAsync(cancellationToken);
            var first = await Task.WhenAny(winTask, wslTask).ConfigureAwait(false);
            if (first == winTask)
            {
                if (await winTask.ConfigureAwait(false))
                    return true;
                return await wslTask.ConfigureAwait(false);
            }

            if (await wslTask.ConfigureAwait(false))
                return true;
            return await winTask.ConfigureAwait(false);
        }

        private async Task<bool> ProbeOllamaHttpFromWindowsAsync(CancellationToken cancellationToken)
        {
            foreach (var baseUrl in new[] { "http://127.0.0.1:11434/", "http://localhost:11434/" })
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient("SystemHealth");
                    using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl);
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if ((int)response.StatusCode < 500)
                        return true;
                }
                catch
                {
                    /* try next */
                }
            }

            return false;
        }

        private async Task<bool> ProbeOllamaHttpViaWslAsync(CancellationToken cancellationToken)
        {
            var curl = await RunWslAsync(
                new[] { "-e", "curl", "-fsS", "-m", "2", "-o", "/dev/null", "http://127.0.0.1:11434/" },
                3500,
                cancellationToken);
            if (curl.ExitedOk && curl.ExitCode == 0)
                return true;

            var wget = await RunWslAsync(
                new[] { "-e", "wget", "-q", "-T", "2", "-O", "/dev/null", "http://127.0.0.1:11434/" },
                3500,
                cancellationToken);
            return wget.ExitedOk && wget.ExitCode == 0;
        }

        private async Task<ComponentStatus> CheckPythonAsync(CancellationToken cancellationToken)
        {
            // Windows 上优先尝试 py 启动器，然后是 python/python3
            var pythonCmds = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "py", "python", "python3" }
                : new[] { "python3", "python" };

            foreach (var cmd in pythonCmds)
            {
                Process? process = null;
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process is null) continue;

                    var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                    if (ok && exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return new ComponentStatus
                        {
                            Name = "Python",
                            Status = "healthy",
                            Version = ExtractVersion(output),
                            Message = $"Python 已安装 ({cmd})"
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 尝试下一个命令
                    continue;
                }
            }

            return new ComponentStatus
            {
                Name = "Python",
                Status = "warning",
                Message = "Python 未安装（可选，用于脚本扩展）"
            };
        }

        private async Task<ComponentStatus> CheckNodeAsync(CancellationToken cancellationToken)
        {
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (process is null)
                {
                    return new ComponentStatus
                    {
                        Name = "Node.js",
                        Status = "warning",
                        Message = "Node.js 未安装"
                    };
                }

                var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                if (!ok)
                    return new ComponentStatus { Name = "Node.js", Status = "critical", Message = "Node.js 检测超时" };
                if (exitCode != 0)
                    return new ComponentStatus { Name = "Node.js", Status = "critical", Message = "Node.js 检测失败" };

                return new ComponentStatus
                {
                    Name = "Node.js",
                    Status = "healthy",
                    Version = ExtractVersion(output),
                    Message = "Node.js 已安装"
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Node.js 检测失败");
                return new ComponentStatus
                {
                    Name = "Node.js",
                    Status = "warning",
                    Message = "Node.js 检测异常"
                };
            }
        }

        private async Task<ComponentStatus> CheckPipAsync(CancellationToken cancellationToken)
        {
            // Windows 上优先尝试 py 启动器，然后是 python/python3
            var pythonCmds = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "py", "python", "python3" }
                : new[] { "python3", "python" };

            foreach (var cmd in pythonCmds)
            {
                Process? process = null;
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "-m pip --version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process is null) continue;

                    var (ok, exitCode, output) = await WaitForProcessAsync(process, 4000, cancellationToken);
                    if (ok && exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return new ComponentStatus
                        {
                            Name = "PIP",
                            Status = "healthy",
                            Version = ExtractVersion(output),
                            Message = $"PIP 已安装 ({cmd})"
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 尝试下一个命令
                    continue;
                }
            }

            return new ComponentStatus
            {
                Name = "PIP",
                Status = "warning",
                Message = "PIP 未安装（可选，用于 Python 包管理）"
            };
        }

        private static bool IsLocalAiProvider(TaskRunner.Models.AiProviderConfig provider)
        {
            if (string.IsNullOrWhiteSpace(provider?.AiBaseUrl))
                return false;
            var url = provider.AiBaseUrl.ToLowerInvariant();
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }

        private string? ExtractVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(output, @"[vV]?(\d+\.\d+\.\d+)");
            return match.Success ? match.Value : output.Trim();
        }

        /// <summary>
        /// 检查 AI API Key 配置状态
        /// </summary>
        private Task<ComponentStatus> CheckApiKeyAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var providers = _aiConfigService.GetProviders();
                if (providers.Count == 0)
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "API Key",
                        Status = "warning",
                        Message = "未配置任何 AI 提供商"
                    });
                }

                var mainProvider = providers.FirstOrDefault(p => p.IsMain) ?? providers.First();
                var apiKey = _aiConfigService.GetApiKey(mainProvider.Id);
                var isLocalProvider = IsLocalAiProvider(mainProvider);

                if (string.IsNullOrEmpty(apiKey))
                {
                    // 本地 AI 服务不需要 API Key
                    if (isLocalProvider)
                    {
                        return Task.FromResult(new ComponentStatus
                        {
                            Name = "API Key",
                            Status = "healthy",
                            Message = $"主提供商 {mainProvider.Name} 为本地服务，无需 API Key"
                        });
                    }

                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "API Key",
                        Status = "critical",
                        Message = $"主提供商 {mainProvider.Name} 未配置 API Key"
                    });
                }

                var summaries = _aiConfigService.GetApiKeySummaries();
                var summary = summaries.FirstOrDefault(s => 
                    s.ProviderId.Equals(mainProvider.Id, StringComparison.OrdinalIgnoreCase));

                if (summary?.HasApiKey == true)
                {
                    var scheme = summary.Scheme switch
                    {
                        EncryptionScheme.AesGcm => "AES加密",
                        _ => "已加密"
                    };
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "API Key",
                        Status = "healthy",
                        Message = $"{mainProvider.Name} API Key 已配置（{scheme}）"
                    });
                }

                return Task.FromResult(new ComponentStatus
                {
                    Name = "API Key",
                    Status = "critical",
                    Message = $"{mainProvider.Name} 未配置 API Key"
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "API Key 检测失败");
                return Task.FromResult(new ComponentStatus
                {
                    Name = "API Key",
                    Status = "warning",
                    Message = "API Key 检测异常"
                });
            }
        }

        /// <summary>
        /// 检查知识库路径配置状态
        /// </summary>
        private Task<ComponentStatus> CheckVaultPathAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var vaultPath = _settingsService.VaultPath;
                
                if (string.IsNullOrWhiteSpace(vaultPath))
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "知识库",
                        Status = "critical",
                        Message = "未配置知识库路径。请前往设置页面配置 Obsidian 知识库路径"
                    });
                }

                if (!Directory.Exists(vaultPath))
                {
                    return Task.FromResult(new ComponentStatus
                    {
                        Name = "知识库",
                        Status = "critical",
                        Message = $"知识库路径不存在: {vaultPath}。该目录可能被删除或移动，请重新配置"
                    });
                }

                // 只要路径存在即视为有效，不再强制要求 .obsidian 目录或 .md 文件
                // 用户可以通过 WebUI 的"在 Obsidian 中打开"按钮来初始化该目录
                var hasObsidianDir = Directory.Exists(Path.Combine(vaultPath, ".obsidian"));
                var mdFiles = Directory.GetFiles(vaultPath, "*.md", SearchOption.TopDirectoryOnly);
                
                return Task.FromResult(new ComponentStatus
                {
                    Name = "知识库",
                    Status = "healthy",
                    Message = hasObsidianDir 
                        ? $"知识库已配置: {Path.GetFileName(vaultPath)} ({mdFiles.Length} 个文档)"
                        : $"知识库路径已配置: {Path.GetFileName(vaultPath)}。点击[在 Obsidian 中打开]按钮初始化"
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "知识库路径检测失败");
                return Task.FromResult(new ComponentStatus
                {
                    Name = "知识库",
                    Status = "warning",
                    Message = "知识库路径检测异常"
                });
            }
        }
    }

}
