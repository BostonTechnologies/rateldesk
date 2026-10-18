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
    private static Command BuildCapabilitiesCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var command = new Command("capabilities", "Summarize CLI capabilities, output modes, and safe configuration.");
        command.SetHandler(async ctx =>
        {
            var config = LoadConfig(ctx, globals, requireAuth: false);
            var profile = config.File.Merge(config.Overrides);
            var apiVersion = await TryGetApiVersionAsync(runtime, profile, ctx.GetCancellationToken()).ConfigureAwait(false);
            var payload = new JsonObject
            {
                ["cliVersion"] = typeof(HelpdeskCli).Assembly.GetName().Version?.ToString() ?? "unknown",
                ["apiVersion"] = apiVersion,
                ["profile"] = new JsonObject
                {
                    ["apiBaseUrl"] = string.IsNullOrWhiteSpace(profile.ApiBaseUrl) ? null : profile.ApiBaseUrl,
                    ["credentialMode"] = CredentialMode(profile),
                    ["authConfigured"] = HasAuthConfig(profile)
                },
                ["output"] = new JsonObject
                {
                    ["modes"] = new JsonArray("text", "json"),
                    ["views"] = new JsonArray("summary", "detail", "raw", "aggregate")
                },
                ["commandGroups"] = StringArray(BuildSchemaEntries().Select(x => x.Group).Distinct().OrderBy(x => x)),
                ["knownRouteCaveats"] = new JsonArray(
                    "request-forms list requires --service-id",
                    "incidents worklogs is unsupported; use incidents timeline",
                    "connectivity orchestration-test is side-effecting test behavior"),
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
            await WriteIntrospectionAsync(runtime, ctx, globals, "CAPABILITIES", payload).ConfigureAwait(false);
        });
        return command;
    }

    private static Command BuildSchemaCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var group = new Argument<string?>("group") { Description = "Command group.", Arity = ArgumentArity.ZeroOrOne };
        var commandName = new Argument<string?>("command") { Description = "Command name.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("schema", "Inspect deterministic Helpdesk CLI command metadata.") { group, commandName };
        command.SetHandler(ctx =>
        {
            var entries = BuildSchemaEntries();
            var requestedGroup = ctx.ParseResult.GetValueForArgument(group);
            var requestedCommand = ctx.ParseResult.GetValueForArgument(commandName);
            JsonObject payload;
            if (string.IsNullOrWhiteSpace(requestedGroup))
            {
                payload = new JsonObject
                {
                    ["groups"] = entries
                        .GroupBy(x => x.Group)
                        .OrderBy(x => x.Key)
                        .Select(g => new JsonObject
                        {
                            ["name"] = g.Key,
                            ["commands"] = StringArray(g.OrderBy(x => x.Command).Select(x => x.Command))
                        })
                        .ToJsonArray()
                };
            }
            else
            {
                var matches = entries.Where(x => string.Equals(x.Group, requestedGroup, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                {
                    throw new CliValidationException($"Unknown schema group '{requestedGroup}'. Supported groups: {string.Join(", ", entries.Select(x => x.Group).Distinct().OrderBy(x => x))}.");
                }

                if (string.IsNullOrWhiteSpace(requestedCommand))
                {
                    payload = new JsonObject
                    {
                        ["group"] = matches[0].Group,
                        ["commands"] = matches.OrderBy(x => x.Command).Select(SchemaEntryToJson).ToJsonArray()
                    };
                }
                else
                {
                    var entry = matches.FirstOrDefault(x => string.Equals(x.Command, requestedCommand, StringComparison.OrdinalIgnoreCase))
                        ?? throw new CliValidationException($"Unknown schema command '{requestedGroup} {requestedCommand}'. Supported commands: {string.Join(", ", matches.Select(x => x.Command).OrderBy(x => x))}.");
                    payload = SchemaEntryToJson(entry);
                }
            }

            return WriteIntrospectionAsync(runtime, ctx, globals, "SCHEMA", payload);
        });
        return command;
    }

    private static Command BuildEnumsCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var name = new Argument<string?>("name") { Description = "Enum name.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("enums", "List Helpdesk.Shared enum values used by projected CLI output.") { name };
        command.SetHandler(ctx =>
        {
            var enums = BuildEnumRegistry();
            var requested = ctx.ParseResult.GetValueForArgument(name);
            JsonObject payload;
            if (string.IsNullOrWhiteSpace(requested))
            {
                payload = new JsonObject
                {
                    ["enums"] = StringArray(enums.Keys.OrderBy(x => x))
                };
            }
            else
            {
                if (!enums.TryGetValue(requested, out var enumType))
                {
                    throw new CliValidationException($"Unknown enum '{requested}'. Supported enums: {string.Join(", ", enums.Keys.OrderBy(x => x))}.");
                }

                payload = EnumToJson(requested, enumType);
            }

            return WriteIntrospectionAsync(runtime, ctx, globals, "ENUMS", payload);
        });
        return command;
    }

    private static Command BuildExamplesCommand(CliRuntime runtime, GlobalOptions globals)
    {
        var topic = new Argument<string?>("topic") { Description = "Example topic: incident-create, triage, lookups, connectivity.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("examples", "Show short AI/operator workflow recipes.") { topic };
        command.SetHandler(async ctx =>
        {
            var requested = ctx.ParseResult.GetValueForArgument(topic);
            var examples = BuildExampleEntries();
            if (!string.IsNullOrWhiteSpace(requested))
            {
                examples = examples
                    .Where(example => string.Equals(GetString(example, "topic"), requested, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (examples.Count == 0)
                {
                    throw new CliValidationException($"Unknown examples topic '{requested}'. Supported topics: {string.Join(", ", BuildExampleEntries().Select(x => GetString(x, "topic")).Where(x => x is not null).OrderBy(x => x))}.");
                }
            }

            var output = ctx.ParseResult.GetValueForOption(globals.Output)?.Trim().ToLowerInvariant();
            var asJson = ctx.ParseResult.GetValueForOption(globals.Json) || output == "json";
            var payload = new JsonObject { ["examples"] = examples.ToJsonArray() };
            if (asJson)
            {
                await WriteJsonPayloadAsync(runtime, payload, ctx, globals).ConfigureAwait(false);
                return;
            }

            var rows = examples.Select(example => new Dictionary<string, string?>
            {
                ["TOPIC"] = GetString(example, "topic"),
                ["TITLE"] = GetString(example, "title"),
                ["COMMAND"] = GetString(example, "command")
            }).ToList();
            await runtime.Out.WriteLineAsync(RenderTable("EXAMPLES", rows, null)).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task WriteIntrospectionAsync(CliRuntime runtime, InvocationContext ctx, GlobalOptions globals, string title, JsonObject payload)
    {
        var output = ctx.ParseResult.GetValueForOption(globals.Output)?.Trim().ToLowerInvariant();
        var asJson = ctx.ParseResult.GetValueForOption(globals.Json) || output == "json";
        if (asJson)
        {
            await WriteJsonPayloadAsync(runtime, payload, ctx, globals).ConfigureAwait(false);
            return;
        }

        if (payload["groups"] is JsonArray groups)
        {
            var rows = groups.OfType<JsonObject>().Select(group => new Dictionary<string, string?>
            {
                ["GROUP"] = GetString(group, "name"),
                ["COMMANDS"] = string.Join(", ", (group["commands"] as JsonArray)?.Select(x => x?.GetValue<string>()) ?? [])
            }).ToList();
            await runtime.Out.WriteLineAsync(RenderTable(title, rows, null)).ConfigureAwait(false);
            return;
        }

        if (payload["enums"] is JsonArray enumList)
        {
            var rows = enumList.Select(item => new Dictionary<string, string?> { ["ENUM"] = item?.GetValue<string>() }).ToList();
            await runtime.Out.WriteLineAsync(RenderTable(title, rows, null)).ConfigureAwait(false);
            return;
        }

        if (payload["values"] is JsonArray values)
        {
            var rows = values.OfType<JsonObject>().Select(row => new Dictionary<string, string?>
            {
                ["VALUE"] = GetInt(row, "value")?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["NAME"] = GetString(row, "name"),
                ["LABEL"] = GetString(row, "label")
            }).ToList();
            await runtime.Out.WriteLineAsync(RenderTable(title, rows, null)).ConfigureAwait(false);
            return;
        }

        await runtime.Out.WriteLineAsync(RenderDetail(title, payload)).ConfigureAwait(false);
    }

    private static async Task<string?> TryGetApiVersionAsync(CliRuntime runtime, CliConfig profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiBaseUrl))
        {
            return null;
        }

        try
        {
            using var client = runtime.CreateHttpClient(new Uri(profile.ApiBaseUrl));
            using var response = await client.GetAsync("/api/v1/system/version", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var node = JsonNode.Parse(body) as JsonObject;
            return GetString(node, "displayVersion") ?? GetString(node, "version") ?? body;
        }
        catch
        {
            return null;
        }
    }

    private static string CredentialMode(CliConfig profile)
        => string.IsNullOrWhiteSpace(profile.CredentialMode) ? "authentik" : profile.CredentialMode.Trim().ToLowerInvariant();

    private static bool HasAuthConfig(CliConfig profile)
        => CredentialMode(profile) == "integration"
            ? !string.IsNullOrWhiteSpace(profile.IntegrationCredential) || !string.IsNullOrWhiteSpace(profile.IntegrationCredentialFile)
            : !string.IsNullOrWhiteSpace(profile.AuthentikTokenUrl)
              && !string.IsNullOrWhiteSpace(profile.AuthentikClientId)
              && !string.IsNullOrWhiteSpace(profile.AuthentikUsername)
              && (!string.IsNullOrWhiteSpace(profile.AuthentikAppPassword) || !string.IsNullOrWhiteSpace(profile.AuthentikAppPasswordFile));

    private static List<JsonObject> BuildExampleEntries()
        =>
        [
            new JsonObject
            {
                ["topic"] = "incident-create",
                ["title"] = "Create a minimal incident after resolving the requester.",
                ["command"] = "helpdesk incidents create --for-email person@example.com --title \"VPN connection fails\" --description \"Cannot connect after password reset\" --priority High",
                ["notes"] = "Use --dry-run first when an agent is composing a new payload."
            },
            new JsonObject
            {
                ["topic"] = "incident-create",
                ["title"] = "Create repeatable mock incidents from a fixture.",
                ["command"] = "helpdesk incidents bulk-create --body-file incidents.json --dry-run",
                ["notes"] = "The fixture may be a JSON array or an object with an items array."
            },
            new JsonObject
            {
                ["topic"] = "triage",
                ["title"] = "Summarize the active incident queue.",
                ["command"] = "helpdesk incidents summary --active-only --json --pretty",
                ["notes"] = "Use summary for aggregate counts; use list --view summary for compact rows."
            },
            new JsonObject
            {
                ["topic"] = "lookups",
                ["title"] = "Resolve user, customer, and organization context before a create.",
                ["command"] = "helpdesk search customers --query person@example.com --output json --pretty",
                ["notes"] = "Use --for-email on incidents create when exactly one enabled customer should be resolved automatically."
            },
            new JsonObject
            {
                ["topic"] = "connectivity",
                ["title"] = "Inspect External orchestration catalog connectivity safely.",
                ["command"] = "helpdesk connectivity orchestration-jobs --json --view summary --pretty",
                ["notes"] = "Use connectivity orchestration-test only when a remote connectivity probe is intended."
            }
        ];

    private static IReadOnlyList<SchemaEntry> BuildSchemaEntries()
    {
        var standardViews = new[] { "summary", "detail", "raw" };
        return
        [
            new("incidents", "list", "List incidents.", "read-only", "GET", "/api/v1/incidents/", ["--page", "--page-size", "--limit", "--take", "--query", "--requester-email", "--active-only", "--include-total", "--summary-only"], standardViews, "summary", ["state", "priority", "slaStatus"], ["helpdesk incidents list --requester-email person@example.com --json --view summary"]),
            new("incidents", "create", "Create an incident with agent-safe customer email resolution and dry-run support.", "side-effecting", "POST", "/api/v1/incidents/", ["--title", "--subject", "--description", "--priority", "--state", "--customer-id", "--organization-id", "--requester-email", "--for-email", "--customer-email", "--body", "--body-file", "--dry-run", "--validate-only"], ["raw"], "raw", ["priority", "state"], ["helpdesk incidents create --for-email person@example.com --title \"VPN down\" --description \"Cannot connect\" --priority High --dry-run", "helpdesk incidents create --body-file incident.json --validate-only"], ["Required fields: title, description, customerId, organizationId. Use --for-email to resolve customerId and organizationId when a unique enabled customer email exists."]),
            new("incidents", "bulk-create", "Create incidents from a JSON fixture.", "side-effecting", "POST", "/api/v1/incidents/", ["--body-file", "--dry-run", "--validate-only", "--continue-on-error"], ["raw"], "raw", ["priority"], ["helpdesk incidents bulk-create --body-file incidents.json --dry-run"], ["Body file must be a JSON array or an object with an items array."]),
            new("incidents", "update", "Update incident fields.", "side-effecting", "PUT", "/api/v1/incidents/{id}", ["--title", "--subject", "--description", "--state", "--priority", "--assigned-to-id", "--category-ids", "--cc-recipients", "--body", "--body-file"], ["raw"], "raw", ["state", "priority"], ["helpdesk incidents update INC-123 --state InProgress"], ["State values are available from 'helpdesk enums ticket-state'."]),
            new("incidents", "summary", "Aggregate incident triage summary.", "read-only", "GET", "/api/v1/incidents/", ["--page-size", "--max-items", "--active-only"], ["aggregate", "raw"], "aggregate", ["state", "priority"], ["helpdesk incidents summary --json"]),
            new("incidents", "worklogs", "Incident worklog listing is unsupported by the API; use timeline.", "unsupported", "GET", "/api/v1/incidents/{id}/worklogs", [], ["raw"], "raw", [], ["helpdesk incidents timeline INC-123"], ["Command returns an actionable error and does not call this unsupported route."]),
            new("requests", "list", "List service requests.", "read-only", "GET", "/api/v1/requests/", ["--page", "--page-size", "--query", "--active-only", "--include-total"], standardViews, "summary", ["state", "priority", "slaStatus"], ["helpdesk requests list --query vpn"]),
            new("requests", "ticket-timeline", "Read request ticket history and recorded worklog effort.", "read-only", "GET", "/api/v1/requests/{id}/timeline", ["--include-body", "--include-html"], standardViews, "detail", ["timeline-event-type"], ["helpdesk requests ticket-timeline REQ-123 --json"], ["requests timeline continues to read workflow notification history. recordedHours is null when effort is unavailable; hours retains its numeric compatibility value."]),
            new("changes", "list", "List changes.", "read-only", "GET", "/api/v1/changes/", ["--page", "--page-size", "--query", "--active-only", "--include-total"], standardViews, "summary", ["state", "priority", "lifecycleState"], ["helpdesk changes list --json --view summary"]),
            new("request-tasks", "list", "List request tasks.", "read-only", "GET", "/api/v1/request-tasks/", ["--page", "--page-size", "--limit", "--take", "--assigned-to-me", "--status", "--type", "--query", "--include-total"], standardViews, "summary", ["status", "type"], ["helpdesk request-tasks list --assigned-to-me"]),
            new("notifications", "list", "List notifications.", "read-only", "GET", "/api/v1/notifications", ["--page", "--page-size", "--limit", "--take", "--query", "--severity"], standardViews, "summary", ["severity"], ["helpdesk notifications list --json --view summary"]),
            new("organizations", "list", "List organizations with client-side cap metadata.", "read-only", "GET", "/api/v1/organizations/", ["--query", "--enabled-only", "--limit", "--take"], standardViews, "summary", ["state"], ["helpdesk organizations list --limit 20"]),
            new("customers", "list", "List customers with client-side cap metadata.", "read-only", "GET", "/api/v1/customers/", ["--query", "--enabled-only", "--limit", "--take"], standardViews, "summary", ["state"], ["helpdesk customers list --query acme"]),
            new("users", "by-email", "Find a user by email.", "read-only", "GET", "/api/v1/users/by-email/{email}", [], standardViews, "detail", [], ["helpdesk users by-email person@example.com"]),
            new("services", "list", "List services with client-side cap metadata.", "read-only", "GET", "/api/v1/services/", ["--query", "--enabled-only", "--limit", "--take"], standardViews, "summary", [], ["helpdesk services list"]),
            new("services", "search-items", "Search mixed service/request-form items.", "read-only", "GET", "/api/v1/service-items/search", ["--query", "--page-size", "--limit", "--take"], standardViews, "summary", ["itemType", "releaseStatus"], ["helpdesk services search-items --query onboarding --json --view summary"]),
            new("request-forms", "list", "List request forms scoped to one service.", "read-only", "GET", "/api/v1/services/{serviceId}/request-forms", ["--service-id"], standardViews, "summary", ["releaseStatus"], ["helpdesk request-forms list --service-id <service-id>"], ["Global request-form listing is unavailable."]),
            new("categories", "list", "List ticket categories.", "read-only", "GET", "/api/v1/categories/", ["--query", "--enabled-only", "--limit", "--take"], standardViews, "summary", ["type"], ["helpdesk categories list --json --view summary"]),
            new("roles", "list", "List permission-aware role definitions.", "read-only", "GET", "/api/v1/admin/role-definitions/", ["--query", "--limit", "--take"], standardViews, "summary", [], ["helpdesk roles list"]),
            new("connectivity", "orchestration-jobs", "List External orchestration catalog jobs.", "read-only", "GET", "/api/v1/admin/orchestration/catalog/jobs", [], standardViews, "summary", [], ["helpdesk connectivity orchestration-jobs --json --view summary"]),
            new("connectivity", "orchestration-test", "Run External orchestration connectivity test.", "side-effecting", "POST", "/api/v1/admin/orchestration/test", [], ["raw"], "raw", [], ["helpdesk connectivity orchestration-test"], ["This command performs remote connectivity probes."]),
            new("raw", "get", "Operator-safe escape hatch.", "escape-hatch", "GET", "operator allow-list", ["--path"], ["raw"], "raw", [], ["helpdesk raw get --path /api/v1/system/version"])
        ];
    }

    private static JsonObject SchemaEntryToJson(SchemaEntry entry)
    {
        var payload = new JsonObject
        {
            ["commandPath"] = entry.Group + " " + entry.Command,
            ["description"] = entry.Description,
            ["safety"] = entry.Safety,
            ["method"] = entry.Method,
            ["path"] = entry.Path,
            ["options"] = StringArray(entry.Options),
            ["aliases"] = new JsonArray(),
            ["outputViews"] = StringArray(entry.Views),
            ["defaultOutput"] = new JsonObject { ["mode"] = "text", ["view"] = entry.DefaultView },
            ["responseShape"] = new JsonObject(),
            ["enumFields"] = StringArray(entry.EnumFields),
            ["examples"] = StringArray(entry.Examples),
            ["routeCaveats"] = StringArray(entry.RouteCaveats)
        };

        AddSchemaFieldMetadata(payload, entry);
        return payload;
    }

    private static void AddSchemaFieldMetadata(JsonObject payload, SchemaEntry entry)
    {
        if (entry.Group != "incidents" || entry.Command is not ("create" or "update"))
        {
            return;
        }

        var fields = entry.Command == "create" ? IncidentCreateSchemaFields() : IncidentUpdateSchemaFields();
        payload["fields"] = fields.Select(SchemaFieldToJson).ToJsonArray();
        payload["requiredFields"] = StringArray(fields.Where(field => field.Required).Select(field => field.Name));
        payload["optionalFields"] = StringArray(fields.Where(field => !field.Required).Select(field => field.Name));
        payload["validationNotes"] = entry.Command == "create"
            ? new JsonArray(
                "title may be supplied with --title or --subject",
                "customerId and organizationId are required unless --for-email/--customer-email resolves a unique enabled customer",
                "priority and state accept numeric enum values, enum names, or display labels",
                "--dry-run and --validate-only return the resolved request body without creating an incident",
                "ambiguous requester resolution returns JSON details with candidate matches and suggested lookup commands")
            : new JsonArray(
                "only supplied fields are sent",
                "priority and state accept numeric enum values, enum names, or display labels",
                "state transition side effects are API-controlled; use incidents state when only changing state");
        payload["sideEffectNotes"] = entry.Command == "create"
            ? new JsonArray("POST creates an incident unless --dry-run or --validate-only is supplied")
            : new JsonArray("PUT updates the target incident and may trigger API-side workflow behavior");
    }

    private static IReadOnlyList<SchemaField> IncidentCreateSchemaFields()
        =>
        [
            new("title", "--title", "string", true, "Incident title. --subject is accepted as an alias."),
            new("description", "--description", "string", true, "Incident description/body."),
            new("customerId", "--customer-id", "string", true, "Required with organizationId unless --for-email/--customer-email is used."),
            new("organizationId", "--organization-id", "string", true, "Required with customerId unless --for-email/--customer-email is used."),
            new("requesterEmail", "--requester-email", "string", false, "Filled from --for-email when omitted."),
            new("priority", "--priority", "enum", false, "Ticket priority.", "ticket-priority", typeof(TicketPriority)),
            new("state", "--state", "enum", false, "Initial ticket state.", "ticket-state", typeof(TicketState)),
            new("assignedToId", "--assigned-to-id", "string", false, "Assignee user id."),
            new("categoryIds", "--category-ids", "array<string>", false, "JSON array of category ids."),
            new("ccRecipients", "--cc-recipients", "array<string>", false, "JSON array of CC recipient email addresses."),
            new("forEmail", "--for-email", "string", false, "Resolve a unique enabled customer and fill customerId, organizationId, and requesterEmail."),
            new("customerEmail", "--customer-email", "string", false, "Alias for --for-email.")
        ];

    private static IReadOnlyList<SchemaField> IncidentUpdateSchemaFields()
        =>
        [
            new("state", "--state", "enum", false, "Ticket state.", "ticket-state", typeof(TicketState)),
            new("priority", "--priority", "enum", false, "Ticket priority.", "ticket-priority", typeof(TicketPriority)),
            new("assignedToId", "--assigned-to-id", "string", false, "Assignee user id."),
            new("categoryIds", "--category-ids", "array<string>", false, "JSON array of category ids."),
            new("ccRecipients", "--cc-recipients", "array<string>", false, "JSON array of CC recipient email addresses."),
            new("title", "--title", "string", false, "Updated title."),
            new("subject", "--subject", "string", false, "Updated subject."),
            new("description", "--description", "string", false, "Updated description/body.")
        ];

    private static JsonObject SchemaFieldToJson(SchemaField field)
    {
        var payload = new JsonObject
        {
            ["name"] = field.Name,
            ["option"] = field.Option,
            ["required"] = field.Required,
            ["type"] = field.Type,
            ["notes"] = field.Notes
        };
        if (!string.IsNullOrWhiteSpace(field.EnumName) && field.EnumType is not null)
        {
            payload["enum"] = new JsonObject
            {
                ["name"] = field.EnumName,
                ["values"] = EnumValuesToJson(field.EnumType)
            };
        }

        return payload;
    }

    private static JsonArray EnumValuesToJson(Type enumType)
    {
        var values = new JsonArray();
        foreach (var raw in Enum.GetValues(enumType))
        {
            var value = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
            var info = EnumInfo(enumType, value);
            values.Add(new JsonObject
            {
                ["value"] = value,
                ["name"] = info.Name,
                ["label"] = info.Label
            });
        }

        return values;
    }

    private static IReadOnlyDictionary<string, Type> BuildEnumRegistry()
        => new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["ticket-state"] = typeof(TicketState),
            ["ticket-priority"] = typeof(TicketPriority),
            ["timeline-event-type"] = typeof(TimelineEventType),
            ["email-delivery-status"] = typeof(EmailDeliveryStatus),
            ["notification-severity"] = typeof(NotificationSeverity),
            ["support-notification-event-type"] = typeof(SupportNotificationEventType),
            ["support-notification-delivery-status"] = typeof(SupportNotificationDeliveryStatus),
            ["change-lifecycle-state"] = typeof(ChangeLifecycleState),
            ["sla-status"] = typeof(SlaStatus),
            ["request-task-status"] = typeof(RequestTaskStatus),
            ["request-task-type"] = typeof(RequestTaskType),
            ["request-form-release-status"] = typeof(RequestFormReleaseStatus),
            ["ticket-category-type"] = typeof(TicketCategoryType)
        };

    private static JsonObject EnumToJson(string name, Type enumType)
    {
        var values = new JsonArray();
        foreach (var raw in Enum.GetValues(enumType))
        {
            var value = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
            var info = EnumInfo(enumType, value);
            values.Add(new JsonObject
            {
                ["value"] = value,
                ["name"] = info.Name,
                ["label"] = info.Label
            });
        }

        return new JsonObject
        {
            ["name"] = name,
            ["source"] = "Helpdesk.Shared",
            ["values"] = values
        };
    }

}
