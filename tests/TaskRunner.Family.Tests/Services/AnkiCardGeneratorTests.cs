using AnkiGen.Core;
using TaskRunner.Services;
using TaskRunner.Data;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TaskRunner.Family.Tests.Services;

public class AnkiCardGeneratorTests
{
    private AnkiCardGenerator CreateGenerator()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<AnkiCardGenerator>();
        
        var mockVaultSettings = new MockVaultSettingsService();
        var mockAiClient = (AiClientService?)null;
        var mockAiSettings = (AiSettingsService?)null;
        
        return new AnkiCardGenerator(mockVaultSettings, mockAiClient!, mockAiSettings!, logger);
    }

    [Fact]
    public void GetDeckName_WithSubdirectory_ReturnsQualifiedName()
    {
        var generator = CreateGenerator();
        
        var result = GetDeckName(generator, "伤寒论/桂枝汤");
        
        Assert.Equal("经方::伤寒论", result);
    }

    [Fact]
    public void GetDeckName_WithoutSubdirectory_ReturnsDefault()
    {
        var generator = CreateGenerator();
        
        var result = GetDeckName(generator, "桂枝汤");
        
        Assert.Equal("经方", result);
    }

    [Fact]
    public void GetTagsFromPath_WithNestedPath_ReturnsAllParts()
    {
        var generator = CreateGenerator();
        
        var result = GetTagsFromPath(generator, "伤寒论/太阳病/桂枝汤");
        
        Assert.Equal(new[] { "经方", "伤寒论", "太阳病" }, result);
    }

    [Fact]
    public void GetTagsFromPath_WithoutSubdirectory_ReturnsDefaultTag()
    {
        var generator = CreateGenerator();
        
        var result = GetTagsFromPath(generator, "桂枝汤");
        
        Assert.Equal(new[] { "经方" }, result);
    }

    [Fact]
    public void ParseQAFormat_ChineseQuestionMark_ExtractsAnswer()
    {
        var deck = AnkiDeck.Create("Test");
        var lines = new[]
        {
            "# 测试标题",
            "桂枝汤的组成是什么？",
            "桂枝三两，芍药三两，甘草二两，生姜三两，大枣十二枚",
            "",
            "另一个问题？",
            "另一个答案"
        };

        var generator = CreateGenerator();
        ParseQAFormat(generator, deck, lines, "测试标题", new List<string>());

        Assert.Equal(2, deck.Notes.Count);
        Assert.Contains(deck.Notes, n => n.Fields[0] == "桂枝汤的组成是什么？");
        Assert.Contains(deck.Notes, n => n.Fields[1] == "桂枝三两，芍药三两，甘草二两，生姜三两，大枣十二枚");
    }

    [Fact]
    public void ParseQAFormat_EnglishQuestionMark_ExtractsAnswer()
    {
        var deck = AnkiDeck.Create("Test");
        var lines = new[]
        {
            "# Title",
            "What is this?",
            "This is a test"
        };

        var generator = CreateGenerator();
        ParseQAFormat(generator, deck, lines, "Title", new List<string>());

        Assert.Single(deck.Notes);
        Assert.Equal("What is this?", deck.Notes[0].Fields[0]);
    }

    [Fact]
    public void ParseQAFormat_NoAnswer_IgnoresQuestion()
    {
        var deck = AnkiDeck.Create("Test");
        var lines = new[]
        {
            "# Title",
            "Question with no answer?"
        };

        var generator = CreateGenerator();
        ParseQAFormat(generator, deck, lines, "Title", new List<string>());

        Assert.Empty(deck.Notes);
    }

    [Fact]
    public void ParseListItems_ChineseColon_Found()
    {
        var deck = AnkiDeck.Create("Test");
        var lines = new[]
        {
            "# Title",
            "- 组成：桂枝三两，芍药三两",
            "- 功效：解肌发表，调和营卫"
        };

        var generator = CreateGenerator();
        ParseListItems(generator, deck, lines, "Title", new List<string>());

        Assert.Equal(2, deck.Notes.Count);
        Assert.Contains(deck.Notes, n => n.Fields[0] == "组成" && n.Fields[1] == "桂枝三两，芍药三两");
        Assert.Contains(deck.Notes, n => n.Fields[0] == "功效" && n.Fields[1] == "解肌发表，调和营卫");
    }

    [Fact]
    public void ParseListItems_AsteriskBullet_Found()
    {
        var deck = AnkiDeck.Create("Test");
        var lines = new[]
        {
            "# Title",
            "* 成分：中药"
        };

        var generator = CreateGenerator();
        ParseListItems(generator, deck, lines, "Title", new List<string>());

        Assert.Single(deck.Notes);
        Assert.Equal("成分", deck.Notes[0].Fields[0]);
    }

    [Fact]
    public void ParseListItems_NoColon_Ignored()
    {
        var deck = AnkiDeck.Create("Test");
        var lines = new[]
        {
            "# Title",
            "- 普通列表项"
        };

        var generator = CreateGenerator();
        ParseListItems(generator, deck, lines, "Title", new List<string>());

        Assert.Empty(deck.Notes);
    }

    [Fact]
    public void ParseDefinitions_ChineseDefinition_Found()
    {
        var deck = AnkiDeck.Create("Test");
        var content = "# Title\n桂枝汤是解肌发表、调和营卫的方剂。\n伤寒论是中医经典著作。";

        var generator = CreateGenerator();
        ParseDefinitions(generator, deck, content, "Title", new List<string>());

        Assert.Equal(2, deck.Notes.Count);
        Assert.Contains(deck.Notes, n => n.Fields[0] == "什么是桂枝汤？");
        Assert.Contains(deck.Notes, n => n.Fields[0] == "什么是伤寒论？");
    }

    [Fact]
    public void ParseDefinitions_ChineseColon_Found()
    {
        var deck = AnkiDeck.Create("Test");
        var content = "# Title\n桂枝汤：解肌发表、调和营卫的方剂。";

        var generator = CreateGenerator();
        ParseDefinitions(generator, deck, content, "Title", new List<string>());

        Assert.Single(deck.Notes);
        Assert.Contains(deck.Notes, n => n.Fields[0] == "什么是桂枝汤？");
    }

    [Fact]
    public void ParseAndAddCards_NoCards_CreatesSummaryCard()
    {
        var deck = AnkiDeck.Create("Test");
        var content = "# 测试标题\n这是一段普通文本内容。\n没有特殊格式。";

        var generator = CreateGenerator();
        ParseAndAddCards(generator, deck, content, "test/note");

        Assert.Single(deck.Notes);
        Assert.Contains(deck.Notes, n => n.Fields[0] == "测试标题 - 概述");
    }

    [Fact]
    public void ParseAndAddCards_HasCards_NoSummaryCard()
    {
        var deck = AnkiDeck.Create("Test");
        var content = "# 测试标题\n桂枝汤的组成是什么？\n桂枝三两";

        var generator = CreateGenerator();
        ParseAndAddCards(generator, deck, content, "test/note");

        Assert.Single(deck.Notes);
        Assert.DoesNotContain(deck.Notes, n => n.Fields[0].Contains("概述"));
    }

    private static string GetDeckName(AnkiCardGenerator generator, string notePath)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("GetDeckName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (string)method!.Invoke(generator, new object[] { notePath })!;
    }

    private static List<string> GetTagsFromPath(AnkiCardGenerator generator, string notePath)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("GetTagsFromPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (List<string>)method!.Invoke(generator, new object[] { notePath })!;
    }

    private static void ParseQAFormat(AnkiCardGenerator generator, AnkiDeck deck, string[] lines, string title, List<string> tags)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("ParseQAFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(generator, new object[] { deck, lines, title, tags });
    }

    private static void ParseListItems(AnkiCardGenerator generator, AnkiDeck deck, string[] lines, string title, List<string> tags)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("ParseListItems", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(generator, new object[] { deck, lines, title, tags });
    }

    private static void ParseDefinitions(AnkiCardGenerator generator, AnkiDeck deck, string content, string title, List<string> tags)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("ParseDefinitions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(generator, new object[] { deck, content, title, tags });
    }

    private static void ParseAndAddCards(AnkiCardGenerator generator, AnkiDeck deck, string content, string notePath)
    {
        var method = typeof(AnkiCardGenerator).GetMethod("ParseAndAddCards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(generator, new object[] { deck, content, notePath });
    }

    private class MockVaultSettingsService : VaultSettingsService
    {
        public MockVaultSettingsService() : base(CreateFactory(), LoggerFactory.Create(b => b.AddConsole()).CreateLogger<VaultSettingsService>())
        {
        }

        private static IDbContextFactory<VaultDbContext> CreateFactory()
        {
            var options = new DbContextOptionsBuilder<VaultDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new InMemoryDbContextFactory<VaultDbContext>(options);
        }
    }

    private class InMemoryDbContextFactory<TContext> : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        private readonly DbContextOptions<TContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<TContext> options)
        {
            _options = options;
        }

        public TContext CreateDbContext()
        {
            return Activator.CreateInstance(typeof(TContext), _options) as TContext ?? throw new InvalidOperationException();
        }
    }
}