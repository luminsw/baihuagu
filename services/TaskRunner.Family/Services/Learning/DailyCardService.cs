using System.Security.Cryptography;
using System.Text.Json;
using TaskRunner.Contracts.Anki;
using TaskRunner.Helpers;
using Microsoft.EntityFrameworkCore;
using TaskRunner.Data;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services;

/// <summary>
/// 每日一帖服务：每日卡片推送、学习进度跟踪、家长出题
/// 同时维护文件系统记录（兼容旧数据）和 SQLite StudyActivities（新数据源）
/// </summary>
public class DailyCardService
{
    private readonly IDbContextFactory<FamilyDbContext> _dbFactory;
    private readonly LearnerService _learnerService;
    private readonly CardRepository _cardRepo;
    private readonly ILogger<DailyCardService> _logger;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _fileLocks = new();

    public DailyCardService(
        IDbContextFactory<FamilyDbContext> dbFactory,
        LearnerService learnerService,
        CardRepository cardRepo,
        ILogger<DailyCardService> logger)
    {
        _dbFactory = dbFactory;
        _learnerService = learnerService;
        _cardRepo = cardRepo;
        _logger = logger;
    }

    /// <summary>
    /// 艾宾浩斯间隔序列（天）
    /// </summary>
    private static readonly int[] _intervalSequence = new[] { 1, 2, 4, 7, 15, 30, 60, 120 };

    /// <summary>
    /// 获取今日卡片（智能复习调度：优先复习到期卡片，其次新卡片，最后随机）
    /// </summary>
    public async Task<DailyCardResult> GetTodayCardAsync(string vaultId, int? seed = null)
    {
        var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
        if (string.IsNullOrEmpty(cardsPath) || !Directory.Exists(cardsPath))
        {
            return new DailyCardResult { HasCard = false, Message = "暂无卡片，请先生成卡片" };
        }

        var allCards = _cardRepo.LoadAllCards(cardsPath);
        if (allCards.Count == 0)
        {
            return new DailyCardResult { HasCard = false, Message = "暂无卡片，请先生成卡片" };
        }

        var todayStudied = GetTodayStudiedIds(vaultId);
        var today = DateTime.UtcNow.Date;
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();

        // 1. 优先：今日到期的复习卡片（且今天还没学过）
        var dueCards = await _cardRepo.GetDueReviewsAsync(vaultId, today);
        var dueUnstudied = dueCards.Where(c => !todayStudied.Contains(c.Id)).ToList();
        if (dueUnstudied.Count > 0)
        {
            var card = dueUnstudied[rng.Next(dueUnstudied.Count)];
            var progress = GetTodayProgress(vaultId);
            return new DailyCardResult
            {
                HasCard = true,
                Card = card,
                TodayProgress = progress,
                Remaining = Math.Max(0, progress.Target - progress.Completed),
                IsReview = true
            };
        }

        // 2. 其次：新卡片（从未学过的）
        var studiedIds = await _cardRepo.GetAllStudiedIdsAsync(vaultId);
        var newCards = allCards.Where(c => !studiedIds.Contains(c.Id) && !todayStudied.Contains(c.Id)).ToList();
        if (newCards.Count > 0)
        {
            var card = newCards[rng.Next(newCards.Count)];
            var progress = GetTodayProgress(vaultId);
            return new DailyCardResult
            {
                HasCard = true,
                Card = card,
                TodayProgress = progress,
                Remaining = Math.Max(0, progress.Target - progress.Completed)
            };
        }

        // 3. 最后：未学过的任意卡片
        var unstudied = allCards.Where(c => !todayStudied.Contains(c.Id)).ToList();
        var pool = unstudied.Count > 0 ? unstudied : allCards;
        var fallbackCard = pool[rng.Next(pool.Count)];
        var fallbackProgress = GetTodayProgress(vaultId);

        return new DailyCardResult
        {
            HasCard = true,
            Card = fallbackCard,
            TodayProgress = fallbackProgress,
            Remaining = Math.Max(0, fallbackProgress.Target - fallbackProgress.Completed)
        };
    }

    /// <summary>
    /// 记录今日卡片学习结果（双写：文件系统 + SQLite）并更新复习状态
    /// </summary>
    public async Task<bool> RecordAnswerAsync(string vaultId, string cardId, string result)
    {
        var fileOk = WriteFileRecord(vaultId, cardId, result);
        var dbOk = await WriteDbRecordAsync(vaultId, cardId, result);
        await UpdateReviewStateAsync(vaultId, cardId, result);
        return fileOk || dbOk;
    }

    private bool WriteFileRecord(string vaultId, string cardId, string result)
    {
        try
        {
            var studyDir = _cardRepo.GetStudyDir(vaultId);
            if (string.IsNullOrEmpty(studyDir)) return false;

            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var dailyFile = Path.Combine(studyDir, $"daily-{today}.json");

            // 按 vault+date 加内存锁，防止并发覆盖
            var lockKey = $"{vaultId}:{today}";
            var fileLock = _fileLocks.GetOrAdd(lockKey, _ => new object());
            lock (fileLock)
            {
                var daily = ReadDailyRecord(dailyFile);
                if (!daily.Answers.ContainsKey(cardId))
                {
                    daily.Answers[cardId] = result;
                    daily.Completed++;
                }
                else
                {
                    daily.Answers[cardId] = result;
                }

                WriteDailyRecord(dailyFile, daily);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "文件系统记录学习结果失败（非关键）");
            return false;
        }
    }

    private async Task<bool> WriteDbRecordAsync(string vaultId, string cardId, string result)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var learner = await _learnerService.GetDefaultAsync();
            var learnerId = learner?.Id ?? 0;
            if (learnerId == 0)
            {
                // 没有学习者时自动创建一个默认学习者
                var newLearner = await _learnerService.CreateAsync("默认学习者");
                learnerId = newLearner.Id;
            }

            db.StudyActivities.Add(new StudyActivity
            {
                LearnerId = learnerId,
                VaultId = vaultId,
                ActivityType = "study",
                CardId = cardId,
                Result = result,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQLite 记录学习结果失败");
            return false;
        }
    }

    /// <summary>
    /// 获取今日学习进度（优先 SQLite，回退文件系统）
    /// </summary>
    public DailyProgress GetTodayProgress(string vaultId)
    {
        try
        {
            var progress = GetTodayProgressFromDb(vaultId);
            if (progress != null) return progress;
            return GetTodayProgressFromFiles(vaultId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取今日进度失败");
            return new DailyProgress();
        }
    }

    private DailyProgress? GetTodayProgressFromDb(string vaultId)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var today = DateTime.UtcNow.Date;
            var todayCount = db.StudyActivities
                .Count(a => a.VaultId == vaultId && a.ActivityType == "study" && a.CreatedAt.Date == today);

            var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
            var totalCards = 0;
            if (!string.IsNullOrEmpty(cardsPath) && Directory.Exists(cardsPath))
            {
                totalCards = _cardRepo.LoadAllCards(cardsPath).Count;
            }

            var streak = CalculateStreakFromDb(vaultId);

            return new DailyProgress
            {
                Completed = todayCount,
                Target = Math.Min(10, Math.Max(3, totalCards > 0 ? totalCards / 10 : 5)),
                TotalCards = totalCards,
                Streak = streak
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "从 SQLite 获取今日进度失败，回退到文件系统");
            return null;
        }
    }

    private DailyProgress GetTodayProgressFromFiles(string vaultId)
    {
        var studyDir = _cardRepo.GetStudyDir(vaultId);
        if (string.IsNullOrEmpty(studyDir)) return new DailyProgress();

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var dailyFile = Path.Combine(studyDir, $"daily-{today}.json");
        var daily = ReadDailyRecord(dailyFile);

        var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
        var totalCards = 0;
        if (!string.IsNullOrEmpty(cardsPath) && Directory.Exists(cardsPath))
        {
            totalCards = _cardRepo.LoadAllCards(cardsPath).Count;
        }

        var streak = CalculateStreakFromFiles(vaultId);

        return new DailyProgress
        {
            Completed = daily.Completed,
            Target = Math.Min(10, Math.Max(3, totalCards > 0 ? totalCards / 10 : 5)),
            TotalCards = totalCards,
            Streak = streak
        };
    }

    /// <summary>
    /// 家长出题：保存自定义卡片
    /// </summary>
    public bool SaveCustomCard(string vaultId, CustomCardRequest request)
    {
        try
        {
            var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
            if (string.IsNullOrEmpty(cardsPath)) return false;

            Directory.CreateDirectory(cardsPath);
            var customFile = Path.Combine(cardsPath, "custom.json");

            var deck = new JsonDeckData
            {
                Name = request.Deck ?? "家长出题",
                Cards = new List<JsonCard>()
            };

            if (File.Exists(customFile))
            {
                var json = File.ReadAllText(customFile);
                var deserialized = JsonSerializer.Deserialize<JsonDeckData>(json);
                if (deserialized != null)
                {
                    deck = deserialized;
                }
            }

            deck ??= new JsonDeckData
            {
                Name = request.Deck ?? "家长出题",
                Cards = new List<JsonCard>()
            };

            deck.Cards ??= new List<JsonCard>();
            deck.Cards.Add(new JsonCard
            {
                Front = request.Front,
                Back = request.Back,
                Tags = request.Tags ?? new List<string>()
            });

            var output = JsonSerializer.Serialize(deck, JsonHelper.IndentedUnicode);
            File.WriteAllText(customFile, output);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存自定义卡片失败");
            return false;
        }
    }

    /// <summary>
    /// 获取最近学过的卡片（用于复习）
    /// </summary>
    public List<StudiedCard> GetRecentStudied(string vaultId, int days = 7)
    {
        var result = new List<StudiedCard>();
        try
        {
            var studyDir = _cardRepo.GetStudyDir(vaultId);
            if (string.IsNullOrEmpty(studyDir)) return result;

            var cardsPath = _cardRepo.ResolveCardsPath(vaultId);
            var allCards = cardsPath != null && Directory.Exists(cardsPath) ? _cardRepo.LoadAllCards(cardsPath) : new List<CardItem>();
            var cardDict = allCards.ToDictionary(c => c.Id, c => c);

            for (int i = 0; i < days; i++)
            {
                var date = DateTime.Today.AddDays(-i);
                var file = Path.Combine(studyDir, $"daily-{date:yyyy-MM-dd}.json");
                if (!File.Exists(file)) continue;

                var daily = ReadDailyRecord(file);
                foreach (var kv in daily.Answers)
                {
                    if (cardDict.TryGetValue(kv.Key, out var card))
                    {
                        result.Add(new StudiedCard
                        {
                            Card = card,
                            Result = kv.Value,
                            Date = date.ToString("yyyy-MM-dd")
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取最近学习记录失败");
        }
        return result;
    }

    private HashSet<string> GetTodayStudiedIds(string vaultId)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var today = DateTime.UtcNow.Date;
            var ids = db.StudyActivities
                .Where(a => a.VaultId == vaultId && a.ActivityType == "study" && a.CreatedAt.Date == today && a.CardId != null)
                .Select(a => a.CardId!)
                .ToHashSet();
            if (ids.Count > 0) return ids;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "从 SQLite 获取今日已学失败，回退文件系统");
        }

        try
        {
            var studyDir = _cardRepo.GetStudyDir(vaultId);
            if (string.IsNullOrEmpty(studyDir)) return new HashSet<string>();

            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var file = Path.Combine(studyDir, $"daily-{today}.json");
            if (!File.Exists(file)) return new HashSet<string>();

            var daily = ReadDailyRecord(file);
            return new HashSet<string>(daily.Answers.Keys);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "从文件读取今日已学ID失败"); return new HashSet<string>(); }
    }

    private int CalculateStreakFromDb(string vaultId)
    {
        try
        {
            using var db = _dbFactory.CreateDbContext();
            var dates = db.StudyActivities
                .Where(a => a.VaultId == vaultId && a.ActivityType == "study")
                .Select(a => a.CreatedAt.Date)
                .Distinct()
                .OrderByDescending(d => d)
                .ToList();

            int streak = 0;
            var today = DateTime.UtcNow.Date;
            for (int i = 0; i < dates.Count; i++)
            {
                var expected = today.AddDays(-i);
                if (dates[i] == expected || (i == 0 && dates[i] == expected.AddDays(-1)))
                {
                    streak++;
                }
                else
                {
                    break;
                }
            }
            return streak;
        }
        catch
        {
            return 0;
        }
    }

    private int CalculateStreakFromFiles(string vaultId)
    {
        var studyDir = _cardRepo.GetStudyDir(vaultId);
        if (string.IsNullOrEmpty(studyDir) || !Directory.Exists(studyDir)) return 0;

        int streak = 0;
        for (int i = 0; ; i++)
        {
            var date = DateTime.Today.AddDays(-i);
            var file = Path.Combine(studyDir, $"daily-{date:yyyy-MM-dd}.json");
            if (!File.Exists(file))
            {
                if (i > 0) break;
                continue;
            }

            var daily = ReadDailyRecord(file);
            if (daily.Completed > 0)
                streak++;
            else if (i > 0)
                break;
        }
        return streak;
    }

    private DailyRecord ReadDailyRecord(string file)
    {
        if (!File.Exists(file)) return new DailyRecord();
        try
        {
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<DailyRecord>(json) ?? new DailyRecord();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "读取每日记录失败: {File}", file); return new DailyRecord(); }
    }

    private void WriteDailyRecord(string file, DailyRecord record)
    {
        var json = JsonSerializer.Serialize(record, JsonHelper.IndentedUnicode);
        File.WriteAllText(file, json);
    }

    /// <summary>
    /// 更新卡片复习状态（艾宾浩斯调度）
    /// </summary>
    private async Task UpdateReviewStateAsync(string vaultId, string cardId, string result)
    {
        try
        {
            var learner = await _learnerService.GetDefaultAsync();
            var learnerId = learner?.Id ?? 0;
            if (learnerId == 0) return;

            using var db = await _dbFactory.CreateDbContextAsync();
            var state = await db.CardReviewStates
                .FirstOrDefaultAsync(r => r.LearnerId == learnerId && r.VaultId == vaultId && r.CardId == cardId);

            if (state == null)
            {
                state = new CardReviewState
                {
                    LearnerId = learnerId,
                    VaultId = vaultId,
                    CardId = cardId,
                    IntervalDays = 1,
                    NextReviewDate = DateTime.UtcNow.Date.AddDays(1),
                    ConsecutiveRemember = 0,
                    TotalReviews = 1,
                    LastResult = result
                };
                db.CardReviewStates.Add(state);
            }
            else
            {
                state.TotalReviews++;
                state.LastResult = result;

                switch (result?.ToLower())
                {
                    case "remember":
                        state.ConsecutiveRemember++;
                        var idx = Math.Min(state.ConsecutiveRemember - 1, _intervalSequence.Length - 1);
                        state.IntervalDays = _intervalSequence[idx];
                        break;
                    case "hard":
                        // 间隔不变，连续记得次数不变
                        break;
                    case "forgot":
                        state.ConsecutiveRemember = 0;
                        state.IntervalDays = 1;
                        break;
                }

                state.NextReviewDate = DateTime.UtcNow.Date.AddDays(state.IntervalDays);
                db.CardReviewStates.Update(state);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "更新复习状态失败（不影响主流程）");
        }
    }
}


