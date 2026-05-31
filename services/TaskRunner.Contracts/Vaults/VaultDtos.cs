namespace TaskRunner.Contracts.Vaults;

public class VaultConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 是否为付费知识库（官网模式下有效）
    /// </summary>
    public bool IsPaid { get; set; }

    /// <summary>
    /// 知识库标签
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 所属行业（如：笔记、开发等）
    /// </summary>
    public string Industry { get; set; } = string.Empty;

    /// <summary>
    /// 知识库来源：local=本地创建, mobile=移动端推送, cloud=官网同步
    /// </summary>
    public string Source { get; set; } = "local";
}

public class VaultsResponse
{
    public List<VaultConfig> Vaults { get; set; } = new();
}

public class VaultNoteResponse
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Modified { get; set; }
    public List<string>? Tags { get; set; }
}

public class VaultRootResponse
{
    public string VaultPath { get; set; } = "";
}

public class VaultRootRequest
{
    public string VaultPath { get; set; } = "";
}

/// <summary>
/// 移动端推送 AI 生成知识库的请求
/// </summary>
public class MobileVaultPushRequest
{
    public string VaultName { get; set; } = "";
    public string Industry { get; set; } = "";
    public List<MobileVaultNoteDto> Notes { get; set; } = new();
}

public class MobileVaultNoteDto
{
    public string RelPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";
}

/// <summary>
/// 知识库浏览项（目录或笔记）
/// </summary>
public class VaultBrowseItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long? Size { get; set; }
    public DateTime? Modified { get; set; }
}

/// <summary>
/// 知识库浏览响应
/// </summary>
public class VaultBrowseResponse
{
    public string VaultId { get; set; } = "";
    public string VaultName { get; set; } = "";
    public string CurrentPath { get; set; } = "";
    public List<VaultBrowseItem> Items { get; set; } = new();
}
