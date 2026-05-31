namespace TaskRunner.Services;
public class DefaultPromptProvider
{
    public PromptTemplate GetTemplateByName(string? industry) => GetTemplate(industry);
    public PromptTemplate GetTemplate(object? scene = null) => new();
    public class PromptTemplate
    {
        public string ChatSystemPrompt { get; set; } = "你是一个知识管理助手，请根据用户的知识库内容回答问题。";
        public string SplitSystemPrompt { get; set; } = "你是一个内容拆分助手。";
        public string SplitUserPrompt { get; set; } = "请将以下内容拆分为原子笔记：";
        public string SupplementUserPrompt { get; set; } = "请补充更多相关内容。";
        public string DisplayName { get; set; } = "通用";
        public List<string> DefaultCategories { get; set; } = ["通用"];
        public string ConfigurationAdvice { get; set; } = "";
        public string KnowledgeBuildPlaceholder { get; set; } = "";
        public string KnowledgeBuildDescription { get; set; } = "";
    }
}
