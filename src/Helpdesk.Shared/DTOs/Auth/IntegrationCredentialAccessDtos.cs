namespace Helpdesk.Shared.DTOs.Auth;

public sealed record IntegrationCredentialAccessDetails(
    Guid CredentialId,
    string? OrganizationId,
    string? OrganizationName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> AvailablePermissions,
    IReadOnlyList<string> EffectivePermissions,
    IReadOnlyList<string> DeniedPermissions,
    string Version,
    bool CanEdit,
    string? EditUnavailableReason,
    string PolicyState,
    string PolicySummary)
{
    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public sealed record IntegrationCredentialAccessDraft(IReadOnlyList<string> Permissions, string ExpectedVersion);

public sealed record IntegrationCredentialAccessReview(
    string Confirmation,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> AddedPermissions,
    IReadOnlyList<string> RemovedPermissions,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() =>
        $"IntegrationCredentialAccessReview {{ Confirmation = [redacted], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

public sealed record ApplyIntegrationCredentialAccess(string Confirmation, bool Consent)
{
    public override string ToString() => "ApplyIntegrationCredentialAccess { Confirmation = [redacted] }";
}
