namespace Helpdesk.Shared.DTOs.McpPolicies;

public sealed record McpPolicyDraft(Guid CredentialId, string OrganizationId, string[] Permissions, DateTime ExpiresUtc, int ExpectedRevision, string State)
{
    public McpPolicyScopeDraft[] Scopes { get; init; } = [];
}
public sealed record McpPolicyDto(Guid CredentialId, string OrganizationId, string[] Permissions, DateTime ExpiresUtc, int Revision, string State)
{
    public McpPolicyScopeDraft[] Scopes { get; init; } = [];
    public string? CredentialName { get; init; }
    public string? OwnerName { get; init; }
    public string? OrganizationName { get; init; }
    public string? Resource { get; init; }
}
public sealed record McpPolicyPreview(string Confirmation, McpPolicyDraft Policy, string Effect);
public sealed record ConfirmMcpPolicy(string Confirmation);
public sealed record McpPolicyCredential(Guid Id, string Name, string Owner, string? OrganizationId, DateTimeOffset ExpiresAtUtc)
{
    public string? OwnerName { get; init; }
    public string? OrganizationName { get; init; }
    public string? Resource { get; init; }
    public string GrantKind { get; init; } = "ordinary";
    public string[] Permissions { get; init; } = [];
    public string? PolicyState { get; init; }
    public int? PolicyRevision { get; init; }
    public bool IsActive { get; init; }
}
public sealed record McpPolicyAuditDto(Guid Id, Guid CredentialId, int Revision, string Actor, DateTime OccurredUtc, string DefinitionJson);

public sealed record McpPolicyScopeDraft(string Name, string Scope, string? OrganizationId, string Effect,
    string[] Permissions, string[] Operations, DateTime ExpiresUtc, McpPolicyExceptionDraft[] Exceptions);
public sealed record McpPolicyExceptionDraft(string Name, string Effect, string[] Operations, DateTime ExpiresUtc);
public sealed record McpPolicyOperation(string Key, string Family, string Name, string Effect);
public sealed record McpPolicyOrganization(string Id, string Name);
public sealed record McpPolicyDirectory(McpPolicyCredential[] Credentials, McpPolicyOrganization[] Organizations,
    McpPolicyOperation[] Operations);
public sealed record McpPolicyDeleteDraft(int ExpectedRevision);

