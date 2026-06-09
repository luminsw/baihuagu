using Microsoft.AspNetCore.Mvc;
using TaskRunner.Services;

namespace TaskRunner.Controllers;

/// <summary>
/// 备份恢复控制器 - 处理全量备份和恢复操作
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BackupController : ControllerBase
{
    private readonly BackupService _backupService;
    private readonly ILogger<BackupController> _logger;

    public BackupController(BackupService backupService, ILogger<BackupController> logger)
    {
        _backupService = backupService;
        _logger = logger;
    }

    /// <summary>
    /// 创建全量备份
    /// </summary>
    [HttpPost("full")]
    public async Task<ActionResult<FullBackupResponse>> CreateFullBackup([FromBody] FullBackupRequest request)
    {
        try
        {
            _logger.LogInformation("开始创建全量备份");
            var result = await _backupService.CreateFullBackupAsync(request.BackupDir, request.Password, HttpContext.RequestAborted);

            return Ok(new FullBackupResponse
            {
                Success = result.Success,
                Message = result.Success ? "全量备份创建成功" : $"备份失败：{result.Error}",
                BackupPath = result.BackupPath,
                BackupTime = result.BackupTime,
                FileSize = result.FileSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建全量备份失败");
            return Ok(new FullBackupResponse
            {
                Success = false,
                Message = $"备份失败：{ex.Message}"
            });
        }
    }

    /// <summary>
    /// 恢复全量备份
    /// </summary>
    [HttpPost("restore")]
    public async Task<ActionResult<FullRestoreResponse>> RestoreFullBackup([FromBody] FullRestoreRequest request)
    {
        try
        {
            _logger.LogInformation("开始恢复全量备份：{BackupPath}", request.BackupPath);
            var result = await _backupService.RestoreFullBackupAsync(
                request.BackupPath,
                request.Password,
                request.VaultRootPathOverride,
                request.Overwrite,
                HttpContext.RequestAborted);

            return Ok(new FullRestoreResponse
            {
                Success = result.Success,
                Message = result.Success ? "备份恢复成功" : $"恢复失败：{result.Error}",
                SourcePlatform = result.SourcePlatform,
                SourceOS = result.SourceOS,
                RestoredAt = result.RestoredAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复全量备份失败");
            return Ok(new FullRestoreResponse
            {
                Success = false,
                Message = $"恢复失败：{ex.Message}"
            });
        }
    }

    /// <summary>
    /// 验证备份文件
    /// </summary>
    [HttpPost("validate")]
    public async Task<ActionResult<ValidateBackupResponse>> ValidateBackup([FromBody] ValidateBackupRequest request)
    {
        try
        {
            var result = await _backupService.ValidateFullBackupAsync(request.BackupPath, request.Password);

            return Ok(new ValidateBackupResponse
            {
                Success = true,
                IsValid = result.IsValid,
                Version = result.Version,
                CreatedAt = result.CreatedAt,
                SourcePlatform = result.SourcePlatform,
                SourceOS = result.SourceOS,
                HasPassword = result.HasPassword,
                HasDatabase = result.HasDatabase,
                HasConfig = result.HasConfig,
                HasVaults = result.HasVaults,
                VaultCount = result.VaultCount,
                Message = result.IsValid ? "备份文件有效" : $"无效：{result.Error}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "验证备份文件失败");
            return Ok(new ValidateBackupResponse
            {
                Success = false,
                IsValid = false,
                Message = $"验证失败：{ex.Message}"
            });
        }
    }

    /// <summary>
    /// 获取备份列表
    /// </summary>
    [HttpGet("list")]
    public ActionResult<BackupListResponse> GetBackupList([FromQuery] string? backupPath = null)
    {
        try
        {
            var backups = _backupService.GetBackupList(backupPath);

            return Ok(new BackupListResponse
            {
                Success = true,
                Backups = backups
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取备份列表失败");
            return Ok(new BackupListResponse
            {
                Success = false,
                Message = $"获取备份列表失败：{ex.Message}"
            });
        }
    }
}

#region Request/Response DTOs

public class FullBackupRequest
{
    /// <summary>备份保存目录（空则使用默认目录）</summary>
    public string? BackupDir { get; set; }

    /// <summary>备份加密密码（空则不加密 API Key）</summary>
    public string? Password { get; set; }
}

public class FullBackupResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? BackupPath { get; set; }
    public DateTime? BackupTime { get; set; }
    public long? FileSize { get; set; }
}

public class FullRestoreRequest
{
    /// <summary>备份文件路径</summary>
    public string BackupPath { get; set; } = "";

    /// <summary>备份加密密码</summary>
    public string? Password { get; set; }

    /// <summary>知识库根路径覆盖（跨平台恢复时指定新根路径）</summary>
    public string? VaultRootPathOverride { get; set; }

    /// <summary>是否覆盖现有数据</summary>
    public bool Overwrite { get; set; } = true;
}

public class FullRestoreResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? SourcePlatform { get; set; }
    public string? SourceOS { get; set; }
    public DateTime? RestoredAt { get; set; }
}

public class ValidateBackupRequest
{
    public string BackupPath { get; set; } = "";
    public string? Password { get; set; }
}

public class ValidateBackupResponse
{
    public bool Success { get; set; }
    public bool IsValid { get; set; }
    public int Version { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? SourcePlatform { get; set; }
    public string? SourceOS { get; set; }
    public bool HasPassword { get; set; }
    public bool HasDatabase { get; set; }
    public bool HasConfig { get; set; }
    public bool HasVaults { get; set; }
    public int VaultCount { get; set; }
    public string Message { get; set; } = "";
}

public class BackupListResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public List<BackupFileInfo> Backups { get; set; } = new();
}

#endregion
