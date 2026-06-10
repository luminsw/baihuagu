using System.Text;
using System.Text.Json;
using TaskRunner.Contracts.Anki;
using TaskRunner.Helpers;
using AnkiGen.Core;

namespace TaskRunner.Services
{
    /// <summary>
    /// Anki 卡片生成服务：将知识库笔记转换为 Anki 记忆卡片
    /// </summary>
    public partial class AnkiCardGenerator
    {
        private readonly VaultSettingsService _vaultSettings;
        private readonly ILogger<AnkiCardGenerator> _logger;

        public AnkiCardGenerator(VaultSettingsService vaultSettings, ILogger<AnkiCardGenerator> logger)
        {
            _vaultSettings = vaultSettings;
            _logger = logger;
        }

        /// <summary>
        /// 从单个笔记生成 Anki 卡片
        /// </summary>
        public async Task<GenerateResult> GenerateFromNote(string notePath, string? cardsPath = null, string? notesBasePath = null)
        {
            var basePath = notesBasePath ?? _vaultSettings.NotesPath;
            if (string.IsNullOrEmpty(basePath))
            {
                return new GenerateResult { Success = false, Message = "笔记目录未配置" };
            }

            var fullPath = Path.Combine(basePath, notePath + ".md");
            if (!File.Exists(fullPath))
            {
                return new GenerateResult { Success = false, Message = $"笔记不存在：{fullPath}" };
            }

            try
            {
                var content = await File.ReadAllTextAsync(fullPath);
                var deckName = GetDeckName(notePath);
                var deck = AnkiDeck.Create(deckName);

                // 解析笔记内容生成卡片
                ParseAndAddCards(deck, content, notePath);

                if (deck.Notes.Count == 0)
                {
                    return new GenerateResult { Success = true, Message = "未生成卡片（笔记内容不适合生成卡片）", CardCount = 0 };
                }

                // 保存为 JSON
                await SaveAsJson(notePath, deck, cardsPath);

                return new GenerateResult
                {
                    Success = true,
                    Message = $"生成 {deck.Notes.Count} 张卡片",
                    CardCount = deck.Notes.Count
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成卡片失败：{NotePath}", notePath);
                return new GenerateResult { Success = false, Message = $"生成失败：{ex.Message}" };
            }
        }

        /// <summary>
        /// 批量生成卡片（从目录）
        /// </summary>
        public async Task<BatchGenerateResult> GenerateFromDirectory(string directory, bool recursive = true, string? vaultId = null)
        {
            // directory 传入的是完整路径（如 /home/lumin/Vaults/中医/失眠/notes），直接使用
            var dirPath = directory;
            if (!Directory.Exists(dirPath))
            {
                return new BatchGenerateResult { Success = false, Message = $"目录不存在：{directory}" };
            }

            // 根据 vaultId 计算正确的 cards 保存路径
            string? cardsPath = null;
            if (!string.IsNullOrEmpty(vaultId))
            {
                var vault = _vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
                if (vault != null)
                {
                    cardsPath = Path.Combine(vault.Path, "cards");
                    _logger.LogInformation("[AnkiGenerate] vaultId={VaultId}, cardsPath={CardsPath}", vaultId, cardsPath);
                }
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(dirPath, "*.md", searchOption);
            _logger.LogInformation("[AnkiGenerate] 发现 {Count} 个笔记文件于 {Dir}", files.Length, dirPath);

            var results = new List<GenerateResult>();
            var totalCards = 0;

            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(dirPath, file);
                var notePath = relativePath.Substring(0, relativePath.Length - 3);

                var result = await GenerateFromNote(notePath, cardsPath, notesBasePath: dirPath);
                results.Add(result);
                if (result.Success)
                {
                    totalCards += result.CardCount;
                }
            }

            _logger.LogInformation("[AnkiGenerate] 完成：处理 {Files} 个笔记，生成 {Cards} 张卡片", files.Length, totalCards);
            return new BatchGenerateResult
            {
                Success = true,
                Message = $"处理 {files.Length} 个笔记，生成 {totalCards} 张卡片",
                TotalCards = totalCards,
                Results = results
            };
        }
        /// <summary>
        /// 从路径获取牌组名
        /// </summary>
        private string GetDeckName(string notePath)
        {
            var parts = notePath.Split('/');
            if (parts.Length >= 2)
            {
                return $"经方::{parts[^2]}";
            }
            return "经方";
        }

        /// <summary>
        /// 从路径获取标签
        /// </summary>
        private List<string> GetTagsFromPath(string notePath)
        {
            var tags = new List<string> { "经方" };
            var parts = notePath.Split('/');
            
            foreach (var part in parts.Take(parts.Length - 1))
            {
                tags.Add(part);
            }
            
            return tags;
        }

    }
}
