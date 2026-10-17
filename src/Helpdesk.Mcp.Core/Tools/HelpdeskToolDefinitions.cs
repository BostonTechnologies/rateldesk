using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Helpdesk.Mcp.Tools;

/// <summary>
/// Builds the transport-neutral MCP tool definitions from the executable catalog.
/// The SDK's reflection discovery cannot express a discriminated request envelope,
/// so this class supplies the JSON Schema seen by every MCP client while retaining
/// the existing <c>operation</c>, <c>request</c>, and <c>confirm</c> wire shape.
/// </summary>
public static class HelpdeskToolDefinitions
{
    private const string EnvelopeGuidance = "Use exactly the outer envelope { operation, request, confirm }. Put every operation-specific field inside request; never place those fields at the tool root.";

    private static readonly IReadOnlySet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "helpdesk_auth", "helpdesk_health", "helpdesk_capabilities", "helpdesk_system", "helpdesk_logs",
        "helpdesk_organizations", "helpdesk_customers", "helpdesk_users", "helpdesk_search", "helpdesk_roles",
        "helpdesk_categories", "helpdesk_services", "helpdesk_request_forms", "helpdesk_self_service",
        "helpdesk_schema", "helpdesk_enums", "helpdesk_examples", "helpdesk_raw"
    };

    private static readonly IReadOnlySet<string> OpenWorldTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "helpdesk_connectivity"
    };

    public static IReadOnlyList<McpServerTool> Create()
        => typeof(HelpdeskTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .Select(Create)
            .ToArray();

    private static McpServerTool Create(MethodInfo method)
    {
        var name = method.Name;
        var readOnly = ReadOnlyTools.Contains(name);
        var definitions = McpOperationCatalog.All.Where(item => item.Tool == name).ToArray();
        var options = new McpServerToolCreateOptions
        {
            Name = name,
            Title = Title(name),
            Description = Description(name, definitions),
            ReadOnly = readOnly,
            Destructive = !readOnly,
            Idempotent = readOnly,
            OpenWorld = OpenWorldTools.Contains(name),
            UseStructuredContent = true
        };

        var tool = McpServerTool.Create(method, context => (context.Services ?? throw new InvalidOperationException("MCP request context has no service provider.")).GetRequiredService<HelpdeskTools>(), options);
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(InputSchema(definitions));
        return tool;
    }

    private static JsonObject InputSchema(IReadOnlyList<McpOperationDefinition> definitions)
    {
        var alternatives = new JsonArray();
        foreach (var definition in definitions)
        {
            var properties = new JsonObject
            {
                ["operation"] = new JsonObject
                {
                    ["const"] = definition.Operation,
                    ["description"] = $"{definition.Description} Select this operation in the outer operation field."
                },
                ["confirm"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = definition.RequiresConfirmation
                        ? "Required as true to perform this mutation. Calling with false performs no upstream change."
                        : "Unused for this read-only operation."
                }
            };

            if (definition.RequestContract != "None")
                properties["request"] = RequestSchema(definition);

            var required = new JsonArray("operation");
            if (definition.RequiresConfirmation)
            {
                properties["confirm"] = new JsonObject
                {
                    ["const"] = true,
                    ["description"] = "Required as true to perform this mutation. Omit it or use false to receive a confirmation preview without an upstream change."
                };
                required.Add("confirm");
            }

            alternatives.Add(new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["required"] = required
            });
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = EnvelopeGuidance,
            ["oneOf"] = alternatives
        };
    }

    private static JsonObject RequestSchema(McpOperationDefinition definition)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var identifier in PathIdentifiers(definition.PathTemplate))
            AddString(properties, required, identifier, $"Helpdesk identifier used by the {definition.Operation} operation.");

        switch (definition.RequestContract)
        {
            case "ListRequest":
            case "TicketOperationRequest":
                AddInteger(properties, "page", "Optional one-based page number.");
                AddInteger(properties, "pageSize", "Optional bounded page size.");
                AddString(properties, null, "query", "Optional bounded search text.");
                break;
            case "SearchRequest":
                AddString(properties, required, "query", "Search text.");
                AddInteger(properties, "limit", "Maximum results; the server caps this value.");
                break;
            case "LogSearchRequest":
                AddString(properties, null, "since", "ISO-8601 lower time bound.");
                AddString(properties, null, "correlationId", "Correlation identifier.");
                AddString(properties, null, "contains", "Redacted diagnostic search text.");
                AddInteger(properties, "limit", "1 through 100; defaults to 50.");
                break;
            case "ConfigKeyRequest":
                AddEnum(properties, required, "key", ["apiBaseUrl", "authentikScope"], "Allowlisted persisted configuration key.");
                break;
            case "ConfigValueRequest":
                AddEnum(properties, required, "key", ["apiBaseUrl", "authentikScope"], "Allowlisted persisted configuration key.");
                AddString(properties, required, "value", "New non-secret value.");
                break;
            case "NotificationMarkReadRequest":
                AddStringArray(properties, required, "ids", "Notification identifiers to mark read.");
                break;
            case "TicketStateRequest":
            case "BulkTicketStateRequest":
                AddEnum(properties, required, "newState", ["New", "WaitingReply", "Replied", "InProgress", "PendingApproval", "OnHold", "Resolved"], "Target ticket state.");
                if (definition.Operation.StartsWith("bulk_", StringComparison.Ordinal)) AddStringArray(properties, required, "ids", "Ticket identifiers.");
                break;
            case "TicketAssignmentRequest":
                AddStringArray(properties, required, "ids", "Ticket identifiers.");
                AddString(properties, required, "assignedToId", "Helpdesk user identifier to assign.");
                break;
            case "ConfiguredAgentAssignmentRequest":
                AddStringArray(properties, required, "ids", "Ticket identifiers.");
                AddString(properties, null, "agentUserEmail", "Optional configured-agent user email override.");
                break;
            case "IncidentWorklogRequest":
            case "TicketWorklogRequest":
                AddNumber(properties, required, "hours", "Non-negative worklog hours; use 0 for a diagnostic note.");
                AddString(properties, required, "notes", "Worklog text.");
                AddBoolean(properties, null, "isInternalNote", "Whether the note is internal; defaults to true where supported.");
                break;
            case "IncidentCloseRequest":
                AddString(properties, required, "closureNote", "Required closure note written before resolution.");
                AddBoolean(properties, null, "isInternalNote", "Whether the closure note is internal; defaults to true.");
                break;
            case "RequestTaskCreateRequest":
                AddString(properties, required, "title", "Short task title.");
                AddString(properties, required, "description", "Task description.");
                AddString(properties, required, "requestId", "Parent Helpdesk request identifier.");
                AddString(properties, required, "organizationId", "Owning organization identifier required by task authorization.");
                AddEnum(properties, null, "priority", ["Low", "Medium", "High", "Critical"], "Optional task priority.");
                AddString(properties, null, "customerId", "Optional customer identifier.");
                AddStringArray(properties, null, "linkedAssetIds", "Optional linked asset identifiers.");
                AddStringArray(properties, null, "attachments", "Optional attachment references.");
                properties["dueDate"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "Optional due date and time." };
                break;
            case "RequestTaskUpdateRequest":
                AddString(properties, null, "title", "Optional replacement task title; it cannot be blank.");
                AddString(properties, null, "description", "Optional replacement task description; it cannot be blank.");
                AddEnum(properties, null, "priority", ["Low", "Medium", "High", "Critical"], "Optional task priority.");
                AddString(properties, null, "assignedToId", "Optional Helpdesk user identifier; use clearAssignment to remove an assignment.");
                AddBoolean(properties, null, "clearAssignment", "Set true to remove the current assignment; cannot be combined with assignedToId.");
                AddStringArray(properties, null, "linkedAssetIds", "Optional complete replacement linked-asset identifiers; an empty array clears the list.");
                AddStringArray(properties, null, "attachments", "Optional complete replacement attachment references; an empty array clears the list.");
                properties["dueDate"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "Optional replacement due date and time; use clearDueDate to remove it." };
                AddBoolean(properties, null, "clearDueDate", "Set true to remove the due date; cannot be combined with dueDate.");
                break;
            case "BulkRequestTaskActionRequest":
                AddStringArray(properties, required, "ids", "Request-task identifiers.");
                break;
            case "TicketCountRequest":
                AddEnum(properties, required, "ticketType", ["incidents", "requests", "changes"], "Ticket collection used to select the route.");
                AddString(properties, required, "ticketId", "Ticket identifier.");
                break;
        }

        if (definition.Tool == "helpdesk_incidents" && definition.Operation == "list")
        {
            const string aliasRule = "Optional incident search text, sent as API q. query and q are aliases: null or blank values leave search unfiltered; equivalent trimmed values deduplicate; conflicting nonempty values fail validation.";
            foreach (var alias in new[] { "query", "q" })
                properties[alias] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = aliasRule };
            foreach (var flag in new[] { "includeTotal", "activeOnly", "historicOnly", "summaryOnly" })
                AddBoolean(properties, null, flag, "Optional incident API list flag; applied with the search before paging.");
            properties["state"] = new JsonObject { ["type"] = new JsonArray("string", "integer"), ["description"] = "Optional incident API state filter; preserves the API's active/historic defaults when absent." };
            AddEnum(properties, null, "view", ["all", "active", "historic"], "Optional incident API list view.");
        }

        if (definition.Operation == "bulk_create")
            AddBulkCreateFields(definition.Tool, properties, required);
        else if (definition.Operation == "create")
            AddCreateFields(definition.Tool, properties, required);
        else if (definition.Operation == "update")
            AddUpdateFields(definition.Tool, properties);

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = !IsClosedRequestContract(definition.RequestContract),
            ["description"] = $"{definition.RequestContract} for {definition.Tool} operation {definition.Operation}. All operation-specific fields belong inside this request object, never at the tool root.",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(value => value!.GetValue<string>())
                .Distinct(StringComparer.Ordinal).Select(value => JsonValue.Create(value)).ToArray())
        };
        if (definition.RequestContract == "RequestTaskUpdateRequest")
        {
            var updateAlternatives = new JsonArray();
            foreach (var field in new[] { "title", "description", "priority", "assignedToId", "linkedAssetIds", "attachments", "dueDate" })
                updateAlternatives.Add(new JsonObject { ["required"] = new JsonArray(field) });
            updateAlternatives.Add(new JsonObject { ["required"] = new JsonArray("clearAssignment"), ["properties"] = new JsonObject { ["clearAssignment"] = new JsonObject { ["const"] = true } } });
            updateAlternatives.Add(new JsonObject { ["required"] = new JsonArray("clearDueDate"), ["properties"] = new JsonObject { ["clearDueDate"] = new JsonObject { ["const"] = true } } });
            schema["anyOf"] = updateAlternatives;
        }
        return schema;
    }

    private static bool IsClosedRequestContract(string requestContract) => requestContract is
        "ListRequest" or "TicketOperationRequest" or "SearchRequest" or "LogSearchRequest" or
        "ConfigKeyRequest" or "ConfigValueRequest" or "NotificationMarkReadRequest" or
        "TicketStateRequest" or "BulkTicketStateRequest" or "TicketAssignmentRequest" or
        "ConfiguredAgentAssignmentRequest" or "IncidentWorklogRequest" or "TicketWorklogRequest" or
        "IncidentCloseRequest" or "TicketCountRequest" or "IncidentRequest" or "RequestRequest" or
        "ChangeRequest" or "RequestTaskRequest" or "RequestTaskCreateRequest" or "RequestTaskUpdateRequest" or "BulkRequestTaskActionRequest" or "NotificationRequest" or "RequestFormRequest" or
        "RequestFormsByServiceRequest" or "OrganizationRequest" or "CustomerRequest" or "UserEmailRequest" or
        "ServiceRequest" or "RoleRequest" or "CategoryRequest" or "IncidentCreateRequest" or
        "IncidentBulkCreateRequest" or "RequestBulkCreateRequest" or "ChangeBulkCreateRequest";

    private static void AddCreateFields(string tool, JsonObject properties, JsonArray required)
    {
        if (tool is "helpdesk_incidents" or "helpdesk_requests" or "helpdesk_changes")
        {
            AddString(properties, required, "title", "Short ticket title. Do not use subject.");
            AddString(properties, required, "description", "Ticket description.");
            AddEnum(properties, null, "priority", ["Low", "Medium", "High", "Critical"], "Ticket priority; defaults to Low.");
        }

        if (tool == "helpdesk_incidents")
        {
            AddString(properties, required, "customerId", "Customer identifier. Required for incident creation; use helpdesk_customers list to find an enabled customer.");
            AddString(properties, required, "organizationId", "Organization identifier that owns customerId. Required for incident creation; it must match the selected customer.");
            AddString(properties, null, "assignedToId", "Optional Helpdesk user identifier.");
            AddStringArray(properties, null, "linkedAssetIds", "Optional linked asset identifiers.");
            AddStringArray(properties, null, "attachments", "Optional attachment references.");
            properties["dueDate"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "Optional due date and time." };
            AddString(properties, null, "impact", "Optional incident impact summary.");
            AddStringArray(properties, null, "ccRecipients", "Optional CC email addresses; maximum 20.");
            properties["requesterEmail"] = new JsonObject { ["type"] = "string", ["format"] = "email", ["description"] = "Optional requester email address." };
            AddStringArray(properties, null, "categoryIds", "Optional category identifiers.");
        }

        if (tool == "helpdesk_requests")
        {
            AddString(properties, required, "customerId", "Enabled customer identifier; required with organizationId.");
            AddString(properties, required, "organizationId", "Organization that owns customerId.");
        }

        if (tool == "helpdesk_changes")
        {
            AddString(properties, required, "organizationId", "Organization that owns the change.");
            AddString(properties, required, "requestedForUserId", "Helpdesk user requesting the change.");
            AddString(properties, required, "implementorUserId", "Helpdesk user implementing the change.");
            properties["implementationStartAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "Required implementation window start." };
            required.Add("implementationStartAt");
            properties["implementationEndAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time", ["description"] = "Required implementation window end after implementationStartAt." };
            required.Add("implementationEndAt");
            AddEnum(properties, required, "changeType", ["Standard", "Normal", "Emergency"], "Change classification.");
            properties["changeTemplate"] = ChangeTemplateSchema();
            required.Add("changeTemplate");
        }
    }

    private static void AddBulkCreateFields(string tool, JsonObject properties, JsonArray required)
    {
        var itemProperties = new JsonObject();
        var itemRequired = new JsonArray();
        AddCreateFields(tool, itemProperties, itemRequired);
        properties["items"] = new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["maxItems"] = 25,
            ["description"] = "One to 25 ticket-create objects with the same contract as create.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = itemProperties,
                ["required"] = itemRequired
            }
        };
        required.Add("items");
    }

    private static JsonObject ChangeTemplateSchema()
    {
        var properties = new JsonObject
        {
            ["scopeOfChange"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            ["affectedSystems"] = StringArraySchema(),
            ["implementationSteps"] = StringArraySchema(),
            ["validationSteps"] = StringArraySchema(),
            ["rollbackPlan"] = new JsonObject { ["type"] = "string" },
            ["rollbackReference"] = new JsonObject { ["type"] = "string" },
            ["riskClassification"] = new JsonObject { ["type"] = "string" },
            ["changeDescription"] = new JsonObject { ["type"] = "string" },
            ["businessJustification"] = new JsonObject { ["type"] = "string" },
            ["impactAssessment"] = new JsonObject { ["type"] = "string" },
            ["riskAssessment"] = new JsonObject { ["type"] = "string" },
            ["preChangeChecks"] = StringArraySchema(),
            ["testingPlan"] = new JsonObject { ["type"] = "string" },
            ["isPreApproved"] = new JsonObject { ["type"] = "boolean" },
            ["existingRunbookReference"] = new JsonObject { ["type"] = "string" },
            ["dependencies"] = OptionalStringArraySchema(),
            ["emergencyReason"] = new JsonObject { ["type"] = "string" },
            ["businessImpactIfNotImplemented"] = new JsonObject { ["type"] = "string" },
            ["immediateRiskAssessment"] = new JsonObject { ["type"] = "string" },
            ["incidentReference"] = new JsonObject { ["type"] = "string" },
            ["postChangeValidation"] = new JsonObject { ["type"] = "string" }
        };
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["description"] = "ITIL change template. All changes require scopeOfChange, affectedSystems, implementationSteps, validationSteps, and rollbackPlan or rollbackReference. Normal also requires businessJustification, impactAssessment, riskAssessment, preChangeChecks, and testingPlan; Standard requires isPreApproved and existingRunbookReference; Emergency requires emergencyReason, businessImpactIfNotImplemented, immediateRiskAssessment, and postChangeValidation.", ["properties"] = properties };
    }

    private static JsonObject StringArraySchema() => new() { ["type"] = "array", ["minItems"] = 1, ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } };

    private static JsonObject OptionalStringArraySchema() => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } };

    private static void AddUpdateFields(string tool, JsonObject properties)
    {
        if (tool is "helpdesk_incidents" or "helpdesk_requests" or "helpdesk_changes")
            AddEnum(properties, null, "priority", ["Low", "Medium", "High", "Critical"], "Updated ticket priority.");
    }

    private static IEnumerable<string> PathIdentifiers(string path)
    {
        var start = 0;
        while (start < path.Length)
        {
            var open = path.IndexOf('{', start);
            if (open < 0) yield break;
            var close = path.IndexOf('}', open + 1);
            if (close < 0) yield break;
            yield return path[(open + 1)..close];
            start = close + 1;
        }
    }

    private static string Title(string tool) => string.Join(' ', tool["helpdesk_".Length..].Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) + " Helpdesk";

    private static string Description(string tool, IReadOnlyList<McpOperationDefinition> definitions)
    {
        var reads = definitions.Where(item => !item.RequiresConfirmation).Select(item => item.Operation).Distinct(StringComparer.Ordinal).ToArray();
        var mutations = definitions.Where(item => item.RequiresConfirmation).Select(item => item.Operation).Distinct(StringComparer.Ordinal).ToArray();
        var readText = reads.Length == 0 ? "" : $" Read operations: {string.Join(", ", reads)}.";
        var mutationText = mutations.Length == 0 ? "" : $" Mutations: {string.Join(", ", mutations)}; every mutation requires confirm=true and a matching operation-specific request.";
        var searchText = tool == "helpdesk_incidents" ? " For list, request.query maps to API q; request.q is an alias. Null or blank search is unfiltered, equivalent trimmed aliases deduplicate, and conflicting nonempty aliases fail validation. The API filters authorized records before counts and paging." : "";
        return $"Use {tool} for its Helpdesk domain only. {EnvelopeGuidance}{ChangeTemplateGuidance(tool)}{WorklogGuidance(tool)}{searchText}{readText}{mutationText}";
    }

    private static string ChangeTemplateGuidance(string tool) => tool == "helpdesk_changes"
        ? " For create, use request.title, request.description, request.organizationId, request.requestedForUserId, request.implementorUserId, request.implementationStartAt, request.implementationEndAt, request.changeType, and request.changeTemplate exactly; never use requesterId, implementorId, or normalTemplate. A Normal change template requires string scopeOfChange, rollbackPlan, businessJustification, impactAssessment, riskAssessment, and testingPlan; affectedSystems, implementationSteps, validationSteps, and preChangeChecks are non-empty string arrays."
        : "";

    private static string WorklogGuidance(string tool) => tool switch
    {
        "helpdesk_incidents" => " For add_worklog, use request.incidentId, request.hours, request.notes, and optional request.isInternalNote.",
        "helpdesk_requests" => " For add_worklog, use request.requestId, request.hours, request.notes, and optional request.isInternalNote.",
        "helpdesk_changes" => " For add_worklog, use request.changeId, request.hours, request.notes, and optional request.isInternalNote.",
        _ => ""
    };

    private static void AddString(JsonObject properties, JsonArray? required, string name, string description)
    {
        properties[name] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = description };
        required?.Add(name);
    }

    private static void AddInteger(JsonObject properties, string name, string description)
        => properties[name] = new JsonObject { ["type"] = "integer", ["description"] = description };

    private static void AddNumber(JsonObject properties, JsonArray? required, string name, string description)
    {
        properties[name] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["description"] = description };
        required?.Add(name);
    }

    private static void AddBoolean(JsonObject properties, JsonArray? required, string name, string description)
    {
        properties[name] = new JsonObject { ["type"] = "boolean", ["description"] = description };
        required?.Add(name);
    }

    private static void AddEnum(JsonObject properties, JsonArray? required, string name, IReadOnlyList<string> values, string description)
    {
        properties[name] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray()), ["description"] = description };
        required?.Add(name);
    }

    private static void AddStringArray(JsonObject properties, JsonArray? required, string name, string description)
    {
        properties[name] = new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }, ["description"] = description };
        required?.Add(name);
    }
}
