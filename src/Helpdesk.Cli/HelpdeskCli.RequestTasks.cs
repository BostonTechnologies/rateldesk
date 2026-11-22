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
    private static Command BuildRequestTasksCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var tasks = new Command("request-tasks", "Manage request tasks.");
        var page = new Option<int?>("--page") { Description = "Page number." };
        var pageSize = new Option<int?>("--page-size") { Description = "Page size." };
        var limit = new Option<int?>("--limit") { Description = "Alias for --page-size." };
        var take = new Option<int?>("--take") { Description = "Alias for --page-size." };
        var assignedToMe = new Option<bool?>("--assigned-to-me") { Description = "Filter to tasks assigned to the authenticated user." };
        var assignedToId = new Option<string?>("--assigned-to-id") { Description = "Filter by assignee id." };
        var status = new Option<string?>("--status") { Description = "Task status." };
        var type = new Option<string?>("--type") { Description = "Task type." };
        var requestId = new Option<string?>("--request-id") { Description = "Request id." };
        var serviceId = new Option<string?>("--service-id") { Description = "Service id." };
        var q = new Option<string?>("--query") { Description = "Search query." };
        var historicOnly = new Option<bool?>("--historic-only") { Description = "Return completed/skipped/cancelled tasks." };
        var includeTotal = new Option<bool?>("--include-total") { Description = "Include total count." };
        tasks.AddCommand(BodyCommand(runtime, globals, "bulk-close", HttpMethod.Post, "/api/v1/request-tasks/bulk/close", ("--selection", "selection"), ("--reason", "reason")));
        tasks.AddCommand(GetProjectedListCommand(runtime, globals, "list", "request-tasks", "/api/v1/request-tasks/", null, (page, "page"), (pageSize, "pageSize"), (limit, "pageSize"), (take, "pageSize"), (assignedToMe, "assignedToMe"), (assignedToId, "assignedToId"), (status, "status"), (type, "type"), (requestId, "requestId"), (serviceId, "serviceId"), (q, "q"), (historicOnly, "historicOnly"), (includeTotal, "includeTotal")));
        tasks.AddCommand(GetProjectedByIdCommand(runtime, globals, "get", "/api/v1/request-tasks/{id}", "request-task"));
        tasks.AddCommand(BodyCommand(runtime, globals, "create", HttpMethod.Post, "/api/v1/request-tasks/", RequestTaskBodyFields));
        tasks.AddCommand(BodyCommand(runtime, globals, "update", HttpMethod.Put, "/api/v1/request-tasks/{id}", RequestTaskBodyFields));
        tasks.AddCommand(DeleteByIdCommand(runtime, globals, "delete", "/api/v1/request-tasks/{id}"));
        tasks.AddCommand(BodyCommand(runtime, globals, "start", HttpMethod.Post, "/api/v1/request-tasks/{id}/start", ("--comment", "comment")));
        tasks.AddCommand(BodyCommand(runtime, globals, "complete", HttpMethod.Post, "/api/v1/request-tasks/{id}/complete", ("--comment", "comment")));
        tasks.AddCommand(BodyCommand(runtime, globals, "fail", HttpMethod.Post, "/api/v1/request-tasks/{id}/fail", ("--reason", "reason"), ("--comment", "comment")));
        tasks.AddCommand(BodyCommand(runtime, globals, "retry", HttpMethod.Post, "/api/v1/request-tasks/{id}/retry", ("--comment", "comment")));
        tasks.AddCommand(BodyCommand(runtime, globals, "bulk-start", HttpMethod.Post, "/api/v1/request-tasks/bulk/start", ("--ids", "ids"), ("--comment", "comment")));
        tasks.AddCommand(BodyCommand(runtime, globals, "bulk-complete", HttpMethod.Post, "/api/v1/request-tasks/bulk/complete", ("--ids", "ids"), ("--comment", "comment")));
        tasks.AddCommand(BodyCommand(runtime, globals, "bulk-retry", HttpMethod.Post, "/api/v1/request-tasks/bulk/retry", ("--ids", "ids"), ("--comment", "comment")));
        tasks.AddCommand(BuildTaskAssignSelfCommand(runtime, globals));
        return tasks;
    }

    private static readonly (string optionName, string jsonName)[] RequestTaskBodyFields =
    [
        ("--request-id", "requestId"),
        ("--name", "name"),
        ("--title", "title"),
        ("--description", "description"),
        ("--type", "type"),
        ("--status", "status"),
        ("--assigned-to-id", "assignedToId"),
        ("--order", "order"),
        ("--due-at", "dueAt"),
        ("--service-id", "serviceId")
    ];

    private static Command BuildTaskAssignSelfCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var ids = new Option<string>("--ids") { Description = "JSON array of task ids, or a comma-separated list.", Required = true };
        var email = new Option<string?>("--agent-user-email") { Description = "Agent user email for self-assignment." };
        var command = new Command("assign-self", "Assign request tasks to the configured agent user.") { ids, email };
        command.SetHandler(async ctx =>
        {
            var resolved = await ResolveAgentUserIdAsync(runtime, globals, ctx, ctx.ParseResult.GetValueForOption(email)).ConfigureAwait(false);
            var idList = ParseIds(ctx.ParseResult.GetValueForOption(ids)!);
            var updated = new JsonArray();
            foreach (var item in idList)
            {
                var id = item?.GetValue<string>() ?? throw new CliValidationException("--ids contained a non-string value.");
                var current = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/request-tasks/" + Escape(id), null).ConfigureAwait(false);
                var node = JsonNode.Parse(current.Body) as JsonObject
                    ?? throw new CliValidationException($"Task '{id}' response was not a JSON object.");
                node["assignedToId"] = resolved;
                var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Put, "/api/v1/request-tasks/" + Escape(id), node.ToJsonString(JsonOptions)).ConfigureAwait(false);
                updated.Add(JsonNode.Parse(response.Body));
            }

            if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
            {
                await WriteJsonAsync(runtime, new { assignedToId = resolved, items = updated }, ctx, globals).ConfigureAwait(false);
            }
        });
        return command;
    }

    private static Command BuildTicketsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var tickets = new Command("tickets", "Shared ticket helper operations.");
        tickets.AddCommand(BodyCommand(runtime, globals, "mark-as-seen", HttpMethod.Post, "/api/v1/tickets/{id}/mark-as-seen"));
        tickets.AddCommand(GetListCommand(runtime, globals, "timeline-count", "/api/v1/tickets/timeline/count"));
        tickets.AddCommand(GetListCommand(runtime, globals, "attachments-count", "/api/v1/tickets/attachments/count"));
        tickets.AddCommand(GetListCommand(runtime, globals, "listeners-count", "/api/v1/tickets/listeners/count"));
        return tickets;
    }

    private static JsonObject ProjectRequestTask(JsonObject source, BodyOutputOptions bodyOptions, bool detail)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["requestId"] = CloneNode(source["requestId"]),
            ["stateVersion"] = CloneNode(source["stateVersion"] ?? source["automationStateVersion"]),
            ["administrativeClosureReason"] = CloneNode(source["administrativeClosureReason"]),
            ["trackingId"] = CloneNode(source["trackingId"] ?? source["requestTrackingId"]),
            ["assignedToId"] = CloneNode(source["assignedToId"]),
            ["assignedToName"] = CloneNode(source["assignedToName"] ?? source["assignedToDisplayName"]),
            ["dueAt"] = CloneNode(source["dueAt"]),
            ["updatedAt"] = CloneNode(source["updatedAt"] ?? source["lastAutomationUpdatedAt"]),
            ["name"] = CloneNode(source["name"] ?? source["title"] ?? source["requestTitle"])
        };
        AddEnumFlexible(row, source, "status", typeof(RequestTaskStatus));
        AddEnumFlexible(row, source, "type", typeof(RequestTaskType));
        if (detail)
        {
            row["order"] = CloneNode(source["order"]);
            row["startedAt"] = CloneNode(source["startedAt"]);
            row["completedAt"] = CloneNode(source["completedAt"]);
            row["retryCount"] = CloneNode(source["retryCount"]);
            AddSafeBodyFields(row, source, bodyOptions, "description", "failureReason", "automationBlockReason", "resultJson", "conditionExpression", "retryError");
        }

        return row;
    }

}
