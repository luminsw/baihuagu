using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using TaskRunner.Contracts.Anki;

namespace TaskRunner.Controllers
{
    /// <summary>
    /// Anki 卡片生成控制器
    /// </summary>
    [ApiController]
    [Route("api/anki")]
    public class AnkiController : ControllerBase
    {
        private readonly Services.AnkiCardGenerator _cardGenerator;
        private readonly Services.DailyCardService _dailyCardService;
        private readonly Services.AchievementEngine _achievementEngine;
        private readonly Services.LearnerService _learnerService;
        private readonly Services.SettingsService _settings;
        private readonly Services.TaskManager _taskManager;
        private readonly ILogger<AnkiController> _logger;

        public AnkiController(
            Services.AnkiCardGenerator cardGenerator,
            Services.DailyCardService dailyCardService,
            Services.AchievementEngine achievementEngine,
            Services.LearnerService learnerService,
            Services.SettingsService settings,
            Services.TaskManager taskManager,
            ILogger<AnkiController> logger)
        {
            _cardGenerator = cardGenerator;
            _dailyCardService = dailyCardService;
            _achievementEngine = achievementEngine;
            _learnerService = learnerService;
            _settings = settings;
            _taskManager = taskManager;
            _logger = logger;
        }

        /// <summary>
        /// 从单个笔记生成 Anki 卡片
        /// </summary>
        [HttpPost("generate")]
        public async Task<ActionResult<Services.GenerateResult>> GenerateFromNote([FromBody] GenerateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NotePath))
            {
                return BadRequest(new Services.GenerateResult { Success = false, Message = "笔记路径不能为空" });
            }

            var result = await _cardGenerator.GenerateFromNote(request.NotePath);
            return Ok(result);
        }

        /// <summary>
        /// 批量生成卡片（从目录）
        /// </summary>
        [HttpPost("generate-batch")]
        public async Task<ActionResult<Services.BatchGenerateResult>> GenerateFromDirectory([FromBody] BatchGenerateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Directory))
            {
                return BadRequest(new Services.BatchGenerateResult { Success = false, Message = "目录不能为空" });
            }

            var result = await _cardGenerator.GenerateFromDirectory(request.Directory, request.Recursive);
            return Ok(result);
        }

        /// <summary>
        /// 搜索卡片
        /// </summary>
        [HttpGet("search")]
        public ActionResult<AnkiSearchResult> SearchCards([FromQuery] string? q, [FromQuery] string vaultId, [FromQuery] int limit = 50)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath) || !System.IO.Directory.Exists(cardsPath))
            {
                return Ok(new AnkiSearchResult { Success = true, Cards = new List<CardItemDto>(), Total = 0 });
            }

            var results = new List<CardItemDto>();
            var files = System.IO.Directory.GetFiles(cardsPath, "*.json");
            var keywords = string.IsNullOrWhiteSpace(q)
                ? Array.Empty<string>()
                : q.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                    
                    var cardsArray = ReadCardsFromFile(json, fileName);
                    
                    for (int i = 0; i < cardsArray.Count; i++)
                    {
                        var card = cardsArray[i];
                        // 为每张卡片生成稳定 ID（文件名 + 索引）
                        if (string.IsNullOrEmpty(card.Id))
                            card.Id = $"{fileName}_{i}";
                        card.Source = fileName;

                        if (keywords.Length == 0)
                        {
                            results.Add(card);
                            if (results.Count >= limit) break;
                            continue;
                        }

                        var frontLower = (card.Front ?? "").ToLower();
                        var backLower = (card.Back ?? "").ToLower();
                        
                        var matchCount = keywords.Count(k => 
                            frontLower.Contains(k) || backLower.Contains(k));
                        
                        if (matchCount == keywords.Length)
                        {
                            results.Add(card);
                            if (results.Count >= limit) break;
                        }
                    }
                    
                    if (results.Count >= limit) break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "解析卡片文件失败：{File}", file);
                }
            }

            return Ok(new AnkiSearchResult
            {
                Success = true,
                Cards = results,
                Total = results.Count,
                Query = q ?? ""
            });
        }

        /// <summary>
        /// 获取知识库总卡片数
        /// </summary>
        [HttpGet("card-count")]
        public ActionResult<int> GetCardCount([FromQuery] string vaultId)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath) || !System.IO.Directory.Exists(cardsPath))
            {
                return Ok(0);
            }

            int totalCount = 0;
            var files = System.IO.Directory.GetFiles(cardsPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                    if (deckData?.Cards != null)
                    {
                        totalCount += deckData.Cards.Count;
                    }
                }
                catch { }
            }

            return Ok(totalCount);
        }

        /// <summary>
        /// 获取所有牌组列表
        /// </summary>
        [HttpGet("decks")]
        public ActionResult<DeckListResult> GetDecks([FromQuery] string vaultId)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath) || !System.IO.Directory.Exists(cardsPath))
            {
                return Ok(new DeckListResult { Decks = new List<DeckInfo>() });
            }

            var decks = new Dictionary<string, DeckInfo>();
            var files = System.IO.Directory.GetFiles(cardsPath, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = System.IO.File.ReadAllText(file);
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(file);
                    
                    var cardsArray = ReadCardsFromFile(json, fileName);
                    
                    if (cardsArray.Count == 0) continue;

                    // 按 Deck 分组
                    foreach (var card in cardsArray)
                    {
                        var deckName = card.Deck ?? "Default";
                        if (!decks.ContainsKey(deckName))
                        {
                            decks[deckName] = new DeckInfo
                            {
                                Name = deckName,
                                CardCount = 0,
                                Sources = new List<string>()
                            };
                        }
                        
                        decks[deckName].CardCount++;
                        if (!decks[deckName].Sources.Contains(fileName))
                        {
                            decks[deckName].Sources.Add(fileName);
                        }
                    }
                }
                catch { }
            }

            return Ok(new DeckListResult
            {
                Decks = decks.Values.OrderByDescending(d => d.CardCount).ToList()
            });
        }

        /// <summary>
        /// 记录学习结果
        /// </summary>
        [HttpPost("study")]
        public ActionResult RecordStudy([FromQuery] string vaultId, [FromBody] StudyRequest request)
        {
            var statsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(statsPath)) 
            {
                return BadRequest(new { error = "必须指定有效的知识库" });
            }

            var studyDir = System.IO.Path.Combine(statsPath, ".study");
            System.IO.Directory.CreateDirectory(studyDir);

            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var statsFile = System.IO.Path.Combine(studyDir, $"{today}.json");

            // 读取今日统计
            var stats = ReadStudyStats(statsFile);
            
            // 更新统计
            stats.Total++;
            switch (request.Result?.ToLower())
            {
                case "remember":
                case "remembered":
                    stats.Remembered++;
                    break;
                case "hard":
                case "forgot":
                    stats.Forgot++;
                    break;
                default:
                    stats.Normal++;
                    break;
            }

            // 记录学习历史
            if (stats.History == null) stats.History = new List<StudyRecord>();
            stats.History.Add(new StudyRecord
            {
                CardFront = request.CardFront ?? "",
                CardBack = request.CardBack ?? "",
                Deck = request.Deck ?? "",
                Result = request.Result ?? "normal",
                Time = DateTime.Now
            });

            // 保存
            WriteStudyStats(statsFile, stats);

            return Ok(new { success = true, stats });
        }

        /// <summary>
        /// 获取学习统计
        /// </summary>
        [HttpGet("stats")]
        public ActionResult<StudyStatsResponse> GetStats([FromQuery] string vaultId, [FromQuery] int days = 7)
        {
            var statsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(statsPath))
            {
                return Ok(new StudyStatsResponse());
            }

            var studyDir = System.IO.Path.Combine(statsPath, ".study");
            var response = new StudyStatsResponse
            {
                DailyStats = new List<DailyStats>()
            };

            // 统计最近 N 天
            var totalStudied = 0;
            var totalRemembered = 0;
            var totalForgot = 0;
            var streak = 0;
            var today = DateTime.Today;

            for (int i = 0; i < days; i++)
            {
                var date = today.AddDays(-i);
                var dateStr = date.ToString("yyyy-MM-dd");
                var statsFile = System.IO.Path.Combine(studyDir, $"{dateStr}.json");
                
                if (System.IO.File.Exists(statsFile))
                {
                    var stats = ReadStudyStats(statsFile);
                    
                    if (i == 0) response.Today = stats;
                    
                    response.DailyStats.Add(new DailyStats
                    {
                        Date = dateStr,
                        Total = stats.Total,
                        Remembered = stats.Remembered,
                        Forgot = stats.Forgot,
                        Normal = stats.Normal
                    });

                    totalStudied += stats.Total;
                    totalRemembered += stats.Remembered;
                    totalForgot += stats.Forgot;

                    // 连续学习天数
                    if (stats.Total > 0 && i == streak) streak++;
                }
                else if (i > 0) // 今天还没学习不算断
                {
                    break;
                }
            }

            response.Summary = new StudySummary
            {
                TotalCards = totalStudied,
                Remembered = totalRemembered,
                Forgot = totalForgot,
                Accuracy = totalStudied > 0 ? (double)totalRemembered / totalStudied * 100 : 0,
                Streak = streak
            };

            return Ok(response);
        }

        private StudyStats ReadStudyStats(string file)
        {
            if (!System.IO.File.Exists(file)) return new StudyStats();
            try
            {
                var json = System.IO.File.ReadAllText(file);
                return JsonSerializer.Deserialize<StudyStats>(json) ?? new StudyStats();
            }
            catch { return new StudyStats(); }
        }

        private void WriteStudyStats(string file, StudyStats stats)
        {
            var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
            });
            System.IO.File.WriteAllText(file, json);
        }

        /// <summary>
        /// 导出所有卡片为 CSV 格式（可用于 Anki 导入）
        /// </summary>
        [HttpGet("export")]
        public async Task<ActionResult> ExportToCsv([FromQuery] string vaultId)
        {
            var cardsPath = ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath))
            {
                return BadRequest(new { error = "必须指定有效的知识库" });
            }

            var csv = await _cardGenerator.ExportToCsv(cardsPath);
            
            if (string.IsNullOrEmpty(csv))
            {
                return NotFound(new { error = "没有可导出的卡片" });
            }

            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "anki_cards.csv");
        }

        // ==================== 每日一帖 API ====================

        /// <summary>
        /// 获取今日卡片（智能复习调度）
        /// </summary>
        [HttpGet("daily")]
        public async Task<ActionResult<DailyCardResultDto>> GetDailyCard([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new DailyCardResultDto { HasCard = false, Message = "必须指定知识库" });

            var result = await _dailyCardService.GetTodayCardAsync(vaultId);
            var dto = new DailyCardResultDto
            {
                HasCard = result.HasCard,
                Message = result.Message,
                Card = result.Card == null ? null : new CardItemDto
                {
                    Id = result.Card.Id,
                    Deck = result.Card.Deck,
                    Front = result.Card.Front,
                    Back = result.Card.Back,
                    Tags = result.Card.Tags,
                    Source = result.Card.Source
                },
                TodayProgress = result.TodayProgress == null ? null : new DailyProgressDto
                {
                    Completed = result.TodayProgress.Completed,
                    Target = result.TodayProgress.Target,
                    TotalCards = result.TodayProgress.TotalCards,
                    Streak = result.TodayProgress.Streak
                },
                Remaining = result.Remaining
            };
            return Ok(dto);
        }

        /// <summary>
        /// 提交今日卡片答案
        /// </summary>
        [HttpPost("daily/answer")]
        public async Task<ActionResult> SubmitDailyAnswer([FromQuery] string vaultId, [FromBody] DailyAnswerRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = "必须指定知识库" });

            var success = await _dailyCardService.RecordAnswerAsync(vaultId, request.CardId, request.Result);
            if (!success)
                return StatusCode(500, new { error = "记录失败" });

            // 异步检查成就（DailyCardService 已记录 StudyActivity）
            var defaultLearner = await _learnerService.GetDefaultAsync();
            if (defaultLearner != null)
            {
                _ = _achievementEngine.CheckAndUnlockAsync(defaultLearner.Id);
            }

            var progress = _dailyCardService.GetTodayProgress(vaultId);
            return Ok(new { success = true, progress });
        }

        /// <summary>
        /// 获取今日学习进度
        /// </summary>
        [HttpGet("daily/progress")]
        public ActionResult<DailyProgressDto> GetDailyProgress([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = "必须指定知识库" });

            var p = _dailyCardService.GetTodayProgress(vaultId);
            return Ok(new DailyProgressDto
            {
                Completed = p.Completed,
                Target = p.Target,
                TotalCards = p.TotalCards,
                Streak = p.Streak
            });
        }

        /// <summary>
        /// 家长出题：保存自定义卡片
        /// </summary>
        [HttpPost("custom-card")]
        public async Task<ActionResult> SaveCustomCard([FromQuery] string vaultId, [FromBody] CustomCardRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { error = "必须指定知识库" });

            var req = new Services.CustomCardRequest
            {
                Front = request.Front,
                Back = request.Back,
                Deck = request.Deck,
                Tags = request.Tags
            };
            var success = _dailyCardService.SaveCustomCard(vaultId, req);
            if (!success)
                return StatusCode(500, new { error = "保存失败" });

            // 记录出题活动并检查成就
            var defaultLearner = await _learnerService.GetDefaultAsync();
            if (defaultLearner != null)
            {
                await _achievementEngine.RecordActivityAsync(defaultLearner.Id, vaultId, "create_card");
            }

            return Ok(new { success = true, message = "卡片已保存" });
        }

        /// <summary>
        /// 获取最近学习记录
        /// </summary>
        [HttpGet("daily/recent")]
        public ActionResult<List<StudiedCardDto>> GetRecentStudied([FromQuery] string vaultId, [FromQuery] int days = 7)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new List<StudiedCardDto>());

            var list = _dailyCardService.GetRecentStudied(vaultId, days).Select(s => new StudiedCardDto
            {
                Card = new CardItemDto
                {
                    Id = s.Card.Id,
                    Deck = s.Card.Deck,
                    Front = s.Card.Front,
                    Back = s.Card.Back,
                    Tags = s.Card.Tags,
                    Source = s.Card.Source
                },
                Result = s.Result,
                Date = s.Date
            }).ToList();

            return Ok(list);
        }

        /// <summary>
        /// 获取指定知识库的卡片总数
        /// </summary>
        [HttpGet("vault-card-count")]
        public ActionResult<int> GetVaultCardCount([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(0);
            var count = _cardGenerator.GetTotalCardCount(vaultId);
            return Ok(count);
        }

        /// <summary>
        /// 为知识库批量生成全部卡片（扫描 notes 目录）
        /// </summary>
        /// <summary>
        /// 为知识库批量生成全部卡片（扫描 notes 目录），以任务形式后台执行
        /// </summary>
        [HttpPost("generate-all")]
        public async Task<ActionResult<object>> GenerateAllCards([FromQuery] string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return BadRequest(new { success = false, message = "知识库 ID 不能为空" });

            var vault = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId);
            if (vault == null)
                return NotFound(new { success = false, message = "知识库不存在" });

            var notesPath = System.IO.Path.Combine(vault.Path, "notes");
            if (!Directory.Exists(notesPath))
                return Ok(new { success = true, message = "笔记目录不存在", totalCards = 0 });

            // 创建后台任务执行卡片生成
            var taskId = _taskManager.CreateTask("anki_generate", new Dictionary<string, string>
            {
                ["vaultId"] = vaultId,
                ["vaultName"] = vault.Name,
                ["notesPath"] = notesPath
            });
            _logger.LogInformation("[AnkiController] 创建卡片生成任务 {TaskId}，知识库 {VaultName}({VaultId})", taskId, vault.Name, vaultId);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _taskManager.UpdateProgress(taskId, 0, 100, $"开始为 {vault.Name} 生成记忆卡片...");
                    var result = await _cardGenerator.GenerateFromDirectory(notesPath, recursive: true, vaultId: vaultId);
                    await _taskManager.UpdateProgress(taskId, 100, 100, result.Message);
                    await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Success, data: new { totalCards = result.TotalCards, message = result.Message });
                    _logger.LogInformation("[AnkiController] 任务 {TaskId} 完成：{Message}", taskId, result.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AnkiController] 任务 {TaskId} 生成卡片失败", taskId);
                    await _taskManager.UpdateStatus(taskId, RunnerTaskStatus.Failed, error: ex.Message);
                }
            });

            return Ok(new { success = true, taskId, message = "卡片生成任务已创建", vaultName = vault.Name });
        }

        /// <summary>
        /// 从 JSON 文件中读取卡片列表，支持 JsonDeckData 和 List&lt;CardItemDto&gt; 两种格式
        /// </summary>
        private List<CardItemDto> ReadCardsFromFile(string json, string fileName)
        {
            try
            {
                // 先尝试解析为 JsonDeckData 格式（{ Name, Cards: [...] }）
                var deckData = JsonSerializer.Deserialize<JsonDeckData>(json);
                if (deckData?.Cards != null && deckData.Cards.Count > 0)
                {
                    return deckData.Cards.Select((c, i) => new CardItemDto
                    {
                        Id = $"{fileName}_{i}",
                        Deck = deckData.Name ?? fileName,
                        Front = c.Front,
                        Back = c.Back,
                        Tags = c.Tags ?? new(),
                        Source = fileName
                    }).ToList();
                }
            }
            catch { }

            try
            {
                // 回退到数组格式（[{ Front, Back, Deck, Tags }]）
                var cardsArray = JsonSerializer.Deserialize<List<CardItemDto>>(json);
                if (cardsArray != null)
                {
                    foreach (var card in cardsArray)
                    {
                        if (string.IsNullOrEmpty(card.Source))
                            card.Source = fileName;
                    }
                    return cardsArray;
                }
            }
            catch { }

            return new List<CardItemDto>();
        }

        private string? ResolveCardsPath(string vaultId)
        {
            if (string.IsNullOrWhiteSpace(vaultId))
                return null;
            var vaultPath = _settings.GetVaults().FirstOrDefault(v => v.Id == vaultId)?.Path;
            if (string.IsNullOrEmpty(vaultPath))
                return null;
            return System.IO.Path.Combine(vaultPath, "cards");
        }
    }

    // DTOs
    public class GenerateRequest
    {
        public string NotePath { get; set; } = "";
    }

    public class BatchGenerateRequest
    {
        public string Directory { get; set; } = "";
        public bool Recursive { get; set; } = true;
    }

    public class JsonDeckData
    {
        public string? Name { get; set; }
        public List<JsonCard>? Cards { get; set; }
    }

    public class JsonCard
    {
        public string Front { get; set; } = "";
        public string Back { get; set; } = "";
        public List<string> Tags { get; set; } = new();
    }

    // 学习统计相关
    public class StudyRequest
    {
        public string? CardFront { get; set; }
        public string? CardBack { get; set; }
        public string? Deck { get; set; }
        public string? Result { get; set; } // remember, normal, forgot
    }

    public class StudyStats
    {
        public int Total { get; set; }
        public int Remembered { get; set; }
        public int Normal { get; set; }
        public int Forgot { get; set; }
        public List<StudyRecord>? History { get; set; }
    }

    public class StudyRecord
    {
        public string CardFront { get; set; } = "";
        public string CardBack { get; set; } = "";
        public string Deck { get; set; } = "";
        public string Result { get; set; } = "";
        public DateTime Time { get; set; }
    }

    public class StudyStatsResponse
    {
        public StudyStats? Today { get; set; }
        public StudySummary? Summary { get; set; }
        public List<DailyStats> DailyStats { get; set; } = new();
    }

    public class StudySummary
    {
        public int TotalCards { get; set; }
        public int Remembered { get; set; }
        public int Forgot { get; set; }
        public double Accuracy { get; set; }
        public int Streak { get; set; }
    }

    public class DailyStats
    {
        public string Date { get; set; } = "";
        public int Total { get; set; }
        public int Remembered { get; set; }
        public int Forgot { get; set; }
        public int Normal { get; set; }
    }
}