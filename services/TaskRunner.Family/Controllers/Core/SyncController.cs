using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        private readonly SettingsService _settings;
        private readonly TaskManager _taskManager;
        private readonly ILogger<SyncController> _logger;

        public SyncController(
            SettingsService settings,
            TaskManager taskManager,
            ILogger<SyncController> logger)
        {
            _settings = settings;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <summary>
        /// 获取所有笔记的元数据列表（用于移动端同步）
        /// </summary>
        [HttpGet("notes")]
        // 放弃增量参数 since，始终返回全部笔记元数据以支持移动端只做全量同步
        public ActionResult<List<NoteMetadata>> GetNotes([FromQuery] string vaultId)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = "必须指定有效的知识库" });

            var notes = GetNotesInternal(vaultPath, null);
            return Ok(notes);
        }

        /// <summary>
        /// 获取所有任务列表（用于移动端同步）
        /// </summary>
        [HttpGet("tasks")]
        public ActionResult GetTasks()
        {
            var tasks = GetTasksInternal(null);
            return Ok(new { tasks, total = tasks.Count });
        }

        /// <summary>
        /// 完整的同步数据（笔记 + 任务）
        /// </summary>
        [HttpGet]
        // 完整同步：始终返回全量数据，忽略 since 参数
        public ActionResult<SyncResponse> Sync([FromQuery] string vaultId, [FromQuery] long? since = null)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = "必须指定有效的知识库" });

            var notes = GetNotesInternal(vaultPath, null);
            var tasks = GetTasksInternal(null);

            return Ok(new SyncResponse
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Notes = notes,
                Tasks = tasks,
                TotalNotes = notes.Count,
                TotalTasks = tasks.Count
            });
        }

        /// <summary>
        /// 获取系统信息（用于移动端配置）
        /// </summary>
        [HttpGet("system")]
        public ActionResult<SystemInfo> GetSystemInfo([FromQuery] string vaultId)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = "必须指定有效的知识库" });

            return Ok(new SystemInfo
            {
                ServerVersion = "1.0.0",
                VaultPath = vaultPath,
                VaultExists = !string.IsNullOrEmpty(vaultPath) && System.IO.Directory.Exists(vaultPath),
                VaultFileCount = !string.IsNullOrEmpty(vaultPath) && System.IO.Directory.Exists(vaultPath)
                    ? System.IO.Directory.GetFiles(vaultPath, "*.md", System.IO.SearchOption.AllDirectories).Length
                    : 0,
                ServiceUptime = GetUptime(),
                ApiBaseUrl = $"{Request.Scheme}://{Request.Host}",
                SupportedFeatures = new List<string> { "notes", "tasks", "search", "ai", "conflict-resolution" }
            });
        }

        /// <summary>
        /// 更新笔记（带冲突检测）
        /// </summary>
        [HttpPut("notes")]
        public ActionResult<ConflictResolution> UpdateNote([FromQuery] string vaultId, [FromBody] NoteUpdateRequest request)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = "必须指定有效的知识库" });

            if (!System.IO.Directory.Exists(vaultPath))
            {
                return BadRequest(new ConflictResolution
                {
                    Path = request.Path,
                    Status = "error",
                    Message = "知识库路径无效"
                });
            }

            var result = UpdateNoteInternal(vaultPath, request);
            return Ok(result);
        }

        /// <summary>
        /// 批量更新笔记（带冲突检测）
        /// </summary>
        [HttpPut("notes/batch")]
        public ActionResult<List<ConflictResolution>> BatchUpdateNotes([FromQuery] string vaultId, [FromBody] List<NoteUpdateRequest> requests)
        {
            var vaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(vaultPath))
                return BadRequest(new { error = "必须指定有效的知识库" });

            var results = new List<ConflictResolution>();

            foreach (var request in requests)
            {
                var result = UpdateNoteInternal(vaultPath, request);
                results.Add(result);
            }

            return Ok(results);
        }

        /// <summary>
        /// 内部方法：获取笔记列表
        /// </summary>
        private List<NoteMetadata> GetNotesInternal(string vaultPath, long? since = null)
        {
            if (string.IsNullOrEmpty(vaultPath) || !System.IO.Directory.Exists(vaultPath))
            {
                _logger.LogWarning("知识库路径无效：{Path}", vaultPath);
                return new List<NoteMetadata>();
            }

            try
            {
                var notes = new List<NoteMetadata>();
                var files = System.IO.Directory.GetFiles(vaultPath, "*.md", System.IO.SearchOption.AllDirectories);
                
                foreach (var file in files)
                {
                    try
                    {
                        var fileName = System.IO.Path.GetFileName(file);
                        if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var relativePath = file.Substring(vaultPath.Length).TrimStart('/').Replace(".md", "");
                        var title = System.IO.Path.GetFileNameWithoutExtension(file);
                        var modified = System.IO.File.GetLastWriteTime(file);
                        var size = new System.IO.FileInfo(file).Length;
                        var hash = CalculateFileHash(file);

                        // 放弃增量判断：始终返回全部笔记元数据（移动端只做全量同步）

                        notes.Add(new NoteMetadata
                        {
                            Path = relativePath,
                            Title = title,
                            Modified = modified,
                            Size = size,
                            Hash = hash
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "读取文件元数据失败：{File}", file);
                    }
                }

                _logger.LogInformation("返回笔记列表：{Count} 条", notes.Count);
                return notes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取笔记列表失败");
                throw;
            }
        }

        /// <summary>
        /// 内部方法：获取任务列表
        /// </summary>
        private List<TaskInfo> GetTasksInternal(long? since = null)
        {
            try
            {
                var tasks = _taskManager.GetAllTasks();
                
                // 如果指定了 since 时间戳，过滤任务
                if (since.HasValue)
                {
                    var sinceTime = DateTimeOffset.FromUnixTimeSeconds(since.Value).UtcDateTime;
                    tasks = tasks.Where(t => t.CreatedAt.ToUniversalTime() > sinceTime).ToList();
                }

                return tasks;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务列表失败");
                throw;
            }
        }
        
        private TimeSpan GetUptime()
        {
            // 简单的启动时间记录（实际中可以用静态变量记录启动时间）
            // 这里返回一个固定值，实际项目可以改进
            return TimeSpan.FromHours(1);
        }

        private string? ResolveVaultPath(string vaultId)
        {
            if (string.IsNullOrEmpty(vaultId))
                return null;
            return _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
        }

        private ConflictResolution UpdateNoteInternal(string vaultPath, NoteUpdateRequest request)
        {
            try
            {
                var fullPath = System.IO.Path.Combine(vaultPath, request.Path + ".md");
                var directory = System.IO.Path.GetDirectoryName(fullPath);
                
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                // 检查是否存在冲突
                if (System.IO.File.Exists(fullPath))
                {
                    var serverHash = CalculateFileHash(fullPath);
                    var serverModified = System.IO.File.GetLastWriteTime(fullPath);

                    // 如果客户端提供了哈希，进行冲突检测
                    if (!string.IsNullOrEmpty(request.Hash))
                    {
                        if (serverHash != request.Hash)
                        {
                            // 发生冲突
                            return new ConflictResolution
                            {
                                Path = request.Path,
                                Status = "conflict",
                                Message = "文件已被其他设备修改",
                                ServerModified = serverModified,
                                ClientModified = request.Modified
                            };
                        }
                    }
                }

                // 无冲突，更新文件
                System.IO.File.WriteAllText(fullPath, request.Content);
                System.IO.File.SetLastWriteTime(fullPath, request.Modified);

                return new ConflictResolution
                {
                    Path = request.Path,
                    Status = "success",
                    Message = "笔记更新成功"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新笔记失败：{Path}", request.Path);
                return new ConflictResolution
                {
                    Path = request.Path,
                    Status = "error",
                    Message = $"更新失败：{ex.Message}"
                };
            }
        }

        /// <summary>
        /// 计算文件哈希值（用于冲突检测）
        /// </summary>
        private string CalculateFileHash(string filePath)
        {
            using var stream = System.IO.File.OpenRead(filePath);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashBytes = sha256.ComputeHash(stream);
            return Convert.ToBase64String(hashBytes);
        }

    }

    public class NoteMetadata
    {
        public string Path { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime Modified { get; set; }
        public long Size { get; set; }
        public string? Hash { get; set; } // 用于冲突检测的文件哈希
    }

    public class NoteUpdateRequest
    {
        public string Path { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Modified { get; set; }
        public string? Hash { get; set; }
    }

    public class ConflictResolution
    {
        public string Path { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty; // "success", "conflict", "error"
        public string? Message { get; set; }
        public DateTime? ServerModified { get; set; }
        public DateTime? ClientModified { get; set; }
    }

    public class SyncResponse
    {
        public long Timestamp { get; set; }
        public List<NoteMetadata> Notes { get; set; } = new List<NoteMetadata>();
        public List<TaskInfo> Tasks { get; set; } = new List<TaskInfo>();
        public int TotalNotes { get; set; }
        public int TotalTasks { get; set; }
    }

    public class SystemInfo
    {
        public string ServerVersion { get; set; } = string.Empty;
        public string? VaultPath { get; set; }
        public bool VaultExists { get; set; }
        public int VaultFileCount { get; set; }
        public TimeSpan ServiceUptime { get; set; }
        public string ApiBaseUrl { get; set; } = string.Empty;
        public List<string> SupportedFeatures { get; set; } = new List<string>();
    }
}
