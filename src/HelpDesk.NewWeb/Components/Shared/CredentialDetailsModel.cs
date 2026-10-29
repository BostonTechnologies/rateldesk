namespace HelpDesk.NewWeb.Components.Shared;

public sealed record CredentialDetailsModel(
    Guid Id,
    string Name,
    string Prefix,
    string Purpose,
    string? OrganizationId,
    string OrganizationName,
    IReadOnlyList<string> Permissions,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? McpResourceUri);
