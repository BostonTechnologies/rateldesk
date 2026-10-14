using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Helpdesk.AgentClient;

/// <summary>Private, bounded configuration storage shared by setup and headless clients.</summary>
public static class SecureConfigurationFiles
{
    public const int DefaultMaxBytes = 65_536;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectoryMode = PrivateFileMode | UnixFileMode.UserExecute;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string ReadText(string path, int maxBytes = DefaultMaxBytes, bool requirePrivate = false)
        => Guard(() => Utf8.GetString(ReadBytes(Normalize(path), maxBytes, requirePrivate)));

    /// <summary>Returns the hash of the file bytes, or null if the file does not exist.</summary>
    public static string? Revision(string path) => Guard(() => RevisionCore(Normalize(path)));

    /// <summary>Serializes a complete profile operation independently of its atomic file writes.</summary>
    public static IDisposable AcquireOperationLease(string path, CancellationToken cancellationToken = default) => Guard(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Normalize(path);
        RejectLinks(destination);
        CreatePrivateDirectories(Path.GetDirectoryName(destination)!);
        return AcquireLock(destination + ".operation.lock", cancellationToken);
    });

    /// <summary>Removes a reviewed local file only while its revision still matches.</summary>
    public static void DeleteAtomic(string path, string? expectedRevision = null) => Guard(() =>
    {
        var destination = Normalize(path);
        RejectLinks(destination);
        if (!File.Exists(destination)) throw new AgentClientValidationException("The selected configuration file does not exist.");
        using var writeLock = AcquireLock(destination + ".lock");
        RejectLinks(destination);
        RequirePrivateFile(destination);
        if (expectedRevision is not null && !string.Equals(expectedRevision, RevisionCore(destination), StringComparison.Ordinal))
            throw new AgentClientValidationException("Configuration changed; reload it before removing.");
        File.Delete(destination);
        return true;
    });

    /// <summary>
    /// Atomically saves a private file. Null revision permits replacement; an empty revision
    /// requires a missing file. Any other revision must match the current file bytes.
    /// </summary>
    public static string WriteAtomic(string path, string text, string? expectedRevision = null) => Guard(() =>
    {
        var destination = Normalize(path);
        var bytes = Utf8.GetBytes(text);
        if (bytes.Length > DefaultMaxBytes)
            throw new AgentClientValidationException("Configuration exceeds the permitted size.");
        RejectLinks(destination);
        var directory = Path.GetDirectoryName(destination)!;
        CreatePrivateDirectories(directory);
        var lockPath = destination + ".lock";
        using var writeLock = AcquireLock(lockPath);
        RejectLinks(destination);
        if (File.Exists(destination)) RequirePrivateFile(destination);
        var currentRevision = RevisionCore(destination);
        if (expectedRevision is not null && !string.Equals(expectedRevision, currentRevision ?? "", StringComparison.Ordinal))
            throw new AgentClientValidationException("Configuration changed; reload it before saving.");

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = OpenPrivateFile(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                RequirePrivateFile(temporary, stream);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Check immediately before replacement as well as before opening files.
            RejectLinks(destination);
            RejectLinks(temporary);
            RequirePrivateFile(temporary);
            if (expectedRevision is not null && !string.Equals(expectedRevision, RevisionCore(destination) ?? "", StringComparison.Ordinal))
                throw new AgentClientValidationException("Configuration changed; reload it before saving.");
            File.Move(temporary, destination, overwrite: true);
            RequirePrivateFile(destination);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    });

    private static byte[] ReadBytes(string path, int maxBytes, bool requirePrivate)
    {
        if (maxBytes <= 0 || maxBytes > DefaultMaxBytes)
            throw new AgentClientValidationException("Configuration read limit is invalid.");
        RejectLinks(path);
        if (requirePrivate) RequirePrivateFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        RejectLinks(path);
        if (requirePrivate) RequirePrivateFile(path);
        if (stream.Length > maxBytes)
            throw new AgentClientValidationException("Configuration exceeds the permitted size.");
        using var bytes = new MemoryStream();
        var buffer = new byte[Math.Min(maxBytes + 1, 4096)];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (bytes.Length + count > maxBytes)
                throw new AgentClientValidationException("Configuration exceeds the permitted size.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    private static string? RevisionCore(string path)
    {
        RejectLinks(path);
        return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(ReadBytes(path, DefaultMaxBytes, false))) : null;
    }

    private static FileStream AcquireLock(string path, CancellationToken cancellationToken = default)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RejectLinks(path);
                if (File.Exists(path)) RequirePrivateFile(path);
                var stream = OpenPrivateFile(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                try
                {
                    RejectLinks(path);
                    RequirePrivateFile(path, stream);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException)
            {
                if (deadline.Elapsed >= TimeSpan.FromSeconds(2))
                    throw new AgentClientValidationException("Configuration is busy; try again.");
                if (cancellationToken.WaitHandle.WaitOne(10)) cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static FileStreamOptions PrivateOptions(FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        return options;
    }

    private static FileStream OpenPrivateFile(string path, FileMode mode, FileAccess access)
        => OperatingSystem.IsWindows() ? OpenWindowsPrivateFile(path, mode, access) : new FileStream(path, PrivateOptions(mode, access));

    [SupportedOSPlatform("windows")]
    private static FileStream OpenWindowsPrivateFile(string path, FileMode mode, FileAccess access)
    {
        var owner = WindowsOwner();
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        var rights = (access == FileAccess.Write ? FileSystemRights.Write : FileSystemRights.Read | FileSystemRights.Write)
            | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;
        // The framework installs the security descriptor during creation, before any secret bytes exist.
        return new FileInfo(path).Create(mode, rights, FileShare.None, 4096, FileOptions.None, security);
    }

    private static void CreatePrivateDirectories(string path)
    {
        RejectLinks(path);
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) CreatePrivateDirectories(parent);
        if (OperatingSystem.IsWindows()) CreateWindowsPrivateDirectory(path);
        else Directory.CreateDirectory(path, PrivateDirectoryMode);
        RejectLinks(path);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new AgentClientValidationException("Configuration path is required.");
        return Path.GetFullPath(path);
    }

    private static void RejectLinks(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var component in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new AgentClientValidationException("Configuration paths cannot contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RequirePrivateFile(string path, FileStream? stream = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = stream is null
                ? new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : stream.GetAccessControl();
            RequireWindowsPrivateSecurity(security);
            return;
        }
        var mode = File.GetUnixFileMode(path);
        if ((mode & ~PrivateFileMode) != 0 || (mode & UnixFileMode.UserRead) == 0)
            throw new AgentClientValidationException("Secret configuration files must have owner-only read/write permissions.");
        RequireCurrentOwner(path);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsPrivateDirectory(string path)
    {
        var owner = WindowsOwner();
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        // A competing creator must not leave a broadly accessible directory in place.
        RequireWindowsPrivateSecurity(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier WindowsOwner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new AgentClientValidationException("Private configuration storage requires an identified Windows account.");
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsPrivateSecurity(FileSystemSecurity security)
    {
        var owner = WindowsOwner();
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new AgentClientValidationException("Secret configuration files must belong to the current user.");
        var ownerCanRead = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            var principal = (SecurityIdentifier)rule.IdentityReference;
            if (!principal.Equals(owner) && !principal.IsWellKnown(WellKnownSidType.LocalSystemSid) && !principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                throw new AgentClientValidationException("Secret configuration files must have owner-only access permissions.");
            if (principal.Equals(owner) && (rule.FileSystemRights & FileSystemRights.ReadData) != 0) ownerCanRead = true;
        }
        // Empty or null DACLs are not evidence of private storage.
        if (!ownerCanRead) throw new AgentClientValidationException("Secret configuration files must grant read access to their owner.");
    }

    private static void RequireCurrentOwner(string path)
    {
        // statx has a stable structure across Linux architectures. Darwin's stat layout
        // has the same uid offset on its supported 64-bit architectures.
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            int result;
            int uidOffset;
            if (OperatingSystem.IsLinux())
            {
                result = Statx(-100, path, 0x100, 0x000A, buffer);
                uidOffset = 20;
            }
            else if (OperatingSystem.IsMacOS())
            {
                result = DarwinLStat(path, buffer);
                uidOffset = 16;
            }
            else throw new AgentClientValidationException("Private configuration storage is unsupported on this platform.");
            if (result != 0 || unchecked((uint)Marshal.ReadInt32(buffer, uidOffset)) != GetEffectiveUserId())
                throw new AgentClientValidationException("Secret configuration files must belong to the current user.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static T Guard<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (AgentClientValidationException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException or EntryPointNotFoundException or DllNotFoundException or NotSupportedException or System.Security.SecurityException)
        {
            // Do not retain OS exception text or an inner exception: paths and content
            // may include credentials, and these errors are rendered by CLI diagnostics.
            throw new AgentClientValidationException("Configuration file could not be accessed securely.");
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, IntPtr buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int DarwinLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
