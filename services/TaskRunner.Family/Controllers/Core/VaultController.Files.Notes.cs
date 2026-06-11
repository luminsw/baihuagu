using TaskRunner.Core.Shared;
using TaskRunner.Core.Shared.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskRunner.Data;
using TaskRunner.Services;
using TaskRunner.Services.Strategies;
using TaskRunner.Contracts.Vaults;

namespace TaskRunner.Controllers;

public partial class VaultController
{
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
}
