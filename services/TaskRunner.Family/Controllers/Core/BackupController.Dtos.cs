using TaskRunner.Services;

namespace TaskRunner.Controllers
{
    public class FullBackupRequest
    {
        public string? BackupDir { get; set; }
        public string? Password { get; set; }
    }

    public class FullBackupResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string? BackupPath { get; set; }
        public DateTime? BackupTime { get; set; }
        public long? FileSize { get; set; }
    }

    public class FullRestoreRequest
    {
        public string BackupPath { get; set; } = "";
        public string? Password { get; set; }
        public string? VaultRootPathOverride { get; set; }
        public bool Overwrite { get; set; } = true;
    }

    public class FullRestoreResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string? SourcePlatform { get; set; }
        public string? SourceOS { get; set; }
        public DateTime? RestoredAt { get; set; }
    }

    public class ValidateBackupRequest
    {
        public string BackupPath { get; set; } = "";
        public string? Password { get; set; }
    }

    public class ValidateBackupResponse
    {
        public bool Success { get; set; }
        public bool IsValid { get; set; }
        public int Version { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? SourcePlatform { get; set; }
        public string? SourceOS { get; set; }
        public bool HasPassword { get; set; }
        public bool HasDatabase { get; set; }
        public bool HasConfig { get; set; }
        public bool HasVaults { get; set; }
        public int VaultCount { get; set; }
        public string Message { get; set; } = "";
    }

    public class BackupListResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public List<BackupFileInfo> Backups { get; set; } = new();
    }
}
