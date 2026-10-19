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
        var definitions = McpOperationCatalog.All.Where(item => item.Tool == name).ToArray();
        var readOnly = definitions.All(definition => definition.Effect == "read");
        var options = new McpServerToolCreateOptions
        {
            Name = name,
            Title = Title(name),
            Description = Description(name, definitions),
            ReadOnly = readOnly,
            Destructive = !readOnly,
            Idempotent = readOnly,
            OpenWorld = OpenWorldTools.Contains(name) || OperatorOperationCatalog.All.Any(item => item.Tool == name && item.ExternalIo),
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
                        : definition.Effect == "read" ? "Unused for this read operation." : "This narrowly authorized autonomous write does not require confirmation; existing API authority and invocation binding still apply."
                }
            };

            if (definition.RequestContract != "None")
                properties["request"] = RequestSchema(definition);

            var required = new JsonArray("operation");
            if (definition.RequiresRequest) required.Add("request");

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
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["description"] = EnvelopeGuidance,
            ["oneOf"] = alternatives
        };
    }

    public static JsonObject RequestSchema(McpOperationDefinition definition) => McpRequestSchemas.Create(definition);

    // Runtime and discovery use the same allowed-property tree. Deliberately open
    // form JSON strings are never traversed or constrained as MCP envelope fields.
    public static string? ValidateSupportedFields(string tool, string operation, JsonElement? request)
    {
        var definition = McpOperationCatalog.All.SingleOrDefault(item => item.Tool == tool && item.Operation == operation);
        if (definition is null || (!definition.RequiresRequest && (request is null || request.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))) return null;
        return McpRequestSchemas.ValidateSupportedFields(RequestSchema(definition), request);
    }

    private static string Title(string tool) => string.Join(' ', tool["helpdesk_".Length..].Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) + " Helpdesk";

    private static string Description(string tool, IReadOnlyList<McpOperationDefinition> definitions)
    {
        var reads = definitions.Where(item => item.Effect == "read").Select(item => item.Operation).Distinct(StringComparer.Ordinal).ToArray();
        var mutations = definitions.Where(item => item.Effect != "read").Select(item => item.Operation).Distinct(StringComparer.Ordinal).ToArray();
        var readText = reads.Length == 0 ? "" : $" Read operations: {string.Join(", ", reads)}.";
        var mutationText = mutations.Length == 0 ? "" : $" Write operations: {string.Join(", ", mutations)}. Operations marked confirmationRequired need confirm=true to execute; omitted/false confirmation returns a preview. Autonomous writes retain their existing API authority and prerequisites.";
        var searchText = tool is "helpdesk_incidents" or "helpdesk_requests" or "helpdesk_changes" or "helpdesk_request_tasks" ? " For list, request.query maps to API q; request.q is an alias. Null or blank search is unfiltered, equivalent trimmed aliases deduplicate, and conflicting nonempty aliases fail validation. The API filters authorized records before counts and paging." : "";
        var rawText = tool == "helpdesk_raw" ? " Fixed named allowlisted GET reads only: system_version, get_incident, get_request, get_change. rawEnabled=false means arbitrary raw access is disabled; request cannot select a method, path, host or target. Existing identity and authority restrictions apply." : "";
        return $"Use {tool} for its Helpdesk domain only. {EnvelopeGuidance}{rawText}{ChangeTemplateGuidance(tool)}{WorklogGuidance(tool)}{searchText}{readText}{mutationText}";
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

}
