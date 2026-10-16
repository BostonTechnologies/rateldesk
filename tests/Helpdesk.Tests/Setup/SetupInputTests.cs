using Helpdesk.AgentClient;
using Helpdesk.Setup;
using Xunit;

namespace Helpdesk.Tests.Setup;

public sealed class SetupInputTests
{
    [Theory]
    [InlineData("https://desk.example", "https://desk.example")]
    [InlineData(" https://desk.example/ ", "https://desk.example")]
    [InlineData("https://desk.example/mcp", "https://desk.example")]
    [InlineData("https://desk.example:8443/mcp/", "https://desk.example:8443")]
    [InlineData("https://desk.example:443/mcp", "https://desk.example:443")]
    [InlineData("https://[2001:db8::1]:8443/mcp/", "https://[2001:db8::1]:8443")]
    public void Supported_user_urls_normalize_once_without_losing_authority(string input, string expected) =>
        Assert.Equal(expected, SetupInput.NormalizeWebUrl(input));

    [Theory]
    [InlineData("http://desk.example")]
    [InlineData("https://user:private@desk.example/mcp")]
    [InlineData("https://desk.example/mcp?token=private")]
    [InlineData("https://desk.example/mcp#private")]
    [InlineData("https://desk.example/prefix/mcp")]
    [InlineData("https://desk.example/prefix/../mcp")]
    [InlineData("https://desk.example/%6dcp")]
    [InlineData("https://desk.example//mcp")]
    [InlineData("https://desk.example\\mcp")]
    public void Unsupported_urls_report_safe_field_errors_without_echoing_input(string input)
    {
        var error = Assert.Throws<AgentClientValidationException>(() => SetupInput.NormalizeWebUrl(input));
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(input, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_probe_creates_private_directory_and_leaves_no_candidate_or_secret()
    {
        var temporary = Directory.CreateTempSubdirectory("rateldesk-setup-storage-");
        try
        {
            var directory = Path.Combine(temporary.FullName, "private");
            SetupInput.CheckPrivateStorage(Path.Combine(directory, "client.json"));
            Assert.Empty(Directory.EnumerateFiles(directory));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
        finally { temporary.Delete(recursive: true); }
    }

    [Fact]
    public void Existing_public_storage_is_rejected_before_it_can_receive_credentials()
    {
        if (OperatingSystem.IsWindows()) return;
        var temporary = Directory.CreateTempSubdirectory("rateldesk-setup-storage-");
        try
        {
            File.SetUnixFileMode(temporary.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            Assert.Throws<AgentClientValidationException>(() => SetupInput.CheckPrivateStorage(Path.Combine(temporary.FullName, "client.json")));
            Assert.Empty(Directory.EnumerateFiles(temporary.FullName));
        }
        finally { temporary.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("ext4", "rw,relatime", true)]
    [InlineData("xfs", "rw,relatime", true)]
    [InlineData("ext4", "ro,relatime", false)]
    [InlineData("tmpfs", "rw,nosuid", false)]
    [InlineData("ramfs", "rw", false)]
    [InlineData("overlay", "rw", false)]
    public void Container_preflight_requires_writable_durable_mount(string fileSystem, string options, bool expected)
    {
        var mounts = new[] { "1 0 0:1 / / rw - overlay overlay rw", $"2 1 0:2 / /data {options} - {fileSystem} /dev/example rw" };
        Assert.Equal(expected, SetupInput.IsPersistentContainerStorage("/data/client/profile.json", mounts));
    }

    [Fact]
    public void Container_preflight_uses_actual_nested_mount_and_respects_path_boundaries()
    {
        var mounts = new[] { "1 0 0:1 / / rw - overlay overlay rw", "2 1 0:2 / /data rw - ext4 /dev/example rw", "3 2 0:3 / /data/temporary rw - tmpfs tmpfs rw" };
        Assert.True(SetupInput.IsPersistentContainerStorage("/data/client", mounts));
        Assert.False(SetupInput.IsPersistentContainerStorage("/database/client", mounts));
        Assert.False(SetupInput.IsPersistentContainerStorage("/data/temporary/client", mounts));
    }

    [Fact]
    public void Container_preflight_accepts_Docker_host_overlay_bind_without_accepting_serving_overlay()
    {
        // Captured topology: the managed Docker host uses overlay, while its existing
        // workspace directory is explicitly bind-mounted into the serving container.
        var mounts = new[]
        {
            "90 1 0:42 / / rw,relatime - overlay overlay rw",
            "94 90 0:42 /workspace/rateldesk/work/docker-acceptance /probe rw,relatime - overlay overlay rw",
            "95 90 0:43 / /ephemeral rw,relatime - overlay overlay rw",
            "96 90 0:42 /workspace/rateldesk/work/docker-acceptance /read-only ro,relatime - overlay overlay rw"
        };
        Assert.True(SetupInput.IsPersistentContainerStorage("/probe/state/profile.json", mounts));
        Assert.False(SetupInput.IsPersistentContainerStorage("/probe-other/state", mounts));
        Assert.False(SetupInput.IsPersistentContainerStorage("/tmp/state", mounts));
        Assert.False(SetupInput.IsPersistentContainerStorage("/ephemeral/state", mounts));
        Assert.False(SetupInput.IsPersistentContainerStorage("/read-only/state", mounts));
    }

    [Theory]
    [InlineData("host", "http-client")]
    [InlineData("client", "http-host")]
    public async Task Conflicting_mode_and_target_fail_before_terminal_initialization(string mode, string target)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var code = await SetupCommands.InvokeAsync(["setup", "--mode", mode, "--target", target], SetupTransport.Http, output, error);
        Assert.Equal(78, code); Assert.Empty(output.ToString()); Assert.Contains("conflicts", error.ToString(), StringComparison.Ordinal);
    }
}
