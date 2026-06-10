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
    public class AnkiCardGenerator
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
        /// 解析笔记内容并添加卡片
        /// </summary>
        private void ParseAndAddCards(AnkiDeck deck, string content, string notePath)
        {
            var lines = content.Split('\n');
            var title = "";
            var tags = GetTagsFromPath(notePath);

            // 提取标题
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("# "))
                {
                    title = trimmed.Substring(2).Trim();
                    break;
                }
            }

            // 解析不同类型的卡片
            ParseQAFormat(deck, lines, title, tags);
            ParseListItems(deck, lines, title, tags);
            ParseDefinitions(deck, content, title, tags);

            // 如果没有解析到卡片，创建概述卡片
            if (deck.Notes.Count == 0 && !string.IsNullOrEmpty(title))
            {
                var summary = string.Join("\n", lines
                    .Where(l => !l.StartsWith("#") && !string.IsNullOrWhiteSpace(l) && !l.StartsWith("```"))
                    .Take(3));
                
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    deck.AddCard($"{title} - 概述", summary, tags.ToArray());
                }
            }
        }

        /// <summary>
        /// 解析问答格式（问句？答案）
        /// </summary>
        private void ParseQAFormat(AnkiDeck deck, string[] lines, string title, List<string> tags)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                
                if (line.EndsWith("？") || line.EndsWith("?"))
                {
                    var answer = new StringBuilder();
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        var next = lines[j].Trim();
                        if (string.IsNullOrWhiteSpace(next) || next.StartsWith("#")) break;
                        if (answer.Length > 0) answer.Append(" ");
                        answer.Append(next);
                    }

                    if (answer.Length > 0)
                    {
                        deck.AddCard(line, answer.ToString(), tags.ToArray());
                    }
                }
            }
        }

        /// <summary>
        /// 解析列表项（Key：Value 格式）
        /// </summary>
        private void ParseListItems(AnkiDeck deck, string[] lines, string title, List<string> tags)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                
                if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) && trimmed.Contains("："))
                {
                    var idx = trimmed.IndexOf('：');
                    if (idx > 2)
                    {
                        var key = trimmed.Substring(2, idx - 2).Trim();
                        var value = trimmed.Substring(idx + 1).Trim();
                        
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                        {
                            deck.AddCard(key, value, tags.ToArray());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 解析定义（XX是XX 格式）
        /// </summary>
        private void ParseDefinitions(AnkiDeck deck, string content, string title, List<string> tags)
        {
            var patterns = new[]
            {
                @"(.{2,10})[是为指即]([^。\n]{5,100})",
                @"(.{2,10})[:：]([^。\n]{5,100})"
            };

            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(content, pattern);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    var key = match.Groups[1].Value.Trim();
                    var value = match.Groups[2].Value.Trim();

                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    {
                        deck.AddCard($"什么是{key}？", value, tags.ToArray());
                    }
                }
            }
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

        /// <summary>
        /// 保存为 JSON 文件
        /// </summary>
        private async Task SaveAsJson(string notePath, AnkiDeck deck, string? cardsPath = null)
        {
            var targetCardsPath = cardsPath ?? _vaultSettings.CardsPath;
            if (string.IsNullOrEmpty(targetCardsPath)) return;

            Directory.CreateDirectory(targetCardsPath);

            var deckData = new JsonDeckData
            {
                Name = deck.Name,
                Cards = deck.Notes.SelectMany(n => NoteToCards(n, deck.Name)).ToList()
            };

            var fileName = notePath.Replace("/", "_") + ".json";
            var fullPath = Path.Combine(targetCardsPath, fileName);

            var json = JsonSerializer.Serialize(deckData, JsonHelper.IndentedUnicode);

            await File.WriteAllTextAsync(fullPath, json);
            _logger.LogInformation("保存 {Count} 张卡片到：{Path}", deck.Notes.Count, fullPath);
        }

        /// <summary>
        /// 将 AnkiNote 转换为 JsonCard
        /// </summary>
        private List<Contracts.Anki.JsonCard> NoteToCards(AnkiNote note, string deckName)
        {
            var cards = new List<Contracts.Anki.JsonCard>();
            var fields = note.Fields;

            if (fields.Count >= 2)
            {
                // 正向卡片
                cards.Add(new Contracts.Anki.JsonCard
                {
                    Front = fields[0],
                    Back = fields[1],
                    Tags = note.Tags.ToList()
                });

                // 如果是反向模板，添加反向卡片
                if (note.Model.Templates.Count == 2)
                {
                    cards.Add(new Contracts.Anki.JsonCard
                    {
                        Front = fields[1],
                        Back = fields[0],
                        Tags = note.Tags.ToList()
                    });
                }
            }

            return cards;
        }

        /// <summary>
        /// 导出所有卡片为 CSV 格式
        /// </summary>
        public async Task<string?> ExportToCsv(string? cardsPath = null)
        {
            cardsPath ??= _vaultSettings.CardsPath;
            if (string.IsNullOrEmpty(cardsPath) || !Directory.Exists(cardsPath))
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Front,Back,Tags,Deck");

            var files = Directory.GetFiles(cardsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                    
                    if (deckData?.Cards == null) continue;

                    foreach (var card in deckData.Cards)
                    {
                        var front = EscapeCsv(card.Front ?? "");
                        var back = EscapeCsv(card.Back ?? "");
                        var tags = EscapeCsv(string.Join(" ", card.Tags ?? new List<string>()));
                        var deck = EscapeCsv(deckData.Name ?? "");
                        
                        sb.AppendLine($"{front},{back},{tags},{deck}");
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "操作失败"); }
            }

            return sb.ToString();
        }

        private string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }

        /// <summary>
        /// 获取指定知识库（或全局）的卡片总数
        /// </summary>
        public int GetTotalCardCount(string? vaultId = null)
        {
            var cardsPath = string.IsNullOrEmpty(vaultId)
                ? _vaultSettings.CardsPath
                : System.IO.Path.Combine(_vaultSettings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path ?? "", "cards");

            if (!Directory.Exists(cardsPath)) return 0;

            int count = 0;
            foreach (var file in Directory.GetFiles(cardsPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                    count += deckData?.Cards?.Count ?? 0;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "文件系统操作失败"); }
            }
            return count;
        }
    }

}