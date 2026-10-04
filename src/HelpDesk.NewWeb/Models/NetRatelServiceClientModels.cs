namespace HelpDesk.NewWeb.Models;

public sealed record NetRatelServiceClientMetadata(
    Guid Id, string Name, string ClientId, string OrganizationId, string PeerInstanceId,
    string PeerTenantId, IReadOnlyList<string> Scopes, IReadOnlyList<string> CustomerIds,
    string Status, string Source, bool ReadOnly, long Revision, long CredentialRevision,
    Guid? SourceInstanceId, Guid? SourceNamespaceId, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc)
{
    public string? LinkId { get; init; }
    public string? DirectionId { get; init; }
}

public sealed record NetRatelServiceClientReveal(
    NetRatelServiceClientMetadata Client, string ClientSecret, string Issuer,
    string TokenEndpoint, string Audience, IReadOnlyList<string> Scopes,
    string DockerEnvironmentExample);
