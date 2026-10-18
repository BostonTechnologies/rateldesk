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
    private static Command BuildIncidentsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var incidents = BuildTicketCollectionCommand(runtime, globals, "incidents", "Manage incidents.", "/api/v1/incidents");
        incidents.AddCommand(GetProjectedByIdCommand(runtime, globals, "peek", "/api/v1/incidents/{id}/peek", "incident-peek"));
        return incidents;
    }

    private static Command BuildChangesCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var changes = BuildTicketCollectionCommand(runtime, globals, "changes", "Manage changes.", "/api/v1/changes");
        var lifecycle = BodyCommand(runtime, globals, "lifecycle", HttpMethod.Post, "/api/v1/changes/{id}/lifecycle", ("--lifecycle-state", "lifecycleState"), ("--comment", "comment"));
        changes.AddCommand(lifecycle);
        return changes;
    }

    private static Command BuildTicketCollectionCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, string basePath)
    {
        var command = new Command(name, description);
        var page = new Option<int?>("--page") { Description = "Page number." };
        var pageSize = new Option<int?>("--page-size") { Description = "Page size." };
        var limit = new Option<int?>("--limit") { Description = "Alias for --page-size." };
        var take = new Option<int?>("--take") { Description = "Alias for --page-size." };
        var state = new Option<string?>("--state") { Description = "Ticket state." };
        var q = new Option<string?>("--query") { Description = "Search query." };
        var activeOnly = new Option<bool?>("--active-only") { Description = "Return active items only." };
        var historicOnly = new Option<bool?>("--historic-only") { Description = "Return historic items only." };
        var aiInvolved = new Option<bool?>("--ai-involved") { Description = "Return AI-involved items only." };
        var includeTotal = new Option<bool?>("--include-total") { Description = "Include total count." };
        var summaryOnly = new Option<bool?>("--summary-only") { Description = "Return summary rows only." };
        var maxItems = new Option<int?>("--max-items") { Description = "Maximum items to scan for aggregate summaries.", DefaultValueFactory = _ => DefaultAggregateMaxItems };
        var requesterEmail = name == "incidents"
            ? new Option<string?>("--requester-email") { Description = "Filter incidents by requester/customer email." }
            : null;
        command.AddCommand(GetTicketListCommand(runtime, globals, name, basePath + "/", page, pageSize, limit, take, state, q, activeOnly, historicOnly, aiInvolved, includeTotal, summaryOnly, maxItems, requesterEmail));
        command.AddCommand(GetTicketSummaryCommand(runtime, globals, name, basePath + "/", pageSize, activeOnly, includeTotal, maxItems));
        command.AddCommand(GetProjectedByIdCommand(runtime, globals, "get", basePath + "/{id}", name.TrimEnd('s')));
        command.AddCommand(name == "incidents"
            ? BuildIncidentCreateCommand(runtime, globals, basePath + "/")
            : BodyCommand(runtime, globals, "create", HttpMethod.Post, basePath + "/", TicketBodyFields));
        if (name == "incidents")
        {
            command.AddCommand(BuildIncidentBulkCreateCommand(runtime, globals, basePath + "/"));
        }
        command.AddCommand(BodyCommand(runtime, globals, "update", HttpMethod.Put, basePath + "/{id}", TicketBodyFields));
        command.AddCommand(DeleteByIdCommand(runtime, globals, "delete", basePath + "/{id}"));
        command.AddCommand(BodyCommand(runtime, globals, "state", HttpMethod.Post, basePath + "/{id}/state", ("--new-state", "newState")));
        command.AddCommand(BodyCommand(runtime, globals, "bulk-state", HttpMethod.Post, basePath + "/bulk/state", ("--ids", "ids"), ("--new-state", "newState"), ("--comment", "comment")));
        command.AddCommand(BodyCommand(runtime, globals, "assign", HttpMethod.Post, basePath + "/bulk/assign", ("--ids", "ids"), ("--assigned-to-id", "assignedToId")));
        command.AddCommand(BuildAssignSelfCommand(runtime, globals, name, basePath + "/bulk/assign"));
        command.AddCommand(BuildTicketWorklogsCommand(runtime, globals, name, basePath));
        command.AddCommand(BodyCommand(runtime, globals, "add-worklog", HttpMethod.Post, basePath + "/{id}/worklogs", ("--hours", "hours"), ("--notes", "notes"), ("--is-internal-note", "isInternalNote")));
        command.AddCommand(BuildTicketTimelineCommand(runtime, globals, name, basePath));
        if (name == "requests")
        {
            command.AddCommand(GetProjectedByIdCommand(runtime, globals, "ticket-timeline", basePath + "/{id}/timeline", "timeline"));
        }
        return command;
    }

    private static readonly (string optionName, string jsonName)[] TicketBodyFields =
    [
        ("--organization-id", "organizationId"),
        ("--customer-id", "customerId"),
        ("--requester-email", "requesterEmail"),
        ("--title", "title"),
        ("--subject", "subject"),
        ("--description", "description"),
        ("--priority", "priority"),
        ("--state", "state"),
        ("--assigned-to-id", "assignedToId"),
        ("--category-ids", "categoryIds"),
        ("--service-id", "serviceId"),
        ("--request-form-id", "requestFormId"),
        ("--payload-json", "payloadJson"),
        ("--change-type", "changeType"),
        ("--requested-for-user-id", "requestedForUserId"),
        ("--implementor-user-id", "implementorUserId"),
        ("--approver-user-ids", "approverUserIds"),
        ("--implementation-start-at", "implementationStartAt"),
        ("--implementation-end-at", "implementationEndAt"),
        ("--change-template", "changeTemplate"),
        ("--cc-recipients", "ccRecipients")
    ];

    private static readonly (string optionName, string jsonName)[] IncidentCreateBodyFields =
    [
        ("--organization-id", "organizationId"),
        ("--customer-id", "customerId"),
        ("--requester-email", "requesterEmail"),
        ("--title", "title"),
        ("--subject", "subject"),
        ("--description", "description"),
        ("--priority", "priority"),
        ("--state", "state"),
        ("--assigned-to-id", "assignedToId"),
        ("--category-ids", "categoryIds"),
        ("--cc-recipients", "ccRecipients")
    ];

    private static Command BuildIncidentCreateCommand(CliRuntime runtime, GlobalOptions globals, string path)
    {
        var body = new Option<string?>("--body") { Description = "JSON request body. Overrides field flags." };
        var bodyFile = new Option<string?>("--body-file") { Description = "Path to JSON request body file. Overrides field flags." };
        var forEmail = new Option<string?>("--for-email") { Description = "Resolve a unique enabled customer by email and fill customerId, organizationId, and requesterEmail." };
        var customerEmail = new Option<string?>("--customer-email") { Description = "Alias for --for-email." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Resolve and validate the create body without POSTing." };
        var validateOnly = new Option<bool>("--validate-only") { Description = "Alias for --dry-run." };
        var command = new Command("create",
            "Create an incident. Required: --title or --subject, --description, and either --for-email or both --customer-id and --organization-id. " +
            "Examples: helpdesk incidents create --for-email person@example.com --title \"VPN down\" --description \"Cannot connect\" --priority Medium; " +
            "helpdesk incidents create --body-file incident.json --dry-run. Priority: 0/Low, 1/Medium, 2/High, 3/Critical. " +
            "State: 0/New, 1/WaitingReply, 2/Replied, 3/InProgress, 4/PendingApproval, 5/OnHold, 6/Resolved. " +
            "--for-email/--customer-email resolves a unique enabled customer and fills customerId, organizationId, and requesterEmail.")
        {
            body,
            bodyFile,
            forEmail,
            customerEmail,
            dryRun,
            validateOnly
        };

        var fields = new List<(Option<string?> option, string jsonName)>();
        foreach (var (optionName, jsonName) in IncidentCreateBodyFields)
        {
            var option = new Option<string?>(optionName) { Description = IncidentCreateOptionDescription(optionName, jsonName) };
            command.AddOption(option);
            fields.Add((option, jsonName));
        }

        command.SetHandler(async ctx =>
        {
            var content = ReadBody(ctx.ParseResult.GetValueForOption(body), ctx.ParseResult.GetValueForOption(bodyFile), null)
                ?? BuildBodyFromOptions(ctx, fields);
            var incidentBody = ParseObjectBody(content, "Incident create body must be a JSON object.");
            var resolvedEmail = FirstNonEmpty(ctx.ParseResult.GetValueForOption(forEmail), ctx.ParseResult.GetValueForOption(customerEmail));
            var prepared = await PrepareIncidentCreateBodyAsync(runtime, globals, ctx, incidentBody, resolvedEmail).ConfigureAwait(false);
            var isDryRun = ctx.ParseResult.GetValueForOption(dryRun) || ctx.ParseResult.GetValueForOption(validateOnly);
            if (isDryRun)
            {
                if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
                {
                    await WriteJsonPayloadAsync(runtime, new JsonObject
                    {
                        ["dryRun"] = true,
                        ["method"] = "POST",
                        ["path"] = path,
                        ["body"] = prepared
                    }, ctx, globals).ConfigureAwait(false);
                }

                return;
            }

            await SendAsync(runtime, globals, ctx, HttpMethod.Post, path, prepared.ToJsonString(JsonOptions)).ConfigureAwait(false);
        });

        return command;
    }

    private static Command BuildIncidentBulkCreateCommand(CliRuntime runtime, GlobalOptions globals, string path)
    {
        var bodyFile = new Option<string>("--body-file") { Description = "Path to a JSON array, or an object with an items array.", Required = true };
        var dryRun = new Option<bool>("--dry-run") { Description = "Resolve and validate all create bodies without POSTing." };
        var validateOnly = new Option<bool>("--validate-only") { Description = "Alias for --dry-run." };
        var continueOnError = new Option<bool>("--continue-on-error") { Description = "Continue after per-item validation or API failures." };
        var command = new Command("bulk-create", "Create incidents from a body file containing a JSON array or { items: [...] }.") { bodyFile, dryRun, validateOnly, continueOnError };
        command.SetHandler(async ctx =>
        {
            var fileContent = ReadBody(null, ctx.ParseResult.GetValueForOption(bodyFile), null)
                ?? throw new CliValidationException("--body-file is required.");
            var sourceItems = ParseIncidentCreateItems(fileContent);
            var isDryRun = ctx.ParseResult.GetValueForOption(dryRun) || ctx.ParseResult.GetValueForOption(validateOnly);
            var keepGoing = ctx.ParseResult.GetValueForOption(continueOnError);
            var results = new JsonArray();

            for (var index = 0; index < sourceItems.Count; index++)
            {
                var item = CloneObject(sourceItems[index]);
                try
                {
                    var embeddedEmail = FirstNonEmpty(GetString(item, "forEmail"), GetString(item, "customerEmail"));
                    item.Remove("forEmail");
                    item.Remove("customerEmail");
                    var prepared = await PrepareIncidentCreateBodyAsync(runtime, globals, ctx, item, embeddedEmail).ConfigureAwait(false);
                    if (isDryRun)
                    {
                        results.Add(new JsonObject
                        {
                            ["index"] = index,
                            ["ok"] = true,
                            ["dryRun"] = true,
                            ["body"] = prepared
                        });
                        continue;
                    }

                    var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Post, path, prepared.ToJsonString(JsonOptions)).ConfigureAwait(false);
                    results.Add(new JsonObject
                    {
                        ["index"] = index,
                        ["ok"] = true,
                        ["statusCode"] = response.StatusCode,
                        ["response"] = ParseBodyOrStatus(response)
                    });
                }
                catch (Exception ex) when (keepGoing && ex is CliValidationException or CliRemoteException)
                {
                    var (code, message, exitCode, responseBody, details) = MapException(ex);
                    var error = new JsonObject
                    {
                        ["code"] = code,
                        ["message"] = message,
                        ["exitCode"] = exitCode,
                        ["responseBody"] = responseBody
                    };
                    if (details is not null)
                    {
                        error["details"] = CloneNode(details);
                    }

                    results.Add(new JsonObject
                    {
                        ["index"] = index,
                        ["ok"] = false,
                        ["error"] = error
                    });
                }
            }

            if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
            {
                await WriteJsonPayloadAsync(runtime, new JsonObject
                {
                    ["dryRun"] = isDryRun,
                    ["count"] = results.Count,
                    ["items"] = results
                }, ctx, globals).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static string IncidentCreateOptionDescription(string optionName, string jsonName)
        => optionName switch
        {
            "--title" => "Required incident title.",
            "--subject" => "Alias for --title.",
            "--description" => "Required incident description.",
            "--priority" => "Incident priority: 0/Low, 1/Medium, 2/High, 3/Critical.",
            "--state" => "Incident state: 0/New, 1/WaitingReply, 2/Replied, 3/InProgress, 4/PendingApproval, 5/OnHold, 6/Resolved.",
            "--category-ids" => "JSON array of category ids.",
            "--cc-recipients" => "JSON array of CC recipient email addresses.",
            "--organization-id" => "Required with --customer-id unless --for-email/--customer-email is used.",
            "--customer-id" => "Required with --organization-id unless --for-email/--customer-email is used.",
            "--requester-email" => "Requester email stored on the incident; filled from --for-email when omitted.",
            _ => $"JSON field '{jsonName}'."
        };

    private static async Task<JsonObject> PrepareIncidentCreateBodyAsync(CliRuntime runtime, GlobalOptions globals, InvocationContext ctx, JsonObject body, string? email)
    {
        if (body["title"] is null && body["subject"] is not null)
        {
            body["title"] = CloneNode(body["subject"]);
        }

        var embeddedEmail = FirstNonEmpty(GetString(body, "forEmail"), GetString(body, "customerEmail"));
        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(embeddedEmail) && !string.Equals(email, embeddedEmail, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliValidationException("--for-email conflicts with forEmail/customerEmail in the body.");
        }

        email = FirstNonEmpty(email, embeddedEmail);
        body.Remove("forEmail");
        body.Remove("customerEmail");
        body.Remove("subject");
        NormalizeKnownBodyEnums(body);

        if (!string.IsNullOrWhiteSpace(email))
        {
            var customer = await ResolveCustomerForEmailAsync(runtime, globals, ctx, email!).ConfigureAwait(false);
            SetOrValidateString(body, "customerId", customer.CustomerId, "--for-email resolved a different customerId than the body supplied.");
            SetOrValidateString(body, "organizationId", customer.OrganizationId, "--for-email resolved a different organizationId than the body supplied.");
            if (string.IsNullOrWhiteSpace(GetString(body, "requesterEmail")))
            {
                body["requesterEmail"] = customer.Email;
            }
        }

        RequireIncidentCreateField(body, "title", "--title");
        RequireIncidentCreateField(body, "description", "--description");
        RequireIncidentCreateField(body, "customerId", "--customer-id or --for-email");
        RequireIncidentCreateField(body, "organizationId", "--organization-id or --for-email");
        return body;
    }

    private static async Task<IncidentCustomerResolution> ResolveCustomerForEmailAsync(CliRuntime runtime, GlobalOptions globals, InvocationContext ctx, string email)
    {
        var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, "/api/v1/global-search/customers" + Query(("q", email), ("pageSize", "25")), null).ConfigureAwait(false);
        var matches = ParseItems(response.Body)
            .Where(item => string.Equals(GetString(item, "email"), email, StringComparison.OrdinalIgnoreCase))
            .Where(IsEnabledCustomer)
            .GroupBy(item => GetString(item, "id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (matches.Count == 0)
        {
            throw new CliValidationException(
                $"No unique customer found for '{email}'. No enabled customer matched exactly. Try 'helpdesk search customers --query {email}'.",
                BuildRequesterResolutionDetails("not_found", email, matches));
        }

        if (matches.Count > 1)
        {
            var choices = string.Join(", ", matches.Select(item => $"{GetString(item, "id") ?? "unknown"} ({GetString(item, "organizationName") ?? GetString(item, "organizationId") ?? "unknown org"})"));
            throw new CliValidationException(
                $"Multiple customers matched '{email}': {choices}. Use --customer-id and --organization-id instead.",
                BuildRequesterResolutionDetails("ambiguous", email, matches));
        }

        var match = matches[0];
        return new IncidentCustomerResolution(
            RequireString(match, "id", "Resolved customer did not include id."),
            RequireString(match, "organizationId", "Resolved customer did not include organizationId."),
            RequireString(match, "email", "Resolved customer did not include email."));
    }

    private static bool IsEnabledCustomer(JsonObject item)
    {
        var isEnabled = GetBool(item, "isEnabled");
        if (isEnabled.HasValue)
        {
            return isEnabled.Value;
        }

        var state = GetInt(item, "state");
        return !state.HasValue || state.Value == (int)EntityState.Enabled;
    }

    private static JsonObject BuildRequesterResolutionDetails(string reason, string email, IReadOnlyList<JsonObject> matches)
        => new()
        {
            ["reason"] = reason,
            ["email"] = email,
            ["suggestedCommands"] = new JsonArray(
                $"helpdesk search customers --query {email} --output json",
                $"helpdesk incidents create --customer-id <customer-id> --organization-id <organization-id> --requester-email {email} ..."),
            ["candidates"] = matches.Select(RequesterCandidateToJson).ToJsonArray()
        };

    private static JsonObject RequesterCandidateToJson(JsonObject item)
        => new()
        {
            ["customerId"] = CloneNode(item["id"]),
            ["email"] = CloneNode(item["email"]),
            ["name"] = CloneNode(item["name"] ?? item["customerName"]),
            ["organizationId"] = CloneNode(item["organizationId"]),
            ["organizationName"] = CloneNode(item["organizationName"]),
            ["isEnabled"] = IsEnabledCustomer(item)
        };

    private static List<JsonObject> ParseIncidentCreateItems(string body)
    {
        var root = JsonNode.Parse(body) ?? throw new CliValidationException("Body file must contain JSON.");
        var array = root as JsonArray;
        if (root is JsonObject obj)
        {
            array = obj["items"] as JsonArray;
        }

        if (array is null)
        {
            throw new CliValidationException("Bulk create body must be a JSON array or an object with an items array.");
        }

        var items = array.OfType<JsonObject>().Select(CloneObject).ToList();
        if (items.Count != array.Count)
        {
            throw new CliValidationException("Every bulk create item must be a JSON object.");
        }

        return items;
    }

    private static JsonObject ParseObjectBody(string body, string message)
        => JsonNode.Parse(body) as JsonObject ?? throw new CliValidationException(message);

    private static List<JsonObject> ParseItems(string body)
    {
        var root = JsonNode.Parse(body) ?? new JsonObject();
        var array = root is JsonObject obj && obj["items"] is JsonArray items
            ? items
            : root as JsonArray ?? [];
        return array.OfType<JsonObject>().Select(CloneObject).ToList();
    }

    private static void SetOrValidateString(JsonObject body, string field, string value, string conflictMessage)
    {
        var existing = GetString(body, field);
        if (!string.IsNullOrWhiteSpace(existing) && !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliValidationException(conflictMessage);
        }

        body[field] = value;
    }

    private static void RequireIncidentCreateField(JsonObject body, string field, string optionName)
    {
        if (string.IsNullOrWhiteSpace(GetString(body, field)))
        {
            throw new CliValidationException($"Incident create requires {optionName}. Use --for-email to resolve customer and organization fields automatically.");
        }
    }

    private static string RequireString(JsonObject body, string field, string message)
        => GetString(body, field) ?? throw new CliValidationException(message);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static Command BuildAssignSelfCommand(CliRuntime runtime, GlobalOptions globals, string resourceName, string assignPath)
    {
        var ids = new Option<string>("--ids") { Description = "JSON array of ids, or a comma-separated list.", Required = true };
        var email = new Option<string?>("--agent-user-email") { Description = "Agent user email for self-assignment." };
        var command = new Command("assign-self", $"Assign {resourceName} to the configured agent user.") { ids, email };
        command.SetHandler(async ctx =>
        {
            var resolved = await ResolveAgentUserIdAsync(runtime, globals, ctx, ctx.ParseResult.GetValueForOption(email)).ConfigureAwait(false);
            var idList = ParseIds(ctx.ParseResult.GetValueForOption(ids)!);
            var body = new JsonObject
            {
                ["ids"] = idList,
                ["assignedToId"] = resolved
            }.ToJsonString(JsonOptions);
            await SendAsync(runtime, globals, ctx, HttpMethod.Post, assignPath, body).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildRequestsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var requests = BuildTicketCollectionCommand(runtime, globals, "requests", "Manage service requests.", "/api/v1/requests");
        requests.AddCommand(GetByIdCommand(runtime, globals, "tasks", "/api/v1/requests/{id}/tasks"));
        requests.AddCommand(GetByIdCommand(runtime, globals, "ai-audit", "/api/v1/requests/{id}/ai-audit"));
        return requests;
    }

    private static Command GetTicketListCommand(
        CliRuntime runtime,
        GlobalOptions globals,
        string resource,
        string path,
        Option<int?> page,
        Option<int?> pageSize,
        Option<int?> limit,
        Option<int?> take,
        Option<string?> state,
        Option<string?> q,
        Option<bool?> activeOnly,
        Option<bool?> historicOnly,
        Option<bool?> aiInvolved,
        Option<bool?> includeTotal,
        Option<bool?> summaryOnly,
        Option<int?> maxItems,
        Option<string?>? requesterEmail = null)
    {
        var command = new Command("list", $"List {resource}.");
        foreach (var option in new Option[] { page, pageSize, limit, take, state, q, activeOnly, historicOnly, aiInvolved, includeTotal, summaryOnly, maxItems })
        {
            command.AddOption(option);
        }

        if (requesterEmail is not null)
        {
            command.AddOption(requesterEmail);
        }

        command.SetHandler(async ctx =>
        {
            var resolvedPageSize = ResolvePageSize(ctx.ParseResult.GetValueForOption(pageSize), ctx.ParseResult.GetValueForOption(limit), ctx.ParseResult.GetValueForOption(take), defaultValue: null);
            var query = Query(
                ("page", ValueToString(ctx.ParseResult.GetValueForOption(page))),
                ("pageSize", ValueToString(resolvedPageSize)),
                ("state", ctx.ParseResult.GetValueForOption(state)),
                ("q", ctx.ParseResult.GetValueForOption(q)),
                ("activeOnly", ValueToString(ctx.ParseResult.GetValueForOption(activeOnly))),
                ("historicOnly", ValueToString(ctx.ParseResult.GetValueForOption(historicOnly))),
                ("aiInvolved", ValueToString(ctx.ParseResult.GetValueForOption(aiInvolved))),
                ("includeTotal", ValueToString(ctx.ParseResult.GetValueForOption(includeTotal))),
                ("summaryOnly", ValueToString(ctx.ParseResult.GetValueForOption(summaryOnly))),
                ("requesterEmail", requesterEmail is null ? null : ctx.ParseResult.GetValueForOption(requesterEmail)));

            var output = ResolveOutput(ctx, globals, defaultView: "summary");
            if (output.View == "aggregate")
            {
                await WriteAggregateSummaryAsync(
                    runtime,
                    globals,
                    ctx,
                    resource,
                    path,
                    ResolvePageSize(ctx.ParseResult.GetValueForOption(pageSize), ctx.ParseResult.GetValueForOption(limit), ctx.ParseResult.GetValueForOption(take), DefaultAggregatePageSize)!.Value,
                    Math.Max(1, ctx.ParseResult.GetValueForOption(maxItems) ?? DefaultAggregateMaxItems),
                    ctx.ParseResult.GetValueForOption(activeOnly),
                    ctx.ParseResult.GetValueForOption(includeTotal)).ConfigureAwait(false);
                return;
            }

            var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, path + query, null).ConfigureAwait(false);
            if (!ctx.ParseResult.GetValueForOption(globals.Quiet))
            {
                await WriteTicketListAsync(runtime, response.Body, resource, output, ctx, globals).ConfigureAwait(false);
            }
        });
        return command;
    }

    private static Command GetTicketSummaryCommand(
        CliRuntime runtime,
        GlobalOptions globals,
        string resource,
        string path,
        Option<int?> pageSize,
        Option<bool?> activeOnly,
        Option<bool?> includeTotal,
        Option<int?> maxItems)
    {
        var command = new Command("summary", $"Aggregate {resource} queue summary.");
        foreach (var option in new Option[] { pageSize, activeOnly, includeTotal, maxItems })
        {
            command.AddOption(option);
        }

        command.SetHandler(ctx => WriteAggregateSummaryAsync(
            runtime,
            globals,
            ctx,
            resource,
            path,
            Math.Max(1, ctx.ParseResult.GetValueForOption(pageSize) ?? DefaultAggregatePageSize),
            Math.Max(1, ctx.ParseResult.GetValueForOption(maxItems) ?? DefaultAggregateMaxItems),
            ctx.ParseResult.GetValueForOption(activeOnly),
            ctx.ParseResult.GetValueForOption(includeTotal)));
        return command;
    }

    private static Command BuildTicketWorklogsCommand(CliRuntime runtime, GlobalOptions globals, string resource, string basePath)
    {
        if (resource == "incidents")
        {
            var id = new Argument<string>("id");
            var command = new Command("worklogs", "Incident worklog listing is not available; use timeline for audit history.") { id };
            command.SetHandler(ctx => throw new CliValidationException("Command 'incidents worklogs' is not supported by the API. Try 'helpdesk incidents timeline " + ctx.ParseResult.GetValueForArgument(id) + "'."));
            return command;
        }

        return GetProjectedByIdCommand(runtime, globals, "worklogs", basePath + "/{id}/worklogs", "worklog");
    }

    private static Command BuildTicketTimelineCommand(CliRuntime runtime, GlobalOptions globals, string resource, string basePath)
    {
        if (resource == "requests")
        {
            return GetProjectedByIdCommand(runtime, globals, "timeline", "/api/v1/requests/{id}/workflow-timeline", "timeline");
        }

        return GetProjectedByIdCommand(runtime, globals, "timeline", basePath + "/{id}/timeline", "timeline");
    }

    private static async Task WriteTicketListAsync(CliRuntime runtime, string body, string resource, ResolvedOutput output, InvocationContext ctx, GlobalOptions globals)
    {
        if (output.View == "raw")
        {
            await WriteRawBodyAsync(runtime, body, ctx, globals).ConfigureAwait(false);
            return;
        }

        var envelope = ProjectTicketEnvelope(body, resource, output.Fields);
        if (output.Format == "json")
        {
            await WriteJsonPayloadAsync(runtime, envelope, ctx, globals).ConfigureAwait(false);
            return;
        }

        var rows = envelope["items"] as JsonArray ?? [];
        var title = resource.ToUpperInvariant();
        var tableRows = output.Fields is null
            ? rows.OfType<JsonObject>().Select(row => new Dictionary<string, string?>
            {
                ["TRACKING ID"] = GetString(row, "trackingId") ?? GetString(row, "id"),
                ["STATE"] = GetString(row, "stateLabel") ?? GetString(row, "stateName"),
                ["PRI"] = FormatPriority(GetString(row, "priorityName") ?? GetString(row, "priorityLabel")),
                ["CUSTOMER/ORG"] = GetString(row, "customerOrgName"),
                ["REQUESTER"] = GetString(row, "customerName") ?? (GetBool(row, "requesterEmailKnown") == true ? "known" : null),
                ["ASSIGNED"] = GetString(row, "assignedToName") ?? GetString(row, "assignedToId"),
                ["UPDATED"] = FormatDate(GetString(row, "updatedAt")),
                ["SUBJECT"] = GetString(row, "subject") ?? GetString(row, "title")
            }).ToList()
            : rows.OfType<JsonObject>().Select(ProjectFieldsForTable).ToList();

        var footer = BuildPageFooter(envelope);
        await runtime.Out.WriteLineAsync(RenderTable(title, tableRows, footer)).ConfigureAwait(false);
    }

    private static async Task WriteAggregateSummaryAsync(
        CliRuntime runtime,
        GlobalOptions globals,
        InvocationContext ctx,
        string resource,
        string path,
        int pageSize,
        int maxItems,
        bool? activeOnly,
        bool? includeTotal)
    {
        var output = ResolveOutput(ctx, globals, defaultView: "aggregate");
        var items = new List<JsonObject>();
        int? totalCount = null;
        var page = 1;
        var stoppedAtMaxItems = false;

        while (items.Count < maxItems)
        {
            var take = Math.Min(pageSize, maxItems - items.Count);
            var query = Query(
                ("page", page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("pageSize", take.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("activeOnly", ValueToString(activeOnly)),
                ("includeTotal", ValueToString(includeTotal ?? true)),
                ("summaryOnly", "true"));
            var response = await SendStringAsync(runtime, globals, ctx, HttpMethod.Get, path + query, null).ConfigureAwait(false);
            var envelope = ProjectTicketEnvelope(response.Body, resource, fields: null);
            totalCount ??= GetInt(envelope, "totalCount");
            var pageItems = (envelope["items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            items.AddRange(pageItems);
            if (pageItems.Count < take)
            {
                break;
            }

            if (items.Count >= maxItems)
            {
                stoppedAtMaxItems = true;
                break;
            }

            page++;
        }

        var capReached = stoppedAtMaxItems && (!totalCount.HasValue || totalCount.Value > items.Count);
        var summary = BuildAggregateSummary(resource, items, totalCount, pageSize, maxItems, activeOnly, capReached);
        if (ctx.ParseResult.GetValueForOption(globals.Quiet))
        {
            return;
        }

        if (output.Format == "json")
        {
            await WriteJsonPayloadAsync(runtime, summary, ctx, globals).ConfigureAwait(false);
        }
        else
        {
            await runtime.Out.WriteLineAsync(RenderAggregateSummary(summary)).ConfigureAwait(false);
        }
    }

    private static JsonObject ProjectTicketEnvelope(string body, string resource, IReadOnlySet<string>? fields)
    {
        var root = JsonNode.Parse(body) ?? new JsonObject();
        var page = GetInt(root, "page") ?? 1;
        var pageSize = GetInt(root, "pageSize") ?? 0;
        var totalCount = GetInt(root, "totalCount") ?? GetInt(root, "total");
        var sourceItems = root is JsonObject obj && obj["items"] is JsonArray array
            ? array
            : root is JsonArray rootArray
                ? rootArray
                : root is JsonObject rootObject
                    ? new JsonArray(CloneObject(rootObject))
                    : [];
        var items = new JsonArray();

        foreach (var item in sourceItems)
        {
            if (item is JsonObject itemObject)
            {
                items.Add(FilterFields(ProjectTicketSummary(itemObject, resource), fields));
            }
        }

        return new JsonObject
        {
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["totalCount"] = totalCount.HasValue ? JsonValue.Create(totalCount.Value) : null,
            ["items"] = items
        };
    }

    private static JsonObject ProjectTicketSummary(JsonObject source, string resource)
    {
        var row = new JsonObject
        {
            ["id"] = CloneNode(source["id"]),
            ["trackingId"] = CloneNode(source["trackingId"]),
            ["subject"] = CloneNode(source["subject"] ?? source["title"]),
            ["title"] = CloneNode(source["title"] ?? source["subject"]),
            ["type"] = resource.TrimEnd('s'),
            ["customerOrgName"] = CloneNode(source["customerOrgName"] ?? source["organizationName"]),
            ["customerName"] = CloneNode(source["customerName"]),
            ["requesterEmail"] = CloneNode(source["requesterEmail"]),
            ["customerEmail"] = CloneNode(source["customerEmail"]),
            ["requesterEmailKnown"] = !string.IsNullOrWhiteSpace(GetString(source, "requesterEmail") ?? GetString(source, "customerEmail")),
            ["assignedToId"] = CloneNode(source["assignedToId"]),
            ["assignedToName"] = CloneNode(source["assignedToName"]),
            ["createdAt"] = CloneNode(source["createdAt"]),
            ["updatedAt"] = CloneNode(source["updatedAt"])
        };

        AddEnum(row, source, "state", typeof(TicketState));
        AddEnum(row, source, "priority", typeof(TicketPriority));
        AddEnum(row, source, "lifecycleState", typeof(ChangeLifecycleState));

        if (source["sla"] is JsonObject sla && sla["status"] is not null)
        {
            var statusValue = GetInt(sla, "status");
            if (statusValue.HasValue)
            {
                row["slaStatus"] = statusValue.Value;
                var info = EnumInfo(typeof(SlaStatus), statusValue.Value);
                row["slaStatusName"] = info.Name;
                row["slaStatusLabel"] = info.Label;
            }
        }

        return row;
    }

    private static JsonObject BuildAggregateSummary(string resource, List<JsonObject> items, int? totalCount, int pageSize, int maxItems, bool? activeOnly, bool capReached)
    {
        var scannedCount = items.Count;
        var partialResults = totalCount.HasValue && totalCount.Value > scannedCount;
        var latest = items
            .OrderByDescending(item => ParseDate(GetString(item, "updatedAt")) ?? DateTimeOffset.MinValue)
            .Take(DefaultLatestUpdatedCount)
            .Select(item => CloneObject(item))
            .ToJsonArray();

        return new JsonObject
        {
            ["resource"] = resource,
            ["filters"] = new JsonObject { ["activeOnly"] = activeOnly },
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["totalCount"] = totalCount.HasValue ? JsonValue.Create(totalCount.Value) : null,
            ["scannedCount"] = scannedCount,
            ["pageSize"] = pageSize,
            ["maxItems"] = maxItems,
            ["capReached"] = capReached,
            ["partialResults"] = partialResults,
            ["byState"] = CountByEnum(items, "state", "stateName", "stateLabel"),
            ["byPriority"] = CountByEnum(items, "priority", "priorityName", "priorityLabel"),
            ["requesterContact"] = new JsonObject
            {
                ["knownCount"] = items.Count(item => GetBool(item, "requesterEmailKnown") == true),
                ["missingCount"] = items.Count(item => GetBool(item, "requesterEmailKnown") != true)
            },
            ["topCustomers"] = CountByString(items, "customerOrgName", DefaultTopCount),
            ["topRequesters"] = CountByString(items, "customerName", DefaultTopCount),
            ["latestUpdated"] = latest
        };
    }

    private static string RenderAggregateSummary(JsonObject summary)
    {
        var resource = (GetString(summary, "resource") ?? "queue").ToUpperInvariant();
        var lines = new List<string>
        {
            $"ACTIVE {resource.TrimEnd('S')} SUMMARY",
            string.Empty,
            $"Total: {GetInt(summary, "totalCount")?.ToString() ?? "unknown"}",
            $"Scanned: {GetInt(summary, "scannedCount") ?? 0}",
            $"Partial: {GetBool(summary, "partialResults")?.ToString().ToLowerInvariant() ?? "false"}",
            string.Empty,
            "By state:"
        };
        AddCountLines(lines, summary["byState"] as JsonArray, "stateLabel");
        lines.Add(string.Empty);
        lines.Add("By priority:");
        AddCountLines(lines, summary["byPriority"] as JsonArray, "priorityLabel");
        if (summary["requesterContact"] is JsonObject contact)
        {
            lines.Add(string.Empty);
            lines.Add("Requester/contact:");
            lines.Add($"  Known: {GetInt(contact, "knownCount") ?? 0}");
            lines.Add($"  Missing: {GetInt(contact, "missingCount") ?? 0}");
        }

        lines.Add(string.Empty);
        lines.Add("Top customers:");
        AddNameCountLines(lines, summary["topCustomers"] as JsonArray);
        lines.Add(string.Empty);
        lines.Add("Latest updated:");
        var latestRows = (summary["latestUpdated"] as JsonArray)?.OfType<JsonObject>().Select(row => new Dictionary<string, string?>
        {
            ["TRACKING ID"] = GetString(row, "trackingId"),
            ["STATE"] = GetString(row, "stateLabel"),
            ["PRI"] = FormatPriority(GetString(row, "priorityName")),
            ["CUSTOMER/ORG"] = GetString(row, "customerOrgName"),
            ["UPDATED"] = FormatDate(GetString(row, "updatedAt")),
            ["SUBJECT"] = GetString(row, "subject") ?? GetString(row, "title")
        }).ToList() ?? [];
        lines.Add(RenderTable(null, latestRows, null));
        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    private static JsonObject ProjectTicketDetail(JsonObject source, string resource, BodyOutputOptions bodyOptions, bool detail)
    {
        var row = ProjectTicketSummary(source, resource);
        AddSafeBodyFields(row, source, bodyOptions, "description", "body", "payloadJson", "messageHtml", "notesHtml", "originalEmailHtml", "changeTemplate");
        return row;
    }

}
