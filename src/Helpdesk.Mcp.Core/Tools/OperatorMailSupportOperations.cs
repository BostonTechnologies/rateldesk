using System.ComponentModel.DataAnnotations;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.DTOs.Sla;
using Helpdesk.Shared.DTOs.SlaPolicy;
using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

namespace Helpdesk.Mcp.Tools;

/// <summary>Named adapters for existing mail, support, SLA and dataset APIs.</summary>
internal static class OperatorMailSupportOperations
{
    public static IEnumerable<OperatorOperation> All =>
        [.. Mail(), .. EmailSettings(), .. Support(), .. Sla(), .. Datasets()];

    private static IEnumerable<OperatorOperation> Mail()
    {
        const string timeline = "/api/v1/timeline";
        yield return new("helpdesk_mail", "pending", "GET", timeline + "/pending",
            QueryType: typeof(OperatorPendingMailQuery), Description: "Search pending deliveries with bounded paging, status, recipient and organization filters. Delivery state is distinct from queue acceptance.");
        yield return new("helpdesk_mail", "pending_count", "GET", timeline + "/pending-count", Description: "Read the number of failed deliveries awaiting review or retry.");
        yield return new("helpdesk_mail", "failed", "GET", timeline + "/failed", Description: "Read up to 100 failed delivery events including retry eligibility and recipient outcome evidence.");
        yield return new("helpdesk_mail", "retry", "POST", timeline + "/{deliveryId}/retry", ExternalIo: true,
            Description: "Retry one eligible failed delivery. Uncertain, partially accepted or changed-sender deliveries require their separate reviewed retry operation.");
        yield return new("helpdesk_mail", "outgoing_retry_preview", "GET", timeline + "/{deliveryId}/outgoing-retry-preview",
            Description: "Read the current outgoing revision and fence required before retrying a reviewed sender configuration.");
        yield return new("helpdesk_mail", "retry_current_outgoing", "POST", timeline + "/{deliveryId}/retry-current-outgoing",
            BodyType: typeof(ConfirmMailboxOutgoingRetryRequest), RequiredBodyFields: ["expectedOutgoingVersion", "expectedMailboxId", "expectedMailboxVersion", "expectedFence", "confirmed"], ExternalIo: true,
            Description: "Retry using the reviewed outgoing configuration with its expected revisions and fence. Preserve the preview values in the request.");
        yield return new("helpdesk_mail", "uncertain_retry_preview", "GET", timeline + "/{deliveryId}/uncertain-retry-preview",
            Description: "Read an uncertain delivery's current fence, sender and recipients before independently establishing whether delivery occurred.");
        yield return new("helpdesk_mail", "retry_confirmed_undelivered", "POST", timeline + "/{deliveryId}/retry-confirmed-undelivered",
            BodyType: typeof(OperatorMailboxUndeliveredRetry), RequiredBodyFields: ["expectedFence", "expectedOutgoingVersion", "evidenceReference", "confirmedNoDelivery"], ExternalIo: true,
            Description: "Retry an uncertain delivery only after independent evidence confirms no delivery. The API retains evidence, outcome and fence checks.");
        yield return new("helpdesk_mail", "changed_route_preview", "GET", timeline + "/{deliveryId}/changed-route-preview",
            Description: "Read the old and current sender routes and all revisions before approving a route change.");
        yield return new("helpdesk_mail", "retry_current_route", "POST", timeline + "/{deliveryId}/retry-current-route",
            BodyType: typeof(ConfirmMailboxRouteRetryRequest), RequiredBodyFields: ["expectedFence", "expectedOriginalMailboxId", "expectedCurrentMailboxId", "expectedMailboxVersion", "expectedOutgoingVersion", "confirmedNewSender"], ExternalIo: true,
            Description: "Retry with an explicitly reviewed new sender using the exact route-preview identifiers and revisions.");
        yield return new("helpdesk_mail", "pending_organizations", "GET", timeline + "/pending/organizations",
            QueryType: typeof(OperatorPendingOrganizationQuery), Description: "Look up organizations for pending-delivery filtering; returns at most 50 matches.");
        yield return new("helpdesk_mail", "pending_actions", "POST", timeline + "/pending/actions",
            BodyType: typeof(OperatorPendingMailAction), RequiredBodyFields: ["ids", "action", "reason", "confirmed"], ExternalIo: true,
            Description: "Apply retry, cancel or remove to 1–100 explicitly selected delivery IDs with a recorded reason. The API reauthorizes and reports each result independently.");
        yield return new("helpdesk_mail", "send", "POST", "/api/v1/email/send",
            BodyType: typeof(OperatorSendMail), RequiredBodyFields: ["recipients", "subject", "htmlMessage"], ExternalIo: true,
            Description: "Send an administrator-authored message through the configured mail service to explicitly supplied recipients. For ticket replies use the ticket add_worklog operation with isInternalNote=false.");
        yield return new("helpdesk_mail", "test_provider", "POST", "/api/v1/email/test", ExternalIo: true,
            Description: "Test availability of the configured email provider.");
    }

    private static IEnumerable<OperatorOperation> EmailSettings()
    {
        const string settings = "/api/v1/email-settings";
        yield return new("helpdesk_email_settings", "list", "GET", settings + "/", Description: "Read saved mailbox settings with stored secrets redacted by the API.");
        yield return new("helpdesk_email_settings", "get", "GET", settings + "/{mailboxId}", Description: "Read one saved mailbox configuration and its concurrency version.");
        yield return new("helpdesk_email_settings", "effective", "GET", settings + "/effective", Description: "Read each organization's effective dedicated or inherited mailbox route.");
        yield return new("helpdesk_email_settings", "worker_status", "GET", settings + "/worker", Description: "Read the persisted instance mailbox worker decision and deployment-level ingestion status.");
        yield return new("helpdesk_email_settings", "set_worker", "POST", settings + "/worker",
            BodyType: typeof(OperatorMailboxWorkerChange), RequiredBodyFields: ["running", "confirmed"], ExternalIo: true,
            Description: "Persist an explicitly confirmed mailbox worker run/pause decision. Deployment-level disabled ingestion remains authoritative.");
        yield return new("helpdesk_email_settings", "create", "POST", settings + "/",
            BodyType: typeof(OperatorMailboxConfiguration), RequiredBodyFields: ["displayName", "provider", "authentication", "scope", "mailboxAddress"],
            Description: "Create a mailbox with explicitly supplied write-only credentials. Credential values never appear in the preview or normal readback; create does not import mail implicitly.");
        yield return new("helpdesk_email_settings", "update", "PUT", settings + "/{mailboxId}",
            BodyType: typeof(OperatorMailboxConfiguration), RequiredBodyFields: ["version", "displayName", "provider", "authentication", "scope", "mailboxAddress"],
            Description: "Save the complete mailbox configuration using the current version. Secret fields are write-only; omitted or empty values preserve saved secrets unless the corresponding clear flag is true.");
        yield return new("helpdesk_email_settings", "test_mailbox", "POST", settings + "/{mailboxId}/test", ExternalIo: true,
            Description: "Test a saved inbound mailbox configuration and retain its test result against the saved revision.");
        yield return new("helpdesk_email_settings", "test_draft_mailbox", "POST", settings + "/test",
            BodyType: typeof(OperatorMailboxConfiguration), RequiredBodyFields: ["displayName", "provider", "authentication", "scope", "mailboxAddress"], ExternalIo: true,
            Description: "Test explicitly supplied draft mailbox settings and write-only credentials without saving the configuration or importing mail.");
        yield return new("helpdesk_email_settings", "enable", "POST", settings + "/{mailboxId}/enable", ExternalIo: true,
            Description: "Enable one saved mailbox through its canonical readiness and credential checks.");
        yield return new("helpdesk_email_settings", "disable", "POST", settings + "/{mailboxId}/disable", Description: "Disable one saved mailbox without deleting its history or credentials.");
        yield return new("helpdesk_email_settings", "sync", "POST", settings + "/{mailboxId}/sync", ExternalIo: true,
            Description: "Queue a sync of one configured mailbox and report queued versus unavailable state.");
        yield return new("helpdesk_email_settings", "archive", "POST", settings + "/{mailboxId}/archive",
            BodyType: typeof(OperatorArchiveMailbox), RequiredBodyFields: ["version", "confirmed"],
            Description: "Archive a mailbox using its current version and explicit confirmation. This changes organization routing through the same canonical transaction used by the UI.");
        yield return new("helpdesk_email_settings", "diagnostics", "GET", settings + "/{mailboxId}/diagnostics", Description: "Read bounded mailbox receipt, cursor and worker diagnostics without secrets.");
        yield return new("helpdesk_email_settings", "retry_receipt", "POST", settings + "/{mailboxId}/receipts/{receiptId}/retry", ExternalIo: true,
            Description: "Queue retry of one eligible inbound receipt. The server checks outcome eligibility and concurrent changes.");
        yield return new("helpdesk_email_settings", "historical_preview", "POST", settings + "/{mailboxId}/historical/preview",
            BodyType: typeof(OperatorHistoricalPreview), ExternalIo: true,
            Description: "Inspect 1–50 eligible historical mailbox messages without importing them; retain the returned receipt identities for a selected import.");
        yield return new("helpdesk_email_settings", "historical_import", "POST", settings + "/{mailboxId}/historical/import",
            BodyType: typeof(OperatorHistoricalImport), RequiredBodyFields: ["receiptIds", "confirmed"], ExternalIo: true,
            Description: "Queue only the explicitly confirmed historical receipt selection. Import eligibility, duplicates and mailbox state remain server-validated.");
        yield return new("helpdesk_email_settings", "outgoing", "GET", settings + "/{mailboxId}/outgoing", Description: "Read the mailbox's outgoing sender configuration and version with credentials redacted.");
        yield return new("helpdesk_email_settings", "save_outgoing", "PUT", settings + "/{mailboxId}/outgoing",
            BodyType: typeof(OperatorOutgoingMailboxConfiguration), RequiredBodyFields: ["version", "enabled", "transport", "displayName", "smtpHost", "smtpPort", "smtpTlsMode", "smtpUsername", "smtpPassword"],
            Description: "Save complete outgoing sender configuration with optimistic concurrency. Supply smtpPassword as an empty string to retain its saved value, a new write-only value to rotate it, or clearSmtpPassword=true to remove it.");
        yield return new("helpdesk_email_settings", "test_outgoing", "POST", settings + "/{mailboxId}/outgoing/test", ExternalIo: true,
            Description: "Test authentication of the saved outgoing transport and record the tested revision.");
        yield return new("helpdesk_email_settings", "send_test", "POST", settings + "/{mailboxId}/outgoing/send-test",
            BodyType: typeof(OperatorMailboxSendTest), RequiredBodyFields: ["recipient", "confirmed"], ExternalIo: true,
            Description: "Send one explicitly confirmed sample message to one recipient using the saved sender. Acceptance by the provider is reported separately from final delivery.");

        const string rules = "/api/v1/inbound-email-rules";
        yield return new("helpdesk_email_settings", "rules", "GET", rules + "/", QueryType: typeof(OperatorMailRuleQuery), Description: "List inbound routing rules for a tenant and/or mailbox.");
        yield return new("helpdesk_email_settings", "get_rule", "GET", rules + "/{ruleId}", Description: "Read an inbound email rule including its typed conditions and actions.");
        yield return new("helpdesk_email_settings", "create_rule", "POST", rules + "/", BodyType: typeof(CreateInboundEmailRuleRequest), RequiredBodyFields: ["scopeType", "name", "enabled", "priority", "conditions", "actions", "stopProcessing"], Description: "Create a typed inbound mail rule, including authorized forwarded-sender processing; configured tenant/mailbox checks remain enforced.");
        yield return new("helpdesk_email_settings", "update_rule", "PUT", rules + "/{ruleId}", BodyType: typeof(UpdateInboundEmailRuleRequest), RequiredBodyFields: ["scopeType", "name", "enabled", "priority", "conditions", "actions", "stopProcessing"], Description: "Replace the typed settings of one inbound email rule.");
        yield return new("helpdesk_email_settings", "enable_rule", "POST", rules + "/{ruleId}/enable", Description: "Enable one inbound rule after canonical binding validation.");
        yield return new("helpdesk_email_settings", "disable_rule", "POST", rules + "/{ruleId}/disable", Description: "Disable one inbound rule while retaining its audit history.");
        yield return new("helpdesk_email_settings", "delete_rule", "DELETE", rules + "/{ruleId}", Description: "Delete one explicitly selected inbound rule while preserving its message processing audit history. Already started message work is not cancelled or replayed.");
        yield return new("helpdesk_email_settings", "reorder_rules", "POST", rules + "/reorder", BodyType: typeof(ReorderInboundEmailRulesRequest), RequiredBodyFields: ["rules"], Description: "Reorder an explicitly selected distinct set of rules in one tenant/mailbox scope.");
        yield return new("helpdesk_email_settings", "rule_audit", "GET", rules + "/audit", QueryType: typeof(OperatorMailRuleAuditQuery), Description: "Read up to 200 inbound rule processing outcomes filtered by ticket, message, tenant or mailbox.");

        const string templates = "/api/v1/email-templates";
        yield return new("helpdesk_email_settings", "templates", "GET", templates + "/", Description: "Read saved email templates.");
        yield return new("helpdesk_email_settings", "create_template", "POST", templates + "/", BodyType: typeof(OperatorEmailTemplateBody), RequiredBodyFields: ["name", "subject", "htmlContent"], Description: "Create a custom email template using the canonical HTML sanitizer.");
        yield return new("helpdesk_email_settings", "update_template", "PUT", templates + "/{templateId}", BodyType: typeof(OperatorEmailTemplateBody), RequiredBodyFields: ["name", "subject", "htmlContent"], Description: "Update template content while the API preserves identity and increments its version.");
        yield return new("helpdesk_email_settings", "delete_template", "DELETE", templates + "/{templateId}", Description: "Delete a custom template; protected system templates remain undeletable.");
        yield return new("helpdesk_email_settings", "preview_template", "POST", templates + "/preview", BodyType: typeof(OperatorEmailTemplatePreview), Description: "Render a saved or draft email template using a selected tenant's branding without sending a message.");
        yield return new("helpdesk_email_settings", "upload_template_image", "POST", templates + "/{templateName}/images", BodyType: typeof(OperatorAttachmentUpload),
            Description: "Upload one inline template image, at most 1 MiB, through the authenticated multipart endpoint and return its stored image URL.");
        const string layouts = "/api/v1/email-layouts";
        yield return new("helpdesk_email_settings", "layouts", "GET", layouts + "/", QueryType: typeof(OperatorEmailLayoutQuery), Description: "Read saved email layouts, optionally scoped to an integer tenant ID.");
        yield return new("helpdesk_email_settings", "layout_choices", "GET", layouts + "/choices", Description: "Read selectable system and current-tenant email layouts.");
        yield return new("helpdesk_email_settings", "create_layout", "POST", layouts + "/", BodyType: typeof(OperatorEmailLayoutBody), RequiredBodyFields: ["name", "htmlContent"], Description: "Create a custom email layout with canonical HTML sanitization.");
        yield return new("helpdesk_email_settings", "update_layout", "PUT", layouts + "/{layoutId}", BodyType: typeof(OperatorEmailLayoutBody), RequiredBodyFields: ["name", "htmlContent"], Description: "Update layout content while preserving identity and system protection.");
        yield return new("helpdesk_email_settings", "delete_layout", "DELETE", layouts + "/{layoutId}", Description: "Delete one custom layout; protected system layouts remain undeletable.");
    }

    private static IEnumerable<OperatorOperation> Support()
    {
        const string support = "/api/v1/support";
        yield return new("helpdesk_support", "groups", "GET", support + "/groups/", QueryType: typeof(OperatorSupportGroupQuery), Description: "List support groups, optionally for one owning organization.");
        yield return new("helpdesk_support", "get_group", "GET", support + "/groups/{groupId}", Description: "Read one support group.");
        yield return new("helpdesk_support", "create_group", "POST", support + "/groups/", BodyType: typeof(CreateSupportGroupDto), Description: "Create a support group in its owning organization.");
        yield return new("helpdesk_support", "update_group", "PUT", support + "/groups/{groupId}", BodyType: typeof(UpdateSupportGroupDto), Description: "Update support group metadata and enabled status.");
        yield return new("helpdesk_support", "delete_group", "DELETE", support + "/groups/{groupId}", Description: "Delete one support group through the canonical repository rules.");
        yield return new("helpdesk_support", "members", "GET", support + "/groups/{groupId}/members/", Description: "List the selected support group's members and enabled state.");
        yield return new("helpdesk_support", "add_member", "POST", support + "/groups/{groupId}/members/", BodyType: typeof(CreateSupportGroupMemberDto), Description: "Add an existing user to the selected support group with explicit membership role and source.");
        yield return new("helpdesk_support", "update_member", "PUT", support + "/groups/{groupId}/members/{memberId}", BodyType: typeof(UpdateSupportGroupMemberDto), Description: "Update or disable an existing membership; the API verifies that it belongs to the selected group.");
        yield return new("helpdesk_support", "remove_member", "DELETE", support + "/groups/{groupId}/members/{memberId}", Description: "Remove one membership from its verified group.");
        yield return new("helpdesk_support", "coverage", "GET", support + "/coverage/", QueryType: typeof(OperatorSupportOrganizationQuery), Description: "List customer-to-provider support coverage routes.");
        yield return new("helpdesk_support", "create_coverage", "POST", support + "/coverage/", BodyType: typeof(CreateOrganizationSupportCoverageDto), Description: "Create explicit customer, provider and group coverage with a primary or backup role.");
        yield return new("helpdesk_support", "update_coverage", "PUT", support + "/coverage/{coverageId}", BodyType: typeof(UpdateOrganizationSupportCoverageDto), Description: "Update or disable a saved support coverage route.");
        yield return new("helpdesk_support", "delete_coverage", "DELETE", support + "/coverage/{coverageId}", Description: "Delete one explicitly selected support coverage route.");
        yield return new("helpdesk_support", "subscriptions", "GET", support + "/notification-subscriptions/", QueryType: typeof(OperatorSupportOrganizationQuery), Description: "List support notification subscriptions for one or all customer organizations.");
        yield return new("helpdesk_support", "create_subscription", "POST", support + "/notification-subscriptions/", BodyType: typeof(CreateSupportNotificationSubscriptionDto), Description: "Create an explicit event and recipient subscription; only configured supported delivery channels are accepted.");
        yield return new("helpdesk_support", "update_subscription", "PUT", support + "/notification-subscriptions/{subscriptionId}", BodyType: typeof(UpdateSupportNotificationSubscriptionDto), Description: "Update or disable one support notification subscription.");
        yield return new("helpdesk_support", "delete_subscription", "DELETE", support + "/notification-subscriptions/{subscriptionId}", Description: "Delete one support notification subscription.");
        yield return new("helpdesk_support", "my_preferences", "GET", support + "/notification-preferences/", Authority: "NotificationAccess for the authenticated caller", Description: "Read the authenticated caller's own support notification preferences.");
        yield return new("helpdesk_support", "set_my_preference", "PUT", support + "/notification-preferences/", BodyType: typeof(OperatorSupportPreference), Authority: "NotificationAccess for the authenticated caller", Description: "Update one of the authenticated caller's preferences; the server selects the caller identity.");
        yield return new("helpdesk_support", "user_preferences", "GET", support + "/users/{userId}/notification-preferences/", Description: "Read one selected user's notification preferences under administrator authority.");
        yield return new("helpdesk_support", "set_user_preference", "PUT", support + "/users/{userId}/notification-preferences/", BodyType: typeof(OperatorSupportPreference), Description: "Update a selected user's preference under administrator authority; the route determines the target user.");
        yield return new("helpdesk_support", "recipient_preview", "GET", support + "/organizations/{organizationId}/recipient-preview", QueryType: typeof(OperatorSupportRecipientQuery), Description: "Resolve the actual recipients for one organization event without sending mail.");
        yield return new("helpdesk_support", "bootstrap_defaults", "POST", support + "/bootstrap/defaults", Description: "Ensure the instance's default support configuration using the canonical idempotent bootstrapper.");
    }

    private static IEnumerable<OperatorOperation> Sla()
    {
        yield return new("helpdesk_sla", "policies", "GET", "/api/v1/slas/", Description: "Read SLA policies and escalation rules.");
        yield return new("helpdesk_sla", "create_policy", "POST", "/api/v1/slas/", BodyType: typeof(SlaPolicyDto), RequiredBodyFields: ["name"], Description: "Create an SLA policy with its scope, matching rules, deadlines and escalations.");
        yield return new("helpdesk_sla", "update_policy", "PUT", "/api/v1/slas/{policyId}", BodyType: typeof(SlaPolicyDto), RequiredBodyFields: ["name"], Description: "Update an existing SLA policy using canonical validation and audit events.");
        yield return new("helpdesk_sla", "delete_policy", "DELETE", "/api/v1/slas/{policyId}", Description: "Delete a selected SLA policy.");
        yield return new("helpdesk_sla", "calendars", "GET", "/api/v1/sla-calendars/", Description: "Read business calendars, weekly hours and date exceptions.");
        yield return new("helpdesk_sla", "create_calendar", "POST", "/api/v1/sla-calendars/", BodyType: typeof(WorkingCalendarDto), RequiredBodyFields: ["name", "timeZoneId"], Description: "Create a validated business-hours calendar.");
        yield return new("helpdesk_sla", "update_calendar", "PUT", "/api/v1/sla-calendars/{calendarId}", BodyType: typeof(WorkingCalendarDto), RequiredBodyFields: ["name", "timeZoneId"], Description: "Update calendar rules and exceptions.");
        yield return new("helpdesk_sla", "delete_calendar", "DELETE", "/api/v1/sla-calendars/{calendarId}", Description: "Delete a selected calendar subject to canonical usage constraints.");
        yield return new("helpdesk_sla", "tenant_settings", "GET", "/api/v1/tenant-sla-settings/{tenantId}", Description: "Read one tenant's business-hours and calendar selection.");
        yield return new("helpdesk_sla", "save_tenant_settings", "PUT", "/api/v1/tenant-sla-settings/{tenantId}", BodyType: typeof(TenantSlaSettingsDto), RequiredBodyFields: ["useBusinessHours"], Description: "Save the selected tenant's business-hours and calendar settings.");
        yield return new("helpdesk_sla", "report_subscriptions", "GET", "/api/v1/sla-report-subscriptions/", Description: "Read scheduled SLA reports and recipients.");
        yield return new("helpdesk_sla", "create_report_subscription", "POST", "/api/v1/sla-report-subscriptions/", BodyType: typeof(SlaReportSubscriptionDto), RequiredBodyFields: ["tenantId", "frequency", "sendTimeLocal", "timeZoneId"], ExternalIo: true, Description: "Create an SLA report schedule with explicit recipients and tenant scope.");
        yield return new("helpdesk_sla", "update_report_subscription", "PUT", "/api/v1/sla-report-subscriptions/{subscriptionId}", BodyType: typeof(SlaReportSubscriptionDto), RequiredBodyFields: ["tenantId", "frequency", "sendTimeLocal", "timeZoneId"], ExternalIo: true, Description: "Update or disable an SLA report schedule.");
        yield return new("helpdesk_sla", "delete_report_subscription", "DELETE", "/api/v1/sla-report-subscriptions/{subscriptionId}", Description: "Remove one SLA report schedule.");
        yield return new("helpdesk_sla", "compliance", "GET", "/api/v1/reports/sla/compliance", QueryType: typeof(OperatorSlaComplianceQuery), Authority: "Ticket manager authority in the selected organization", Description: "Read SLA compliance for a declared period and ticket type.");
        yield return new("helpdesk_sla", "breached", "GET", "/api/v1/reports/sla/breached", QueryType: typeof(OperatorSlaTicketListQuery), Authority: "Ticket manager authority in the selected organization", Description: "Read a page of breached open tickets.");
        yield return new("helpdesk_sla", "near_breach", "GET", "/api/v1/reports/sla/near-breach", QueryType: typeof(OperatorSlaNearBreachQuery), Authority: "Ticket manager authority in the selected organization", Description: "Read a page of tickets approaching the selected SLA threshold.");
        yield return new("helpdesk_sla", "completed", "GET", "/api/v1/reports/sla/completed", QueryType: typeof(OperatorSlaCompletedQuery), Authority: "Ticket manager authority in the selected organization", Description: "Read completed ticket SLA outcomes for a declared period.");
        yield return new("helpdesk_sla", "pause_ticket", "POST", "/api/v1/tickets/{ticketId}/sla/pause", BodyType: typeof(OperatorPauseSla), Authority: "Parent ticket manager authority", Description: "Pause one eligible ticket SLA with a recorded reason; parent ticket authorization is enforced.");
        yield return new("helpdesk_sla", "resume_ticket", "POST", "/api/v1/tickets/{ticketId}/sla/resume", BodyType: typeof(ResumeSlaRequest), Authority: "Parent ticket manager authority", Description: "Resume one eligible ticket SLA through its canonical lifecycle.");
    }

    private static IEnumerable<OperatorOperation> Datasets()
    {
        const string datasets = "/api/v1/resources/datasets";
        const string authority = "DataManagementAccess and dataset organization scope";
        yield return new("helpdesk_datasets", "list", "GET", datasets + "/", QueryType: typeof(OperatorDatasetOrganizationQuery), Authority: authority, Description: "List datasets in the explicitly selected authorized organization.");
        yield return new("helpdesk_datasets", "get", "GET", datasets + "/{datasetId}", Authority: authority, Description: "Read an authorized dataset definition and schema.");
        yield return new("helpdesk_datasets", "create", "POST", datasets + "/", BodyType: typeof(CreateDatasetDefinitionDto), Authority: authority, Description: "Create a dataset with typed columns and explicit organization scope. Non-administrators remain restricted to custom sources.");
        yield return new("helpdesk_datasets", "update", "PUT", datasets + "/{datasetId}", BodyType: typeof(UpdateDatasetDefinitionDto), Authority: authority, Description: "Update a dataset definition within its actual stored organization scope.");
        yield return new("helpdesk_datasets", "delete", "DELETE", datasets + "/{datasetId}", Authority: authority, Description: "Delete one authorized dataset through the canonical service.");
        yield return new("helpdesk_datasets", "rows", "GET", datasets + "/{datasetId}/rows", QueryType: typeof(OperatorDatasetRowsQuery), Authority: authority, Description: "Read bounded, searchable dataset rows.");
        yield return new("helpdesk_datasets", "upsert_rows", "POST", datasets + "/{datasetId}/rows", BodyType: typeof(UpsertDatasetRowsDto), RequiredBodyFields: ["rows", "deleteMissing"], Authority: authority,
            Description: "Update up to 100 dataset rows using the authenticated operator's actual organization authority. Set deleteMissing explicitly; true removes existing rows omitted from this selected update. The separate ingestion API-key contract remains unchanged.");
        yield return new("helpdesk_datasets", "credentials", "GET", datasets + "/{datasetId}/credentials", Authority: authority, Description: "Read dataset ingestion credential metadata without secret keys.");
        yield return new("helpdesk_datasets", "create_credential", "POST", datasets + "/{datasetId}/credentials", BodyType: typeof(CreateDatasetIngestCredentialDto), RequiredBodyFields: ["name"], Authority: authority,
            Description: "Create one dataset ingestion credential. Its plaintext key is returned once to this authenticated creation request; subsequent credential reads return metadata only.", ReturnsOneTimeSecret: true);
        yield return new("helpdesk_datasets", "revoke_credential", "DELETE", datasets + "/{datasetId}/credentials/{credentialId}", Authority: authority, Description: "Revoke one ingestion credential under the dataset's API-management authority.");
        yield return new("helpdesk_datasets", "graph_settings", "GET", datasets + "/graph-settings", QueryType: typeof(OperatorDatasetOrganizationQuery), Authority: authority, Description: "Read Graph dataset configuration and synchronization diagnostics without stored secrets.");
        yield return new("helpdesk_datasets", "save_graph_settings", "PUT", datasets + "/graph-settings", BodyType: typeof(OperatorGraphDatasetConfiguration), RequiredBodyFields: ["organizationId"], ExternalIo: true, Authority: authority, Description: "Save Graph dataset configuration, a write-only client secret and schedule controls. Omit or leave clientSecret empty to preserve its existing protected value.");
        yield return new("helpdesk_datasets", "sync_graph", "POST", datasets + "/graph-settings/{organizationId}/sync", ExternalIo: true, Authority: authority, Description: "Synchronize enabled built-in datasets through the saved Graph integration.");
        yield return new("helpdesk_datasets", "options", "GET", "/api/v1/self-service/datasets/{datasetId}/options", QueryType: typeof(OperatorDatasetOptionsQuery), Authority: "SelfService.User in the dataset organization", Description: "Read authorized searchable dataset options for self-service form inputs.");
    }
}

internal sealed record OperatorPendingMailQuery(
    [property: MaxLength(200)] string? Search = null,
    EmailDeliveryStatus? Status = null,
    [property: MaxLength(200)] string? OrganizationId = null,
    [property: MaxLength(200)] string? Sender = null,
    [property: MaxLength(200)] string? Recipient = null,
    [property: MaxLength(200)] string? Reason = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    [property: RegularExpression("^(created|status|recipient|ticket)$")] string Sort = "created",
    bool Descending = true,
    [property: Range(0, 1000000)] int Page = 0,
    [property: AllowedValues(25, 50, 100)] int PageSize = 25);

internal sealed record OperatorPendingOrganizationQuery(
    [property: MaxLength(100)] string? Q = null,
    [property: MaxLength(200)] string? Id = null,
    [property: Range(1, 50)] int Take = 20);

internal sealed record OperatorPendingMailAction(
    [property: Required, MinLength(1), MaxLength(100)] Guid[] Ids,
    [property: Required, RegularExpression("^(retry|cancel|remove)$")] string Action,
    [property: Required, MaxLength(200)] string Reason,
    bool Confirmed);

internal sealed record OperatorMailboxUndeliveredRetry(
    [property: Range(0, long.MaxValue)] long ExpectedFence,
    [property: Range(1, long.MaxValue)] long ExpectedOutgoingVersion,
    [property: Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._:/-]{7,127}$")] string EvidenceReference,
    bool ConfirmedNoDelivery);

internal sealed record OperatorSendMail(
    [property: Required, MinLength(1), MaxLength(100)] string[] Recipients,
    [property: Required, MaxLength(998)] string Subject,
    [property: Required, MaxLength(262144)] string HtmlMessage);

internal sealed record OperatorMailboxWorkerChange(bool Running, bool Confirmed);
internal sealed record OperatorArchiveMailbox([property: Range(1, int.MaxValue)] long Version, bool Confirmed);
internal sealed record OperatorMailboxSendTest([property: Required, EmailAddress, MaxLength(254)] string Recipient, bool Confirmed);
internal sealed record OperatorHistoricalPreview(long? FromUnixMilliseconds = null, long? ToUnixMilliseconds = null,
    [property: Range(1, 50)] int Count = 25, [property: Range(0, 10000)] int Skip = 0);
internal sealed record OperatorHistoricalImport([property: Required, MinLength(1), MaxLength(25)] Guid[] ReceiptIds, bool Confirmed);

// Secret inputs are explicit and write-only. Canonical API update semantics preserve
// saved credentials when their corresponding input value is omitted or empty.
internal sealed record OperatorMailboxConfiguration
{
    [Required, MaxLength(200)] public string DisplayName { get; init; } = string.Empty;
    public InboundMailboxProvider Provider { get; init; } = InboundMailboxProvider.Graph;
    public MailboxAuthentication Authentication { get; init; } = MailboxAuthentication.MicrosoftApplication;
    public MailboxScope Scope { get; init; } = MailboxScope.Global;
    [MaxLength(200)] public string? OrganizationId { get; init; }
    [Range(1, int.MaxValue)] public long Version { get; init; } = 1;
    [MaxLength(254)] public string Username { get; init; } = string.Empty;
    [DataType(DataType.Password), MaxLength(4096)] public string Password { get; init; } = string.Empty;
    public bool ClearPassword { get; init; }
    [DataType(DataType.Password), MaxLength(4096)] public string ClientSecret { get; init; } = string.Empty;
    public bool ClearClientSecret { get; init; }
    public bool ConfirmExistingImport { get; init; }
    public InitialMailImport InitialImport { get; init; } = InitialMailImport.NewOnly;
    public MailboxTlsMode TlsMode { get; init; } = MailboxTlsMode.TlsOnConnect;
    [MaxLength(512)] public string? ProcessedFolder { get; init; }
    public bool MarkReadAfterSuccess { get; init; } = true;
    [Range(10, 300)] public int PollIntervalSeconds { get; init; } = 30;
    [Range(1, 100)] public int BatchSize { get; init; } = 25;
    [MaxLength(253)] public string MailHost { get; init; } = string.Empty;
    [Range(1, 65535)] public int Port { get; init; } = 993;
    public bool UseSsl { get; init; } = true;
    [Required, EmailAddress, MaxLength(254)] public string MailboxAddress { get; init; } = string.Empty;
    [MaxLength(200)] public string TenantId { get; init; } = string.Empty;
    [MaxLength(200)] public string ClientId { get; init; } = string.Empty;
    [MaxLength(512)] public string MailboxFolder { get; init; } = "INBOX";
    public bool Enabled { get; init; }
    public bool BackgroundSyncEnabled { get; init; }
}

internal sealed record OperatorOutgoingMailboxConfiguration(
    [property: Range(0, int.MaxValue)] long Version,
    bool Enabled,
    MailboxOutgoingTransport Transport,
    [property: MaxLength(200)] string DisplayName,
    [property: MaxLength(253)] string SmtpHost,
    [property: Range(1, 65535)] int SmtpPort,
    MailboxTlsMode SmtpTlsMode,
    [property: MaxLength(320)] string SmtpUsername,
    [property: DataType(DataType.Password), MaxLength(4096)] string SmtpPassword,
    bool ClearSmtpPassword = false);

internal sealed record OperatorMailRuleQuery([property: MaxLength(200)] string? TenantId = null, Guid? MailboxId = null);
internal sealed record OperatorMailRuleAuditQuery([property: MaxLength(512)] string? MessageId = null,
    [property: MaxLength(200)] string? TicketId = null, [property: MaxLength(200)] string? TenantId = null, Guid? MailboxId = null);
internal sealed record OperatorEmailLayoutQuery([property: Range(1, int.MaxValue)] int? TenantId = null);
internal sealed record OperatorEmailTemplateBody([property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(998)] string Subject, [property: Required, MaxLength(262144)] string HtmlContent, int? LayoutId = null);
internal sealed record OperatorEmailLayoutBody([property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(262144)] string HtmlContent, int? TenantId = null);
internal sealed record OperatorEmailTemplatePreview(int? TemplateId = null, [property: MaxLength(200)] string? TemplateName = null,
    [property: MaxLength(200)] string? TenantId = null, [property: MaxLength(998)] string? DraftSubject = null,
    [property: MaxLength(262144)] string? DraftHtmlContent = null, [property: MaxLength(262144)] string? DraftLayoutHtml = null,
    int? DraftLayoutId = null, OperatorEmailTemplateSampleContext? SampleContext = null);

internal sealed record OperatorEmailTemplateSampleContext
{
    public string? UserName { get; init; }
    public string? TicketRef { get; init; }
    public string? TicketLink { get; init; }
    public string? UpdateMessageHtml { get; init; }
    public string? UpdateMessageText { get; init; }
    public string? ServiceName { get; init; }
    public string? RequestTitle { get; init; }
    public string? RequestDescription { get; init; }
    public string? RequestApprovalTaskName { get; init; }
    public string? RequestApprovalApprover { get; init; }
    public string? RequestApprovalRejectionReason { get; init; }
    public string? FailureReason { get; init; }
    public string? IncidentRef { get; init; }
    public string? IncidentLink { get; init; }
    public string? CompletedAt { get; init; }
    public string? ResolvedAt { get; init; }
    public string? ChangeTitle { get; init; }
    public string? ChangeDescription { get; init; }
    public string? ChangeType { get; init; }
    public string? ChangePriority { get; init; }
    public string? ChangeOrganization { get; init; }
    public string? ChangeRequestedFor { get; init; }
    public string? ChangeImplementor { get; init; }
    public string? ChangeApprovers { get; init; }
    public string? ChangeImplementationStart { get; init; }
    public string? ChangeImplementationEnd { get; init; }
    public string? ChangeApprovalLink { get; init; }
    public string? ChangeViewLink { get; init; }
    public string? ChangeCompletionState { get; init; }
    public string? ChangeScopeOfChange { get; init; }
    public string? ChangeAffectedSystems { get; init; }
    public string? ChangeImplementationSteps { get; init; }
    public string? ChangeValidationSteps { get; init; }
    public string? ChangeRollbackPlan { get; init; }
    public string? ChangeRollbackReference { get; init; }
    public string? OrganizationName { get; init; }
    public string? InviteLink { get; init; }
    public string? InviteExpiresAt { get; init; }
    public string? HelpdeskUrl { get; init; }
}

internal sealed record OperatorSupportGroupQuery([property: MaxLength(200)] string? OwningOrganizationId = null);
internal sealed record OperatorSupportOrganizationQuery([property: MaxLength(200)] string? CustomerOrganizationId = null);
internal sealed record OperatorSupportPreference(SupportNotificationEventType EventType, SupportNotificationChannel Channel, bool IsEnabled);
internal sealed record OperatorSupportRecipientQuery([property: Required] SupportNotificationEventType EventType);
internal sealed record OperatorPauseSla([property: Required, MaxLength(200)] string Reason);
internal sealed record OperatorSlaComplianceQuery(
    [property: Required] DateTimeOffset FromUtc, [property: Required] DateTimeOffset ToUtc,
    [property: MaxLength(200)] string? TenantId = null, TicketType? TicketType = null);
internal sealed record OperatorSlaTicketListQuery(
    [property: Required, Range(1, 100000)] int Page, [property: Required, Range(1, 100)] int PageSize,
    [property: MaxLength(200)] string? TenantId = null, TicketType? TicketType = null);
internal sealed record OperatorSlaNearBreachQuery(
    [property: Required, Range(1, 100000)] int Page, [property: Required, Range(1, 100)] int PageSize,
    [property: Required, Range(1, 100)] int ThresholdPercent, [property: Required] SlaMetricType Metric,
    [property: MaxLength(200)] string? TenantId = null, TicketType? TicketType = null);
internal sealed record OperatorSlaCompletedQuery(
    [property: Required] DateTimeOffset FromUtc, [property: Required] DateTimeOffset ToUtc,
    [property: Required, Range(1, 100000)] int Page, [property: Required, Range(1, 100)] int PageSize,
    [property: MaxLength(200)] string? TenantId = null, TicketType? TicketType = null, bool? WithinResolutionSla = null);
internal sealed record OperatorDatasetOrganizationQuery([property: Required, MaxLength(200)] string OrganizationId);
internal sealed record OperatorDatasetRowsQuery([property: MaxLength(500)] string? Query = null,
    [property: Required, Range(1, 100000)] int Page = 1, [property: Required, Range(1, 100)] int PageSize = 25);
internal sealed record OperatorDatasetOptionsQuery([property: MaxLength(500)] string? Query = null,
    [property: Required, Range(1, 100000)] int Page = 1, [property: Required, Range(1, 50)] int PageSize = 25);
internal sealed record OperatorGraphDatasetConfiguration
{
    [Required, MaxLength(200)] public string OrganizationId { get; init; } = string.Empty;
    [MaxLength(200)] public string? TenantId { get; init; }
    [MaxLength(200)] public string? ClientId { get; init; }
    [DataType(DataType.Password), MaxLength(4096)] public string? ClientSecret { get; init; }
    public bool EnableUsers { get; init; }
    public bool EnableDevices { get; init; }
    public bool EnableGroups { get; init; }
    public bool EnableSharePointSites { get; init; }
    public bool BackgroundSyncEnabled { get; init; }
    [MaxLength(100)] public string BackgroundSyncCronExpression { get; init; } = "0 */6 * * *";
}
