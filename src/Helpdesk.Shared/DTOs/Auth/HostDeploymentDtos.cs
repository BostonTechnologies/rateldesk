namespace Helpdesk.Shared.DTOs.Auth;

/// <summary>A bounded, non-secret deployment review; approval creates no credential.</summary>
public sealed record HostDeploymentProposalRequest(string Name, string ProposalHash, string SourceHash,
    string PublicOrigin, string ResourceUri, string Instance, string AuthenticationMode,
    string ApiBaseUrl, string McpDestination, IReadOnlyList<string> Changes)
{
    public IReadOnlyList<HostDeploymentSetting> ReviewSettings { get; init; } = [];
}
public sealed record HostDeploymentSetting(string Key, string Value, string Source);
public sealed record HostDeploymentReview(string HumanCode, HostDeploymentProposalRequest Proposal, DateTimeOffset ExpiresAtUtc);
public sealed record HostDeploymentApprovalRequest(string HumanCode, string ProposalHash, string SourceHash, bool Consent);
public sealed record HostDeploymentApprovalStatus(string Status, string? ProposalHash = null, string? SourceHash = null,
    string? ApprovedBy = null, DateTimeOffset? ApprovedAtUtc = null);
