using BaihuaguSdk.Services;
using Xunit;

namespace BaihuaguSdk.Tests.Services;

public class SyncServiceTests
{
    [Theory]
    [InlineData("file.md", true)]
    [InlineData("path/to/readme.json", true)]
    [InlineData("image.png", false)]
    [InlineData("photo.jpg", false)]
    [InlineData("notes.MD", true)] // case insensitive
    public void IsTextFile(string path, bool expected)
    {
        Assert.Equal(expected, SyncServiceImpl.IsTextFile(path));
    }

    [Theory]
    [InlineData("notes/readme.md")]
    [InlineData("simple-file.txt")]
    [InlineData("a/b/c/d/file.json")]
    public void AssertValidRelPath_Valid_ReturnsNormalized(string path)
    {
        var result = SyncServiceImpl.AssertValidRelPath(path);
        Assert.Equal(path, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute/path")]
    [InlineData("../escape")]
    [InlineData("file:name")]
    [InlineData("contains../inside")]
    public void AssertValidRelPath_Invalid_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => SyncServiceImpl.AssertValidRelPath(path));
    }

    [Fact]
    public void AssertValidRelPath_ConvertsBackslash()
    {
        var result = SyncServiceImpl.AssertValidRelPath(@"dir\file.md");
        Assert.Equal("dir/file.md", result);
    }
}
