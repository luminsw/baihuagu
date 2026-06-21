using TaskRunner.Services;
using Xunit;

namespace TaskRunner.Family.Tests.Services;

public class WikiLinkRewriterTests
{
    private readonly Dictionary<string, string> _titleToPath = new()
    {
        ["桂枝汤"] = "方剂/桂枝汤",
        ["麻黄汤"] = "方剂/麻黄汤",
        ["太阳病"] = "病机/太阳病",
        ["桂枝"] = "药材/桂枝"
    };

    #region RewriteMissingCategoryLinks - Basic

    [Fact]
    public void RewriteMissingCategoryLinks_EmptyContent_ReturnsEmpty()
    {
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks("", _titleToPath);
        Assert.Equal("", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_NullContent_ReturnsNull()
    {
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(null!, _titleToPath);
        Assert.Null(result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_EmptyMap_ReturnsOriginal()
    {
        var content = "See [[桂枝汤]] for details";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, new Dictionary<string, string>());
        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_NoWikiLinks_ReturnsOriginal()
    {
        var content = "No links here, just plain text";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal(content, result);
    }

    #endregion

    #region RewriteMissingCategoryLinks - Single Link

    [Fact]
    public void RewriteMissingCategoryLinks_SingleLink_RewritesCorrectly()
    {
        var content = "See [[桂枝汤]] for details";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("See [[方剂/桂枝汤]] for details", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_LinkNotFoundInMap_ReturnsOriginal()
    {
        var content = "See [[未知方剂]] for details";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_LinkAlreadyHasPath_ReturnsOriginal()
    {
        var content = "See [[方剂/桂枝汤]] for details";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal(content, result);
    }

    #endregion

    #region RewriteMissingCategoryLinks - Multiple Links

    [Fact]
    public void RewriteMissingCategoryLinks_MultipleLinks_RewritesAll()
    {
        var content = "See [[桂枝汤]] and [[麻黄汤]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("See [[方剂/桂枝汤]] and [[方剂/麻黄汤]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_MixedLinks_RewritesOnlyMissing()
    {
        var content = "See [[桂枝汤]] and [[方剂/麻黄汤]] and [[未知]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("See [[方剂/桂枝汤]] and [[方剂/麻黄汤]] and [[未知]]", result);
    }

    #endregion

    #region RewriteMissingCategoryLinks - With Alias

    [Fact]
    public void RewriteMissingCategoryLinks_WithAlias_PreservesAlias()
    {
        var content = "See [[桂枝汤|桂枝汤方]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("See [[方剂/桂枝汤|桂枝汤方]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_WithAliasAndPath_ReturnsOriginal()
    {
        var content = "See [[方剂/桂枝汤|桂枝汤方]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal(content, result);
    }

    #endregion

    #region RewriteMissingCategoryLinks - With Header

    [Fact]
    public void RewriteMissingCategoryLinks_WithHeader_PreservesHeader()
    {
        var content = "See [[桂枝汤#组成]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("See [[方剂/桂枝汤#组成]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_WithHeaderAndAlias_PreservesBoth()
    {
        var content = "See [[桂枝汤#组成|桂枝汤组成]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("See [[方剂/桂枝汤#组成|桂枝汤组成]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_WithHeaderAndPath_ReturnsOriginal()
    {
        var content = "See [[方剂/桂枝汤#组成]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal(content, result);
    }

    #endregion

    #region RewriteMissingCategoryLinks - Edge Cases

    [Fact]
    public void RewriteMissingCategoryLinks_BackslashInLink_Normalizes()
    {
        var map = new Dictionary<string, string> { ["Test"] = "folder/test" };
        var content = "See [[Test\\Sub]]"; // Backslash path
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, map);
        // Should not rewrite because it contains path separator after normalization
        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_WhitespaceInLink_Trims()
    {
        var content = "See [[ 桂枝汤 ]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("See [[方剂/桂枝汤]]", result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_EmptyWikiLink_ReturnsOriginal()
    {
        var content = "See [[]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal(content, result);
    }

    [Fact]
    public void RewriteMissingCategoryLinks_ChineseCharacters_WorksCorrectly()
    {
        var content = "参考[[太阳病]]和[[桂枝]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath);
        Assert.Equal("参考[[病机/太阳病]]和[[药材/桂枝]]", result);
    }

    #endregion

    #region RewriteMissingCategoryLinks - With Logger

    [Fact]
    public void RewriteMissingCategoryLinks_WithLogger_WorksSameAsWithout()
    {
        var content = "See [[桂枝汤]]";
        var result = WikiLinkRewriter.RewriteMissingCategoryLinks(content, _titleToPath, null);
        Assert.Equal("See [[方剂/桂枝汤]]", result);
    }

    #endregion
}