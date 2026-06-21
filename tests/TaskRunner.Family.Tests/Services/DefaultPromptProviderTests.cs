using TaskRunner.Services;
using Xunit;

namespace TaskRunner.Family.Tests.Services;

public class DefaultPromptProviderTests
{
    [Fact]
    public void GetTemplateByName_WithNull_ReturnsDefaultTemplate()
    {
        var provider = new DefaultPromptProvider();
        var template = provider.GetTemplateByName(null);
        Assert.NotNull(template);
        Assert.Equal("通用", template.DisplayName);
    }

    [Fact]
    public void GetTemplate_WithNull_ReturnsDefaultTemplate()
    {
        var provider = new DefaultPromptProvider();
        var template = provider.GetTemplate(null);
        Assert.NotNull(template);
        Assert.Equal("通用", template.DisplayName);
    }

    [Fact]
    public void GetTemplate_WithAnyScene_ReturnsDefaultTemplate()
    {
        var provider = new DefaultPromptProvider();
        var template = provider.GetTemplate("any-scene");
        Assert.NotNull(template);
        Assert.Equal("通用", template.DisplayName);
    }

    [Fact]
    public void Template_HasChatSystemPrompt()
    {
        var provider = new DefaultPromptProvider();
        var template = provider.GetTemplate();
        Assert.NotNull(template.ChatSystemPrompt);
        Assert.Contains("知识管理助手", template.ChatSystemPrompt);
    }

    [Fact]
    public void Template_HasSplitSystemPrompt()
    {
        var provider = new DefaultPromptProvider();
        var template = provider.GetTemplate();
        Assert.NotNull(template.SplitSystemPrompt);
        Assert.Contains("原子笔记", template.SplitSystemPrompt);
    }

    [Fact]
    public void Template_HasSplitUserPrompt()
    {
        var provider = new DefaultPromptProvider();
        var template = provider.GetTemplate();
        Assert.NotNull(template.SplitUserPrompt);
        Assert.Contains("拆分为原子笔记", template.SplitUserPrompt);
    }

    [Fact]
    public void Template_HasDefaultCategories()
    {
        var provider = new DefaultPromptProvider();
        var template = provider.GetTemplate();
        Assert.NotNull(template.DefaultCategories);
        Assert.Contains("通用", template.DefaultCategories);
    }
}