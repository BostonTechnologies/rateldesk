namespace Helpdesk.Shared.DTOs.Auth;

public sealed record IntegrationPairingCapabilities(string Product, int ProtocolVersion, bool PairingSupported,
    string? PublicOrigin, string? VerificationUri, int DefaultLifetimeDays = 30, int MaximumLifetimeDays = 90);
public sealed record IntegrationPairingStartRequest(string Name, string Purpose, string? McpResourceUri = null);
public sealed record IntegrationPairingStarted(string DeviceCode, string HumanCode, string VerificationUri,
    DateTimeOffset ExpiresAtUtc, int PollIntervalSeconds)
{
    public override string ToString() => $"IntegrationPairingStarted {{ HumanCode = {HumanCode}, DeviceCode = [redacted], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}
public sealed record IntegrationPairingDeviceRequest(string DeviceCode)
{
    public override string ToString() => "IntegrationPairingDeviceRequest { DeviceCode = [redacted] }";
}
public sealed record IntegrationPairingAckRequest(string DeviceCode, Guid CredentialId)
{
    public override string ToString() => $"IntegrationPairingAckRequest {{ DeviceCode = [redacted], CredentialId = {CredentialId} }}";
}
public sealed record IntegrationPairingReviewRequest(string HumanCode);
public sealed record IntegrationPairingOrganization(string Id, string Name, IReadOnlyList<string> Permissions);
public sealed record IntegrationPairingReview(string HumanCode, string Name, string Purpose, string? McpResourceUri,
    DateTimeOffset ExpiresAtUtc, IReadOnlyList<IntegrationPairingOrganization> Organizations);
public sealed record IntegrationPairingApprovalRequest(string HumanCode, string OrganizationId,
    IReadOnlyList<string> Permissions, int? LifetimeDays, bool Consent);
