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

namespace TaskRunner.Vault.Controllers;

public partial class VaultController
{
    [HttpGet("cards")]
    public ActionResult<object> GetCards([FromQuery] string vaultId)
    {
        // 移动端 API 统一使用 HMAC 签名验证（在 Program.cs 中间件中完成）
        // 不再额外要求 Bearer Token，与 GetManifest/GetFile 保持一致
        var targetVault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
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
        var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
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
}
