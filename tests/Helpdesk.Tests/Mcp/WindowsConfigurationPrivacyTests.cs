using System.Security.AccessControl;
using System.Security.Principal;
using Helpdesk.AgentClient;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class WindowsConfigurationPrivacyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rateldesk-windows-acl-" + Guid.NewGuid().ToString("N"));

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Initial_creation_and_atomic_replacement_remain_private_under_a_broad_parent_acl()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(directory);
        var parent = new DirectoryInfo(directory).GetAccessControl();
        parent.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(parent);
        var path = Path.Combine(directory, "credential.json");
        var revision = SecureConfigurationFiles.WriteAtomic(path, "first-private-secret", "");
        AssertPrivateOwnerAcl(path);
        AssertPrivateOwnerAcl(path + ".lock");
        SecureConfigurationFiles.WriteAtomic(path, "second-private-secret", revision);
        AssertPrivateOwnerAcl(path);
        Assert.Equal("second-private-secret", SecureConfigurationFiles.ReadText(path, requirePrivate: true));
        Assert.Contains(new DirectoryInfo(directory).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Broad_existing_secret_acl_is_rejected_without_repair_or_replacement()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = Path.Combine(directory, "secret");
        SecureConfigurationFiles.WriteAtomic(path, "existing-sensitive-value", "");
        var security = new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        var error = Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.ReadText(path, requirePrivate: true));
        Assert.DoesNotContain("existing-sensitive-value", error.ToString());
        Assert.Throws<AgentClientValidationException>(() => SecureConfigurationFiles.WriteAtomic(path, "replacement-secret"));
        Assert.Equal("existing-sensitive-value", File.ReadAllText(path));
        Assert.Contains(new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertPrivateOwnerAcl(string path)
    {
        using var user = WindowsIdentity.GetCurrent();
        var owner = user.User!;
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Equal(owner, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var allows = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0).ToArray();
        Assert.NotEmpty(allows);
        Assert.All(allows, rule => Assert.Equal(owner, rule.IdentityReference));
        Assert.Contains(allows, rule => (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires the Windows filesystem ACL implementation.";
    }
}
