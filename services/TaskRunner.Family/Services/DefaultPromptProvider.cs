namespace TaskRunner.Services;
public class DefaultPromptProvider
{
    public PromptTemplate GetTemplateByName(string? industry) => GetTemplate(industry);
    public PromptTemplate GetTemplate(object? scene = null) => new();
    public class PromptTemplate
    {
        public string ChatSystemPrompt { get; set; } = "你是一个知识管理助手，请根据用户的知识库内容回答问题。";
        public string SplitSystemPrompt { get; set; } = "你是一位专业的内容拆分专家。你的任务是将长文本拆分为「原子笔记」——每个笔记只聚焦一个核心概念，内容高度结构化，拒绝冗长描述。";
        public string SplitUserPrompt { get; set; } = "请将以下内容拆分为原子笔记。每条笔记必须遵循以下原则：\n\n1. 聚焦单一主题：一条笔记只讲一个核心概念\n2. 高度结构化：必须包含核心定义（1-3句话）、关键要点（3-5条列表）、关联概念（1-2个）、记忆锚点（口诀/类比）、典型场景/案例\n3. 无冗余：不讨论历史沿革、文化背景、个人经验\n4. 使用 Markdown 格式\n5. 语言专业、清晰、客观\n\n原始内容：";
        public string SupplementUserPrompt { get; set; } = "请检查是否有遗漏的核心概念，补充为新的原子笔记。保持同样的格式和原则。";
        public string DisplayName { get; set; } = "通用";
        public List<string> DefaultCategories { get; set; } = ["通用"];
        public string ConfigurationAdvice { get; set; } = "";
        public string KnowledgeBuildPlaceholder { get; set; } = "";
        public string KnowledgeBuildDescription { get; set; } = "";
    }
}
