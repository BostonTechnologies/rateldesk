using System.ComponentModel.DataAnnotations;

namespace Helpdesk.Mcp.Tools;

internal static class OperatorOwnerAdministrationOperations
{
    public static IReadOnlyList<OperatorOperation> All { get; } =
    [
        new("helpdesk_tenant_admin", "my_credentials", "GET", "/api/v1/integration-credentials/",
            Description: "List the authenticated owner's integration credential metadata, scopes, expiry and revocation state. Never returns credential secrets or another owner's records; creating additional account credentials retains its separate interactive consent boundary."),
        new("helpdesk_tenant_admin", "revoke_my_credential", "DELETE", "/api/v1/integration-credentials/{credentialId}",
            QueryType: typeof(OperatorOwnedCredentialSelector), Description: "Revoke one of the authenticated owner's credentials. Existing cached execution tokens are invalidated by current credential checks. A credential owned by another account remains inaccessible."),
        new("helpdesk_tenant_admin", "delete_my_credential_record", "DELETE", "/api/v1/integration-credentials/{credentialId}/record",
            QueryType: typeof(OperatorOwnedCredentialSelector), Description: "Revoke and permanently remove one of the authenticated owner's credential records through the canonical lifecycle. Read owned metadata first and preserve any required audit evidence before confirmation."),
        new("helpdesk_connections", "incident_sources", "GET", "/api/v1/integrations/netratel/sources/",
            Description: "List the configured incident receiver source namespaces, organization/customer targets, enabled state and revision."),
        new("helpdesk_connections", "register_incident_source", "POST", "/api/v1/integrations/netratel/sources/",
            typeof(OperatorRegisterIncidentSource), Description: "Register a NetRatel source instance against an enabled organization/customer and an existing valid API-purpose credential. The API validates that credential's owner, organization, purpose, expiry and revocation; this operation does not issue a new credential."),
        new("helpdesk_connections", "set_incident_source_state", "PATCH", "/api/v1/integrations/netratel/sources/{namespaceId}",
            typeof(OperatorIncidentSourceState), Description: "Enable or disable a registered incident source at its currently read expectedRevision. Concurrent changes return conflict; read the source again before a reviewed update."),
        new("helpdesk_connections", "set_incident_source_credential", "PUT", "/api/v1/integrations/netratel/sources/{namespaceId}/principal-bindings/{credentialId}",
            typeof(OperatorIncidentSourceState), typeof(OperatorSourceCredentialSelector), Description: "Enable or disable an explicitly selected API credential binding for a source namespace at its current expectedRevision. The API verifies active credentials belong to the source organization and persists the source audit.")
    ];
}

internal sealed record OperatorOwnedCredentialSelector([property: Required] Guid CredentialId);
internal sealed record OperatorSourceCredentialSelector([property: Required] Guid CredentialId);
internal sealed record OperatorRegisterIncidentSource(
    [property: Required] Guid SourceInstanceId,
    [property: Required, MaxLength(128)] string OrganizationId,
    [property: Required, MaxLength(128)] string CustomerId,
    [property: Required] Guid CredentialId);
internal sealed record OperatorIncidentSourceState(
    [property: Required, Range(typeof(long), "1", "9223372036854775807")] long ExpectedRevision,
    [property: Required] bool IsEnabled);
