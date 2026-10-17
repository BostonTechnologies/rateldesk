using System.Text.Json;
using Helpdesk.AgentClient;

namespace Helpdesk.Setup;

/// <summary>The same applied-generation checkpoint and temporary identity are used by both UIs.</summary>
public interface IHostVerificationWorkflow : IAsyncDisposable
{
    string Origin { get; }
    bool Supported { get; }
    bool IsAdoption => false;
    Task<PairingSession> StartAsync(CancellationToken ct);
    Task<PairingPollResult> PollAsync(CancellationToken ct);
    Task<HostVerificationResult> RunAsync(CancellationToken ct);
}

public sealed record HostVerificationResult(IReadOnlyList<SetupCheck> Checks, bool CleanupPending, string Message)
{
    public bool Passed => Checks.All(check => check.Status == "passed") && !CleanupPending;
}

public sealed class HostVerificationWorkflow : IHostVerificationWorkflow
{
    private readonly string _directory;
    private readonly HttpMcpDeploymentProposal _proposal;
    private readonly SetupVerificationSession _verifier;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Origin => _proposal.Settings.PublicOrigin;
    public bool Supported => _proposal.Settings.AuthenticationMode == "gateway";
    public bool IsAdoption => _proposal.IsAdoption;
    public const string ExternalMessage = "External Authentik configuration/runtime preflight passed. End-to-end verification remains pending until the external OAuth client performs authenticated initialize, catalog and API-backed status with its own approved token.";

    public HostVerificationWorkflow(string directory)
    {
        _directory = directory;
        _proposal = HostDeploymentWorkflow.ResumeApproved(directory);
        SecureConfigurationFiles.WriteAtomic(Path.Combine(directory, "host-phase.json"), JsonSerializer.Serialize(new { phase = IsAdoption ? "adopted-verify-pending" : "owner-applied-verify-pending", _proposal.SourceHash, _proposal.ProposalHash }, Json));
        var observed = HttpMcpEffectiveDeployment.Observe(_proposal);
        if (observed.Count > 0) throw new AgentClientValidationException(HostDeploymentWorkflow.Describe(observed));
        _verifier = new(Origin, Path.Combine(directory, "verifier-cleanup.json"));
    }

    public Task<PairingSession> StartAsync(CancellationToken ct) => Supported
        ? _verifier.StartAsync(ct) : throw new AgentClientValidationException(ExternalMessage);
    public Task<PairingPollResult> PollAsync(CancellationToken ct) => _verifier.PollAsync(ct);
    public async Task<HostVerificationResult> RunAsync(CancellationToken ct)
    {
        var checks = await _verifier.RunAsync(async (credential, token) => await McpSetupDiagnostics.CheckHttpAsync(
            AgentClientConfiguration.Empty with { ApiBaseUrl = Origin, CredentialMode = "integration", IntegrationCredential = credential.Secret,
                CredentialTarget = Origin, ClientTransport = "http", CredentialPurpose = "mcp", HttpClientAuthentication = "integration", McpResourceUri = _proposal.Settings.PublicResourceUri }, token), ct);
        if (checks.Any(check => check.Status != "passed") || _verifier.CleanupPending)
            return new(checks, _verifier.CleanupPending, _verifier.CleanupPending
                ? "Verification cleanup is pending. Retry verify to recover cleanup; server expiry bounds abandoned access."
                : "End-to-end verification failed. Correct the reported layer, then invoke verify again.");
        HostDeploymentWorkflow.ResumeApproved(_directory);
        SecureConfigurationFiles.WriteAtomic(Path.Combine(_directory, "host-phase.json"), JsonSerializer.Serialize(new { phase = "verification-probes-passed", probesCompletedAtUtc = DateTimeOffset.UtcNow, _proposal.SourceHash, _proposal.ProposalHash }, Json));
        return new(checks, false, "Authenticated initialize, complete catalog, API-backed status and temporary access cleanup passed. Final Docker observation remains pending.");
    }
    public ValueTask DisposeAsync() => _verifier.DisposeAsync();
}
