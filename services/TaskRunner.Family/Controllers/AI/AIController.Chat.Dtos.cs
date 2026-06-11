using Microsoft.AspNetCore.Mvc;
using TaskRunner.Contracts.Ai;

namespace TaskRunner.Controllers
{
    public partial class AIController
    {
    }

    public class AskRequest
    {
        public string Query { get; set; } = string.Empty;
        public bool SaveToVault { get; set; } = true;
        public string? VaultPath { get; set; }
        public string? VaultId { get; set; }
        public string? ProviderId { get; set; }
        public string? Model { get; set; }
        /// <summary>
        /// 是否启用 Function Calling（工具调用）。默认 false（生成笔记场景通常不需要工具）
        /// </summary>
        public bool? EnableTools { get; set; }
    }

    public class GenerateMissingNoteRequest
    {
        /// <summary>
        /// 缺失笔记的链接路径（如 "桂枝汤" 或 "方剂/桂枝汤"）
        /// </summary>
        public string LinkPath { get; set; } = string.Empty;

        public string? VaultId { get; set; }
        public string? ProviderId { get; set; }
        public string? Model { get; set; }
        /// <summary>
        /// 是否启用 Function Calling（工具调用）。默认 false
        /// </summary>
        public bool? EnableTools { get; set; }
    }
}
