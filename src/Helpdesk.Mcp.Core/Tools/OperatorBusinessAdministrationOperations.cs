using System.ComponentModel.DataAnnotations;
using Helpdesk.Shared.DTOs.Ops;

namespace Helpdesk.Mcp.Tools;

/// <summary>Named adapters for the existing business administration screens and canonical APIs.</summary>
internal static class OperatorBusinessAdministrationOperations
{
    public static IEnumerable<OperatorOperation> All
    {
        get
        {
            const string rules = "/api/v1/automation/rules";
            yield return new("helpdesk_automation", "rules", "GET", rules + "/", Description: "List saved automation rules with their trigger, conditions and actions.");
            yield return new("helpdesk_automation", "get_rule", "GET", rules + "/{ruleId}", Description: "Read one saved automation rule before editing or deleting it.");
            yield return new("helpdesk_automation", "create_rule", "POST", rules + "/", typeof(OperatorAutomationRule), Description: "Create an automation rule through the same canonical API as the administration screen. Conditions and actions retain the API's text representation; this operation saves the rule and does not claim a provider execution.");
            yield return new("helpdesk_automation", "update_rule", "PUT", rules + "/{ruleId}", typeof(OperatorAutomationRule), RequiredBodyFields: ["conditions", "actions"], Description: "Replace the selected rule's name, trigger, conditions and actions. Read the current rule first; explicitly supply null conditions/actions to clear them. The route determines the stored identifier.");
            yield return new("helpdesk_automation", "delete_rule", "DELETE", rules + "/{ruleId}", Description: "Delete exactly one selected automation rule.");

            const string assets = "/api/v1/assets";
            yield return new("helpdesk_tenant_admin", "assets", "GET", assets + "/", Description: "List asset records visible to the authorized instance administrator.");
            yield return new("helpdesk_tenant_admin", "get_asset", "GET", assets + "/{assetId}", Description: "Read the selected asset and its saved organization before editing.");
            yield return new("helpdesk_tenant_admin", "create_asset", "POST", assets + "/", typeof(OperatorAsset), Description: "Create an asset with an explicit organization identifier and bounded inventory fields. An explicit empty organizationId preserves the canonical unassigned asset behavior. The API assigns its identifier.");
            yield return new("helpdesk_tenant_admin", "update_asset", "PUT", assets + "/{assetId}", typeof(OperatorAsset), RequiredBodyFields: ["type", "manufacturer", "model", "serialNumber", "notes"], Description: "Replace the selected asset's inventory fields and organization. Read current values first; supply all optional fields explicitly, using null only to clear them. The route determines the stored identifier.");
            yield return new("helpdesk_tenant_admin", "delete_asset", "DELETE", assets + "/{assetId}", Description: "Delete exactly one selected asset through the canonical administrator endpoint.");

            yield return new("helpdesk_tenant_admin", "instance_branding", "GET", "/api/v1/admin/branding/", Description: "Read effective instance branding, persisted values, defaults and each field's source/editability. Deployment-owned values remain authoritative.");
            yield return new("helpdesk_tenant_admin", "update_instance_branding", "PUT", "/api/v1/admin/branding/", typeof(OperatorInstanceBranding), RequiredBodyFields:
                ["applicationName", "organizationName", "applicationUrl", "organizationUrl", "supportUrl", "supportEmail", "logoUrl", "compactLogoUrl", "faviconUrl", "emailFromDisplayName", "tagline"],
                Description: "Save all instance branding fields after reading their current values and editability. Explicit null or blank resets an editable field to its configured default. The canonical provider preserves deployment-owned values and validates web/image URLs.");
            yield return new("helpdesk_tenant_admin", "tenant_branding", "GET", "/api/v1/tenants/{brandingTenantId}/branding", Description: "Read saved or default email branding for the canonical positive integer branding tenant identifier; organization and SLA identifiers remain separate string selectors.");
            yield return new("helpdesk_tenant_admin", "upload_branding_logo", "POST", "/api/v1/tenants/{brandingTenantId}/branding/logo", typeof(OperatorAttachmentUpload), Description: "Upload one image logo of at most 1 MiB through the canonical multipart file endpoint, then read tenant branding to verify the stored logo URL. Use the positive integer branding tenant identifier.");

            yield return new("helpdesk_tenant_admin", "background_worker_settings", "GET", "/api/v1/ops/hangfire/settings", Description: "Read saved background-worker enablement, actual queue/storage runtime and recurring/Graph synchronization job counts.");
            yield return new("helpdesk_tenant_admin", "update_background_worker_settings", "PUT", "/api/v1/ops/hangfire/settings", typeof(UpdateHangfireRuntimeSettingsDto), RequiredBodyFields: ["enabled"], Description: "Explicitly enable or disable background processing through the runtime administration service, which reconciles Graph dataset schedules. Inspect pending work before disabling, and verify the resulting settings and job counts.");
        }
    }
}

internal sealed record OperatorAutomationRule(
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(1000)] string Trigger,
    [property: MaxLength(65536)] string? Conditions = null,
    [property: MaxLength(65536)] string? Actions = null);

internal sealed record OperatorAsset(
    [property: Required, MaxLength(200)] string Name,
    [property: Required(AllowEmptyStrings = true), MaxLength(128)] string OrganizationId,
    [property: MaxLength(200)] string? Type = null,
    [property: MaxLength(200)] string? Manufacturer = null,
    [property: MaxLength(200)] string? Model = null,
    [property: MaxLength(200)] string? SerialNumber = null,
    [property: MaxLength(32768)] string? Notes = null);

internal sealed record OperatorInstanceBranding(
    [property: MaxLength(200)] string? ApplicationName = null,
    [property: MaxLength(200)] string? OrganizationName = null,
    [property: MaxLength(2048)] string? ApplicationUrl = null,
    [property: MaxLength(2048)] string? OrganizationUrl = null,
    [property: MaxLength(2048)] string? SupportUrl = null,
    [property: MaxLength(320)] string? SupportEmail = null,
    [property: MaxLength(2048)] string? LogoUrl = null,
    [property: MaxLength(2048)] string? CompactLogoUrl = null,
    [property: MaxLength(2048)] string? FaviconUrl = null,
    [property: MaxLength(200)] string? EmailFromDisplayName = null,
    [property: MaxLength(2000)] string? Tagline = null);
