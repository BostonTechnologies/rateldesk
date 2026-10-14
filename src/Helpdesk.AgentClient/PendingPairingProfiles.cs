using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helpdesk.AgentClient;

public sealed record PendingPairingProfile(string TargetPath, string CandidatePath, string ExpectedRevision,
    string CandidateRevision, string Origin, PairingSession Session, Guid CredentialId,
    DateTimeOffset DeliveryExpiresAtUtc, bool Acknowledged = false)
{
    [JsonIgnore] public string StateRevision { get; init; } = "";
    public override string ToString() => "PendingPairingProfile { deliveryCapability = [REDACTED], credential = [REDACTED] }";
}

/// <summary>Durable secure-save/acknowledgement boundary. A working final profile is replaced only after confirmed acknowledgement.</summary>
public static class PendingPairingProfiles
{
    private static string Candidate(string target) => Path.GetFullPath(target) + ".pairing-candidate";
    private static string State(string target) => Path.GetFullPath(target) + ".pairing-pending";

    public static PendingPairingProfile Stage(string targetPath, AgentClientConfiguration configuration, string expectedRevision,
        string origin, PairingSession session, Guid credentialId, DateTimeOffset deliveryExpiresAtUtc, CancellationToken cancellationToken = default)
    {
        targetPath = ValidatedPath(targetPath);
        if (File.Exists(targetPath) && new FileInfo(targetPath).IsReadOnly)
            throw new AgentClientValidationException("The selected profile is read-only; choose a writable profile or export directory.");
        ValidateSession(session);
        if (!ValidRevision(expectedRevision, allowEmpty: true) || credentialId == Guid.Empty)
            throw InvalidState();
        using var operation = SecureConfigurationFiles.AcquireOperationLease(targetPath, cancellationToken);
        if (configuration.CredentialMode != "integration" || string.IsNullOrWhiteSpace(configuration.IntegrationCredential) ||
            configuration.IntegrationCredentialFile is not null || configuration.AuthentikAppPassword is not null || configuration.AuthentikAppPasswordFile is not null)
            throw new AgentClientValidationException("Pairing staging requires a newly delivered integration credential, never an existing credential reference.");
        if (!string.Equals(SecureConfigurationFiles.Revision(targetPath) ?? "", expectedRevision, StringComparison.Ordinal))
            throw new AgentClientValidationException("Configuration changed; reload it before staging a credential.");
        if (deliveryExpiresAtUtc <= DateTimeOffset.UtcNow || deliveryExpiresAtUtc > session.ExpiresAtUtc)
            throw new AgentClientValidationException("Protected credential delivery has expired or has an invalid deadline.");
        var canonical = IntegrationCredentialPairingClient.ValidateOrigin(origin).AbsoluteUri.TrimEnd('/');
        if (!AgentClientConfigurationValidator.SameTarget(new Uri(canonical), AgentClientConfigurationValidator.ApiUri(configuration.ApiBaseUrl)))
            throw new AgentClientValidationException("A staged credential must bind to the reviewed instance.");
        var saved = ConfigurationProfiles.SaveFile(Candidate(targetPath), configuration, "", cancellationToken);
        var pending = new PendingPairingProfile(targetPath, saved.Path, expectedRevision, saved.Revision, canonical, session, credentialId, deliveryExpiresAtUtc);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = SecureConfigurationFiles.WriteAtomic(State(targetPath), JsonSerializer.Serialize(pending, AgentClientConfiguration.JsonOptions), "");
            return pending with { StateRevision = revision };
        }
        catch
        {
            DeleteCandidate(pending);
            throw;
        }
    }

    public static PendingPairingProfile? Load(string targetPath, CancellationToken cancellationToken = default)
    {
        targetPath = ValidatedPath(targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        // Looking at a managed profile with no pending operation must remain read-only.
        if (!File.Exists(State(targetPath)) && !File.Exists(Candidate(targetPath))) return null;
        using var operation = SecureConfigurationFiles.AcquireOperationLease(targetPath, cancellationToken);
        var statePath = State(targetPath);
        if (!File.Exists(statePath))
        {
            // A crash between the two private writes leaves no recoverable device capability.
            // Remove that orphan on explicit setup re-entry; normal serving never loads it.
            if (File.Exists(Candidate(targetPath)))
            {
                var orphan = new PendingPairingProfile(targetPath, Candidate(targetPath), "", SecureConfigurationFiles.Revision(Candidate(targetPath))!, "", new("", "", "", default, 1), Guid.Empty, default);
                DeleteCandidate(orphan);
            }
            return null;
        }
        PendingPairingProfile value;
        try { value = JsonSerializer.Deserialize<PendingPairingProfile>(SecureConfigurationFiles.ReadText(statePath, 65536, true), AgentClientConfiguration.JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new AgentClientValidationException("Pending pairing state is invalid; the working profile was preserved."); }
        ValidatePending(value);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(ValidatedPath(value.TargetPath), targetPath, pathComparison) ||
            !string.Equals(ValidatedPath(value.CandidatePath), Candidate(targetPath), pathComparison))
            throw new AgentClientValidationException("Pending pairing state does not bind this selected profile.");
        var origin = IntegrationCredentialPairingClient.ValidateOrigin(value.Origin);
        using var validation = new IntegrationCredentialPairingClient(origin.AbsoluteUri);
        validation.ValidateVerificationUri(value.Session.VerificationUri);
        value = value with { StateRevision = SecureConfigurationFiles.Revision(statePath)! };
        if (!File.Exists(value.CandidatePath))
        {
            // Cleanup deletes an unactivated candidate's private secret before its file.
            // A crash before the final sidecar removal therefore leaves no candidate to recover.
            SecureConfigurationFiles.DeleteAtomic(statePath, value.StateRevision);
            return null;
        }
        if (value.Acknowledged && SecureConfigurationFiles.Revision(targetPath) == value.CandidateRevision)
        {
            // Final activation completed before a crash interrupted sidecar cleanup.
            // Its referenced secret belongs to the working profile and must remain.
            if (SecureConfigurationFiles.Revision(value.CandidatePath) == value.CandidateRevision)
                SecureConfigurationFiles.DeleteAtomic(value.CandidatePath, value.CandidateRevision);
            SecureConfigurationFiles.DeleteAtomic(statePath, value.StateRevision);
            return null;
        }
        var candidate = ReadCandidateCore(value);
        if (!AgentClientConfigurationValidator.SameTarget(origin, AgentClientConfigurationValidator.ApiUri(candidate.ApiBaseUrl)) || candidate.CredentialMode != "integration")
            throw new AgentClientValidationException("Pending credential target does not match the reviewed pairing instance.");
        return value;
    }

    public static AgentClientConfiguration ReadCandidate(PendingPairingProfile pending, CancellationToken cancellationToken = default)
    {
        ValidatePending(pending);
        using var operation = SecureConfigurationFiles.AcquireOperationLease(pending.TargetPath, cancellationToken);
        RequireCurrentState(pending);
        return ReadCandidateCore(pending);
    }

    private static AgentClientConfiguration ReadCandidateCore(PendingPairingProfile pending)
    {
        if (!string.Equals(SecureConfigurationFiles.Revision(pending.CandidatePath), pending.CandidateRevision, StringComparison.Ordinal))
            throw new AgentClientValidationException("Pending candidate changed; no working profile was overwritten.");
        return new AgentClientConfigurationStore(pending.CandidatePath).Load();
    }

    public static PendingPairingProfile MarkAcknowledged(PendingPairingProfile pending, CancellationToken cancellationToken = default)
    {
        ValidatePending(pending);
        using var operation = SecureConfigurationFiles.AcquireOperationLease(pending.TargetPath, cancellationToken);
        RequireCurrentState(pending);
        var acknowledged = pending with { Acknowledged = true };
        var revision = SecureConfigurationFiles.WriteAtomic(State(pending.TargetPath), JsonSerializer.Serialize(acknowledged, AgentClientConfiguration.JsonOptions), pending.StateRevision);
        return acknowledged with { StateRevision = revision };
    }

    public static ConfigurationProfileSaveResult Activate(PendingPairingProfile pending, CancellationToken cancellationToken = default)
    {
        ValidatePending(pending);
        using var operation = SecureConfigurationFiles.AcquireOperationLease(pending.TargetPath, cancellationToken);
        RequireCurrentState(pending);
        if (!pending.Acknowledged) throw new AgentClientValidationException("A pending credential cannot replace the working profile before confirmed acknowledgement.");
        var candidate = ReadCandidateCore(pending);
        var saved = ConfigurationProfiles.SaveFileUnderLease(pending.TargetPath, candidate, pending.ExpectedRevision, cancellationToken);
        // A crash after final activation is recognized on re-entry by the same candidate identity.
        SecureConfigurationFiles.DeleteAtomic(pending.CandidatePath, pending.CandidateRevision);
        SecureConfigurationFiles.DeleteAtomic(State(pending.TargetPath), pending.StateRevision);
        return saved;
    }

    public static void Discard(PendingPairingProfile pending, CancellationToken cancellationToken = default)
    {
        ValidatePending(pending);
        using var operation = SecureConfigurationFiles.AcquireOperationLease(pending.TargetPath, cancellationToken);
        RequireCurrentState(pending);
        if (SecureConfigurationFiles.Revision(pending.TargetPath) == pending.CandidateRevision)
            SecureConfigurationFiles.DeleteAtomic(pending.CandidatePath, pending.CandidateRevision);
        else DeleteCandidate(pending);
        SecureConfigurationFiles.DeleteAtomic(State(pending.TargetPath), pending.StateRevision);
    }

    private static void DeleteCandidate(PendingPairingProfile pending)
    {
        var candidate = ReadCandidateCore(pending);
        var secretRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pending.TargetPath)!, "secrets")) + Path.DirectorySeparatorChar;
        var active = File.Exists(pending.TargetPath) ? new AgentClientConfigurationStore(pending.TargetPath).Load() : AgentClientConfiguration.Empty;
        var activeReferences = new HashSet<string>(new[] { active.IntegrationCredentialFile, active.AuthentikAppPasswordFile }
            .Where(path => path is not null).Select(path => Path.GetFullPath(path!)), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var secrets = new[] { candidate.IntegrationCredentialFile, candidate.AuthentikAppPasswordFile }
            .Where(path => path is not null && !activeReferences.Contains(Path.GetFullPath(path)) && Path.GetFullPath(path).StartsWith(secretRoot, StringComparison.Ordinal) && Path.GetFileName(path).StartsWith("credential-", StringComparison.Ordinal))
            .Select(path => (Path: path!, Revision: SecureConfigurationFiles.Revision(path!))).ToArray();
        foreach (var secret in secrets)
            if (secret.Revision is not null) SecureConfigurationFiles.DeleteAtomic(secret.Path, secret.Revision);
        SecureConfigurationFiles.DeleteAtomic(pending.CandidatePath, pending.CandidateRevision);
    }

    private static void RequireCurrentState(PendingPairingProfile pending)
    {
        ValidatePending(pending);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(ValidatedPath(pending.CandidatePath), Candidate(ValidatedPath(pending.TargetPath)), comparison) ||
            string.IsNullOrEmpty(pending.StateRevision) ||
            !string.Equals(SecureConfigurationFiles.Revision(State(pending.TargetPath)), pending.StateRevision, StringComparison.Ordinal))
            throw new AgentClientValidationException("Pending pairing changed; reload it before continuing.");
    }

    private static void ValidatePending(PendingPairingProfile pending)
    {
        if (pending is null || !Bounded(pending.TargetPath, 4096) || !Bounded(pending.CandidatePath, 4096) ||
            !Bounded(pending.Origin, 2048) || !ValidRevision(pending.ExpectedRevision, allowEmpty: true) ||
            !ValidRevision(pending.CandidateRevision) || pending.CredentialId == Guid.Empty)
            throw InvalidState();
        _ = ValidatedPath(pending.TargetPath);
        _ = ValidatedPath(pending.CandidatePath);
        ValidateSession(pending.Session);
        if (pending.DeliveryExpiresAtUtc == default || pending.DeliveryExpiresAtUtc > pending.Session.ExpiresAtUtc)
            throw InvalidState();
    }

    private static void ValidateSession(PairingSession session)
    {
        if (session is null || !Bounded(session.DeviceCode, 1024) || !Bounded(session.HumanCode, 32) ||
            !Bounded(session.VerificationUri, 4096) || session.ExpiresAtUtc == default || session.PollIntervalSeconds is < 1 or > 60)
            throw InvalidState();
    }

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool ValidRevision(string? value, bool allowEmpty = false) => value is not null &&
        ((allowEmpty && value.Length == 0) || (value.Length == 64 && value.All(char.IsAsciiHexDigit)));
    private static AgentClientValidationException InvalidState() => new("Pending pairing state is invalid; the working profile was preserved.");
    private static string ValidatedPath(string? value)
    {
        if (!Bounded(value, 4096)) throw InvalidState();
        try { return Path.GetFullPath(value!); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw InvalidState(); }
    }
}
