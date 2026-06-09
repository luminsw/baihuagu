using TaskRunner.Core.Shared.Notifications;
using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;
using TaskRunner.Contracts.Vaults;

namespace TaskRunner.Controllers
{
    [ApiController]
    [Route("api/settings")]
    public class VaultSettingsController : ControllerBase
    {
        private readonly VaultSettingsService _vaultSettings;
        private readonly WebUINotificationService _webUINotification;
        private readonly ILogger<VaultSettingsController> _logger;

        public VaultSettingsController(VaultSettingsService vaultSettings, WebUINotificationService webUINotification, ILogger<VaultSettingsController> logger)
        {
            _vaultSettings = vaultSettings;
            _webUINotification = webUINotification;
            _logger = logger;
        }

        [HttpGet("vault-root")]
        public ActionResult<VaultRootResponse> GetVaultRoot()
        {
            return Ok(new VaultRootResponse { VaultPath = _vaultSettings.VaultPath });
        }

        [HttpPost("vault-root")]
        public IActionResult SetVaultRoot([FromBody] VaultRootRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "请求不能为空" });

            var next = string.IsNullOrWhiteSpace(request.VaultPath) ? "" : request.VaultPath.Trim();
            if (string.IsNullOrWhiteSpace(next))
                return BadRequest(new { error = "VaultPath 不能为空" });

            // 不做强校验：允许先配置，再由具体写入逻辑创建目录
            _vaultSettings.SetVaultPath(next);

            // 通知 WebUI 刷新全局状态
            _ = _webUINotification.NotifyVaultStatusChangedAsync();

            _logger.LogInformation("Runtime VaultPath updated: {Path}", next);

            return Ok(new { success = true });
        }

        #region 知识库根路径偏好

        /// <summary>
        /// 获取知识库根路径偏好（新建知识库时的默认父目录）
        /// </summary>
        [HttpGet("vault-root-path-preference")]
        public ActionResult<VaultRootPathPreferenceResponse> GetVaultRootPathPreference()
        {
            return Ok(new VaultRootPathPreferenceResponse
            {
                VaultRootPath = _vaultSettings.VaultRootPathPreference
            });
        }


        /// <summary>
        /// 修复知识库路径：为路径不存在的知识库创建目录，或尝试跨平台路径映射（如 WSL）
        /// </summary>
        [HttpPost("vaults/fix-paths")]
        public IActionResult FixVaultPaths()
        {
            var vaults = _vaultSettings.GetVaults();
            var fixedPaths = new List<string>();
            var migratedPaths = new List<string>();

            foreach (var vault in vaults)
            {
                if (string.IsNullOrWhiteSpace(vault.Path)) continue;
                if (Directory.Exists(vault.Path)) continue;

                // 跨平台路径修复：Windows 路径在 Linux/macOS 上尝试映射为 WSL 路径
                var originalPath = vault.Path;
                var isWindowsPath = System.Text.RegularExpressions.Regex.IsMatch(originalPath, @"^[A-Za-z]:\\");
                if (isWindowsPath && !OperatingSystem.IsWindows())
                {
                    var drive = originalPath[0].ToString().ToLowerInvariant();
                    var wslPath = "/mnt/" + drive + "/" + originalPath[3..].Replace('\\', '/');
                    if (Directory.Exists(wslPath))
                    {
                        if (_vaultSettings.UpdateVaultPath(vault.Id, wslPath))
                        {
                            migratedPaths.Add($"{vault.Name}: {originalPath} -> {wslPath}");
                            _logger.LogInformation("知识库路径跨平台迁移: {Name} {Original} -> {Wsl}", vault.Name, originalPath, wslPath);
                            continue;
                        }
                    }
                }

                // 尝试创建目录
                try
                {
                    Directory.CreateDirectory(vault.Path);
                    fixedPaths.Add(vault.Name);
                    _logger.LogInformation("修复知识库路径: {Name} -> {Path}", vault.Name, vault.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "无法创建知识库目录: {Name} -> {Path}", vault.Name, vault.Path);
                }
            }

            var totalFixed = fixedPaths.Count + migratedPaths.Count;
            if (totalFixed > 0)
            {
                _ = _webUINotification.NotifyVaultStatusChangedAsync();
            }

            return Ok(new
            {
                success = true,
                fixedCount = fixedPaths.Count,
                migratedCount = migratedPaths.Count,
                fixedVaults = fixedPaths,
                migratedVaults = migratedPaths
            });
        }

        #endregion

        /// <summary>
        /// 手动同步知识库：扫描根目录，与数据库比对，补录新目录、清理已删除目录的记录
        /// </summary>
        [HttpPost("vaults/sync")]
        public IActionResult SyncVaults()
        {
            var rootPath = _vaultSettings.VaultRootPathPreference;
            if (string.IsNullOrWhiteSpace(rootPath))
                return BadRequest(new { error = "知识库根路径未设置" });

            if (!Directory.Exists(rootPath))
                return BadRequest(new { error = $"根路径不存在: {rootPath}" });

            var (added, removed) = _vaultSettings.SyncVaultsWithFilesystem(rootPath);
            if (added > 0 || removed > 0)
            {
                _ = _webUINotification.NotifyVaultStatusChangedAsync();
            }

            return Ok(new { success = true, added, removed });
        }

        #region 多知识库管理 API

        /// <summary>
        /// 获取所有知识库
        /// </summary>
        [HttpGet("vaults")]
        public ActionResult<VaultsResponse> GetVaults()
        {
            var vaults = _vaultSettings.GetVaults();

            return Ok(new VaultsResponse
            {
                Vaults = vaults.Select(v => new VaultConfig
                {
                    Id = v.Id,
                    Name = v.Name,
                    Path = v.Path,
                    CreatedAt = v.CreatedAt,
                    Tags = v.Tags,
                    Industry = v.Industry,
                    Source = v.Source
                }).ToList()
            });
        }

        /// <summary>
        /// 添加新知识库
        /// </summary>
        [HttpPost("vaults")]
        public IActionResult AddVault([FromBody] AddVaultRequest request)
        {
            if (request == null)
                return BadRequest(new { error = "请求不能为空" });

            if (string.IsNullOrWhiteSpace(request.Name))
                return BadRequest(new { error = "知识库名称不能为空" });

            if (string.IsNullOrWhiteSpace(request.Path))
                return BadRequest(new { error = "知识库路径不能为空" });

            var industry = string.IsNullOrWhiteSpace(request.Industry) ? "笔记" : request.Industry.Trim();
            var vault = _vaultSettings.AddVault(request.Name.Trim(), request.Path.Trim(), industry);

            // 如果路径不存在，自动创建目录
            if (!Directory.Exists(request.Path.Trim()))
            {
                try
                {
                    Directory.CreateDirectory(request.Path.Trim());
                    _logger.LogInformation("创建知识库目录: {Path}", request.Path.Trim());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "无法创建知识库目录: {Path}", request.Path.Trim());
                }
            }

            // 通知 WebUI 刷新全局状态
            _ = _webUINotification.NotifyVaultStatusChangedAsync();

            _logger.LogInformation("添加知识库: {Name} ({Id}) at {Path} 行业={Industry}", vault.Name, vault.Id, vault.Path, industry);

            return Ok(new VaultConfig
            {
                Id = vault.Id,
                Name = vault.Name,
                Path = vault.Path,
                CreatedAt = vault.CreatedAt,
                Industry = industry
            });
        }

        /// <summary>
        /// 更新知识库（名称、付费状态）
        /// </summary>
        [HttpPut("vaults/{vaultId}")]
        public IActionResult UpdateVault(string vaultId, [FromBody] UpdateVaultRequest request)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = "知识库 ID 不能为空" });

            if (request == null)
                return BadRequest(new { error = "请求不能为空" });

            // 更新名称
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                var success = _vaultSettings.UpdateVaultName(vaultId, request.Name.Trim());
                if (!success)
                    return NotFound(new { error = "知识库不存在" });
                _logger.LogInformation("更新知识库名称: {VaultId} -> {Name}", vaultId, request.Name);
            }

            // 更新付费状态
            if (request.IsPaid.HasValue)
            {
                var success = _vaultSettings.UpdateVaultPaid(vaultId, request.IsPaid.Value);
                if (!success)
                    return NotFound(new { error = "知识库不存在" });
                _logger.LogInformation("更新知识库付费状态: {VaultId} -> {IsPaid}", vaultId, request.IsPaid.Value);
            }

            // 更新标签
            if (request.Tags != null)
            {
                var success = _vaultSettings.UpdateVaultTags(vaultId, request.Tags.Trim());
                if (!success)
                    return NotFound(new { error = "知识库不存在" });
                _logger.LogInformation("更新知识库标签: {VaultId} -> {Tags}", vaultId, request.Tags);
            }

            // 更新行业
            if (!string.IsNullOrWhiteSpace(request.Industry))
            {
                var success = _vaultSettings.UpdateVaultIndustry(vaultId, request.Industry.Trim());
                if (!success)
                    return NotFound(new { error = "知识库不存在" });
                _logger.LogInformation("更新知识库行业: {VaultId} -> {Industry}", vaultId, request.Industry);
            }

            return Ok(new { success = true });
        }

        /// <summary>
        /// 删除知识库
        /// </summary>
        [HttpDelete("vaults/{vaultId}")]
        public IActionResult RemoveVault(string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = "知识库 ID 不能为空" });

            var success = _vaultSettings.RemoveVault(vaultId);
            if (!success)
                return NotFound(new { error = "知识库不存在" });

            // 通知 WebUI 刷新全局状态
            _ = _webUINotification.NotifyVaultStatusChangedAsync();

            _logger.LogInformation("删除知识库: {VaultId}", vaultId);
            return Ok(new { success = true });
        }

        /// <summary>
        /// 获取回收站中的知识库列表
        /// </summary>
        [HttpGet("vaults/trash")]
        public ActionResult<VaultsResponse> GetTrashVaults()
        {
            var vaults = _vaultSettings.GetTrashVaults();
            return Ok(new VaultsResponse
            {
                Vaults = vaults.Select(v => new VaultConfig
                {
                    Id = v.Id,
                    Name = v.Name,
                    Path = v.Path,
                    CreatedAt = v.CreatedAt,
                    Tags = v.Tags,
                    Industry = v.Industry
                }).ToList()
            });
        }

        /// <summary>
        /// 恢复回收站中的知识库
        /// </summary>
        [HttpPost("vaults/{vaultId}/restore")]
        public IActionResult RestoreVault(string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = "知识库 ID 不能为空" });

            var success = _vaultSettings.RestoreVault(vaultId);
            if (!success)
                return BadRequest(new { error = "恢复失败，知识库不存在或原始路径已被占用" });

            _ = _webUINotification.NotifyVaultStatusChangedAsync();
            _logger.LogInformation("恢复知识库: {VaultId}", vaultId);
            return Ok(new { success = true });
        }

        /// <summary>
        /// 清空回收站（永久删除）
        /// </summary>
        [HttpPost("vaults/trash/empty")]
        public IActionResult EmptyTrash()
        {
            _vaultSettings.EmptyTrash();
            _ = _webUINotification.NotifyVaultStatusChangedAsync();
            _logger.LogInformation("回收站已清空");
            return Ok(new { success = true });
        }

        #endregion
    }

    public class VaultRootPathPreferenceResponse
    {
        public string VaultRootPath { get; set; } = "";
    }

    public class AddVaultRequest
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Industry { get; set; }
    }

    public class UpdateVaultRequest
    {
        public string? Name { get; set; }
        public bool? IsPaid { get; set; }
        public string? Tags { get; set; }
        public string? Industry { get; set; }
    }

}
