using TaskRunner.Services;
using Xunit;

namespace TaskRunner.Family.Tests.Services;

public class LeaderboardDtoTests
{
    #region DashboardData

    [Fact]
    public void DashboardData_DefaultValues_AreEmptyLists()
    {
        var data = new DashboardData();

        Assert.Empty(data.FamilyStats);
        Assert.Empty(data.WeeklyTrend);
        Assert.Empty(data.RecentAchievements);
        Assert.NotNull(data.ResultDistribution);
    }

    [Fact]
    public void DashboardData_CanSetProperties()
    {
        var data = new DashboardData
        {
            FamilyStats = new List<FamilyMemberStat> { new() { Name = "Test" } },
            WeeklyTrend = new List<DailyTrend> { new() { Date = "01-01" } },
            RecentAchievements = new List<RecentAchievement> { new() { Title = "Test" } },
            ResultDistribution = new ResultDistribution { Remember = 10, Hard = 5, Forgot = 2 }
        };

        Assert.Single(data.FamilyStats);
        Assert.Single(data.WeeklyTrend);
        Assert.Single(data.RecentAchievements);
        Assert.Equal(10, data.ResultDistribution.Remember);
    }

    #endregion

    #region FamilyMemberStat

    [Fact]
    public void FamilyMemberStat_DefaultValues_AreCorrect()
    {
        var stat = new FamilyMemberStat();

        Assert.Equal(0, stat.LearnerId);
        Assert.Equal("", stat.Name);
        Assert.Equal("", stat.AvatarEmoji);
        Assert.Equal("", stat.Color);
        Assert.Equal(0, stat.WeekTotal);
        Assert.Equal(0, stat.Accuracy);
        Assert.Equal(0, stat.Streak);
        Assert.Equal(0, stat.TotalCards);
    }

    [Fact]
    public void FamilyMemberStat_CanSetProperties()
    {
        var stat = new FamilyMemberStat
        {
            LearnerId = 1,
            Name = "小明",
            AvatarEmoji = "👦",
            Color = "#FF5733",
            WeekTotal = 20,
            Accuracy = 85.5,
            Streak = 7,
            TotalCards = 100
        };

        Assert.Equal(1, stat.LearnerId);
        Assert.Equal("小明", stat.Name);
        Assert.Equal("👦", stat.AvatarEmoji);
        Assert.Equal("#FF5733", stat.Color);
        Assert.Equal(20, stat.WeekTotal);
        Assert.Equal(85.5, stat.Accuracy);
        Assert.Equal(7, stat.Streak);
        Assert.Equal(100, stat.TotalCards);
    }

    #endregion

    #region DailyTrend

    [Fact]
    public void DailyTrend_DefaultValues_AreCorrect()
    {
        var trend = new DailyTrend();

        Assert.Equal("", trend.Date);
        Assert.Equal(0, trend.Count);
    }

    [Fact]
    public void DailyTrend_CanSetProperties()
    {
        var trend = new DailyTrend
        {
            Date = "06-21",
            Count = 15
        };

        Assert.Equal("06-21", trend.Date);
        Assert.Equal(15, trend.Count);
    }

    #endregion

    #region RecentAchievement

    [Fact]
    public void RecentAchievement_DefaultValues_AreCorrect()
    {
        var ach = new RecentAchievement();

        Assert.Equal("", ach.LearnerName);
        Assert.Equal("", ach.AvatarEmoji);
        Assert.Equal("", ach.Title);
        Assert.Equal("", ach.Icon);
        Assert.Equal("", ach.Tier);
        Assert.Equal(default, ach.UnlockedAt);
    }

    [Fact]
    public void RecentAchievement_CanSetProperties()
    {
        var unlockedAt = DateTime.UtcNow;
        var ach = new RecentAchievement
        {
            LearnerName = "小明",
            AvatarEmoji = "👦",
            Title = "第一步",
            Icon = "👶",
            Tier = "bronze",
            UnlockedAt = unlockedAt
        };

        Assert.Equal("小明", ach.LearnerName);
        Assert.Equal("👦", ach.AvatarEmoji);
        Assert.Equal("第一步", ach.Title);
        Assert.Equal("👶", ach.Icon);
        Assert.Equal("bronze", ach.Tier);
        Assert.Equal(unlockedAt, ach.UnlockedAt);
    }

    #endregion

    #region ResultDistribution

    [Fact]
    public void ResultDistribution_DefaultValues_AreZero()
    {
        var dist = new ResultDistribution();

        Assert.Equal(0, dist.Remember);
        Assert.Equal(0, dist.Hard);
        Assert.Equal(0, dist.Forgot);
    }

    [Fact]
    public void ResultDistribution_CanSetProperties()
    {
        var dist = new ResultDistribution
        {
            Remember = 50,
            Hard = 20,
            Forgot = 10
        };

        Assert.Equal(50, dist.Remember);
        Assert.Equal(20, dist.Hard);
        Assert.Equal(10, dist.Forgot);
    }

    [Fact]
    public void ResultDistribution_Total_CanBeCalculated()
    {
        var dist = new ResultDistribution { Remember = 50, Hard = 20, Forgot = 10 };
        var total = dist.Remember + dist.Hard + dist.Forgot;
        Assert.Equal(80, total);
    }

    #endregion

    #region LeaderboardEntry

    [Fact]
    public void LeaderboardEntry_DefaultValues_AreCorrect()
    {
        var entry = new LeaderboardEntry();

        Assert.Equal(0, entry.LearnerId);
        Assert.Equal("", entry.LearnerName);
        Assert.Equal("", entry.AvatarEmoji);
        Assert.Equal("", entry.Color);
        Assert.Equal(0, entry.CardsStudied);
        Assert.Equal(0, entry.Accuracy);
        Assert.Equal(0, entry.Score);
        Assert.Equal(0, entry.Streak);
        Assert.Equal(0, entry.Rank);
    }

    [Fact]
    public void LeaderboardEntry_CanSetProperties()
    {
        var entry = new LeaderboardEntry
        {
            LearnerId = 1,
            LearnerName = "小明",
            AvatarEmoji = "👦",
            Color = "#FF5733",
            CardsStudied = 50,
            Accuracy = 85.5,
            Score = 120,
            Streak = 7,
            Rank = 1
        };

        Assert.Equal(1, entry.LearnerId);
        Assert.Equal("小明", entry.LearnerName);
        Assert.Equal("👦", entry.AvatarEmoji);
        Assert.Equal("#FF5733", entry.Color);
        Assert.Equal(50, entry.CardsStudied);
        Assert.Equal(85.5, entry.Accuracy);
        Assert.Equal(120, entry.Score);
        Assert.Equal(7, entry.Streak);
        Assert.Equal(1, entry.Rank);
    }

    #endregion
}