using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using ModelContextProtocol.Server;

namespace Helpdesk.Mcp.Tools;

public sealed partial class HelpdeskTools
{
    [McpServerTool(UseStructuredContent = true), Description("Create and edit self-service forms, automation rules, bindings and provider targets; synchronize inputs, inspect workflow evidence and decide the caller's designated task approvals.")]
    public Task<HelpdeskToolResponse> helpdesk_automation(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_automation", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read and upload bounded ticket attachments using the parent ticket's permissions.")]
    public Task<HelpdeskToolResponse> helpdesk_attachments(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_attachments", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Manage ticket mail, reviewed retry/cancel actions and independently read delivery evidence.")]
    public Task<HelpdeskToolResponse> helpdesk_mail(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_mail", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Manage saved email settings, sender and worker lifecycle, templates and rule controls.")]
    public Task<HelpdeskToolResponse> helpdesk_email_settings(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_email_settings", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Manage support groups, membership, organization coverage and notification routing.")]
    public Task<HelpdeskToolResponse> helpdesk_support(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_support", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Manage service-level policies, working calendars, tenant settings and reporting.")]
    public Task<HelpdeskToolResponse> helpdesk_sla(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_sla", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Manage self-service datasets, Graph source settings and ingest credentials; inspect bounded rows/options and write explicitly selected row batches or reviewed complete replacements.")]
    public Task<HelpdeskToolResponse> helpdesk_datasets(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_datasets", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Pair and manage system connections, organization mappings, Netclaw settings/session ownership, incident receiver source bindings and connectivity tests.")]
    public Task<HelpdeskToolResponse> helpdesk_connections(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_connections", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Manage organizations, membership, scoped roles, assets, branding and logos, background worker settings, local accounts and one-time activation, owned credential metadata/revocation, and administrative dashboards.")]
    public Task<HelpdeskToolResponse> helpdesk_tenant_admin(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => OperatorOperation("helpdesk_tenant_admin", operation, request, confirm, cancellationToken);

    private async Task<HelpdeskToolResponse> OperatorOperation(string tool, string operation, JsonElement? request, bool confirm, CancellationToken ct)
    {
        var adapter = OperatorOperationCatalog.Find(tool, operation);
        if (adapter is null) return Invalid(tool, operation, McpOperationCatalog.OperationsFor(tool));
        if (OperatorRequestSchemas.Validate(adapter, request) is { } error) return Validation(error);

        var values = OperatorRequestSchemas.Normalize(adapter, request);
        var identifiers = McpRequestSchemas.Identifiers(adapter.Path).Distinct(StringComparer.Ordinal).ToArray();
        var path = adapter.Path;
        foreach (var name in identifiers) path = path.Replace("{" + name + "}", Uri.EscapeDataString(values[name]!.ToString()), StringComparison.Ordinal);
        // Only compile-time query names are emitted. Body and route identifiers never become query parameters.
        if (adapter.QueryType is not null)
        {
            var schema = OperatorRequestSchemas.Create(adapter);
            var queryNames = schema["properties"]!.AsObject().Select(pair => pair.Key).Except(identifiers).Where(name => name != "body");
            var query = queryNames.Where(name => values[name] is not null).SelectMany(name => values[name] is JsonArray array
                ? array.Where(item => item is not null).Select(item => Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(item!.ToString()))
                : [Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(values[name]!.ToString())]);
            var text = string.Join("&", query);
            if (text.Length > 0) path += "?" + text;
        }

        if (tool == "helpdesk_attachments" && operation is "upload" or "download" ||
            tool == "helpdesk_email_settings" && operation == "upload_template_image" ||
            tool == "helpdesk_tenant_admin" && operation == "upload_branding_logo")
            return await AttachmentOperation(operation, path, values, confirm, ct).ConfigureAwait(false);

        if (tool == "helpdesk_connections" && operation == "save_mapping" &&
            (values["body"]?["id"]?.ToString() != values["mappingId"]?.ToString() || values["body"]?["pairId"]?.ToString() != values["pairId"]?.ToString()))
            return Validation("request.body.id and request.body.pairId must match the selected mappingId and pairId.");
        if (tool == "helpdesk_connections" && operation == "confirm_netclaw_session_owner" &&
            values["body"] is JsonObject selection && selection["conversationIds"] is JsonArray conversations &&
            (conversations.Count != selection["expectedEligibleConversations"]!.GetValue<long>() ||
             conversations.Select(id => id!.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != conversations.Count))
            return Validation("request.body.conversationIds must contain each reviewed conversation exactly once and match expectedEligibleConversations.");
        var body = values["body"]?.DeepClone();
        if (tool == "helpdesk_automation" && operation is "create_form" or "update_form")
        {
            var form = body!.AsObject();
            form["jsonSchema"] = form["schema"]!.ToJsonString();
            form.Remove("schema");
        }

        HelpdeskToolResponse response;
        try
        {
            response = adapter.Method == "GET" && !adapter.MutatingRead
                ? await Execute(() => client.GetAsync(path, true, ct), $"{operation} completed.").ConfigureAwait(false)
                : await Mutation(operation, identifiers.Select(name => values[name]!.ToString()).ToArray(), confirm,
                    new HttpMethod(adapter.Method), path, body, ct).ConfigureAwait(false);
        }
        catch (AgentClientValidationException exception) { response = Validation(exception.Message); }
        return OperatorSecretPolicy.SanitizeResponse(adapter, values, response);
    }

    private async Task<HelpdeskToolResponse> AttachmentOperation(string operation, string path, JsonObject request, bool confirm, CancellationToken ct)
    {
        if (client is not IHelpdeskAgentAttachmentClient attachments) return Validation("The configured client does not support attachment transport.");
        if (operation == "download") return await Execute(() => attachments.DownloadAttachmentAsync(path, ct), "Attachment downloaded.").ConfigureAwait(false);
        var body = request["body"]!.AsObject();
        var fileName = body["fileName"]!.GetValue<string>();
        var contentType = body["contentType"]!.GetValue<string>();
        if (fileName is "." or ".." || fileName.IndexOfAny(['/', '\\', '\r', '\n']) >= 0 || fileName.Any(char.IsControl)) return Validation("request.body.fileName must be a plain filename without path separators or control characters.");
        if (!MediaTypeHeaderValue.TryParse(contentType, out _)) return Validation("request.body.contentType must be a valid media type.");
        if (operation is "upload_branding_logo" or "upload_template_image" && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Validation("request.body.contentType must be an image media type for a logo or template image.");
        byte[] content;
        try { content = Convert.FromBase64String(body["contentBase64"]!.GetValue<string>()); }
        catch (FormatException) { return Validation("request.body.contentBase64 must contain valid base64."); }
        if (content.Length is 0 or > 1048576) return Validation("Attachment content must contain between 1 byte and 1 MiB.");
        var idField = operation switch { "upload_template_image" => "templateName", "upload_branding_logo" => "brandingTenantId", _ => "ticketId" };
        var ids = new[] { request[idField]!.ToString() };
        if (!confirm) return Response(false, "confirmation_required", "This would upload one attachment.", AffectedIds: ids, Confirmation: new HelpdeskConfirmation("confirm", true, "upload", ids));
        try
        {
            var result = operation is "upload_template_image" or "upload_branding_logo"
                ? await attachments.UploadTemplateImageAsync(path, fileName, contentType, content, ct).ConfigureAwait(false)
                : await attachments.UploadAttachmentAsync(path, fileName, contentType, content, ct).ConfigureAwait(false);
            return Response(true, "completed", "Attachment uploaded.", result, AffectedIds: ids);
        }
        catch (AgentClientRemoteException ex)
        {
            return Response(false, Status(ex.StatusCode), ex.Message, AffectedIds: ids,
                NextActions: ex.StatusCode >= 500 ? [operation switch
                {
                    "upload_branding_logo" => "Read helpdesk_tenant_admin.tenant_branding for the selected branding tenant before repeating the upload; the logo may already have been saved.",
                    "upload_template_image" => "Inspect the template's current image references and the correlated upload outcome before repeating; a stored image may exist even when its returned URL was lost.",
                    _ => "Read the ticket attachment list before repeating the upload; its result may be uncertain."
                }] : null,
                Failure: new HelpdeskToolFailure(ex.Code, false, ex.StatusCode), CorrelationId: ex.CorrelationId);
        }
    }
}
