using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text;
using TaskRunner.Services;
using TaskRunner.Contracts.Git;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// Git 管理控制器：提供知识库的 Git 操作
    /// </summary>
    [ApiController]
    [Route("api/git")]
    public class GitController : ControllerBase
    {
        private readonly ILogger<GitController> _logger;
        private readonly SettingsService _settings;

        public GitController(ILogger<GitController> logger, SettingsService settings)
        {
            _logger = logger;
            _settings = settings;
        }

        /// <summary>
        /// 检查知识库是否配置了 Git
        /// </summary>
        [HttpGet("status")]
        public async Task<ActionResult<GitStatusResponse>> GetStatus([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return Ok(new GitStatusResponse
                    {
                        IsGitRepo = false,
                        Message = "必须指定有效的知识库"
                    });
                }

                var gitDir = Path.Combine(vaultPath, ".git");
                if (!Directory.Exists(gitDir))
                {
                    return Ok(new GitStatusResponse
                    {
                        IsGitRepo = false,
                        Message = "知识库未配置 Git"
                    });
                }

                // 获取当前分支
                var branch = await RunGitCommand(vaultPath, "rev-parse --abbrev-ref HEAD");
                
                // 获取状态（-uall 显示所有未跟踪文件，不只是目录）
                var status = await RunGitCommand(vaultPath, "status --porcelain -uall");
                var remote = await RunGitCommand(vaultPath, "remote -v");
                
                // 解析变更文件
                var changes = new List<ChangeResponse>();
                if (!string.IsNullOrWhiteSpace(status))
                {
                    foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            if (line.Length >= 3)
                            {
                                var changeType = line.Substring(0, Math.Min(2, line.Length)).Trim();
                                var filePath = line.Length > 3 ? line.Substring(3) : line;
                                
                                // 解码 Git 返回的八进制编码（如 \347\224\237 -> 生）
                                filePath = DecodeGitOctalPath(filePath);
                                
                                // 移除引号
                                filePath = filePath.Trim('"');
                                
                                changes.Add(new ChangeResponse
                                {
                                    Status = MapGitStatus(changeType),
                                    File = filePath
                                });
                            }
                            else if (line.Length > 0)
                            {
                                var decodedLine = DecodeGitOctalPath(line);
                                changes.Add(new ChangeResponse
                                {
                                    Status = "Unknown",
                                    File = decodedLine
                                });
                            }
                        }
                        catch
                        {
                            // 忽略解析错误的行
                        }
                    }
                }

                // 获取 ahead/behind 信息
                var ahead = 0;
                var behind = 0;
                try
                {
                    var branchInfo = await RunGitCommand(vaultPath, $"rev-list --left-right --count origin/{branch}...HEAD");
                    if (!string.IsNullOrWhiteSpace(branchInfo))
                    {
                        var parts = branchInfo.Split('\t');
                        if (parts.Length == 2)
                        {
                            behind = int.TryParse(parts[0].Trim(), out var b) ? b : 0;
                            ahead = int.TryParse(parts[1].Trim(), out var a) ? a : 0;
                        }
                    }
                }
                catch
                {
                    // 可能没有远程分支
                }

                return Ok(new GitStatusResponse
                {
                    IsGitRepo = true,
                    Branch = branch.Trim(),
                    HasRemote = !string.IsNullOrWhiteSpace(remote),
                    Changes = changes,
                    Ahead = ahead,
                    Behind = behind,
                    Message = changes.Count == 0 ? "工作区干净" : $"有 {changes.Count} 个文件变更"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 Git 状态失败");
                return Ok(new GitStatusResponse
                {
                    IsGitRepo = false,
                    Message = $"获取状态失败: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 提交变更
        /// </summary>
        [HttpPost("commit")]
        public async Task<ActionResult<GitResultResponse>> Commit([FromQuery] string vaultId, [FromBody] CommitRequest request)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = "必须指定有效的知识库" });
                }

                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = "提交信息不能为空" });
                }

                // 先检查是否有变更
                var status = await RunGitCommand(vaultPath, "status --porcelain");
                if (string.IsNullOrWhiteSpace(status))
                {
                    return Ok(new GitResultResponse { Success = true, Message = "没有需要提交的变更" });
                }

                // 添加所有变更
                await RunGitCommand(vaultPath, "add -A");
                
                // 提交
                var escapedMessage = request.Message.Replace("\"", "\\\"");
                var commitResult = await RunGitCommand(vaultPath, $"commit -m \"{escapedMessage}\"");
                
                _logger.LogInformation("Git 提交成功: {Message}", request.Message);
                
                return Ok(new GitResultResponse
                {
                    Success = true,
                    Message = "提交成功",
                    Output = commitResult
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git 提交失败");
                return Ok(new GitResultResponse
                {
                    Success = false,
                    Message = $"提交失败: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 推送到远程
        /// </summary>
        [HttpPost("push")]
        public async Task<ActionResult<GitResultResponse>> Push([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = "必须指定有效的知识库" });
                }

                // 检查远程配置
                var remote = await RunGitCommand(vaultPath, "remote -v");
                if (string.IsNullOrWhiteSpace(remote))
                {
                    return Ok(new GitResultResponse { Success = false, Message = "未配置远程仓库" });
                }

                // 获取当前分支
                var branch = await RunGitCommand(vaultPath, "rev-parse --abbrev-ref HEAD");
                
                // 推送
                var pushResult = await RunGitCommand(vaultPath, $"push origin {branch.Trim()}", timeoutMs: 60000);
                
                _logger.LogInformation("Git 推送成功: {Branch}", branch.Trim());
                
                return Ok(new GitResultResponse
                {
                    Success = true,
                    Message = "推送成功",
                    Output = pushResult
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git 推送失败");
                return Ok(new GitResultResponse
                {
                    Success = false,
                    Message = $"推送失败: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 拉取远程变更
        /// </summary>
        [HttpPost("pull")]
        public async Task<ActionResult<GitResultResponse>> Pull([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = "必须指定有效的知识库" });
                }

                var pullResult = await RunGitCommand(vaultPath, "pull", timeoutMs: 60000);
                
                _logger.LogInformation("Git 拉取成功");
                
                return Ok(new GitResultResponse
                {
                    Success = true,
                    Message = "拉取成功",
                    Output = pullResult
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git 拉取失败");
                return Ok(new GitResultResponse
                {
                    Success = false,
                    Message = $"拉取失败: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 查看提交日志
        /// </summary>
        [HttpGet("log")]
        public async Task<ActionResult<List<CommitResponse>>> GetLog([FromQuery] string vaultId, [FromQuery] int count = 10)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new { error = "必须指定有效的知识库" });
                }

                var log = await RunGitCommand(vaultPath, $"log --oneline -{count} --format=\"%H|%s|%an|%ar\"");
                var commits = new List<CommitResponse>();
                
                foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 4)
                    {
                        commits.Add(new CommitResponse
                        {
                            Hash = parts[0],
                            Message = parts[1],
                            Author = parts[2],
                            Date = parts[3]
                        });
                    }
                }
                
                return Ok(commits);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取提交日志失败");
                return Ok(new List<CommitResponse>());
            }
        }

        /// <summary>
        /// 获取待推送的提交
        /// </summary>
        [HttpGet("unpushed")]
        public async Task<ActionResult<List<CommitResponse>>> GetUnpushedCommits([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new { error = "必须指定有效的知识库" });
                }

                // 获取当前分支
                var branch = await RunGitCommand(vaultPath, "rev-parse --abbrev-ref HEAD");
                
                // 获取待推送的提交
                var log = await RunGitCommand(vaultPath, $"log origin/{branch.Trim()}..HEAD --format=\"%H|%s|%an|%ar\"");
                var commits = new List<CommitResponse>();
                
                foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('|');
                    if (parts.Length >= 4)
                    {
                        commits.Add(new CommitResponse
                        {
                            Hash = parts[0],
                            Message = parts[1],
                            Author = parts[2],
                            Date = parts[3]
                        });
                    }
                }
                
                return Ok(commits);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取待推送提交失败");
                return Ok(new List<CommitResponse>());
            }
        }

        /// <summary>
        /// 撤销工作区更改
        /// </summary>
        [HttpPost("discard")]
        public async Task<ActionResult<GitResultResponse>> DiscardChanges([FromQuery] string vaultId)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath))
                {
                    return BadRequest(new GitResultResponse { Success = false, Message = "必须指定有效的知识库" });
                }

                // 检查是否有变更
                var status = await RunGitCommand(vaultPath, "status --porcelain");
                if (string.IsNullOrWhiteSpace(status))
                {
                    return Ok(new GitResultResponse { Success = true, Message = "没有需要撤销的更改" });
                }

                // 撤销所有工作区更改（不包括未跟踪的文件）
                await RunGitCommand(vaultPath, "checkout -- .");
                
                // 删除未跟踪的文件
                await RunGitCommand(vaultPath, "clean -fd");
                
                _logger.LogInformation("已撤销工作区更改");
                
                return Ok(new GitResultResponse
                {
                    Success = true,
                    Message = "已撤销所有工作区更改"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "撤销更改失败");
                return Ok(new GitResultResponse
                {
                    Success = false,
                    Message = $"撤销失败: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 执行 git 命令
        /// </summary>
        private async Task<string> RunGitCommand(string vaultPath, string args, int timeoutMs = 10000)
        {
            if (string.IsNullOrEmpty(vaultPath))
            {
                throw new InvalidOperationException("知识库路径未配置");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = vaultPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (s, e) => 
            { 
                try { if (e.Data != null) output.AppendLine(e.Data); } catch { } 
            };
            process.ErrorDataReceived += (s, e) => 
            { 
                try { if (e.Data != null) error.AppendLine(e.Data); } catch { } 
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill();
                throw new TimeoutException("Git 命令超时");
            }

            // 等待异步读取完成
            await Task.Delay(100);

            if (process.ExitCode != 0 && error.Length > 0)
            {
                throw new Exception(error.ToString().Trim());
            }

            return output.ToString().Trim();
        }

        /// <summary>
        /// 解码 Git 返回的八进制编码路径（如 \347\224\237 -> 生）
        /// Git 使用 UTF-8 字节的八进制表示
        /// </summary>
        private string DecodeGitOctalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            
            // Git 使用八进制编码 UTF-8 字节，格式如 \347\224\237
            // 需要将连续的 \xxx 转换为 UTF-8 字节，然后解码
            try
            {
                var bytes = new List<byte>();
                var i = 0;
                
                while (i < path.Length)
                {
                    if (path[i] == '\\' && i + 3 < path.Length)
                    {
                        // 收集连续的八进制编码
                        var byteList = new List<byte>();
                        while (i < path.Length && path[i] == '\\' && i + 3 < path.Length)
                        {
                            var octal = path.Substring(i + 1, 3);
                            try
                            {
                                var byteValue = Convert.ToByte(octal, 8);
                                byteList.Add(byteValue);
                                i += 4;
                            }
                            catch
                            {
                                break;
                            }
                        }
                        
                        // 将收集的 UTF-8 字节解码为字符串
                        if (byteList.Count > 0)
                        {
                            var decoded = Encoding.UTF8.GetString(byteList.ToArray());
                            bytes.AddRange(Encoding.UTF8.GetBytes(decoded));
                        }
                    }
                    else
                    {
                        // 普通 ASCII 字符
                        bytes.Add((byte)path[i]);
                        i++;
                    }
                }
                
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            catch
            {
                return path;
            }
        }

        private string MapGitStatus(string status)
        {
            return status switch
            {
                "M" or "MM" or " M" => "Modified",
                "A" or "AM" => "Added",
                "D" or " D" => "Deleted",
                "R" => "Renamed",
                "C" => "Copied",
                "??" => "Untracked",
                _ => "Unknown"
            };
        }
    }

    // DTOs

}
