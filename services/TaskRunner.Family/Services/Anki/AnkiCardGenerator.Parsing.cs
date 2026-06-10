using System.Text;
using System.Text.Json;
using TaskRunner.Contracts.Anki;
using TaskRunner.Helpers;
using AnkiGen.Core;

namespace TaskRunner.Services
{
    /// <summary>

    public partial class AnkiCardGenerator
    {
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
    }
}
