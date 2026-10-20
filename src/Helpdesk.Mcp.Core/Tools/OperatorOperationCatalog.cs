using System.ComponentModel.DataAnnotations;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Pairing;

namespace Helpdesk.Mcp.Tools;

/// <summary>A compiled, named adapter to one canonical API operation. Callers cannot supply routes or methods.</summary>
internal sealed record OperatorOperation(
    string Tool,
    string Operation,
    string Method,
    string Path,
    Type? BodyType = null,
    Type? QueryType = null,
    string[]? RequiredBodyFields = null,
    bool ExternalIo = false,
    string Authority = "HelpdeskAdmin",
    string Description = "",
    bool ReturnsOneTimeSecret = false,
    bool MutatingRead = false)
{
    public McpOperationDefinition Definition => new(Tool, Operation,
        BodyType is null && QueryType is null && !Path.Contains('{') ? "None" : "OperatorRequest",
        Method, Path, Method != "GET" || MutatingRead, Description);

    public IReadOnlyList<string> SensitiveResultFields => !ReturnsOneTimeSecret ? [] : (Tool, Operation) switch
    {
        ("helpdesk_tenant_admin", "create_local_account" or "reset_local_account" or "reset_customer_login") => ["activationToken"],
        ("helpdesk_datasets", "create_credential") => ["plainTextKey"],
        ("helpdesk_connections", "generate_pairing_code") => ["code"],
        _ => []
    };
}

internal static class OperatorOperationCatalog
{
    public static IReadOnlyList<OperatorOperation> All { get; } =
    [
        .. Automation(), .. Administration(), .. OperatorMailSupportOperations.All,
        .. OperatorAiOperations.All, .. OperatorBusinessAdministrationOperations.All,
        .. OperatorOwnerAdministrationOperations.All, .. OperatorTicketOperations.All,
        .. OperatorIncidentOperations.All
    ];

    public static OperatorOperation? Find(string tool, string operation) =>
        All.FirstOrDefault(item => item.Tool == tool && item.Operation == operation);

    private static IEnumerable<OperatorOperation> Automation()
    {
        const string orchestration = "/api/v1/admin/orchestration";
        yield return new("helpdesk_automation", "create_form", "POST", "/api/v1/request-forms/", typeof(OperatorCreateForm), Description: "Create a self-service request form with a typed fields/tasks schema. Set releaseStatus, audience and each automation task's autoStart deliberately. Task IDs and dependency IDs are UUIDs; the API validates the dependency graph and dataset bindings.");
        yield return new("helpdesk_automation", "update_form", "PUT", "/api/v1/request-forms/{requestFormId}", typeof(OperatorUpdateForm), Description: "Edit a self-service form through its typed fields/tasks schema; omitted audience and release status remain unchanged. Existing bindings are managed separately through binding operations.");
        yield return new("helpdesk_automation", "create_request_definition", "POST", orchestration + "/catalog/request-definitions",
            typeof(CreateOrchestrationCatalogRequestDefinitionDto), typeof(OperatorConnectionSelection), ["name", "clientIdentity"], true,
            Description: "Create a provider automation target using the selected saved organization mapping. This creates a request definition through the supported provider adapter.");
        yield return new("helpdesk_automation", "bindings", "GET", orchestration + "/bindings", QueryType: typeof(OperatorFormFilter), Description: "List automation bindings, optionally for one request form.");
        yield return new("helpdesk_automation", "get_binding", "GET", orchestration + "/bindings/{bindingId}", Description: "Read one saved automation binding.");
        yield return new("helpdesk_automation", "binding_by_task", "GET", orchestration + "/bindings/by-task", QueryType: typeof(OperatorBindingTaskFilter), Description: "Resolve the binding for one task template in one form.");
        yield return new("helpdesk_automation", "create_binding", "POST", orchestration + "/bindings", typeof(CreateAutomationBindingDto), Description: "Bind a form task template to a provider request definition through a saved connection.");
        yield return new("helpdesk_automation", "update_binding", "PUT", orchestration + "/bindings/{bindingId}", typeof(UpdateAutomationBindingDto), Description: "Update or enable/disable an existing automation binding. Omitted properties retain their current values; explicit clear fields remove optional values.");
        yield return new("helpdesk_automation", "delete_binding", "DELETE", orchestration + "/bindings/{bindingId}", Description: "Delete one automation binding.");
        yield return new("helpdesk_automation", "sync_binding_inputs", "POST", orchestration + "/bindings/{bindingId}/sync-inputs", ExternalIo: true, Description: "Synchronize the form task inputs with its bound automation target.");
        yield return new("helpdesk_automation", "refresh_binding_drift", "POST", orchestration + "/bindings/refresh-drift", QueryType: typeof(OperatorFormFilter), ExternalIo: true, Description: "Refresh provider schema drift for saved bindings, optionally limited to a form.");
        yield return new("helpdesk_automation", "mark_binding_import_pending", "POST", orchestration + "/bindings/{bindingId}/mark-import-pending", Description: "Mark a binding for explicit reviewed schema import.");
        yield return new("helpdesk_automation", "binding_drift_preview", "GET", orchestration + "/bindings/{bindingId}/drift-preview", ExternalIo: true, Description: "Compare the saved form inputs and provider target schema before importing.");
        yield return new("helpdesk_automation", "import_binding_inputs", "POST", orchestration + "/bindings/{bindingId}/import-inputs", ExternalIo: true, Description: "Import the bound provider input definitions into the form task template.");
        yield return new("helpdesk_automation", "approver_candidates", "GET", "/api/v1/request-task-approvers", QueryType: typeof(OperatorApproverFilter), Description: "Find designated user/customer approval candidates for a self-service task template.");
        yield return new("helpdesk_automation", "my_task_approval", "GET", "/api/v1/request-tasks/{taskId}/approval/", Authority: "Stored designated approver identity plus Request.User, SelfService.User or request management authority in the request organization", Description: "Read the caller's assigned approval. Even an owner grant cannot impersonate another designated approver.");
        yield return new("helpdesk_automation", "approve_task", "POST", "/api/v1/request-tasks/{taskId}/approval/approve", ExternalIo: true, Authority: "Stored designated approver identity plus Request.User, SelfService.User or request management authority in the request organization", Description: "Approve the caller's own assigned pending task before its deadline; resumes the workflow. Repeating the same accepted decision is idempotent.");
        yield return new("helpdesk_automation", "reject_task", "POST", "/api/v1/request-tasks/{taskId}/approval/reject", typeof(OperatorApprovalRejection), ExternalIo: true, Authority: "Stored designated approver identity plus Request.User, SelfService.User or request management authority in the request organization", Description: "Reject the caller's own assigned pending task with a reason through the canonical cancellation and notification lifecycle.");
        yield return new("helpdesk_automation", "workflow_timeline", "GET", "/api/v1/requests/{requestId}/workflow-timeline", QueryType: typeof(OperatorWorkflowTimelineFilter), Authority: "Request.Manager in the request organization or HelpdeskAdmin", Description: "Read independently persisted request workflow events, including provider execution identities and effects.");
        foreach (var (name, path) in new[] { ("overdue_tasks", "overdue"), ("escalated_tasks", "escalated"), ("pending_retries", "retries-pending"), ("critical_failures", "critical-failures"), ("callback_rejections", "orchestration/callback-rejections"), ("binding_issues", "orchestration/binding-issues") })
            yield return new("helpdesk_automation", name, "GET", "/api/v1/ops/tasks/" + path, QueryType: name == "critical_failures" ? typeof(OperatorCriticalFailureFilter) : name == "callback_rejections" ? typeof(OperatorCallbackFilter) : typeof(OperatorPageFilter), Authority: "Request.Manager in an authorized organization or HelpdeskAdmin", Description: "Read bounded workflow operational evidence and pending recovery work.");
        yield return new("helpdesk_automation", "workflow_metrics", "GET", "/api/v1/metrics/workflows", Authority: "Request.Read, Request.Write or Request.Manager in the selected organizations", Description: "Read organization-filtered workflow operational counters as Prometheus text.");

        yield return new("helpdesk_attachments", "list", "GET", "/api/v1/tickets/{ticketId}/attachments/", Authority: "Parent ticket visibility", Description: "List authorized attachment metadata for an incident, request or change.");
        yield return new("helpdesk_attachments", "download", "GET", "/api/v1/attachments/{attachmentId}", Authority: "Parent ticket visibility", Description: "Read a bounded attachment as base64 with its media type and filename; maximum file size is 1 MiB.");
        yield return new("helpdesk_attachments", "upload", "POST", "/api/v1/tickets/{ticketId}/attachments/", typeof(OperatorAttachmentUpload), Authority: "Parent ticket manager or authorized self-service owner", Description: "Upload one base64 attachment, at most 1 MiB, using the authenticated multipart endpoint. The API enforces the parent ticket's authority.");
    }

    private static IEnumerable<OperatorOperation> Administration()
    {
        yield return new("helpdesk_auth", "revoke_self", "POST", "/api/v1/integration-credentials/self/revoke", Authority: "Current API integration credential or source MCP execution credential; setup verifier excluded", Description: "Revoke the credential used by this request. The next authenticated call will fail until a separately authorized credential is supplied. This cannot issue or broaden another credential.");
        const string connections = "/api/v1/admin/system-connections";
        yield return new("helpdesk_connections", "list", "GET", connections + "/", Description: "List saved system connections and their health without returning peer credentials.");
        yield return new("helpdesk_connections", "generate_pairing_code", "POST", connections + "/code", Description: "Generate an expiring single-use system pairing code as a sensitive result. Deliver only to the authorized peer setup workflow; exclude it from campaign evidence and logs.", ReturnsOneTimeSecret: true);
        yield return new("helpdesk_connections", "pair", "POST", connections + "/pair", typeof(OperatorPairConnection), ExternalIo: true, Description: "Connect to an explicitly selected peer using its supplied short-lived write-only pairing code. Reuse the same operationId when reading the result of an uncertain attempt; the canonical signed peer protocol, address validation and owner checks apply.");
        yield return new("helpdesk_connections", "directory", "GET", connections + "/{pairId}/directory", ExternalIo: true, Description: "Read authorized organization, customer and provider-tenant choices for a saved pair.");
        yield return new("helpdesk_connections", "save_mapping", "PUT", connections + "/{pairId}/mappings/{mappingId}", typeof(PairingMapping), RequiredBodyFields: ["id", "pairId", "name", "netRatelTenantId", "ratelDeskOrganizationId", "createIncidents", "runAutomation"], ExternalIo: true, Description: "Create or update a named organization/provider mapping on an existing securely paired peer. body.id must equal mappingId and body.pairId must equal pairId.");
        yield return new("helpdesk_connections", "test_mapping", "POST", connections + "/{pairId}/mappings/{mappingId}/test", ExternalIo: true, Description: "Test the exact saved mapping's approved capabilities and organization binding.");
        yield return new("helpdesk_connections", "delete_mapping", "DELETE", connections + "/{pairId}/mappings/{mappingId}", ExternalIo: true, Description: "Remove one saved named mapping from the paired systems.");
        yield return new("helpdesk_connections", "delete_pair", "DELETE", connections + "/{pairId}", ExternalIo: true, Description: "Delete a saved pair and its mappings through the supported cleanup lifecycle.");
        yield return new("helpdesk_connections", "netclaw_settings", "GET", "/api/v1/admin/netclaw/", Description: "Read saved Netclaw connectivity metadata with secret-state flags.");
        yield return new("helpdesk_connections", "update_netclaw_settings", "PUT", "/api/v1/admin/netclaw/", typeof(OperatorNetclawSettings), RequiredBodyFields: ["enabled"], Description: "Update Netclaw connectivity settings with optional write-only device-token replacement or explicit clearing. Omit the token to preserve its protected saved value. expectedRevision enforces concurrent-update detection.");
        yield return new("helpdesk_connections", "test_netclaw", "POST", "/api/v1/admin/netclaw/test", ExternalIo: true, Description: "Test the saved Netclaw connection and authenticated session protocol.");
        yield return new("helpdesk_connections", "test_netclaw_draft", "POST", "/api/v1/admin/netclaw/test-draft", typeof(OperatorNetclawSettings), RequiredBodyFields: ["enabled"], ExternalIo: true, Description: "Test an explicit Netclaw settings draft through the authenticated session protocol without saving it. Omitted deviceToken retains the saved credential only where its endpoint binding matches. Read the saved profile and revision first.");
        yield return new("helpdesk_connections", "pair_netclaw", "POST", "/api/v1/admin/netclaw/pair-and-save", typeof(OperatorPairNetclaw), ExternalIo: true, Description: "Exchange a supplied one-time Netclaw pairing code, save the resulting protected device credential, and verify the authenticated session. Read netclaw_settings first and supply its current expectedRevision. If older unbound conversations exist, inspect netclaw_unbound_sessions and confirm their actual historical provider with confirm_netclaw_session_owner before pairing. On conflict or uncertain verification read the saved profile; a consumed pairing code must not be blindly replayed. Native Netclaw pairing requires its PostgreSQL durable-session capability.");
        yield return new("helpdesk_connections", "netclaw_unbound_sessions", "GET", "/api/v1/admin/netclaw/legacy-sessions/unbound", Description: "Read the exact legacy conversation IDs, ticket and organization context that still need historical Netclaw provider ownership confirmation.");
        yield return new("helpdesk_connections", "confirm_netclaw_session_owner", "POST", "/api/v1/admin/netclaw/legacy-sessions/confirm-owner", typeof(OperatorConfirmNetclawSessionOwner), Description: "Bind explicitly reviewed legacy conversation IDs to their actual historical Netclaw endpoint and instance. expectedEligibleConversations must equal the unique submitted IDs. The API compares current eligibility and commits its owner audit; reload on a conflict before submitting a new reviewed selection.");

        const string tenant = "/api/v1/tenant-admin/organizations";
        yield return new("helpdesk_tenant_admin", "organizations", "GET", tenant, Authority: "Tenant membership/settings administration or HelpdeskAdmin", Description: "List the organizations the caller can administer.");
        yield return new("helpdesk_tenant_admin", "settings", "GET", tenant + "/{organizationId}/settings/", Authority: "Tenant.Settings.Manage in organization or HelpdeskAdmin", Description: "Read one organization's display and contact settings.");
        yield return new("helpdesk_tenant_admin", "update_settings", "PUT", tenant + "/{organizationId}/settings/", typeof(OperatorTenantSettings), Authority: "Tenant.Settings.Manage in organization or HelpdeskAdmin", Description: "Update one organization's display and contact settings.");
        yield return new("helpdesk_tenant_admin", "members", "GET", tenant + "/{organizationId}/users/", Authority: "Tenant user read/manage authority or HelpdeskAdmin", Description: "List local team members and their scoped role assignments.");
        yield return new("helpdesk_tenant_admin", "delegable_roles", "GET", tenant + "/{organizationId}/users/roles", Authority: "Tenant role-management authority or HelpdeskAdmin", Description: "List operational built-in and tenant-owned roles that may be delegated in this organization.");
        yield return new("helpdesk_tenant_admin", "assignments", "GET", tenant + "/{organizationId}/users/{userId}/assignments", Authority: "Tenant role-management authority or HelpdeskAdmin", Description: "Read the selected local user's role assignments within this organization.");
        yield return new("helpdesk_tenant_admin", "replace_assignments", "PUT", tenant + "/{organizationId}/users/{userId}/assignments", typeof(OperatorRoleAssignments), Authority: "Tenant role-management authority or HelpdeskAdmin", Description: "Replace the selected local user's delegable role assignments; an empty list removes those assignments. Other scopes and instance-admin authority remain governed by the API.");
        yield return new("helpdesk_tenant_admin", "create_local_account", "POST", "/api/v1/local-auth/users", typeof(OperatorCreateLocalAccount), Description: "Create a local account and return its newly issued one-time activation token as an explicitly sensitive result. This does not read an existing password. Deliver the token only through the authorized private activation workflow.", ReturnsOneTimeSecret: true);
        yield return new("helpdesk_tenant_admin", "reset_local_account", "POST", "/api/v1/local-auth/users/{userId}/activation-token", Description: "Issue a new one-time local-account activation token as an explicitly sensitive result; preserve it only through the authorized private activation workflow.", ReturnsOneTimeSecret: true);
        yield return new("helpdesk_tenant_admin", "reset_customer_login", "POST", "/api/v1/customers/{customerId}/auth/local-activation-token", Description: "Issue a new one-time activation token for the customer's single linked enabled local account. Inspect helpdesk_customers.auth_status first; external customer invite/resend/sync flows have their separate identity-provider contract.", ReturnsOneTimeSecret: true);
        yield return new("helpdesk_tenant_admin", "local_account_status", "GET", "/api/v1/local-auth/users/{userId}/status", Description: "Read a local account's enabled status.");
        yield return new("helpdesk_tenant_admin", "disable_local_account", "POST", "/api/v1/local-auth/users/{userId}/disable", Description: "Disable a local account through the existing administrator lifecycle, including session/credential invalidation.");
        yield return new("helpdesk_tenant_admin", "enable_local_account", "POST", "/api/v1/local-auth/users/{userId}/enable", Description: "Enable a disabled local account.");
        yield return new("helpdesk_tenant_admin", "local_account_assignments", "GET", "/api/v1/local-auth/users/{userId}/assignments", Description: "Read all scoped role assignments and local/instance-administrator flags for one account.");
        yield return new("helpdesk_tenant_admin", "replace_local_account_assignments", "PUT", "/api/v1/local-auth/users/{userId}/assignments", typeof(OperatorLocalAccountAssignments), Description: "Replace the selected non-instance-admin account's explicit scoped role assignments; each role and organization is validated by the API.");
    }
}

internal sealed record OperatorConnectionSelection(string? OrganizationId = null, Guid? ConnectionId = null);
internal sealed record OperatorFormFilter(string? RequestFormId = null);
internal sealed record OperatorBindingTaskFilter([property: Required] string RequestFormId, [property: Required] Guid TaskTemplateId);
internal sealed record OperatorApproverFilter(string? OrganizationId = null, [property: MaxLength(200)] string? Q = null, [property: Range(1, 100)] int? PageSize = null);
internal sealed record OperatorPageFilter(
    [property: Range(1, 10000)] int? Page = null,
    [property: Range(1, 100)] int? PageSize = null);
internal sealed record OperatorCriticalFailureFilter([property: Range(1, 10000)] int? Page = null, [property: Range(1, 100)] int? PageSize = null, DateTimeOffset? SinceUtc = null);
internal sealed record OperatorCallbackFilter([property: Range(1, 10000)] int? Page = null, [property: Range(1, 100)] int? PageSize = null, [property: Range(1, 30)] int? LookbackDays = null);
internal sealed record OperatorWorkflowTimelineFilter([property: Range(1, 10000)] int? Page = null, [property: Range(1, 100)] int? PageSize = null, bool? IncludePayload = null, string? CorrelationId = null, string? Categories = null, DateTimeOffset? SinceUtc = null);
internal sealed record OperatorTenantSettings([property: Required, MaxLength(200)] string Name, [property: MaxLength(4000)] string? ContactInfo = null);
internal sealed record OperatorApprovalRejection([property: Required, MaxLength(1000)] string Reason);
internal sealed record OperatorRoleAssignments([property: Required, MaxLength(100)] IReadOnlyList<string> RoleKeys);
internal sealed record OperatorPairConnection(
    [property: Required, MaxLength(2048)] string Address,
    [property: Required, MaxLength(32), DataType(DataType.Password)] string PairingCode,
    [property: Required] Guid OperationId);
internal sealed record OperatorCreateLocalAccount(
    [property: Required, MaxLength(200)] string DisplayName,
    [property: Required, MaxLength(254)] string Email,
    bool IsInstanceAdministrator = false,
    [property: AllowedValues("User", "Technician")] string Role = "User",
    string? OrganizationId = null, bool IsTestUser = false);
internal sealed record OperatorLocalAccountAssignments([property: Required, MaxLength(100)] IReadOnlyList<OperatorLocalRoleAssignment> Assignments);
internal sealed record OperatorLocalRoleAssignment([property: Required] string RoleKey, [property: Required] string OrganizationId);
internal sealed record OperatorNetclawSettings(
    int? ExpectedRevision, bool Enabled, string? Instance, string? Endpoint, bool AllowPrivateHttp,
    [property: Range(1, 1440)] int IdleMinutes = 15,
    [property: Range(1, 1000)] int ConnectionCapacity = 25,
    TimeSpan TurnInactivityTimeout = default,
    TimeSpan ActivityHeartbeatInterval = default,
    [property: DataType(DataType.Password)] string? DeviceToken = null,
    bool ClearDeviceToken = false);
internal sealed record OperatorPairNetclaw(
    [property: Required, MaxLength(1024), DataType(DataType.Password)] string PairingCode,
    [property: Required, Range(0, int.MaxValue)] int ExpectedRevision,
    [property: Required, MaxLength(2048)] string Endpoint,
    [property: Required] bool Enabled,
    [property: MaxLength(200)] string? Instance = null,
    bool AllowPrivateHttp = false,
    [property: Range(1, 1440)] int IdleMinutes = 15,
    [property: Range(1, 1000)] int ConnectionCapacity = 25,
    TimeSpan TurnInactivityTimeout = default,
    TimeSpan ActivityHeartbeatInterval = default,
    [property: MaxLength(128), DataType(DataType.Password)] string? LegacyOwnershipReviewToken = null);
internal sealed record OperatorConfirmNetclawSessionOwner(
    [property: Required, MaxLength(2048)] string HistoricalEndpoint,
    [property: Required, Range(1, 100)] int ExpectedEligibleConversations,
    [property: Required, MinLength(1), MaxLength(100)] IReadOnlyList<Guid> ConversationIds,
    [property: MaxLength(200)] string? HistoricalInstance = null,
    bool AllowPrivateHttp = false);
internal sealed record OperatorCreateForm(
    [property: Required] string ServiceId,
    [property: Required, MaxLength(200)] string Title,
    [property: Required] RequestFormSchemaModel Schema,
    string? Description = null, string? OrganizationId = null,
    IReadOnlyList<string>? AllowedOrganizationIds = null,
    RequestFormReleaseStatus? ReleaseStatus = null);
internal sealed record OperatorUpdateForm(
    [property: Required] RequestFormSchemaModel Schema,
    [property: MaxLength(200)] string? Title = null,
    string? Description = null, string? OrganizationId = null,
    IReadOnlyList<string>? AllowedOrganizationIds = null,
    RequestFormReleaseStatus? ReleaseStatus = null,
    bool? ServiceIdHasValue = null, string? ServiceId = null);
internal sealed record OperatorAttachmentUpload(
    [property: Required, MaxLength(255)] string FileName,
    [property: Required, MaxLength(128)] string ContentType,
    [property: Required, MaxLength(1398104)] string ContentBase64);
