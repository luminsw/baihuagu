using TaskRunner.Services;
using Xunit;

namespace TaskRunner.Family.Tests.Services;

public class WikiLinkResolverTests
{
    #region ExtractWikiLinkTargets

    [Fact]
    public void ExtractWikiLinkTargets_EmptyList_ReturnsEmpty()
    {
        var result = WikiLinkResolver.ExtractWikiLinkTargets([]);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_NoLinks_ReturnsEmpty()
    {
        var notes = new List<Note>
        {
            new() { Title = "Test", Content = "No links here" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_SingleLink_ExtractsCorrectly()
    {
        var notes = new List<Note>
        {
            new() { Title = "Test", Content = "See [[Target Note]] for more" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Single(result);
        Assert.Contains("Target Note", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_MultipleLinks_ExtractsAll()
    {
        var notes = new List<Note>
        {
            new() { Title = "Test", Content = "See [[Link A]] and [[Link B]]" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Equal(2, result.Count);
        Assert.Contains("Link A", result);
        Assert.Contains("Link B", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_DuplicateLinks_Deduplicates()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "[[Same]]" },
            new() { Title = "B", Content = "[[Same]]" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Single(result);
        Assert.Contains("Same", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_CaseInsensitive_Deduplicates()
    {
        var notes = new List<Note>
        {
            new() { Title = "A", Content = "[[Note]]" },
            new() { Title = "B", Content = "[[note]]" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Single(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_LinkWithAlias_ExtractsTargetOnly()
    {
        var notes = new List<Note>
        {
            new() { Title = "Test", Content = "[[Target|Display Text]]" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Single(result);
        Assert.Contains("Target", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_LinkWithHeading_ExtractsTargetOnly()
    {
        var notes = new List<Note>
        {
            new() { Title = "Test", Content = "[[Target#Section]]" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Single(result);
        Assert.Contains("Target", result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_NullContent_SkipsNote()
    {
        var notes = new List<Note>
        {
            new() { Title = "Test", Content = null! }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractWikiLinkTargets_EmptyContent_SkipsNote()
    {
        var notes = new List<Note>
        {
            new() { Title = "Test", Content = "" }
        };
        var result = WikiLinkResolver.ExtractWikiLinkTargets(notes);
        Assert.Empty(result);
    }

    #endregion

    #region ResolveLinkToPath

    [Fact]
    public void ResolveLinkToPath_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(WikiLinkResolver.ResolveLinkToPath(null!, new Dictionary<string, string>()));
        Assert.Null(WikiLinkResolver.ResolveLinkToPath("", new Dictionary<string, string>()));
        Assert.Null(WikiLinkResolver.ResolveLinkToPath("   ", new Dictionary<string, string>()));
    }

    [Fact]
    public void ResolveLinkToPath_PathWithSlash_ReturnsAsIs()
    {
        var result = WikiLinkResolver.ResolveLinkToPath("folder/note", new Dictionary<string, string>());
        Assert.Equal("folder/note", result);
    }

    [Fact]
    public void ResolveLinkToPath_FoundInMap_ReturnsPath()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["My Note"] = "vault/folder/my-note.md"
        };
        var result = WikiLinkResolver.ResolveLinkToPath("My Note", map);
        Assert.Equal("vault/folder/my-note.md", result);
    }

    [Fact]
    public void ResolveLinkToPath_CaseInsensitive_FindsInMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["My Note"] = "vault/my-note.md"
        };
        var result = WikiLinkResolver.ResolveLinkToPath("my note", map);
        Assert.Equal("vault/my-note.md", result);
    }

    [Fact]
    public void ResolveLinkToPath_NotFoundInMap_ReturnsNull()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Other Note"] = "other.md"
        };
        var result = WikiLinkResolver.ResolveLinkToPath("My Note", map);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveLinkToPath_NullMap_ReturnsNull()
    {
        var result = WikiLinkResolver.ResolveLinkToPath("My Note", null!);
        Assert.Null(result);
    }

    #endregion

    #region BuildTitleToPathMap

    [Fact]
    public void BuildTitleToPathMap_EmptyList_ReturnsEmpty()
    {
        var result = WikiLinkResolver.BuildTitleToPathMap([], []);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_SingleNote_ReturnsMap()
    {
        var note = new Note { Title = "Test", Content = "Content" };
        var pathMap = new Dictionary<Note, string> { [note] = "test.md" };
        var result = WikiLinkResolver.BuildTitleToPathMap([note], pathMap);
        Assert.Single(result);
        Assert.Equal("test.md", result["Test"]);
    }

    [Fact]
    public void BuildTitleToPathMap_DuplicateTitles_ExcludesBoth()
    {
        var note1 = new Note { Title = "Same", Content = "A" };
        var note2 = new Note { Title = "Same", Content = "B" };
        var pathMap = new Dictionary<Note, string>
        {
            [note1] = "same1.md",
            [note2] = "same2.md"
        };
        var result = WikiLinkResolver.BuildTitleToPathMap([note1, note2], pathMap);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_CaseInsensitiveDuplicate_ExcludesBoth()
    {
        var note1 = new Note { Title = "Test", Content = "A" };
        var note2 = new Note { Title = "test", Content = "B" };
        var pathMap = new Dictionary<Note, string>
        {
            [note1] = "test1.md",
            [note2] = "test2.md"
        };
        var result = WikiLinkResolver.BuildTitleToPathMap([note1, note2], pathMap);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildTitleToPathMap_MultipleUniqueTitles_ReturnsAll()
    {
        var note1 = new Note { Title = "Alpha", Content = "A" };
        var note2 = new Note { Title = "Beta", Content = "B" };
        var pathMap = new Dictionary<Note, string>
        {
            [note1] = "alpha.md",
            [note2] = "beta.md"
        };
        var result = WikiLinkResolver.BuildTitleToPathMap([note1, note2], pathMap);
        Assert.Equal(2, result.Count);
        Assert.Equal("alpha.md", result["Alpha"]);
        Assert.Equal("beta.md", result["Beta"]);
    }

    #endregion
}