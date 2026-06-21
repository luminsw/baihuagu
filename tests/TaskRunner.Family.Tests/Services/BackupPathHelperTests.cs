using TaskRunner.Services;
using Xunit;

namespace TaskRunner.Family.Tests.Services;

public class BackupPathHelperTests
{
    #region MakeRelativePath

    [Fact]
    public void MakeRelativePath_NullPath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath(null!, "/base");
        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_EmptyPath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath("", "/base");
        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_NullBasePath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath("/path/to/file", null);
        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_EmptyBasePath_ReturnsNull()
    {
        var result = BackupPathHelper.MakeRelativePath("/path/to/file", "");
        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_SamePath_ReturnsEmpty()
    {
        var basePath = Path.GetTempPath();
        var result = BackupPathHelper.MakeRelativePath(basePath, basePath);
        Assert.Equal("", result);
    }

    [Fact]
    public void MakeRelativePath_ChildPath_ReturnsRelative()
    {
        var basePath = Path.GetTempPath();
        var childPath = Path.Combine(basePath, "child", "file.txt");
        var result = BackupPathHelper.MakeRelativePath(childPath, basePath);
        Assert.Equal("child/file.txt", result);
    }

    [Fact]
    public void MakeRelativePath_DeepChildPath_ReturnsRelative()
    {
        var basePath = Path.GetTempPath();
        var childPath = Path.Combine(basePath, "a", "b", "c", "file.txt");
        var result = BackupPathHelper.MakeRelativePath(childPath, basePath);
        Assert.Equal("a/b/c/file.txt", result);
    }

    [Fact]
    public void MakeRelativePath_DifferentRoot_ReturnsNull()
    {
        // On Linux, different roots are different mount points
        // On Windows, different drive letters
        // This test may behave differently across platforms
        var result = BackupPathHelper.MakeRelativePath("/completely/different/path", "/base/path");
        Assert.Null(result);
    }

    [Fact]
    public void MakeRelativePath_TrailingSlashInBase_Works()
    {
        var basePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var childPath = Path.Combine(basePath.TrimEnd(Path.DirectorySeparatorChar), "child", "file.txt");
        var result = BackupPathHelper.MakeRelativePath(childPath, basePath);
        Assert.Equal("child/file.txt", result);
    }

    [Fact]
    public void MakeRelativePath_NoTrailingSlashInBase_Works()
    {
        var basePath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var childPath = Path.Combine(basePath, "child", "file.txt");
        var result = BackupPathHelper.MakeRelativePath(childPath, basePath);
        Assert.Equal("child/file.txt", result);
    }

    [Fact]
    public void MakeRelativePath_UsesForwardSlash()
    {
        var basePath = Path.GetTempPath();
        var childPath = Path.Combine(basePath, "folder with spaces", "file.txt");
        var result = BackupPathHelper.MakeRelativePath(childPath, basePath);
        Assert.DoesNotContain('\\', result!);
    }

    #endregion

    #region RemapPath

    [Fact]
    public void RemapPath_NullPath_ReturnsNull()
    {
        var result = BackupPathHelper.RemapPath(null!);
        Assert.Null(result);
    }

    [Fact]
    public void RemapPath_EmptyPath_ReturnsEmpty()
    {
        var result = BackupPathHelper.RemapPath("");
        Assert.Equal("", result);
    }

    [Fact]
    public void RemapPath_ExistingDirectory_ReturnsSame()
    {
        var tempPath = Path.GetTempPath();
        var result = BackupPathHelper.RemapPath(tempPath);
        Assert.Equal(tempPath, result);
    }

    [Fact]
    public void RemapPath_ExistingFile_ReturnsSame()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var result = BackupPathHelper.RemapPath(tempFile);
            Assert.Equal(tempFile, result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void RemapPath_NonExistingPath_ReturnsSame()
    {
        var nonExisting = "/this/path/does/not/exist";
        var result = BackupPathHelper.RemapPath(nonExisting);
        Assert.Equal(nonExisting, result);
    }

    #endregion

    #region CopyDirectory

    [Fact]
    public void CopyDirectory_CopiesFilesAndSubdirectories()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "source_" + Guid.NewGuid());
        var targetDir = Path.Combine(Path.GetTempPath(), "target_" + Guid.NewGuid());

        try
        {
            // Create source structure
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(Path.Combine(sourceDir, "subdir"));
            File.WriteAllText(Path.Combine(sourceDir, "file1.txt"), "content1");
            File.WriteAllText(Path.Combine(sourceDir, "subdir", "file2.txt"), "content2");

            // Copy
            BackupPathHelper.CopyDirectory(sourceDir, targetDir);

            // Verify
            Assert.True(File.Exists(Path.Combine(targetDir, "file1.txt")));
            Assert.True(File.Exists(Path.Combine(targetDir, "subdir", "file2.txt")));
            Assert.Equal("content1", File.ReadAllText(Path.Combine(targetDir, "file1.txt")));
            Assert.Equal("content2", File.ReadAllText(Path.Combine(targetDir, "subdir", "file2.txt")));
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        }
    }

    [Fact]
    public void CopyDirectory_OverwritesExistingFiles()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "source_" + Guid.NewGuid());
        var targetDir = Path.Combine(Path.GetTempPath(), "target_" + Guid.NewGuid());

        try
        {
            // Create source
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "file.txt"), "new content");

            // Create target with old content
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "file.txt"), "old content");

            // Copy with overwrite
            BackupPathHelper.CopyDirectory(sourceDir, targetDir);

            // Verify overwritten
            Assert.Equal("new content", File.ReadAllText(Path.Combine(targetDir, "file.txt")));
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        }
    }

    [Fact]
    public void CopyDirectory_CreatesTargetDirectory()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "source_" + Guid.NewGuid());
        var targetDir = Path.Combine(Path.GetTempPath(), "target_" + Guid.NewGuid());

        try
        {
            // Create source
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "file.txt"), "content");

            // Target doesn't exist yet
            Assert.False(Directory.Exists(targetDir));

            // Copy
            BackupPathHelper.CopyDirectory(sourceDir, targetDir);

            // Verify target created
            Assert.True(Directory.Exists(targetDir));
            Assert.True(File.Exists(Path.Combine(targetDir, "file.txt")));
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        }
    }

    [Fact]
    public void CopyDirectory_Cancelled_Throws()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "source_" + Guid.NewGuid());
        var targetDir = Path.Combine(Path.GetTempPath(), "target_" + Guid.NewGuid());
        var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "file.txt"), "content");

            Assert.Throws<OperationCanceledException>(() =>
                BackupPathHelper.CopyDirectory(sourceDir, targetDir, true, cts.Token));
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
        }
    }

    #endregion
}