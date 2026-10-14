using Helpdesk.AgentClient;
using System.Text.Json.Nodes;
using Xunit;

namespace Helpdesk.Tests.Setup;

public sealed class PendingPairingProfileTests
{
    [Fact]
    public void Readonly_working_profile_is_preserved_without_new_secret_or_pairing_candidate()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-readonly-");
        var path = Path.Combine(directory.FullName, "client.json");
        try
        {
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var bytes = File.ReadAllBytes(path);
            var secretCount = Directory.GetFiles(Path.Combine(directory.FullName, "secrets")).Length;
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            Assert.Throws<AgentClientValidationException>(() => ConfigurationProfiles.SaveFile(path, Configuration("rdk_new_approval"), old.Revision));
            Assert.Throws<AgentClientValidationException>(() => Stage(path, old.Revision));
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.True(new FileInfo(path).IsReadOnly);
            Assert.False(File.Exists(path + ".pairing-candidate"));
            Assert.False(File.Exists(path + ".pairing-pending"));
            Assert.Equal(secretCount, Directory.GetFiles(Path.Combine(directory.FullName, "secrets")).Length);
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Crash_before_acknowledgement_preserves_working_profile_and_recovers_private_candidate()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var bytes = File.ReadAllBytes(path);
            var pending = Stage(path, old.Revision);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Throws<AgentClientValidationException>(() => PendingPairingProfiles.Activate(pending));
            var recovered = PendingPairingProfiles.Load(path)!;
            Assert.Equal("private-delivery-capability", recovered.Session.DeviceCode);
            Assert.DoesNotContain("private-delivery-capability", recovered.ToString(), StringComparison.Ordinal);
            Assert.False(recovered.Acknowledged);
            var candidate = PendingPairingProfiles.ReadCandidate(recovered);
            Assert.Null(candidate.IntegrationCredential);
            Assert.Equal("rdk_new_approval", SecureConfigurationFiles.ReadText(candidate.IntegrationCredentialFile!, requirePrivate: true).Trim());
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Confirmed_acknowledgement_is_durable_before_atomic_activation_and_later_writer_is_preserved()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = PendingPairingProfiles.MarkAcknowledged(Stage(path, old.Revision));
            var recovered = PendingPairingProfiles.Load(path)!;
            Assert.True(recovered.Acknowledged);
            Assert.Equal("rdk_previous_working", new AgentClientConfigurationStore(path).Load().Resolve().IntegrationCredential);
            var later = ConfigurationProfiles.SaveFile(path, Configuration("rdk_later_writer"), old.Revision);
            Assert.Throws<AgentClientValidationException>(() => PendingPairingProfiles.Activate(pending));
            Assert.Equal("rdk_later_writer", new AgentClientConfigurationStore(path).Load().Resolve().IntegrationCredential);
            Assert.Equal(later.Revision, SecureConfigurationFiles.Revision(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Expired_or_cancelled_candidate_cleanup_removes_only_candidate_secret_and_preserves_working_profile()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = Stage(path, old.Revision);
            var candidate = PendingPairingProfiles.ReadCandidate(pending);
            PendingPairingProfiles.Discard(pending);
            Assert.Null(PendingPairingProfiles.Load(path));
            Assert.False(File.Exists(candidate.IntegrationCredentialFile));
            Assert.True(File.Exists(old.Configuration.IntegrationCredentialFile));
            Assert.Equal(old.Revision, SecureConfigurationFiles.Revision(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Crash_after_final_activation_finishes_sidecar_cleanup_without_deleting_active_secret()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = PendingPairingProfiles.MarkAcknowledged(Stage(path, old.Revision));
            var candidate = PendingPairingProfiles.ReadCandidate(pending);
            // Simulate the atomic final write followed by process loss before sidecar cleanup.
            var activatedRevision = SecureConfigurationFiles.WriteAtomic(path, SecureConfigurationFiles.ReadText(pending.CandidatePath, requirePrivate: true), old.Revision);
            Assert.Equal(pending.CandidateRevision, activatedRevision);
            Assert.Null(PendingPairingProfiles.Load(path));
            Assert.True(File.Exists(candidate.IntegrationCredentialFile));
            Assert.Equal("rdk_new_approval", new AgentClientConfigurationStore(path).Load().Resolve().IntegrationCredential);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Cleanup_after_activation_and_later_metadata_edit_preserves_active_secret_reference()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = PendingPairingProfiles.MarkAcknowledged(Stage(path, old.Revision));
            var candidate = PendingPairingProfiles.ReadCandidate(pending);
            var activated = ConfigurationProfiles.SaveFile(path, candidate, old.Revision);
            var edited = ConfigurationProfiles.SaveFile(path, candidate with { AgentUserEmail = "later-writer@example.com" }, activated.Revision);
            PendingPairingProfiles.Discard(pending);
            Assert.Equal(edited.Revision, SecureConfigurationFiles.Revision(path));
            Assert.True(File.Exists(candidate.IntegrationCredentialFile));
            Assert.Equal("rdk_new_approval", new AgentClientConfigurationStore(path).Load().Resolve().IntegrationCredential);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Crash_after_cancelled_candidate_removal_finishes_sidecar_cleanup_and_keeps_working_profile()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = Stage(path, old.Revision);
            var candidate = PendingPairingProfiles.ReadCandidate(pending);
            SecureConfigurationFiles.DeleteAtomic(candidate.IntegrationCredentialFile!, SecureConfigurationFiles.Revision(candidate.IntegrationCredentialFile!)!);
            SecureConfigurationFiles.DeleteAtomic(pending.CandidatePath, pending.CandidateRevision);
            Assert.Null(PendingPairingProfiles.Load(path));
            Assert.Equal(old.Revision, SecureConfigurationFiles.Revision(path));
            Assert.Equal("rdk_previous_working", new AgentClientConfigurationStore(path).Load().Resolve().IntegrationCredential);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Competing_activation_and_discard_wait_for_the_private_lease_and_honor_cancellation()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = PendingPairingProfiles.MarkAcknowledged(Stage(path, old.Revision));
            using (SecureConfigurationFiles.AcquireOperationLease(path))
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                var activate = Task.Run(() => PendingPairingProfiles.Activate(pending, cancellation.Token));
                var discard = Task.Run(() => PendingPairingProfiles.Discard(pending, cancellation.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await activate; });
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await discard; });
                Assert.Equal(old.Revision, SecureConfigurationFiles.Revision(path));
                Assert.Equal(pending.CandidateRevision, SecureConfigurationFiles.Revision(pending.CandidatePath));
            }
            Assert.True(PendingPairingProfiles.Load(path)!.Acknowledged);
            PendingPairingProfiles.Discard(pending);
            Assert.Equal("rdk_previous_working", new AgentClientConfigurationStore(path).Load().Resolve().IntegrationCredential);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Concurrent_activation_and_discard_leave_one_complete_working_identity()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = PendingPairingProfiles.MarkAcknowledged(Stage(path, old.Revision));
            using var start = new ManualResetEventSlim(false);
            var activate = Task.Run(() =>
            {
                start.Wait();
                try { PendingPairingProfiles.Activate(pending); return true; }
                catch (AgentClientValidationException) { return false; }
            });
            var discard = Task.Run(() =>
            {
                start.Wait();
                try { PendingPairingProfiles.Discard(pending); return true; }
                catch (AgentClientValidationException) { return false; }
            });
            start.Set();
            var outcomes = await Task.WhenAll(activate, discard);
            Assert.Single(outcomes, completed => completed);
            var active = new AgentClientConfigurationStore(path).Load();
            Assert.True(File.Exists(active.IntegrationCredentialFile));
            Assert.Equal(outcomes[0] ? "rdk_new_approval" : "rdk_previous_working", active.Resolve().IntegrationCredential);
            Assert.Null(PendingPairingProfiles.Load(path));
            Assert.False(File.Exists(pending.CandidatePath));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Metadata_writer_cannot_publish_a_candidate_reference_after_discard()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = Stage(path, old.Revision);
            var reviewed = PendingPairingProfiles.ReadCandidate(pending);
            PendingPairingProfiles.Discard(pending);
            Assert.Throws<AgentClientValidationException>(() => ConfigurationProfiles.SaveFile(path, reviewed with { AgentUserEmail = "later@example.com" }, old.Revision));
            Assert.Equal(old.Revision, SecureConfigurationFiles.Revision(path));
            Assert.Equal("rdk_previous_working", new AgentClientConfigurationStore(path).Load().Resolve().IntegrationCredential);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("session")]
    [InlineData("targetPath")]
    [InlineData("candidatePath")]
    public void Invalid_null_pending_state_is_rejected_without_disclosing_capability_or_changing_working_profile(string property)
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = Stage(path, old.Revision);
            var statePath = path + ".pairing-pending";
            var json = JsonNode.Parse(SecureConfigurationFiles.ReadText(statePath, requirePrivate: true))!.AsObject();
            json[property] = null;
            SecureConfigurationFiles.WriteAtomic(statePath, json.ToJsonString(), pending.StateRevision);
            var error = Assert.Throws<AgentClientValidationException>(() => PendingPairingProfiles.Load(path));
            Assert.DoesNotContain("private-delivery-capability", error.Message, StringComparison.Ordinal);
            Assert.Null(error.InnerException);
            Assert.Equal(old.Revision, SecureConfigurationFiles.Revision(path));
            Assert.Equal(pending.CandidateRevision, SecureConfigurationFiles.Revision(pending.CandidatePath));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Public_pending_operations_reject_invalid_identity_with_generic_validation_error()
    {
        var directory = Directory.CreateTempSubdirectory("rateldesk-pending-");
        try
        {
            var path = Path.Combine(directory.FullName, "client.json");
            var old = ConfigurationProfiles.SaveFile(path, Configuration("rdk_previous_working"), "");
            var pending = Stage(path, old.Revision);
            Assert.Throws<AgentClientValidationException>(() => PendingPairingProfiles.ReadCandidate(pending with { Session = null! }));
            Assert.Throws<AgentClientValidationException>(() => PendingPairingProfiles.MarkAcknowledged(pending with { TargetPath = null! }));
            Assert.Throws<AgentClientValidationException>(() => PendingPairingProfiles.Activate(pending with { CandidatePath = null! }));
            Assert.Throws<AgentClientValidationException>(() => PendingPairingProfiles.Discard(pending with { TargetPath = "invalid\0path" }));
            Assert.Equal(old.Revision, SecureConfigurationFiles.Revision(path));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static PendingPairingProfile Stage(string path, string revision) => PendingPairingProfiles.Stage(path, Configuration("rdk_new_approval"), revision,
        "https://desk.example", new("private-delivery-capability", "ABCD-EFGH", "https://desk.example/account/integration-credentials/approve", DateTimeOffset.UtcNow.AddMinutes(5), 1), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(2));
    private static AgentClientConfiguration Configuration(string secret) => AgentClientConfiguration.Empty with
    { ApiBaseUrl = "https://desk.example", CredentialTarget = "https://desk.example", CredentialMode = "integration", IntegrationCredential = secret, CredentialPurpose = "api", ClientTransport = "cli", CredentialOrganizationId = "org-one", CredentialPermissions = ["ticket.view"], CredentialExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30) };
}
