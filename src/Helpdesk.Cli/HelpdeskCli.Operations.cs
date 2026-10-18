using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

internal static partial class HelpdeskCli
{
    private static Command BuildConnectivityCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var connectivity = new Command("connectivity", "Inspect and test External orchestration orchestration connectivity.");
        connectivity.AddCommand(GetProjectedListCommand(runtime, globals, "orchestration", "connectivity", "/api/v1/admin/orchestration", null));
        connectivity.AddCommand(BodyCommand(runtime, globals, "orchestration-test", HttpMethod.Post, "/api/v1/admin/orchestration/test"));
        connectivity.AddCommand(GetProjectedListCommand(runtime, globals, "orchestration-jobs", "connectivity-jobs", "/api/v1/admin/orchestration/catalog/jobs", null));
        connectivity.AddCommand(GetProjectedListCommand(runtime, globals, "orchestration-tenants", "connectivity-tenants", "/api/v1/admin/orchestration/catalog/tenants", null));
        connectivity.AddCommand(GetProjectedListCommand(runtime, globals, "orchestration-request-definitions", "connectivity-request-definitions", "/api/v1/admin/orchestration/catalog/request-definitions", null));
        connectivity.AddCommand(GetProjectedListCommand(runtime, globals, "orchestration-bindings", "connectivity-bindings", "/api/v1/admin/orchestration/bindings", null));
        return connectivity;
    }

    private static Command BuildNotificationsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var notifications = new Command("notifications", "Inspect notifications.");
        var page = new Option<int?>("--page") { Description = "Page number." };
        var pageSize = new Option<int?>("--page-size") { Description = "Page size." };
        var limit = new Option<int?>("--limit") { Description = "Alias for --page-size." };
        var take = new Option<int?>("--take") { Description = "Alias for --page-size." };
        var query = new Option<string?>("--query") { Description = "Search query." };
        var severity = new Option<string?>("--severity") { Description = "Notification severity." };
        var source = new Option<string?>("--source") { Description = "Notification source." };
        var category = new Option<string?>("--category") { Description = "Notification category." };
        notifications.AddCommand(GetProjectedListCommand(runtime, globals, "list", "notifications", "/api/v1/notifications", null, (page, "page"), (pageSize, "pageSize"), (limit, "pageSize"), (take, "pageSize"), (query, "search"), (severity, "severity"), (source, "source"), (category, "category")));
        notifications.AddCommand(GetProjectedByStringIdCommand(runtime, globals, "get", "/api/v1/notifications/{id}", "id", "notification"));
        notifications.AddCommand(GetListCommand(runtime, globals, "summary", "/api/v1/notifications/summary"));
        var unreadTake = new Option<int?>("--take") { Description = "Maximum unread error notifications to return." };
        var unreadLimit = new Option<int?>("--limit") { Description = "Alias for --take." };
        notifications.AddCommand(GetProjectedListCommand(runtime, globals, "unread-errors", "notifications", "/api/v1/notifications/unread-errors", ctx => Query(("take", ValueToString(ResolvePageSize(null, ctx.ParseResult.GetValueForOption(unreadLimit), ctx.ParseResult.GetValueForOption(unreadTake), null)))), (unreadTake, "take"), (unreadLimit, "take")));
        notifications.AddCommand(BodyCommand(runtime, globals, "mark-read", HttpMethod.Post, "/api/v1/notifications/mark-read", ("--ids", "ids")));
        return notifications;
    }

    private static Command BuildSystemCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var system = new Command("system", "Inspect safe system endpoints.");
        system.AddCommand(GetListCommand(runtime, globals, "version", "/api/v1/system/version"));
        system.AddCommand(GetListCommand(runtime, globals, "info", "/api/v1/system/version"));
        var status = new Command("status", "Check health and API version.");
        status.SetHandler(async ctx =>
        {
            var loaded = LoadConfig(ctx, globals);
            var live = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/health/live", authenticated: false, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            var ready = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/health/ready", authenticated: false, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            var version = await SendRawAsync(runtime, loaded.Resolved!, HttpMethod.Get, "/api/v1/system/version", authenticated: false, null, ctx.GetCancellationToken()).ConfigureAwait(false);
            var payload = new JsonObject
            {
                ["live"] = ParseBodyOrStatus(live),
                ["ready"] = ParseBodyOrStatus(ready),
                ["version"] = ParseBodyOrStatus(version)
            };
            await WriteProjectedAsync(runtime, payload, ctx, globals, "SYSTEM STATUS").ConfigureAwait(false);
        });
        system.AddCommand(status);
        return system;
    }

    private static JsonObject ProjectNotification(JsonObject source, BodyOutputOptions bodyOptions, bool detail)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["createdUtc"] = CloneNode(source["createdUtc"]),
            ["source"] = CloneNode(source["source"]),
            ["category"] = CloneNode(source["category"]),
            ["title"] = CloneNode(source["title"]),
            ["read"] = CloneNode(source["isRead"] ?? JsonValue.Create(GetString(source, "readUtc") is not null)),
            ["reference"] = CloneNode(source["reference"]),
            ["correlationId"] = CloneNode(source["correlationId"])
        };
        AddEnumFlexible(row, source, "severity", typeof(NotificationSeverity));
        AddEnumFlexible(row, source, "eventType", typeof(SupportNotificationEventType));
        if (detail)
        {
            AddSafeBodyFields(row, source, bodyOptions, "message", "messageText", "metadata", "metadataJson", "messageHtml", "notesHtml", "originalEmailHtml");
        }

        return row;
    }

    private static JsonObject ProjectConnectivitySettings(JsonObject source)
        => new()
        {
            ["enabled"] = CloneNode(source["enabled"]),
            ["status"] = GetBool(source, "enabled") == true ? "Enabled" : "Disabled",
            ["remoteSystemName"] = CloneNode(source["remoteSystemName"]),
            ["baseUrl"] = CloneNode(source["baseUrl"]),
            ["updatedAt"] = CloneNode(source["updatedAtUtc"] ?? source["updatedAt"])
        };

    private static JsonObject ProjectConnectivityJob(JsonObject source, BodyOutputOptions bodyOptions)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["displayName"] ?? source["name"]),
            ["jobDefinitionId"] = CloneNode(source["id"] ?? source["jobDefinitionId"]),
            ["jobDefinitionName"] = CloneNode(source["displayName"] ?? source["name"] ?? source["jobDefinitionName"]),
            ["tenantId"] = CloneNode(source["tenantId"] ?? source["orchestrationTenantId"] ?? source["tenant_id"]),
            ["tenantName"] = CloneNode(source["tenantName"] ?? source["orchestrationTenantName"] ?? source["tenant_name"]),
            ["status"] = CloneNode(source["status"]),
            ["updatedAt"] = CloneNode(source["updatedAt"] ?? source["updatedAtUtc"])
        };
        AddSafeBodyFields(row, source, bodyOptions, "description");
        return row;
    }

    private static JsonObject ProjectConnectivityTenant(JsonObject source)
        => new()
        {
            ["id"] = CloneNode(source["tenantId"] ?? source["id"]),
            ["name"] = CloneNode(source["name"] ?? source["tenantName"]),
            ["tenantId"] = CloneNode(source["tenantId"] ?? source["id"]),
            ["tenantName"] = CloneNode(source["name"] ?? source["tenantName"]),
            ["status"] = GetBool(source, "isActive") == false ? "Inactive" : "Active",
            ["enabled"] = CloneNode(source["isActive"] ?? source["enabled"]),
            ["updatedAt"] = CloneNode(source["updatedAt"] ?? source["updatedAtUtc"])
        };

    private static JsonObject ProjectConnectivityRequestDefinition(JsonObject source, BodyOutputOptions bodyOptions)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["requestDefinitionId"] ?? source["id"]),
            ["name"] = CloneNode(source["displayName"] ?? source["requestDefinitionName"] ?? source["name"]),
            ["requestDefinitionId"] = CloneNode(source["requestDefinitionId"] ?? source["id"]),
            ["requestDefinitionName"] = CloneNode(source["displayName"] ?? source["requestDefinitionName"] ?? source["name"]),
            ["jobDefinitionId"] = CloneNode(source["orchestrationJobDefinitionId"] ?? source["jobDefinitionId"]),
            ["jobDefinitionName"] = CloneNode(source["orchestrationJobDefinitionName"] ?? source["jobDefinitionName"]),
            ["tenantId"] = CloneNode(source["tenantId"] ?? source["orchestrationTenantId"] ?? source["tenant_id"]),
            ["tenantName"] = CloneNode(source["tenantName"] ?? source["orchestrationTenantName"] ?? source["tenant_name"]),
            ["status"] = CloneNode(source["status"]),
            ["updatedAt"] = CloneNode(source["updatedAt"] ?? source["updatedAtUtc"])
        };
        AddSafeBodyFields(row, source, bodyOptions, "description");
        return row;
    }

    private static JsonObject ProjectConnectivityBinding(JsonObject source)
        => new()
        {
            ["id"] = CloneNode(source["id"]),
            ["name"] = CloneNode(source["name"] ?? source["requestFormTitle"] ?? source["taskTemplateName"]),
            ["requestDefinitionId"] = CloneNode(source["orchestrationRequestDefinitionId"] ?? source["requestDefinitionId"]),
            ["requestDefinitionName"] = CloneNode(source["orchestrationRequestDefinitionName"] ?? source["requestDefinitionName"]),
            ["jobDefinitionId"] = CloneNode(source["orchestrationJobDefinitionId"] ?? source["jobDefinitionId"]),
            ["jobDefinitionName"] = CloneNode(source["orchestrationJobDefinitionName"] ?? source["jobDefinitionName"]),
            ["enabled"] = CloneNode(source["enabled"]),
            ["status"] = CloneNode(source["syncState"] ?? source["status"]),
            ["updatedAt"] = CloneNode(source["updatedAt"] ?? source["updatedAtUtc"])
        };

    private static JsonObject ProjectTimelineEvent(JsonObject source, BodyOutputOptions bodyOptions, bool detail)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["ticketId"] = CloneNode(source["ticketId"] ?? source["requestId"]),
            ["createdUtc"] = CloneNode(source["createdUtc"] ?? source["createdAt"]),
            ["createdByUserName"] = CloneNode(source["createdByUserName"] ?? source["actorName"]),
            ["hours"] = CloneNode(source["hours"]),
            ["recordedHours"] = CloneNode(source["recordedHours"]),
            ["workLogId"] = CloneNode(source["workLogId"]),
            ["status"] = CloneNode(source["status"])
        };
        AddEnumFlexible(row, source, "eventType", typeof(TimelineEventType));
        AddEnumFlexible(row, source, "emailStatus", typeof(EmailDeliveryStatus));
        AddSafeBodyFields(row, source, bodyOptions, "messageText", "message", "description", "messageHtml", "notesHtml", "originalEmailHtml", "payloadJson", "metadataJson");
        return row;
    }

    private static JsonObject ProjectWorklog(JsonObject source, BodyOutputOptions bodyOptions, bool detail)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["ticketId"] = CloneNode(source["ticketId"]),
            ["createdAt"] = CloneNode(source["createdAt"] ?? source["loggedAt"]),
            ["createdByUserName"] = CloneNode(source["createdByUserName"] ?? source["technicianName"]),
            ["hours"] = CloneNode(source["hours"] ?? source["timeSpentHours"]),
            ["isInternalNote"] = CloneNode(source["isInternalNote"])
        };
        AddSafeBodyFields(row, source, bodyOptions, "notes", "description", "body", "messageHtml", "notesHtml", "payloadJson");
        return row;
    }

    private static void AddSafeBodyFields(JsonObject target, JsonObject source, BodyOutputOptions options, params string[] names)
    {
        foreach (var name in names)
        {
            if (source[name] is null)
            {
                continue;
            }

            var isHtml = name.Contains("html", StringComparison.OrdinalIgnoreCase);
            var isJsonBlob = name.Contains("json", StringComparison.OrdinalIgnoreCase) || name.Contains("metadata", StringComparison.OrdinalIgnoreCase);
            var isLongBody = name is "description" or "body" or "notes" or "message" or "messageText" or "failureReason" or "automationBlockReason";
            if (isHtml && !options.IncludeHtml)
            {
                continue;
            }

            if (isJsonBlob && !options.IncludeBody)
            {
                continue;
            }

            if (isLongBody && options.BodyFormat == "none")
            {
                continue;
            }

            if (isLongBody && !options.IncludeBody)
            {
                target[name + "Excerpt"] = TrimBodyText(GetString(source, name), options);
                continue;
            }

            if (isLongBody && source[name] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                target[name] = TrimBodyText(text, options);
                continue;
            }

            target[name] = CloneNode(source[name]);
        }
    }

    private static string TrimBodyText(string? value, BodyOutputOptions options)
    {
        var text = value ?? string.Empty;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length > options.BodyLines)
        {
            text = string.Join(Environment.NewLine, lines.Take(options.BodyLines));
        }

        return Truncate(text, options.Truncate);
    }

}
