using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace TaskRunner.Controllers;

    public class SearchResult
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public int Score { get; set; }
    }

    public class SearchStatus
    {
        public bool VaultConfigured { get; set; }
        public bool VaultExists { get; set; }
        public bool ObsidianRunning { get; set; }
        public string SearchMethod { get; set; } = "unknown"; // obsidian-cli, file-scan, semantic, none
        public string? ErrorMessage { get; set; }
    }

    public class ReindexRequest
    {
        public string VaultId { get; set; } = "";
    }
