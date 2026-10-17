using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Shared.DTOs.Auth;

namespace Helpdesk.Setup;

/// <summary>A proposal approval capability is separate from a verifier and from durable consumer access.</summary>
public interface IHostDeploymentWorkflow : IDisposable
{
    Task<PairingSession> StartAsync(HttpMcpDeploymentProposal proposal, CancellationToken ct);
    Task<HostDeploymentApprovalStatus> PollAsync(PairingSession session, CancellationToken ct);
    Task CancelAsync(PairingSession session, CancellationToken ct);
    Task<HttpMcpHostingExport> SaveApprovedAsync(HttpMcpDeploymentProposal proposal, PairingSession session, string directory, bool overwrite, CancellationToken ct);
}

public sealed class HostDeploymentWorkflow : IHostDeploymentWorkflow
{
    private readonly HttpClient client;
    private readonly Uri origin;
    private readonly bool ownsClient;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Prefix = "/api/v1/integration-credentials/host-proposals/";
    public HostDeploymentWorkflow(string publicOrigin, HttpClient? testClient = null)
    {
        origin = IntegrationCredentialPairingClient.ValidateOrigin(publicOrigin);
        client = testClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        ownsClient = testClient is null;
    }
    public async Task<PairingSession> StartAsync(HttpMcpDeploymentProposal proposal, CancellationToken ct)
    {
        if (!proposal.Valid) throw new AgentClientValidationException(Describe(proposal.Diagnostics));
        var started = await PostAsync<IntegrationPairingStarted>("start", proposal.Approval, ct);
        if (!Uri.TryCreate(started.VerificationUri, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath != "/account/integration-credentials/approve" || uri.Query != "?kind=host" || uri.UserInfo.Length != 0 ||
            started.DeviceCode.Length != 64 || !started.DeviceCode.All(char.IsAsciiHexDigit) || started.HumanCode.Length > 16 ||
            started.ExpiresAtUtc <= DateTimeOffset.UtcNow || started.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(11) || started.PollIntervalSeconds is < 1 or > 30)
            throw new AgentClientValidationException("The instance returned an invalid host approval checkpoint.");
        return new(started.DeviceCode, started.HumanCode, started.VerificationUri, started.ExpiresAtUtc, started.PollIntervalSeconds);
    }
    public Task<HostDeploymentApprovalStatus> PollAsync(PairingSession session, CancellationToken ct) => PostAsync<HostDeploymentApprovalStatus>("poll", new IntegrationPairingDeviceRequest(session.DeviceCode), ct);
    public async Task CancelAsync(PairingSession session, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await client.PostAsJsonAsync(new Uri(origin, Prefix + "cancel"), new IntegrationPairingDeviceRequest(session.DeviceCode), deadline.Token);
        if (!response.IsSuccessStatusCode) throw new AgentClientValidationException("Host approval cancellation was not confirmed; its bounded server expiry remains in force.");
    }
    public async Task<HttpMcpHostingExport> SaveApprovedAsync(HttpMcpDeploymentProposal proposal, PairingSession session, string directory, bool overwrite, CancellationToken ct)
    {
        var current = HttpMcpEffectiveDeployment.Create(proposal.Settings);
        if (!current.Valid || current.SourceHash != proposal.SourceHash || current.ProposalHash != proposal.ProposalHash)
            throw new AgentClientValidationException("HOST_SOURCE_CHANGED: Sources or effective settings changed after review. Revalidate and obtain a new deployment approval.");
        var approval = await PollAsync(session, ct);
        if (approval.Status != "approved" || approval.ProposalHash != proposal.ProposalHash || approval.SourceHash != proposal.SourceHash || approval.ApprovedBy is null)
            throw new AgentClientValidationException("Explicit current administrator approval of this deployment generation is required before export.");
        var approvedSnapshot = SecureConfigurationFiles.ReadPrivateDocument(proposal.SnapshotPath ?? Environment.GetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable)!);
        if (HttpMcpEffectiveDeployment.Hash(approvedSnapshot) != proposal.SourceHash)
            throw new AgentClientValidationException("HOST_SOURCE_CHANGED: The reviewed source snapshot changed before export.");
        var export = HttpMcpHostingCoordinator.Export(proposal.Settings, directory, overwrite, proposal.Overlay);
        SecureConfigurationFiles.WritePrivateDocument(Path.Combine(directory, "approved-effective-deployment.json"), approvedSnapshot);
        SecureConfigurationFiles.WriteAtomic(Path.Combine(directory, "host-approval-session.json"), JsonSerializer.Serialize(
            new SavedHostApproval(proposal.Settings.PublicOrigin, session.DeviceCode, proposal.ProposalHash, proposal.SourceHash), Json));
        var hashes = export.Files.ToDictionary(path => Path.GetFileName(path)!, path => SecureConfigurationFiles.Revision(path)!, StringComparer.Ordinal);
        var receipt = new HostApprovalReceipt(true, proposal.SourceHash, proposal.ProposalHash, approval.ApprovedBy,
            approval.ApprovedAtUtc!.Value, Environment.GetEnvironmentVariable("RATELDESK_SETUP_ATTEMPT"), hashes);
        SecureConfigurationFiles.WriteAtomic(Path.Combine(directory, "host-approval.json"), JsonSerializer.Serialize(receipt, Json));
        SecureConfigurationFiles.WriteAtomic(Path.Combine(directory, "host-phase.json"), JsonSerializer.Serialize(new { phase = "saved-apply-pending", proposal.SourceHash, proposal.ProposalHash }, Json));
        return export;
    }

    public static HttpMcpDeploymentProposal ResumeApproved(string directory)
    {
        HostApprovalReceipt receipt;
        try { receipt = JsonSerializer.Deserialize<HostApprovalReceipt>(SecureConfigurationFiles.ReadText(Path.Combine(directory, "host-approval.json"), requirePrivate: true), Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new AgentClientValidationException("The host approval checkpoint is invalid. Reopen the launcher and review a fresh proposal."); }
        if (!receipt.Approved || receipt.ExportHashes.Count is < 4 or > 8 || string.IsNullOrWhiteSpace(receipt.ApprovedBy))
            throw new AgentClientValidationException("A complete approved host generation is required for verification.");
        foreach (var export in receipt.ExportHashes)
            if (Path.GetFileName(export.Key) != export.Key || SecureConfigurationFiles.Revision(Path.Combine(directory, export.Key)) != export.Value)
                throw new AgentClientValidationException("HOST_EXPORT_CHANGED: Approved export changed. Review a fresh proposal before applying or verifying.");
        var frozenPath = Path.Combine(directory, "approved-effective-deployment.json");
        var frozen = SecureConfigurationFiles.ReadPrivateDocument(frozenPath);
        if (HttpMcpEffectiveDeployment.Hash(frozen) != receipt.SourceHash)
            throw new AgentClientValidationException("HOST_APPROVED_SOURCE_CHANGED: The saved reviewed source generation changed. Start a fresh review.");
        var proposal = HttpMcpEffectiveDeployment.Create(HttpMcpHostingCoordinator.Load(directory), frozenPath);
        if (!proposal.Valid || proposal.SourceHash != receipt.SourceHash || proposal.ProposalHash != receipt.ProposalHash)
            throw new AgentClientValidationException("HOST_SOURCE_CHANGED: Current sources differ from the approved generation. Revalidate and review before continuing.");
        var currentText = SecureConfigurationFiles.ReadPrivateDocument(Environment.GetEnvironmentVariable(HttpMcpEffectiveDeployment.EffectiveSourceVariable)!);
        if (HttpMcpEffectiveDeployment.Hash(currentText) != receipt.SourceHash)
        {
            // The sole accepted source transition is importing the exact reviewed complete document.
            // The Docker host separately fences all original source bytes and interpolation context.
            var state = HttpMcpEffectiveDeployment.Parse(SecureConfigurationFiles.ReadPrivateDocument(Path.Combine(directory, "launcher-state.json")));
            var complete = SecureConfigurationFiles.ReadPrivateDocument(Path.Combine(directory, "deployment.json"));
            var completeHash = HttpMcpEffectiveDeployment.Hash(complete);
            var current = HttpMcpEffectiveDeployment.Parse(currentText);
            var original = HttpMcpEffectiveDeployment.Parse(frozen);
            var before = SourceRecords(original); var after = SourceRecords(current);
            var changed = after.Where(item => before.GetValueOrDefault(item.Key) != item.Value).ToArray();
            if (state["sourceHash"]?.ToString() != receipt.SourceHash ||
                !string.Equals(state["deploymentSha256"]?.ToString(), completeHash, StringComparison.OrdinalIgnoreCase) ||
                before.Count != after.Count || before.Keys.Any(key => !after.ContainsKey(key)) || changed.Length != 1 ||
                !string.Equals(changed[0].Value, completeHash, StringComparison.OrdinalIgnoreCase))
                throw new AgentClientValidationException("HOST_SOURCE_CHANGED: Only the exact reviewed complete-document import may resume without renewed host approval.");
            var transition = HttpMcpEffectiveDeployment.CheckApprovedEffective(proposal, current);
            if (transition.Count > 0) throw new AgentClientValidationException(Describe(transition));
        }
        return proposal;
    }

    private static Dictionary<string, string> SourceRecords(System.Text.Json.Nodes.JsonObject snapshot)
    {
        if (snapshot["x-rateldesk-source-files"] is not System.Text.Json.Nodes.JsonArray files)
            throw new AgentClientValidationException("HOST_SOURCE_CHANGED: The complete source record is unavailable.");
        return files.ToDictionary(file => file!["path"]!.GetValue<string>(), file => file!["sha256"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    public static async Task CheckApplyAuthorityAsync(string directory, CancellationToken ct)
    {
        var proposal = ResumeApproved(directory);
        SavedHostApproval saved;
        try { saved = JsonSerializer.Deserialize<SavedHostApproval>(SecureConfigurationFiles.ReadText(Path.Combine(directory, "host-approval-session.json"), requirePrivate: true), Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new AgentClientValidationException("The saved administrator approval capability is invalid. Review the current host generation again."); }
        if (saved.Origin != proposal.Settings.PublicOrigin || saved.ProposalHash != proposal.ProposalHash || saved.SourceHash != proposal.SourceHash ||
            saved.DeviceCode.Length != 64 || !saved.DeviceCode.All(char.IsAsciiHexDigit))
            throw new AgentClientValidationException("The saved administrator approval does not belong to this exact deployment generation.");
        using var workflow = new HostDeploymentWorkflow(saved.Origin);
        var status = await workflow.PollAsync(new(saved.DeviceCode, "", "", DateTimeOffset.MaxValue, 5), ct);
        if (status.Status != "approved" || status.ProposalHash != proposal.ProposalHash || status.SourceHash != proposal.SourceHash)
            throw new AgentClientValidationException("Current administrator authority for this generation could not be confirmed. Reopen the host review; no deployment was applied.");
    }

    private sealed record SavedHostApproval(string Origin, string DeviceCode, string ProposalHash, string SourceHash)
    { public override string ToString() => "SavedHostApproval { capability = [REDACTED] }"; }

    public static void CompleteObservation(string directory)
    {
        var proposal = ResumeApproved(directory);
        var phasePath = Path.Combine(directory, "host-phase.json");
        var phase = HttpMcpEffectiveDeployment.Parse(SecureConfigurationFiles.ReadText(phasePath, requirePrivate: true));
        var now = DateTimeOffset.UtcNow;
        if (phase["phase"]?.ToString() != "verification-probes-passed" ||
            phase["sourceHash"]?.ToString() != proposal.SourceHash || phase["proposalHash"]?.ToString() != proposal.ProposalHash ||
            !DateTimeOffset.TryParse(phase["probesCompletedAtUtc"]?.ToString(), out var completed) || completed > now || completed < now.AddMinutes(-10))
            throw new AgentClientValidationException("HOST_VERIFICATION_PENDING: Fresh authenticated probes and confirmed temporary access cleanup are required before the final Docker observation.");
        var runtimePath = Environment.GetEnvironmentVariable(HttpMcpEffectiveDeployment.RuntimeSourceVariable);
        if (string.IsNullOrWhiteSpace(runtimePath))
            throw new AgentClientValidationException("HOST_RUNTIME_REQUIRED: Invoke --verify through the Docker host launcher for a fresh running-container observation.");
        var runtime = HttpMcpEffectiveDeployment.Parse(SecureConfigurationFiles.ReadPrivateDocument(runtimePath));
        if (!DateTimeOffset.TryParse(runtime["x-rateldesk-observed-at-utc"]?.ToString(), out var observed) || observed < completed || observed > now.AddSeconds(5))
            throw new AgentClientValidationException("HOST_RUNTIME_STALE: Capture the running deployment after authenticated probes and cleanup; rerun --verify through the Docker host launcher.");
        var diagnostics = HttpMcpEffectiveDeployment.Observe(proposal, runtimePath);
        if (diagnostics.Count > 0) throw new AgentClientValidationException(Describe(diagnostics));
        SecureConfigurationFiles.WriteAtomic(phasePath, JsonSerializer.Serialize(new { phase = "end-to-end-verified", verifiedAtUtc = now, proposal.SourceHash, proposal.ProposalHash }, Json));
    }

    public static async Task<int> VerifyAsync(string directory, TextWriter output, TextWriter error, CancellationToken ct)
    {
        IHostVerificationWorkflow prepared;
        try { prepared = new HostVerificationWorkflow(directory); }
        catch (AgentClientValidationException exception)
        { await error.WriteLineAsync(TerminalText.Sanitize(exception.Message)); return SetupRunner.ConfigurationError; }
        await using var verifier = prepared;
        if (!verifier.Supported) { await error.WriteLineAsync(HostVerificationWorkflow.ExternalMessage); return 1; }
        var session = await verifier.StartAsync(ct);
        await error.WriteLineAsync("Temporary status verification · applied—verify pending");
        await error.WriteLineAsync("Trusted instance: " + TerminalText.Sanitize(verifier.Origin));
        await error.WriteLineAsync("Purpose: status only; one authorized organization; no business access or ordinary client profile.");
        await error.WriteLineAsync("Approval URL: " + TerminalText.Sanitize(session.VerificationUri));
        await error.WriteLineAsync("Code: " + TerminalText.Sanitize(session.HumanCode));
        await error.WriteLineAsync($"Status: approval pending · expires {session.ExpiresAtUtc:O}");
        await error.WriteLineAsync("Open the trusted URL on any signed-in device. Ctrl+C cancels; rerun --verify to return to this applied-host checkpoint.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(10));
        while (DateTimeOffset.UtcNow < session.ExpiresAtUtc)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(session.PollIntervalSeconds, 1, 30)), deadline.Token);
            var poll = await verifier.PollAsync(deadline.Token);
            if (poll.Status is "denied" or "expired" or "cancelled") throw new AgentClientValidationException("Temporary verification approval ended. Host settings remain applied—verify pending.");
            if (poll.Credential is null) continue;
            var result = await verifier.RunAsync(deadline.Token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { checks = result.Checks, cleanupPending = result.CleanupPending }, Json));
            await error.WriteLineAsync(result.Message);
            return result.Passed ? 0 : 1;
        }
        throw new AgentClientValidationException("Verification approval expired. Host configuration remains applied—verify pending.");
    }

    private async Task<T> PostAsync<T>(string action, object body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, Prefix + action)) { Content = JsonContent.Create(body) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new AgentClientValidationException($"Host approval {action} failed (HTTP {(int)response.StatusCode}). Check the trusted Web URL and current administrator access; no host credential was created.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0) { if (bytes.Length + count > 65536) throw new AgentClientValidationException("Host approval response exceeds the permitted bound."); await bytes.WriteAsync(buffer.AsMemory(0, count), deadline.Token); }
        try { return JsonSerializer.Deserialize<T>(bytes.ToArray(), Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new AgentClientValidationException("The trusted instance returned an invalid host approval response."); }
    }
    public static string Describe(IEnumerable<DeploymentDiagnostic> diagnostics) => string.Join('\n', diagnostics.Select(diagnostic => $"{diagnostic.Code} [{diagnostic.Layer}] {diagnostic.Key} ({diagnostic.Source}): {diagnostic.Message} {diagnostic.NextAction}"));
    public void Dispose() { if (ownsClient) client.Dispose(); }
}

public sealed record HostApprovalReceipt(bool Approved, string SourceHash, string ProposalHash, string ApprovedBy,
    DateTimeOffset ApprovedAtUtc, string? LauncherAttempt, IReadOnlyDictionary<string, string> ExportHashes);
