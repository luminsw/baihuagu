using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace TaskRunner.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SearchController : ControllerBase
    {
        private readonly Services.SettingsService _settings;
        private readonly Services.EmbeddingService _embeddingService;
        private readonly Services.VaultNoteIndexer _vaultNoteIndexer;
        private readonly ILogger<SearchController> _logger;

        public SearchController(
            Services.SettingsService settings,
            Services.EmbeddingService embeddingService,
            Services.VaultNoteIndexer vaultNoteIndexer,
            ILogger<SearchController> logger)
        {
            _settings = settings;
            _embeddingService = embeddingService;
            _vaultNoteIndexer = vaultNoteIndexer;
            _logger = logger;
        }

        /// <summary>
        /// 搜索知识库
        /// 优先使用 obsidian-cli（需要 Obsidian 桌面客户端运行），回退到直接扫描文件
        /// </summary>
        [HttpGet]
        public async Task<ActionResult> Search([FromQuery] string q = "", [FromQuery] string vaultId = "")
        {
            Console.WriteLine($"[Search] Query: '{q}', VaultId: '{vaultId}'");

            if (string.IsNullOrWhiteSpace(vaultId))
            {
                return Ok(new
                {
                    results = new List<SearchResult>(),
                    status = new SearchStatus
                    {
                        VaultConfigured = false,
                        SearchMethod = "none",
                        ErrorMessage = "必须指定有效的知识库"
                    }
                });
            }

            var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
            if (string.IsNullOrEmpty(vaultPath) || !System.IO.Directory.Exists(vaultPath))
            {
                _logger.LogWarning("知识库路径无效：VaultId={VaultId}, Path={Path}", vaultId, vaultPath);
                return Ok(new
                {
                    results = new List<SearchResult>(),
                    status = new SearchStatus
                    {
                        VaultConfigured = !string.IsNullOrEmpty(vaultPath),
                        VaultExists = !string.IsNullOrEmpty(vaultPath) && System.IO.Directory.Exists(vaultPath),
                        SearchMethod = "none",
                        ErrorMessage = string.IsNullOrEmpty(vaultPath)
                            ? "未找到指定的知识库"
                            : $"知识库路径不存在：{vaultPath}"
                    }
                });
            }

            if (string.IsNullOrWhiteSpace(q))
            {
                return Ok(new { results = new List<SearchResult>(), status = new SearchStatus { VaultConfigured = true, VaultExists = true } });
            }

            _logger.LogInformation("搜索知识库：{Query}", q);

            try
            {
                var canUseCli = Services.ObsidianExecutableResolver.TryGetPath(out var obsidianExe);
                var obsidianRunning = Process.GetProcessesByName("Obsidian").Length > 0;
                string searchMethod = "file-scan";
                string? errorMessage = null;
                
                if (canUseCli && obsidianRunning)
                {
                    var vaultName = System.IO.Path.GetFileName(vaultPath.TrimEnd('/'));
                    var cliResults = await SearchWithObsidianCli(obsidianExe, vaultName, q);
                    
                    if (cliResults != null && cliResults.Count > 0)
                    {
                        _logger.LogInformation("obsidian-cli 搜索成功：找到 {Count} 条结果", cliResults.Count);
                        return Ok(new
                        {
                            results = cliResults,
                            status = new SearchStatus
                            {
                                VaultConfigured = true,
                                VaultExists = true,
                                ObsidianRunning = true,
                                SearchMethod = "obsidian-cli"
                            }
                        });
                    }
                    
                    searchMethod = "file-scan";
                    if (cliResults == null)
                    {
                        _logger.LogDebug("obsidian-cli 搜索失败或超时，回退到文件扫描");
                    }
                }
                else if (canUseCli && !obsidianRunning)
                {
                    _logger.LogDebug("Obsidian 未运行，使用文件扫描");
                    searchMethod = "file-scan";
                }
                else
                {
                    _logger.LogDebug("obsidian-cli 不可用，使用文件扫描");
                    searchMethod = "file-scan";
                }

                // 尝试 FTS5 全文搜索
                var ftsResults = await _vaultNoteIndexer.SearchAsync(vaultId, q, HttpContext.RequestAborted);
                if (ftsResults.Count > 0)
                {
                    _logger.LogInformation("FTS5 搜索成功：找到 {Count} 条结果", ftsResults.Count);
                    searchMethod = "fts5";

                    // 如果配置了语义搜索，进行重排
                    if (_embeddingService.IsSemanticSearchEnabled())
                    {
                        var rerankedResults = await _embeddingService.RerankBySimilarityAsync(q, ftsResults);
                        searchMethod = "fts5+semantic";
                        return Ok(new
                        {
                            results = rerankedResults,
                            status = new SearchStatus
                            {
                                VaultConfigured = true,
                                VaultExists = true,
                                ObsidianRunning = obsidianRunning,
                                SearchMethod = searchMethod
                            }
                        });
                    }

                    return Ok(new
                    {
                        results = ftsResults,
                        status = new SearchStatus
                        {
                            VaultConfigured = true,
                            VaultExists = true,
                            ObsidianRunning = obsidianRunning,
                            SearchMethod = searchMethod
                        }
                    });
                }

                // 回退到直接扫描文件
                var fileResults = await SearchByScanningFiles(vaultPath, q);
                _logger.LogInformation("文件扫描完成：找到 {Count} 条结果", fileResults.Count);
                
                // 如果配置了语义搜索，进行重排
                if (_embeddingService.IsSemanticSearchEnabled() && fileResults.Count > 0)
                {
                    var rerankedResults = await _embeddingService.RerankBySimilarityAsync(q, fileResults);
                    searchMethod = "semantic";
                    return Ok(new
                    {
                        results = rerankedResults,
                        status = new SearchStatus
                        {
                            VaultConfigured = true,
                            VaultExists = true,
                            ObsidianRunning = obsidianRunning,
                            SearchMethod = searchMethod
                        }
                    });
                }
                
                if (fileResults.Count == 0)
                {
                    errorMessage = "未在知识库中找到匹配内容";
                }
                
                return Ok(new
                {
                    results = fileResults,
                    status = new SearchStatus
                    {
                        VaultConfigured = true,
                        VaultExists = true,
                        ObsidianRunning = obsidianRunning,
                        SearchMethod = searchMethod,
                        ErrorMessage = errorMessage
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索失败");
                return StatusCode(500, new { error = "搜索失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 使用 Obsidian 可执行文件做搜索（失败/超时会降级到文件扫描）
        /// </summary>
        private async Task<List<SearchResult>?> SearchWithObsidianCli(string obsidianExe, string vaultName, string query)
        {
            try
            {
                var escapedQuery = query.Replace("\"", "\\\"");
                var searchProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = obsidianExe,
                    Arguments = $"vault=\"{vaultName}\" search query=\"{escapedQuery}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // 关键：obsidian-cli 输出编码在 Windows 上经常不是 UTF-8
                    // 退回到系统默认编码，避免中文路径乱码。
                    StandardOutputEncoding = System.Text.Encoding.Default,
                    StandardErrorEncoding = System.Text.Encoding.Default
                });

                if (searchProcess == null) return null;
                
                // 15 秒超时
                var timeoutTask = Task.Delay(15000);
                var outputTask = searchProcess.StandardOutput.ReadToEndAsync();
                
                var completed = await Task.WhenAny(outputTask, timeoutTask);
                if (completed == timeoutTask)
                {
                    _logger.LogWarning("obsidian-cli 搜索超时");
                    searchProcess.Kill();
                    return null;
                }
                
                var output = await outputTask;
                await searchProcess.WaitForExitAsync();
                
                if (searchProcess.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return null;

                // 解析输出
                var results = new List<SearchResult>();
                foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)))
                {
                    if (line.StartsWith("path") || line.StartsWith("-") || line.StartsWith("id"))
                        continue;
                    
                    var path = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".md"))
                    {
                        var relativePath = NormalizeRelPath(path, removeNotesPrefix: true);
                        var title = System.IO.Path.GetFileNameWithoutExtension(path);
                        
                        results.Add(new SearchResult
                        {
                            Id = title,
                            Title = title,
                            Path = relativePath,
                            Preview = $"[obsidian-cli] {path}",
                            Score = 10
                        });
                    }
                }

                return results.Count > 0 ? results : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "obsidian-cli 搜索失败");
                return null;
            }
        }

        /// <summary>
        /// 直接扫描文件搜索
        /// </summary>
        private async Task<List<SearchResult>> SearchByScanningFiles(string vaultPath, string query)
        {
            var results = new List<SearchResult>();
            var queryLower = query.ToLower();

            var files = System.IO.Directory.GetFiles(vaultPath, "*.md", System.IO.SearchOption.AllDirectories);
            
            foreach (var file in files)
            {
                try
                {
                    var fileName = System.IO.Path.GetFileName(file);
                    if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var content = await System.IO.File.ReadAllTextAsync(file);
                    var title = System.IO.Path.GetFileNameWithoutExtension(file);
                    var relativePath = NormalizeRelPath(
                        file.Substring(vaultPath.Length),
                        removeNotesPrefix: true
                    );
                    
                    var titleMatch = title.ToLower().Contains(queryLower);
                    var contentMatch = content.ToLower().Contains(queryLower);
                    
                    if (titleMatch || contentMatch)
                    {
                        var preview = ExtractPreview(content, queryLower);
                        
                        results.Add(new SearchResult
                        {
                            Id = title,
                            Title = title,
                            Path = relativePath,
                            Preview = preview,
                            Score = titleMatch ? 10 : 5
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取文件失败：{File}", file);
                }
            }

            return results.OrderByDescending(r => r.Score).Take(50).ToList();
        }

        /// <summary>
        /// 统一 Path 的分隔符为正斜杠，并在可能时剔除 "notes/" 前缀。
        /// 这样前端分组规则（按 "症状/" 等字符串匹配）在 Windows/Linux 都一致。
        /// </summary>
        private static string NormalizeRelPath(string raw, bool removeNotesPrefix)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            // raw 可能以 \ 或 / 开头（来自 file.Substring(vaultPath.Length)）
            var p = raw.Replace('\\', '/').Trim().TrimStart('/');

            if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                p = p[..^3];
            }

            if (removeNotesPrefix && p.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring("notes/".Length);
            }

            return p;
        }

        private string ExtractPreview(string content, string query)
        {
            // 找到查询词的位置
            var index = content.ToLower().IndexOf(query);
            if (index < 0) index = 0;

            // 提取前后 100 个字符
            var start = Math.Max(0, index - 50);
            var length = Math.Min(200, content.Length - start);
            var preview = content.Substring(start, length);

            // 清理格式
            preview = preview.Replace("\n", " ").Replace("#", "").Replace("*", "");
            
            // 添加省略号
            if (start > 0) preview = "..." + preview;
            if (start + length < content.Length) preview = preview + "...";

            return preview.Trim();
        }

        #region FTS5 Index Management

        /// <summary>
        /// 重建指定知识库的 FTS5 全文索引
        /// </summary>
        [HttpPost("reindex")]
        public async Task<ActionResult> Reindex([FromBody] ReindexRequest request)
        {
            try
            {
                var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == request.VaultId)?.Path;
                if (string.IsNullOrEmpty(vaultPath) || !Directory.Exists(vaultPath))
                {
                    return BadRequest(new { error = "知识库路径无效" });
                }

                _logger.LogInformation("开始重建知识库 {VaultId} 的 FTS5 索引", request.VaultId);
                await _vaultNoteIndexer.IndexVaultAsync(request.VaultId, vaultPath, HttpContext.RequestAborted);

                return Ok(new { success = true, message = $"知识库 {request.VaultId} 的 FTS5 索引已重建" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建 FTS5 索引失败");
                return StatusCode(500, new { error = "重建索引失败", message = ex.Message });
            }
        }

        /// <summary>
        /// 获取知识库 FTS5 索引状态
        /// </summary>
        [HttpGet("index-status")]
        public async Task<ActionResult> IndexStatus([FromQuery] string vaultId)
        {
            try
            {
                var (count, _) = await _vaultNoteIndexer.GetIndexStatsAsync(vaultId, HttpContext.RequestAborted);
                return Ok(new { vaultId, indexedCount = count, hasIndex = count > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 FTS5 索引状态失败");
                return StatusCode(500, new { error = "获取状态失败", message = ex.Message });
            }
        }

        #endregion
    }

    public class SearchResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public int Score { get; set; }
    }

    public class SearchStatus
    {
        public bool VaultConfigured { get; set; }
        public bool VaultExists { get; set; }
        public bool ObsidianRunning { get; set; }
        public string SearchMethod { get; set; } = "unknown"; // obsidian-cli, file-scan, semantic, none
        public string? ErrorMessage { get; set; }
    }

    public class ReindexRequest
    {
        public string VaultId { get; set; } = "";
    }
}
