using Helpdesk.AgentClient;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class SecureConfigurationFilesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"rateldesk-secure-config-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(directory, "profiles", "client.json");

    [Fact]
    public void Atomic_save_creates_private_files_and_directories_and_returns_revision()
    {
        var revision = SecureConfigurationFiles.WriteAtomic(FilePath, "private-token", "");

        Assert.Equal("private-token", SecureConfigurationFiles.ReadText(FilePath, requirePrivate: true));
        Assert.Equal(revision, SecureConfigurationFiles.Revision(FilePath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath + ".lock"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(FilePath)!));
        }
    }

    [Fact]
    public void Readonly_file_cannot_be_replaced_or_deleted_through_writable_parent_directory()
    {
        var revision = SecureConfigurationFiles.WriteAtomic(FilePath, "existing-fixture", "");
        File.SetAttributes(FilePath, File.GetAttributes(FilePath) | FileAttributes.ReadOnly);
        try
        {
            Assert.True(new FileInfo(FilePath).IsReadOnly);
            var error = Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.WriteAtomic(FilePath, "replacement-fixture", revision));
            Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
            Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.DeleteAtomic(FilePath, revision));
            Assert.Equal("existing-fixture", File.ReadAllText(FilePath));
            Assert.True(new FileInfo(FilePath).IsReadOnly);
        }
        finally { File.SetAttributes(FilePath, File.GetAttributes(FilePath) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public void Revision_conflict_preserves_existing_file_and_has_no_secret_in_error()
    {
        var initial = SecureConfigurationFiles.WriteAtomic(FilePath, "initial-secret");
        SecureConfigurationFiles.WriteAtomic(FilePath, "new-secret", initial);

        var error = Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.WriteAtomic(FilePath, "replacement-secret", initial));

        Assert.Equal("new-secret", SecureConfigurationFiles.ReadText(FilePath));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_initial_creations_save_one_complete_value()
    {
        using var start = new ManualResetEventSlim(false);
        var writers = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            start.Wait();
            try { SecureConfigurationFiles.WriteAtomic(FilePath, $"token-{index}", ""); return true; }
            catch (AgentClientValidationException) { return false; }
        })).ToArray();
        start.Set();

        var results = await Task.WhenAll(writers);

        Assert.Single(results, result => result);
        Assert.Matches("^token-[0-7]$", SecureConfigurationFiles.ReadText(FilePath, requirePrivate: true));
    }

    [Fact]
    public void Reads_are_bounded_and_failed_large_save_preserves_profile()
    {
        SecureConfigurationFiles.WriteAtomic(FilePath, "existing-secret");

        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.ReadText(FilePath, 4));
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.WriteAtomic(FilePath, new string('x', 65_537)));
        Assert.Equal("existing-secret", SecureConfigurationFiles.ReadText(FilePath));
    }

    [Fact]
    public void Missing_revision_is_null_and_explicit_reads_fail()
    {
        Assert.Null(SecureConfigurationFiles.Revision(FilePath));
        var error = Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.ReadText(FilePath));
        Assert.DoesNotContain(FilePath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_secret_file_is_rejected_and_is_never_silently_repaired()
    {
        if (OperatingSystem.IsWindows()) return;
        SecureConfigurationFiles.WriteAtomic(FilePath, "sensitive-value");
        File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.ReadText(FilePath, requirePrivate: true));
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.WriteAtomic(FilePath, "replacement"));
        Assert.Equal("sensitive-value", File.ReadAllText(FilePath));
        Assert.True((File.GetUnixFileMode(FilePath) & UnixFileMode.GroupRead) != 0);
    }

    [Fact]
    public void Symbolic_file_and_ancestor_links_are_rejected_without_modifying_target()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(directory);
        var realDirectory = Path.Combine(directory, "real");
        Directory.CreateDirectory(realDirectory);
        var target = Path.Combine(realDirectory, "secret");
        SecureConfigurationFiles.WriteAtomic(target, "existing-secret");
        var fileLink = Path.Combine(directory, "linked-secret");
        File.CreateSymbolicLink(fileLink, target);
        var directoryLink = Path.Combine(directory, "linked-directory");
        Directory.CreateSymbolicLink(directoryLink, realDirectory);

        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.ReadText(fileLink));
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.Revision(fileLink));
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.WriteAtomic(fileLink, "replacement"));
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.ReadText(Path.Combine(directoryLink, "secret")));
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.WriteAtomic(Path.Combine(directoryLink, "new-secret"), "replacement"));
        Assert.Equal("existing-secret", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(realDirectory, "new-secret")));
    }

    [Fact]
    public void Operation_lease_rejects_an_existing_public_or_symbolic_lock()
    {
        if (OperatingSystem.IsWindows()) return;
        using (SecureConfigurationFiles.AcquireOperationLease(FilePath)) { }
        var lockPath = FilePath + ".operation.lock";
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(lockPath));
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.AcquireOperationLease(FilePath));
        File.Delete(lockPath);
        var target = Path.Combine(directory, "unrelated-lock");
        SecureConfigurationFiles.WriteAtomic(target, "unchanged");
        File.CreateSymbolicLink(lockPath, target);
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.AcquireOperationLease(FilePath));
        Assert.Equal("unchanged", SecureConfigurationFiles.ReadText(target));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
