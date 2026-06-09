using TaskRunner.Core.Shared;
using TaskRunner.Core.Shared.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskRunner.Data;
using TaskRunner.Services;
using TaskRunner.Services.Strategies;
using TaskRunner.Core.Shared.Security;
using TaskRunner.Contracts.Vaults;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// 验证 Token 请求
    /// </summary>
    public class VerifyTokenRequest
    {
        public string? Token { get; set; }
    }
    /// <summary>
    /// 配对请求
    /// </summary>
    public class PairRequest
    {
        public string? PairCode { get; set; }
        public string? DeviceName { get; set; }
    }

    /// <summary>
    /// 配对响应
    /// </summary>
    public class PairResponse
    {
        public string? RequestId { get; set; }
        public string? AccessToken { get; set; }
        public long ExpiresIn { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
    }

    [ApiController]
    public class PairController : ControllerBase
    {
        private readonly DeviceService _deviceService;
        private readonly ILogger<PairController> _logger;
        private readonly IOneHopService _oneHopService;
        private readonly IPairingStrategy _pairingStrategy;

        public PairController(DeviceService deviceService, ILogger<PairController> logger, IOneHopService oneHopService, IPairingStrategy pairingStrategy)
        {
            _deviceService = deviceService;
            _logger = logger;
            _oneHopService = oneHopService;
            _pairingStrategy = pairingStrategy;
        }

        /// <summary>
        /// 配对端点（移动端使用）
        /// 1. 新设备：提交配对请求，返回 requestId，等待授权
        /// 2. 已授权设备：直接返回 accessToken
        /// </summary>
        [HttpPost("/vault/pair")]
        [HttpPost("/pair")]
        [HttpPost("/mg/pair")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult<PairResponse> Pair([FromBody] PairRequest request)
        {
            if (string.IsNullOrEmpty(request?.PairCode))
            {
                return BadRequest(new { error = "配对码不能为空" });
            }

            // 验证配对码
            if (!_deviceService.ValidatePairCode(request.PairCode))
            {
                return BadRequest(new { error = "配对码错误" });
            }

            var deviceName = request.DeviceName ?? "未知设备";
            var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

            // 检查该设备名称是否已授权
            var existingDevice = _deviceService.GetAuthorizedDeviceByName(deviceName);
            if (existingDevice != null)
            {
                // 设备已授权，直接返回令牌
                _logger.LogInformation("已授权设备重新配对: {DeviceName}", deviceName);
                return Ok(new PairResponse
                {
                    AccessToken = existingDevice.AccessToken,
                    ExpiresIn = 3600 * 24 * 365, // 1年
                    Status = "authorized",
                    Message = "设备已授权"
                });
            }

            // 通过策略执行配对（cloud 自动授权 / family 提交审批）
            return _pairingStrategy.Pair(deviceName, ipAddress, request.PairCode);
        }

        /// <summary>
        /// 获取当前配对码及服务器设备ID（用于移动端验证服务器身份）
        /// </summary>
        [HttpGet("/vault/pair/code")]
        [HttpGet("/pair/code")]
        [HttpGet("/mg/pair/code")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult<object> GetPairCode()
        {
            var code = _deviceService.GetPairCode();
            return Ok(new { pairCode = code, deviceId = _oneHopService.DeviceId });
        }

        /// <summary>
        /// 刷新配对码（生成新的随机配对码）
        /// </summary>
        [HttpPost("/vault/pair/code/refresh")]
        [HttpPost("/pair/code/refresh")]
        [HttpPost("/mg/pair/code/refresh")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("pair")]
        public ActionResult<object> RefreshPairCode()
        {
            var newCode = _deviceService.RefreshPairCode();
            _logger.LogInformation("配对码已通过 API 刷新");
            return Ok(new { pairCode = newCode, message = "配对码已刷新" });
        }
    }

    [ApiController]
    [Route("vault")]
    [Route("api")]
    [Route("mg")]
    public class VaultController : ControllerBase
    {
        private readonly Services.SettingsService _settings;
        private readonly DeviceService _deviceService;
        private readonly PairingService? _pairingService;
        private readonly ILogger<VaultController> _logger;
        private readonly ISyncAuthorizationStrategy _syncAuthStrategy;
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly RequestSignatureService _signatureService;

        // 支持的文件扩展名
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md",
            ".json",  // Anki 卡片文件
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico"
        };

        // 排除的目录
        private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".obsidian", ".trash", "node_modules", ".DS_Store"
        };

        /// <summary>
        /// 根据vaultId解析知识库路径，不修改全局活跃状态。
        /// 不再回退到当前活跃知识库，必须显式指定 vaultId。
        /// </summary>
        private string? ResolveVaultPath(string? vaultId)
        {
            if (string.IsNullOrEmpty(vaultId))
            {
                return null;
            }

            var targetVault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
            if (targetVault != null && !string.IsNullOrEmpty(targetVault.Path))
            {
                return targetVault.Path;
            }

            _logger.LogWarning("指定的知识库不存在或路径为空：{VaultId}", vaultId);
            return null;
        }

        public VaultController(
            Services.SettingsService settings,
            DeviceService deviceService,
            ILogger<VaultController> logger,
            ISyncAuthorizationStrategy syncAuthStrategy,
            IDbContextFactory<AppDbContext> dbContextFactory,
            RequestSignatureService signatureService,
            PairingService? pairingService = null)
        {
            _settings = settings;
            _deviceService = deviceService;
            _logger = logger;
            _syncAuthStrategy = syncAuthStrategy;
            _dbContextFactory = dbContextFactory;
            _signatureService = signatureService;
            _pairingService = pairingService;
        }

        /// <summary>
        /// 获取所有知识库列表
        /// </summary>
        [HttpGet("vaults")]
        public ActionResult<IEnumerable<object>> GetVaults()
        {
            var vaults = _settings.GetVaults();

            var result = vaults.Select(v => new
            {
                id = v.Id,
                name = v.Name,
                path = v.Path,
                industry = v.Industry,
                source = v.Source
            });

            _logger.LogDebug("返回知识库列表，共 {Count} 个", vaults.Count);
            return Ok(result);
        }

        /// <summary>
        /// 验证请求的设备是否已授权（支持新旧两种 Token 验证方式）
        /// </summary>
        private bool ValidateDeviceAuthorization()
        {
            var authHeader = Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                return false;
            }

            var token = authHeader.Substring("Bearer ".Length).Trim();
            
            // 优先使用新的 PairingService 验证（支持 Token 过期检查）
            // 使用DeviceService验证
            return _deviceService.ValidateAccessToken(token);
        }

        /// <summary>
        /// 验证访问令牌（移动端 API）
        /// </summary>
        [HttpPost("verify-token")]
        public ActionResult<object> VerifyToken([FromBody] VerifyTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.Token))
            {
                return BadRequest(new { valid = false, error = "Token 不能为空" });
            }

            var isValid = _deviceService.ValidateAccessToken(request.Token);

            if (!isValid)
            {
                return Ok(new { valid = false, error = "Token 无效或已过期" });
            }

            return Ok(new { valid = true, deviceId = "" });
        }

        /// <summary>
        /// 获取知识库清单（增量同步）
        /// cloud 模式：HMAC签名 + deviceId + 配额/频率检查
        /// 家庭版/本地模式：仍需 Bearer Token 验证
        /// </summary>
        [HttpGet("manifest")]
        public ActionResult<VaultManifestResponse> GetManifest([FromQuery] string vaultId, [FromQuery] string? deviceId = null)
        {
            var authResult = _syncAuthStrategy.ValidateManifest(HttpContext, vaultId, deviceId);
            if (authResult != null)
            {
                return authResult;
            }

            var targetVault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
            var baseVaultPath = targetVault?.Path;
            _logger.LogDebug("GetManifest called. VaultPath={VaultPath}, VaultId={VaultId}", baseVaultPath, vaultId);

            if (string.IsNullOrEmpty(baseVaultPath))
            {
                return NotFound(new { error = "知识库不存在或已被删除" });
            }

            if (!System.IO.Directory.Exists(baseVaultPath))
            {
                _logger.LogError("知识库路径无效：{Path}，数据库记录存在但物理目录已丢失", baseVaultPath);
                return StatusCode(410, new { error = "知识库数据不一致：物理目录已丢失", vaultId });
            }

            try
            {
                var files = new List<ManifestFile>();
                long maxMtime = 0;

                // 同步 notes/ 目录
                var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
                if (System.IO.Directory.Exists(notesPath))
                {
                    ScanDirectory(notesPath, notesPath, files, ref maxMtime, "");
                }

                // 同步 cards/ 目录
                var cardsPath = System.IO.Path.Combine(baseVaultPath, "cards");
                if (System.IO.Directory.Exists(cardsPath))
                {
                    ScanDirectory(cardsPath, cardsPath, files, ref maxMtime, "cards/");
                }

                // 回退：如果 notes/ 和 cards/ 都不存在，扫描根目录下的直接文件
                if (files.Count == 0 && !System.IO.Directory.Exists(notesPath) && !System.IO.Directory.Exists(cardsPath))
                {
                    ScanDirectory(baseVaultPath, baseVaultPath, files, ref maxMtime, "");
                }

                var cursor = maxMtime;

                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var syncDeviceId = !string.IsNullOrWhiteSpace(deviceId) ? deviceId : "ip-" + ipAddress.GetHashCode().ToString("x");
                var syncDeviceName = !string.IsNullOrWhiteSpace(deviceId) ? deviceId : "移动端(" + ipAddress + ")";
                _deviceService.RecordSyncActivity(syncDeviceId, syncDeviceName, vaultId, files.Count, "manifest", ipAddress);

                _logger.LogInformation("返回全量清单：{Count} 个文件，cursor={Cursor}, vaultId={VaultId}", files.Count, cursor, vaultId);

                return Ok(new VaultManifestResponse
                {
                    VaultId = vaultId,
                    VaultName = targetVault?.Name ?? "指定知识库",
                    Cursor = cursor,
                    Files = files
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取清单失败");
                return StatusCode(500, new { error = "获取失败", message = ex.Message });
            }
        }

        private void ScanDirectory(string rootPath, string currentPath, List<ManifestFile> files, ref long maxMtime, string pathPrefix = "")
        {
            foreach (var dir in System.IO.Directory.GetDirectories(currentPath))
            {
                var dirName = System.IO.Path.GetFileName(dir);
                if (ExcludedDirs.Contains(dirName))
                {
                    _logger.LogDebug("ScanDirectory 跳过排除目录: {DirName}", dirName);
                    continue;
                }
                ScanDirectory(rootPath, dir, files, ref maxMtime, pathPrefix);
            }

            foreach (var file in System.IO.Directory.GetFiles(currentPath))
            {
                var ext = System.IO.Path.GetExtension(file);
                if (!AllowedExtensions.Contains(ext))
                {
                    _logger.LogDebug("ScanDirectory 跳过不支持的文件类型: {File} ({Ext})", file, ext);
                    continue;
                }

                var relativePath = pathPrefix + file.Substring(rootPath.Length).TrimStart('/', '\\');
                relativePath = relativePath.Replace('\\', '/').TrimStart('/');
                var modified = System.IO.File.GetLastWriteTime(file);
                var modifiedUnix = new DateTimeOffset(modified).ToUnixTimeSeconds();
                
                if (modifiedUnix > maxMtime)
                {
                    maxMtime = modifiedUnix;
                }
                
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    _logger.LogWarning("ScanDirectory 计算出空的相对路径: {File}", file);
                    continue;
                }

                var fileInfo = new System.IO.FileInfo(file);
                if (fileInfo.Length == 0)
                {
                    _logger.LogWarning("ScanDirectory 跳过空文件: {File}", file);
                    continue;
                }

                files.Add(new ManifestFile
                {
                    RelPath = relativePath,
                    Op = "upsert",
                    Mtime = modifiedUnix,
                    Size = fileInfo.Length,
                    Sha256 = modifiedUnix.ToString()
                });
            }
        }

        /// <summary>
        /// 获取文件内容
        /// cloud 模式：HMAC签名 + deviceId（不额外扣配额，manifest已控制）
        /// 家庭版/本地模式：仍需 Bearer Token 验证
        /// </summary>
        [HttpGet("file")]
        public IActionResult GetFile([FromQuery] string path, [FromQuery] string vaultId, [FromQuery] string? deviceId = null)
        {
            var authResult = _syncAuthStrategy.ValidateFile(HttpContext, vaultId, deviceId);
            if (authResult != null)
            {
                return authResult;
            }

            _logger.LogInformation("GetFile请求: path={Path}, vaultId={VaultId}", path, vaultId);
            
            if (string.IsNullOrEmpty(path))
            {
                return BadRequest(new { error = "路径不能为空" });
            }

            var baseVaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(baseVaultPath))
            {
                return BadRequest(new { error = "必须指定有效的知识库" });
            }

            try
            {
                // 路径安全检查：阻止目录遍历
                path = path.Replace("\\", "/").TrimStart('/');
                if (path.Contains(".."))
                {
                    _logger.LogWarning("检测到目录遍历尝试: {Path}", path);
                    return BadRequest(new { error = "非法路径" });
                }

                var ext = System.IO.Path.GetExtension(path);
                if (!AllowedExtensions.Contains(ext))
                {
                    return BadRequest(new { error = $"不支持的文件类型: {ext}" });
                }

                string filePath;
                if (path.StartsWith("cards/"))
                {
                    filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseVaultPath, path));
                }
                else if (path.StartsWith("notes/"))
                {
                    filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseVaultPath, path));
                }
                else
                {
                    var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
                    filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(notesPath, path));
                }

                // 确保文件路径在知识库目录内（防止路径遍历）
                var baseFullPath = System.IO.Path.GetFullPath(baseVaultPath);
                if (!filePath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("路径遍历被阻止: {FilePath} 不在 {BasePath} 内", filePath, baseFullPath);
                    return BadRequest(new { error = "非法路径" });
                }
                
                if (!System.IO.File.Exists(filePath))
                {
                    _logger.LogWarning("文件不存在：{Path}", path);
                    return NotFound();
                }

                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var content = System.IO.File.ReadAllText(filePath);
                    return Ok(content);
                }
                
                if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = System.IO.File.ReadAllBytes(filePath);
                    var mimeType = GetMimeType(ext);
                    return File(bytes, mimeType);
                }

                var mdContent = System.IO.File.ReadAllText(filePath);
                return Ok(mdContent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取文件失败：{Path}", path);
                return StatusCode(500, new { error = "读取失败", message = ex.Message });
            }
        }

        private string GetMimeType(string ext)
        {
            return ext.ToLower() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".bmp" => "image/bmp",
                ".ico" => "image/x-icon",
                ".json" => "application/json",
                _ => "application/octet-stream"
            };
        }

        /// <summary>
        /// 浏览知识库目录结构（WebUI 使用）
        /// </summary>
        [HttpGet("vaults/{vaultId}/browse")]
        public ActionResult<VaultBrowseResponse> BrowseVault(string vaultId, [FromQuery] string? path = "")
        {
            var baseVaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(baseVaultPath))
            {
                return NotFound(new { error = "知识库不存在" });
            }

            // 使用 notes/ 子目录作为知识库内容根目录
            var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
            var effectiveRoot = System.IO.Directory.Exists(notesPath) ? notesPath : baseVaultPath;

            var targetPath = string.IsNullOrEmpty(path)
                ? effectiveRoot
                : System.IO.Path.Combine(effectiveRoot, path.Trim('/').Replace('/', System.IO.Path.DirectorySeparatorChar));

            var fullRootPath = System.IO.Path.GetFullPath(effectiveRoot);
            var fullTargetPath = System.IO.Path.GetFullPath(targetPath);
            if (!fullTargetPath.StartsWith(fullRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { error = "非法路径" });
            }

            if (!System.IO.Directory.Exists(targetPath))
            {
                return NotFound(new { error = "目录不存在" });
            }

            var items = new List<VaultBrowseItem>();

            foreach (var dir in System.IO.Directory.GetDirectories(targetPath))
            {
                var dirName = System.IO.Path.GetFileName(dir);
                if (ExcludedDirs.Contains(dirName)) continue;
                var relativePath = dir.Substring(fullRootPath.Length).TrimStart('/', '\\').Replace('\\', '/');
                items.Add(new VaultBrowseItem
                {
                    Name = dirName,
                    Path = relativePath,
                    IsDirectory = true,
                    Modified = System.IO.Directory.GetLastWriteTime(dir)
                });
            }

            foreach (var file in System.IO.Directory.GetFiles(targetPath, "*.md"))
            {
                var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                var relativePath = file.Substring(fullRootPath.Length).TrimStart('/', '\\').Replace('\\', '/');
                var fileInfo = new System.IO.FileInfo(file);
                items.Add(new VaultBrowseItem
                {
                    Name = fileName,
                    Path = relativePath[..^3],
                    IsDirectory = false,
                    Size = fileInfo.Length,
                    Modified = fileInfo.LastWriteTime
                });
            }

            items = items.OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name).ToList();

            return Ok(new VaultBrowseResponse
            {
                VaultId = vaultId,
                VaultName = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Name ?? "",
                CurrentPath = path ?? "",
                Items = items
            });
        }

        /// <summary>
        /// 读取笔记内容（WebUI 使用）
        /// </summary>
        [HttpGet("read/{*path}")]
        public ActionResult<VaultNote> ReadNote(string path, [FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return BadRequest(new { error = "路径不能为空" });
            }

            var baseVaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(baseVaultPath))
            {
                return BadRequest(new { error = "必须指定有效的知识库" });
            }

            try
            {
                path = path.TrimEnd('/', '\\');
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    path = path[..^3];
                }

                var notesPath = System.IO.Path.Combine(baseVaultPath, "notes");
                var filePath = System.IO.Path.Combine(notesPath, path + ".md");
                
                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { error = $"笔记不存在：{path}" });
                }

                var content = System.IO.File.ReadAllText(filePath);
                var title = System.IO.Path.GetFileNameWithoutExtension(path);
                var modified = System.IO.File.GetLastWriteTime(filePath);
                var tags = ExtractTags(content);

                return Ok(new VaultNote
                {
                    Path = path,
                    Title = title,
                    Content = content,
                    Modified = modified,
                    Tags = tags
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取笔记失败：{Path}", path);
                return StatusCode(500, new { error = "读取失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 写入笔记内容（WebUI 编辑用）。
        /// 统一写入 notes/ 子目录；兼容传入带 notes/ 前缀的路径。
        /// </summary>
        [HttpPost("write/{*path}")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10MB 限制，防止 DoS
        public async Task<IActionResult> WriteNote(string path, [FromQuery] string vaultId, [FromBody] WriteNoteRequest request)
        {
            if (string.IsNullOrWhiteSpace(path))
                return BadRequest(new { error = "路径不能为空" });
            if (request == null || request.Content == null)
                return BadRequest(new { error = "内容不能为空" });

            var baseVaultPath = ResolveVaultPath(vaultId);
            if (string.IsNullOrEmpty(baseVaultPath))
                return BadRequest(new { error = "必须指定有效的知识库" });

            try
            {
                path = path.TrimEnd('/', '\\');
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    path = path[..^3];

                // 路径安全检查：阻止目录遍历
                path = path.Replace("\\", "/");
                if (path.Contains(".."))
                {
                    _logger.LogWarning("写入操作检测到目录遍历尝试: {Path}", path);
                    return BadRequest(new { error = "非法路径" });
                }

                var notesRoot = System.IO.Path.Combine(baseVaultPath, "notes");
                var filePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(notesRoot, path + ".md"));
                var baseFullPath = System.IO.Path.GetFullPath(baseVaultPath);

                // 确保文件路径在知识库目录内（防止路径遍历）
                if (!filePath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("写入路径遍历被阻止: {FilePath} 不在 {BasePath} 内", filePath, baseFullPath);
                    return BadRequest(new { error = "非法路径" });
                }

                var dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                await System.IO.File.WriteAllTextAsync(filePath, request.Content);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入笔记失败：{Path}", path);
                return StatusCode(500, new { error = "写入失败", message = ex.Message });
            }
        }

        private List<string> ExtractTags(string content)
        {
            var tags = new List<string>();
            
            if (content.StartsWith("---"))
            {
                var endIndex = content.IndexOf("---", 3);
                if (endIndex > 0)
                {
                    var frontmatter = content.Substring(0, endIndex);
                    var lines = frontmatter.Split('\n');
                    
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("tags:"))
                        {
                            var tagPart = line.Substring(5).Trim();
                            if (tagPart.StartsWith("["))
                            {
                                var tagStr = tagPart.Trim('[', ']', ' ');
                                if (!string.IsNullOrWhiteSpace(tagStr))
                                {
                                    tags.AddRange(tagStr.Split(',').Select(t => t.Trim().Trim('"', '\'')));
                                }
                            }
                        }
                    }
                }
            }

            return tags.Take(10).ToList();
        }

        /// <summary>
        /// 获取知识库卡片列表（MobileGateway 兼容，供移动端同步）
        /// </summary>
        [HttpGet("cards")]
        public ActionResult<object> GetCards([FromQuery] string vaultId)
        {
            // 移动端 API 统一使用 HMAC 签名验证（在 Program.cs 中间件中完成）
            // 不再额外要求 Bearer Token，与 GetManifest/GetFile 保持一致
            var targetVault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
            if (targetVault == null || string.IsNullOrEmpty(targetVault.Path))
            {
                return NotFound(new { error = "知识库不存在" });
            }

            var cardsPath = System.IO.Path.Combine(targetVault.Path, "cards");
            if (!System.IO.Directory.Exists(cardsPath))
            {
                return Ok(new { vaultId, count = 0, cards = new List<object>() });
            }

            var cards = new List<object>();
            var files = System.IO.Directory.GetFiles(cardsPath, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var cardsArray = JsonSerializer.Deserialize<List<MobileCardItem>>(json);
                    if (cardsArray != null)
                    {
                        foreach (var card in cardsArray)
                        {
                            cards.Add(new
                            {
                                front = card.Front,
                                back = card.Back,
                                deck = card.Deck,
                                tags = string.Join(",", card.Tags),
                                source = card.Source,
                                notePath = System.IO.Path.GetFileName(file)
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "解析卡片文件失败：{File}", file);
                }
            }

            return Ok(new { vaultId, count = cards.Count, cards });
        }

        /// <summary>
        /// 获取移动端认证配置（Family 版返回实际 sharedSecret，供自动发现流程使用）
        /// </summary>
        [HttpPost("auth/config")]
        public ActionResult<object> GetMobileAuthConfig()
        {
            return Ok(new { sharedSecret = _signatureService.GetSharedSecret() });
        }

        /// <summary>
        /// 获取知识库笔记数量
        /// </summary>
        [HttpGet("note-count")]
        public ActionResult<int> GetNoteCount([FromQuery] string vaultId)
        {
            var vault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
            if (vault == null)
            {
                return NotFound(new { error = "知识库不存在", vaultId });
            }
            if (string.IsNullOrEmpty(vault.Path))
            {
                return StatusCode(500, new { error = "知识库路径为空", vaultId });
            }

            var notesPath = System.IO.Path.Combine(vault.Path, "notes");
            if (!System.IO.Directory.Exists(notesPath))
            {
                _logger.LogWarning("知识库 notes 目录不存在：{Path}", notesPath);
                return Ok(0);
            }

            var files = System.IO.Directory.GetFiles(notesPath, "*.md", System.IO.SearchOption.AllDirectories);
            return Ok(files.Length);
        }

        /// <summary>
        /// 移动端推送 AI 生成的知识库（接收来自手机端的 DeepSeek 生成内容）
        /// </summary>
        [HttpPost("/mobile-vaults/push")]
        public async Task<ActionResult> PushMobileVault([FromBody] MobileVaultPushRequest request)
        {
            _logger.LogInformation("[PushMobileVault] Received from {RemoteIP}, VaultName={VaultName}, Industry={Industry}, NotesCount={NotesCount}",
                HttpContext.Connection.RemoteIpAddress, request.VaultName, request.Industry, request.Notes?.Count ?? 0);

            if (string.IsNullOrWhiteSpace(request.VaultName) || request.Notes == null || request.Notes.Count == 0)
            {
                return BadRequest(new { error = "知识库名称和笔记列表不能为空" });
            }

            try
            {
                var vaultRoot = _settings.VaultRootPathPreference;
                var mobileDir = Path.Combine(vaultRoot, "mobile");
                var industry = string.IsNullOrWhiteSpace(request.Industry) ? "移动端生成" : request.Industry.Trim();
                var safeVaultName = VaultNameResolver.ToSafeDirectoryName(request.VaultName.Trim());
                var industryDir = Path.Combine(mobileDir, industry);
                Directory.CreateDirectory(industryDir);

                using var dbContext = _dbContextFactory.CreateDbContext();

                // 查找是否已有同名同行业的 mobile 知识库
                var existingVault = dbContext.Vaults
                    .FirstOrDefault(v => !v.IsDeleted
                        && v.Source == "mobile"
                        && v.Industry == industry
                        && v.Name == request.VaultName.Trim());

                string vaultId;
                string vaultDir;
                bool isNewVault = false;
                bool migrated = false;

                if (existingVault != null)
                {
                    vaultId = existingVault.VaultId;

                    // 检查现有路径是否符合新的三级结构 mobile/{行业}/{名称}/
                    var expectedPath = Path.Combine(industryDir, safeVaultName);
                    var isOldGuidStructure = !existingVault.Path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
                        && !existingVault.Path.StartsWith(expectedPath + "_", StringComparison.OrdinalIgnoreCase);

                    if (isOldGuidStructure && Directory.Exists(existingVault.Path))
                    {
                        // 旧 GUID 结构需要迁移到三级目录结构
                        _logger.LogWarning("移动端知识库路径结构过时: {OldPath}，迁移到: {NewPath}",
                            existingVault.Path, expectedPath);

                        if (Directory.Exists(expectedPath))
                        {
                            // 目标目录已存在（不太可能，但防御）
                            vaultDir = VaultNameResolver.GetUniqueDirectoryPath(industryDir, safeVaultName);
                        }
                        else
                        {
                            vaultDir = expectedPath;
                        }

                        Directory.Move(existingVault.Path, vaultDir);
                        existingVault.Path = vaultDir;
                        migrated = true;
                        _logger.LogInformation("知识库路径迁移完成: {VaultId} -> {NewPath}", vaultId, vaultDir);
                    }
                    else if (Directory.Exists(existingVault.Path))
                    {
                        vaultDir = existingVault.Path;
                    }
                    else
                    {
                        // 数据库记录存在但物理目录已丢失，报错而不是静默创建新的
                        _logger.LogError("知识库数据库记录存在但物理目录丢失: {VaultId} {Path}",
                            existingVault.VaultId, existingVault.Path);
                        return StatusCode(500, new { error = "知识库数据不一致：数据库记录存在但物理目录已丢失，请联系管理员" });
                    }

                    _logger.LogInformation("复用已有移动端知识库: {VaultId} {VaultName}{MigrationNote}，追加笔记",
                        vaultId, request.VaultName, migrated ? "（已迁移路径）" : "");
                }
                else
                {
                    vaultId = Guid.NewGuid().ToString("N");
                    vaultDir = VaultNameResolver.GetUniqueDirectoryPath(industryDir, safeVaultName);
                    isNewVault = true;
                }

                var notesDir = Path.Combine(vaultDir, "notes");
                Directory.CreateDirectory(notesDir);

                // 写入笔记文件
                foreach (var note in request.Notes)
                {
                    var safeRelPath = string.IsNullOrWhiteSpace(note.RelPath)
                        ? $"{note.Title}.md"
                        : note.RelPath;
                    // 防止路径穿越：拒绝包含 .. 的路径
                    if (safeRelPath.Contains(".."))
                    {
                        _logger.LogWarning("检测到路径穿越尝试，已拒绝: {RelPath}", safeRelPath);
                        return BadRequest(new { error = $"非法文件路径: {safeRelPath}" });
                    }
                    safeRelPath = safeRelPath.TrimStart('/', '\\');
                    var notePath = Path.Combine(notesDir, safeRelPath);
                    var noteDir = Path.GetDirectoryName(notePath);
                    if (!string.IsNullOrEmpty(noteDir))
                    {
                        Directory.CreateDirectory(noteDir);
                    }
                    await System.IO.File.WriteAllTextAsync(notePath, note.Content ?? "");
                }

                // 注册到数据库（仅当是新知识库时）
                if (isNewVault)
                {
                    dbContext.Vaults.Add(new Data.Entities.Vault
                    {
                        VaultId = vaultId,
                        Name = request.VaultName.Trim(),
                        Path = vaultDir,
                        IsActive = true,
                        Industry = industry,
                        Source = "mobile",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                    await dbContext.SaveChangesAsync();
                }
                else if (migrated)
                {
                    existingVault!.UpdatedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                }

                _logger.LogInformation("移动端知识库推送成功: {VaultId} {VaultName}，共 {NoteCount} 条笔记",
                    vaultId, request.VaultName, request.Notes.Count);

                return Ok(new { success = true, vaultId, message = migrated ? "知识库推送成功（已迁移路径结构）" : "知识库推送成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移动端知识库推送失败: {VaultName}", request.VaultName);
                return StatusCode(500, new { error = $"推送失败: {ex.Message}" });
            }
        }

        private class MobileCardItem
        {
            public string Front { get; set; } = "";
            public string Back { get; set; } = "";
            public string Deck { get; set; } = "";
            public List<string> Tags { get; set; } = new();
            public string Source { get; set; } = "";
        }
    }

    // 响应模型
    public class VaultManifestResponse
    {
        [JsonPropertyName("cursor")]
        public long Cursor { get; set; }

        [JsonPropertyName("vaultId")]
        public string? VaultId { get; set; }
        
        [JsonPropertyName("vaultName")]
        public string? VaultName { get; set; }
        
        [JsonPropertyName("files")]
        public List<ManifestFile>? Files { get; set; }
    }

    public class ManifestFile
    {
        [JsonPropertyName("relPath")]
        public string? RelPath { get; set; }
        
        [JsonPropertyName("op")]
        public string? Op { get; set; }
        
        [JsonPropertyName("mtime")]
        public long? Mtime { get; set; }
        
        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }

    public class VaultNote
    {
        public string Path { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Modified { get; set; }
        public List<string>? Tags { get; set; }
    }

    public class WriteNoteRequest
    {
        public string? Content { get; set; }
    }
}
