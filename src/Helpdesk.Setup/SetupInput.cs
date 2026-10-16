using Helpdesk.AgentClient;

namespace Helpdesk.Setup;

/// <summary>The user-input boundary, separate from exact persisted credential target validation.</summary>
public static class SetupInput
{
    public static string NormalizeWebUrl(string value)
    {
        value = value.Trim();
        if (value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new AgentClientValidationException("RatelDesk Web URL must be HTTPS without credentials, query, or fragment.");
        // Check the original path as well: Uri can otherwise collapse dot segments into a supported path.
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOf('/', authorityStart);
        var path = authorityEnd < 0 ? "" : value[authorityEnd..];
        if (path is not ("" or "/" or "/mcp" or "/mcp/"))
            throw new AgentClientValidationException("Use the RatelDesk Web URL root or /mcp. Application path prefixes are not supported.");
        // Retain an explicitly entered port, including :443. Persisted credential validators
        // still compare the exact URI target rather than using this permissive input helper.
        return "https://" + value[authorityStart..(authorityEnd < 0 ? value.Length : authorityEnd)];
    }

    public static string HostDirectory => Environment.GetEnvironmentVariable("RATELDESK_MCP_HOST_DIRECTORY") ??
        Path.Combine(UserDataDirectory, "rateldesk", "http-host");
    public static string HttpClientProfileRoot => Environment.GetEnvironmentVariable("RATELDESK_MCP_CLIENT_DIRECTORY") ??
        Path.Combine(UserDataDirectory, "rateldesk", "http-client", "profiles");
    private static string UserDataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (Path.IsPathFullyQualified(appData)) return appData;
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Path.IsPathFullyQualified(userProfile)) return Path.Combine(userProfile, ".local", "share");
            throw new AgentClientValidationException("A durable per-user app data directory is unavailable. Select a private persistent setup store or use the Docker-host setup launcher.");
        }
    }

    public static void CheckPrivateStorage(string profilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(profilePath))!;
        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsLinux())
        {
            var mounted = IsPersistentContainerStorage(directory, File.ReadLines("/proc/self/mountinfo"));
            if (!mounted)
                throw new AgentClientValidationException("Container setup storage requires a persistent mounted directory. Run ./setup-http-mcp.sh from the extracted deployment archive on the Docker host.");
        }
        var probe = Path.Combine(directory, $".setup-storage-{Guid.NewGuid():N}");
        try
        {
            var revision = SecureConfigurationFiles.WriteAtomic(probe, "storage preflight\n", "");
            _ = SecureConfigurationFiles.ReadText(probe, requirePrivate: true);
            SecureConfigurationFiles.DeleteAtomic(probe, revision);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) &
                (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                throw new AgentClientValidationException("Setup storage must be a private owner-only directory (chmod 700). Choose a durable private location before approval.");
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
            if (File.Exists(probe + ".lock")) File.Delete(probe + ".lock");
        }
    }

    internal static bool IsPersistentContainerStorage(string directory, IEnumerable<string> mountInfo)
    {
        var selectedLength = -1;
        var persistent = false;
        foreach (var line in mountInfo.Take(4096))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Length < 8 || separator < 6 || separator + 2 >= fields.Length) continue;
            var mount = fields[4].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\");
            if (!Path.IsPathFullyQualified(mount)) continue;
            var relative = Path.GetRelativePath(mount, directory);
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) || mount.Length <= selectedLength) continue;
            // A nested tmpfs defeats a persistent parent mount; use the actual winning mount.
            selectedLength = mount.Length;
            var fileSystem = fields[separator + 1];
            // Docker-in-Docker can expose a persistent host bind backed by overlay.
            // Its non-root source subtree and separate mount boundary distinguish it
            // from the serving container's own disposable overlay filesystem.
            var mappedOverlay = fileSystem == "overlay" && fields[3] != "/" && Path.IsPathFullyQualified(fields[3]);
            persistent = mount != "/" && fields[5].Split(',').Contains("rw", StringComparer.Ordinal) &&
                (fileSystem != "overlay" || mappedOverlay) &&
                fileSystem is not ("tmpfs" or "ramfs" or "proc" or "sysfs" or "devtmpfs" or "devpts" or "cgroup" or "cgroup2" or
                    "mqueue" or "securityfs" or "debugfs" or "tracefs" or "hugetlbfs" or "configfs" or "autofs" or "nsfs" or "rpc_pipefs" or "pstore" or "fusectl");
        }
        return persistent;
    }
}
