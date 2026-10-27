namespace Helpdesk.Shared.DTOs.McpPolicies;

public sealed record McpPolicyDraft(Guid CredentialId, string OrganizationId, string[] Permissions, DateTime ExpiresUtc, int ExpectedRevision, string State);
public sealed record McpPolicyDto(Guid CredentialId, string OrganizationId, string[] Permissions, DateTime ExpiresUtc, int Revision, string State);
public sealed record McpPolicyPreview(string Confirmation, McpPolicyDraft Policy, string Effect);
public sealed record ConfirmMcpPolicy(string Confirmation);
public sealed record McpPolicyCredential(Guid Id, string Name, string Owner, string? OrganizationId, DateTimeOffset ExpiresAtUtc);
public sealed record McpPolicyAuditDto(Guid Id, Guid CredentialId, int Revision, string Actor, DateTime OccurredUtc, string DefinitionJson);
