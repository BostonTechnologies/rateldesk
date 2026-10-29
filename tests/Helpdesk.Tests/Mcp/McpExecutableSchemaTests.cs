using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Text.RegularExpressions;
using Helpdesk.Mcp.Tools;
using Json.Schema;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class McpExecutableSchemaTests
{
    [Fact]
    public void Every_operation_has_schema_valid_positive_preview_and_invalid_examples()
    {
        var definitions = HelpdeskToolDefinitions.Create().ToDictionary(tool => tool.ProtocolTool.Name);
        foreach (var operation in McpOperationCatalog.All)
        {
            var schema = JsonSchema.FromText(definitions[operation.Tool].ProtocolTool.InputSchema.GetRawText());
            var arguments = ArgumentsFromDiscovery(definitions[operation.Tool].ProtocolTool.InputSchema, operation.Operation);
            AssertValid(schema, arguments, operation);
            if (operation.RequiresConfirmation)
            {
                arguments["confirm"] = false;
                AssertValid(schema, arguments, operation);
                arguments.Remove("confirm");
                AssertValid(schema, arguments, operation);
            }
            var wrongOperation = arguments.DeepClone().AsObject(); wrongOperation["operation"] = "not_a_supported_operation";
            Assert.False(schema.Evaluate(wrongOperation).IsValid, $"{operation.Tool}.{operation.Operation} accepts unknown operation");
            if (operation.RequestContract != "None")
            {
                arguments["request"] ??= new JsonObject();
                AssertValid(schema, arguments, operation);
                var requestSchema = HelpdeskToolDefinitions.RequestSchema(operation);
                foreach (var name in requestSchema["required"]!.AsArray().Select(value => value!.GetValue<string>()))
                {
                    var missingField = arguments.DeepClone().AsObject(); missingField["request"]!.AsObject().Remove(name);
                    Assert.False(schema.Evaluate(missingField).IsValid, $"{operation.Tool}.{operation.Operation} accepts missing request.{name}");
                }
                foreach (var field in requestSchema["properties"]!.AsObject().Where(field => field.Value?["enum"] is JsonArray))
                {
                    var invalidEnum = arguments.DeepClone().AsObject(); invalidEnum["request"]![field.Key] = "not_an_enum_value";
                    Assert.False(schema.Evaluate(invalidEnum).IsValid, $"{operation.Tool}.{operation.Operation} accepts invalid request.{field.Key}");
                }
                var missingRequest = arguments.DeepClone().AsObject(); missingRequest.Remove("request");
                Assert.Equal(!operation.RequiresRequest, schema.Evaluate(missingRequest).IsValid);
                var wrongType = arguments.DeepClone().AsObject(); wrongType["request"] = 123;
                Assert.False(schema.Evaluate(wrongType).IsValid, $"{operation.Tool}.{operation.Operation} accepts nonobject request");
                var unknown = arguments.DeepClone().AsObject(); unknown["request"]!["unsupportedField"] = true;
                Assert.False(schema.Evaluate(unknown).IsValid, $"{operation.Tool}.{operation.Operation} accepts unsupported field");
            }
        }
    }

    [Fact]
    public void Catalog_export_is_available_for_review_and_fresh_campaign_baselines()
    {
        Assert.NotEmpty(McpOperationCatalog.All);
        Assert.Equal(McpOperationCatalog.All.Count, McpOperationCatalog.All.Select(item => (item.Tool, item.Operation)).Distinct().Count());
        Assert.Matches("^[0-9a-f]{64}$", McpOperationCatalog.CatalogHash);
        var directory = Environment.GetEnvironmentVariable("RATELDESK_CATALOG_EXPORT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var operations = McpOperationCatalog.ToJson();
        var exampleRows = operations.DeepClone().AsArray();
        var schemas = HelpdeskToolDefinitions.Create().ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema);
        foreach (var row in exampleRows.OfType<JsonObject>())
        {
            var arguments = ArgumentsFromDiscovery(schemas[row["tool"]!.ToString()], row["operation"]!.ToString());
            var examples = new JsonObject { ["schemaValidConfirmed"] = arguments.DeepClone() };
            if (row["confirmationRequired"]!.GetValue<bool>())
            {
                var preview = arguments.DeepClone().AsObject(); preview["confirm"] = false; examples["schemaValidPreview"] = preview;
            }
            var invalid = arguments.DeepClone().AsObject(); invalid["operation"] = "not_a_supported_operation";
            examples["schemaInvalid"] = invalid;
            examples["scope"] = "Synthetic schema construction only. Record existence, authority, effects and live prerequisites require independent fixtures and readback.";
            row["examples"] = examples;
        }
        var toolSchemas = new JsonArray(HelpdeskToolDefinitions.Create().Select(tool => (JsonNode)new JsonObject
        {
            ["tool"] = tool.ProtocolTool.Name, ["inputSchema"] = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())
        }).ToArray());
        var schemaHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(toolSchemas.ToJsonString()))).ToLowerInvariant();
        var export = new JsonObject
        {
            ["schemaHash"] = schemaHash,
            ["catalogRevision"] = McpOperationCatalog.Revision, ["catalogHash"] = McpOperationCatalog.CatalogHash,
            ["operationCount"] = operations.Count, ["operations"] = operations,
            ["schemaExamples"] = new JsonArray(exampleRows.OfType<JsonObject>().Select(row => (JsonNode)new JsonObject { ["tool"] = row["tool"]!.DeepClone(), ["operation"] = row["operation"]!.DeepClone(), ["examples"] = row["examples"]!.DeepClone() }).ToArray()),
            ["toolSchemas"] = toolSchemas
        };
        File.WriteAllText(Path.Combine(directory, "mcp-operation-catalog.json"), export.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void Roles_require_enabled_owner_identifier_and_permission_dependencies_in_discovery()
    {
        var schema = Schema("helpdesk_admin_mutations");
        var request = new JsonObject { ["operation"] = "create_role", ["request"] = new JsonObject { ["name"] = "Owned role", ["ownerOrganizationId"] = "org-a", ["permissions"] = new JsonArray("Incident.Read", "Incident.Write") } };
        Assert.True(schema.Evaluate(request).IsValid);
        var payload = request["request"]!.AsObject();
        payload.Remove("ownerOrganizationId"); Assert.False(schema.Evaluate(request).IsValid);
        payload["ownerOrganizationId"] = null; Assert.False(schema.Evaluate(request).IsValid);
        payload["ownerOrganizationId"] = " "; Assert.False(schema.Evaluate(request).IsValid);
        payload["ownerOrganizationId"] = "org-a";
        payload["permissions"] = new JsonArray("Incident.Write"); Assert.False(schema.Evaluate(request).IsValid);
        payload["permissions"] = new JsonArray("Incident.Manager"); Assert.False(schema.Evaluate(request).IsValid);
        payload["permissions"] = new JsonArray("Incident.Manager", "Incident.User"); Assert.True(schema.Evaluate(request).IsValid);
    }

    [Fact]
    public void Log_search_requires_a_filter_and_exposes_a_numeric_sequence_cursor()
    {
        var schema = Schema("helpdesk_logs");
        var request = new JsonObject { ["operation"] = "search", ["request"] = new JsonObject() };
        Assert.False(schema.Evaluate(request).IsValid);
        request["request"]!["since"] = 42; Assert.True(schema.Evaluate(request).IsValid);
        request["request"]!["since"] = "2026-10-17T00:00:00Z"; Assert.False(schema.Evaluate(request).IsValid);
        request["request"]!.AsObject().Remove("since"); request["request"]!["correlationId"] = "trace-1";
        Assert.True(schema.Evaluate(request).IsValid);
        request["request"]!["correlationId"] = ""; Assert.False(schema.Evaluate(request).IsValid);
        request["request"]!["correlationId"] = " "; Assert.False(schema.Evaluate(request).IsValid);
    }

    [Fact]
    public void Authority_metadata_distinguishes_delete_approval_and_specific_tool_boundaries()
    {
        var rows = McpOperationCatalog.ToJson().OfType<JsonObject>().ToArray();
        string Authority(string tool, string operation) => rows.Single(row => row["tool"]!.ToString() == tool && row["operation"]!.ToString() == operation)["authority"]!.ToString();
        foreach (var (tool, family) in new[] { ("helpdesk_incidents", "Incident"), ("helpdesk_requests", "Request"), ("helpdesk_changes", "Change") })
            Assert.Contains(family + ".Delete", Authority(tool, "delete"));
        Assert.Contains("Change.Approve or Change.Manager", Authority("helpdesk_changes", "lifecycle"));
        Assert.Contains("HelpdeskAdmin", Authority("helpdesk_search", "users"));
        Assert.Contains("personal notices must belong", Authority("helpdesk_notifications", "mark_read"));
        Assert.Contains("unread Error/Critical", Authority("helpdesk_notifications", "unread_errors"));
        Assert.Contains("TicketReadAccess", Authority("helpdesk_categories", "list"));
        Assert.Contains("SelfService.User", Authority("helpdesk_services", "items"));
        Assert.Contains("SelfService.User", Authority("helpdesk_request_forms", "get"));
        Assert.Contains("public", Authority("helpdesk_raw", "system_version"));
        Assert.Contains("Incident.Read", Authority("helpdesk_raw", "get_incident"));
        Assert.Contains("CanViewIncident", Authority("helpdesk_tickets", "mark_as_seen"));
        Assert.Contains("no business API authority", Authority("helpdesk_schema", "list"));
    }

    [Fact]
    public void Arrays_clearing_and_mutually_exclusive_task_edits_are_machine_checkable()
    {
        var schema = Schema("helpdesk_request_tasks");
        var request = new JsonObject { ["operation"] = "update", ["request"] = new JsonObject { ["taskId"] = "task-1", ["attachments"] = new JsonArray(), ["linkedAssetIds"] = new JsonArray() } };
        Assert.True(schema.Evaluate(request).IsValid);
        request["request"]!["clearAssignment"] = true; request["request"]!["assignedToId"] = "user-1";
        Assert.False(schema.Evaluate(request).IsValid);
        request["request"]!.AsObject().Remove("assignedToId"); request["request"]!["clearDueDate"] = true; request["request"]!["dueDate"] = "2027-01-01T00:00:00Z";
        Assert.False(schema.Evaluate(request).IsValid);
    }

    [Fact]
    public void Change_updates_advertise_incomplete_drafts_and_reject_numeric_type_strings()
    {
        var schema = Schema("helpdesk_changes");
        var arguments = new JsonObject
        {
            ["operation"] = "update",
            ["request"] = new JsonObject { ["changeId"] = "change-one", ["changeType"] = "Normal", ["changeTemplate"] = new JsonObject { ["preChangeChecks"] = new JsonArray() } }
        };
        Assert.True(schema.Evaluate(arguments).IsValid);
        arguments["request"]!["changeType"] = "2";
        Assert.False(schema.Evaluate(arguments).IsValid);
    }

    [Fact]
    public void Incident_state_discovery_preserves_defined_numeric_string_compatibility()
    {
        var schema = Schema("helpdesk_incidents");
        var arguments = new JsonObject { ["operation"] = "state", ["request"] = new JsonObject { ["incidentId"] = "incident-one", ["newState"] = "2" } };
        Assert.True(schema.Evaluate(arguments).IsValid);
        arguments["request"]!["newState"] = "999";
        Assert.False(schema.Evaluate(arguments).IsValid);
    }

    [Fact]
    public void Notification_and_category_identifiers_expose_the_actual_uuid_contract()
    {
        var options = new EvaluationOptions { RequireFormatValidation = true };
        var notifications = Schema("helpdesk_notifications");
        var arguments = new JsonObject { ["operation"] = "mark_read", ["request"] = new JsonObject { ["ids"] = new JsonArray("00000000-0000-0000-0000-000000000001") } };
        Assert.True(notifications.Evaluate(arguments, options).IsValid);
        arguments["request"]!["ids"] = new JsonArray("not-a-guid");
        Assert.False(notifications.Evaluate(arguments, options).IsValid);
        var category = McpOperationCatalog.All.Single(item => item.Operation == "update_category");
        Assert.Equal("uuid", HelpdeskToolDefinitions.RequestSchema(category)["properties"]!["categoryId"]!["format"]!.ToString());
        var role = McpOperationCatalog.All.Single(item => item.Operation == "update_role");
        Assert.Null(HelpdeskToolDefinitions.RequestSchema(role)["properties"]!["roleId"]!["format"]);
    }

    [Theory]
    [InlineData("Standard", "existingRunbookReference")]
    [InlineData("Normal", "businessJustification")]
    [InlineData("Emergency", "emergencyReason")]
    public void Type_specific_change_template_fields_are_required(string changeType, string removed)
    {
        var tool = HelpdeskToolDefinitions.Create().Single(tool => tool.ProtocolTool.Name == "helpdesk_changes");
        var arguments = ArgumentsFromDiscovery(tool.ProtocolTool.InputSchema, "create");
        var request = arguments["request"]!.AsObject(); request["changeType"] = changeType;
        var template = request["changeTemplate"]!.AsObject();
        foreach (var field in new[] { "existingRunbookReference", "businessJustification", "impactAssessment", "riskAssessment", "testingPlan", "emergencyReason", "businessImpactIfNotImplemented", "immediateRiskAssessment", "postChangeValidation" }) template[field] = "Example";
        template["isPreApproved"] = true;
        template["preChangeChecks"] = changeType == "Normal" ? new JsonArray("Example") : new JsonArray();
        var schema = JsonSchema.FromText(tool.ProtocolTool.InputSchema.GetRawText());
        Assert.True(schema.Evaluate(arguments).IsValid);
        if (changeType == "Normal")
        {
            template["preChangeChecks"] = new JsonArray(); Assert.False(schema.Evaluate(arguments).IsValid);
            template["preChangeChecks"] = new JsonArray("Example");
        }
        template.Remove(removed);
        Assert.False(schema.Evaluate(arguments).IsValid);
    }

    [Fact]
    public void Effects_authority_raw_access_and_identifier_metadata_are_explicit()
    {
        var rows = McpOperationCatalog.ToJson().Select(row => row!.AsObject()).ToArray();
        var ai = rows.Single(row => row["tool"]!.ToString() == "helpdesk_ai_assistant" && row["operation"]!.ToString() == "report");
        Assert.Equal("write", ai["effect"]!.ToString()); Assert.False(ai["confirmationRequired"]!.GetValue<bool>());
        Assert.All(rows.Where(row => row["tool"]!.ToString() == "helpdesk_ai_assistant" &&
            row["operation"]!.ToString() != "report" && row["effect"]!.ToString() != "read"),
            row => Assert.True(row["confirmationRequired"]!.GetValue<bool>()));
        var tool = HelpdeskToolDefinitions.Create().Single(tool => tool.ProtocolTool.Name == "helpdesk_ai_assistant");
        Assert.DoesNotContain("Read operations: report", tool.ProtocolTool.Description);
        Assert.Contains("Write operations: report", tool.ProtocolTool.Description);
        var branding = rows.Single(row => row["operation"]!.ToString() == "update_organization_branding");
        Assert.Contains("tenantId", branding["identifiers"]!.ToJsonString());
        Assert.Contains("integer", branding["identifiers"]!.ToJsonString());
        Assert.Equal(4, rows.Count(row => row["tool"]!.ToString() == "helpdesk_raw"));
        Assert.All(rows, row => { Assert.NotEmpty(row["transports"]!.AsArray()); Assert.NotEmpty(row["authority"]!.ToString()); Assert.NotNull(row["prerequisites"]); });
        Assert.Equal(64, McpOperationCatalog.CatalogHash.Length);
    }

    [Theory]
    [InlineData("helpdesk_incidents", "incidentId")]
    [InlineData("helpdesk_requests", "requestId")]
    [InlineData("helpdesk_changes", "changeId")]
    public void Strict_update_fields_share_discovery_and_runtime_contract(string tool, string identifier)
    {
        using var mixed = JsonDocument.Parse($$"""{"{{identifier}}":"ticket-1","priority":"High","title":"Ignored before this fix"}""");
        Assert.Equal("request.title is not supported for this operation.", HelpdeskToolDefinitions.ValidateSupportedFields(tool, "update", mixed.RootElement));
        using var supported = JsonDocument.Parse($$"""{"{{identifier}}":"ticket-1","priority":"High","categoryIds":[]}""");
        Assert.Null(HelpdeskToolDefinitions.ValidateSupportedFields(tool, "update", supported.RootElement));
    }

    internal static JsonObject ArgumentsFromDiscovery(JsonElement schema, string operation)
    {
        var alternative = schema.GetProperty("oneOf").EnumerateArray().Single(item => item.GetProperty("properties").GetProperty("operation").GetProperty("const").GetString() == operation);
        var arguments = Generate(JsonNode.Parse(alternative.GetRawText())!.AsObject()).AsObject();
        arguments["operation"] = operation; arguments["confirm"] = true;
        return arguments;
    }

    // This client-side constructor reads JSON Schema alone; no DTO/route field list.
    private static JsonNode Generate(JsonObject schema)
    {
        if (schema["const"] is { } constant) return constant.DeepClone();
        if (schema["enum"] is JsonArray values) return values.First(value => value is not null)!.DeepClone();
        if (schema["oneOf"] is JsonArray oneOf) return Generate(oneOf[0]!.AsObject());
        var type = schema["type"] is JsonArray types ? types[0]!.GetValue<string>() : schema["type"]?.GetValue<string>();
        if (type == "array")
        {
            var array = new JsonArray(); var count = schema["minItems"]?.GetValue<int>() ?? 0;
            for (var i = 0; i < count; i++) array.Add(Generate(schema["items"]!.AsObject()));
            return array;
        }
        if (type == "boolean") return JsonValue.Create(true)!;
        if (type is "integer" or "number")
        {
            if (schema["minimum"] is not { } lower || lower.ToString().StartsWith('-')) return JsonValue.Create(1)!;
            return lower.DeepClone();
        }
        if (type == "string")
        {
            if (schema["examples"] is JsonArray examples && examples.Count > 0) return examples[0]!.DeepClone();
            var example = schema["format"]?.ToString() switch
            {
                "uuid" => "00000000-0000-0000-0000-000000000001", "date-time" => "2027-01-01T00:00:00Z",
                "date" => "2027-01-01", "time-span" or "time-local" => "09:00:00", "email" => "operator@example.test", _ => "Example"
            };
            var minimumLength = schema["minLength"]?.GetValue<int>() ?? 0;
            if (example.Length < minimumLength) example = example.PadRight(minimumLength, 'a');
            if (schema["pattern"] is { } pattern && !Regex.IsMatch(example, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var alternatives = Regex.Match(pattern.ToString(), @"^\^\(([A-Za-z0-9_]+(?:\|[A-Za-z0-9_]+)+)\)\$$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (alternatives.Success) example = alternatives.Groups[1].Value.Split('|')[0];
                else example = new[] { "Example123", "operator@example.test", "2027-01-01", "09:00:00" }
                    .FirstOrDefault(candidate => candidate.Length >= minimumLength &&
                        candidate.Length <= (schema["maxLength"]?.GetValue<int>() ?? int.MaxValue) &&
                        Regex.IsMatch(candidate, pattern.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    ?? throw new InvalidOperationException($"Add a schema-derived example for the pattern {pattern}.");
            }
            return JsonValue.Create(example)!;
        }
        var result = new JsonObject();
        Fill(result, schema);
        return result;
    }

    private static void Fill(JsonObject target, JsonObject schema, JsonObject? inheritedProperties = null)
    {
        var properties = schema["properties"] as JsonObject ?? inheritedProperties;
        foreach (var name in (schema["required"] as JsonArray ?? []).Select(value => value!.GetValue<string>()))
            if (!target.ContainsKey(name) && (properties?[name] ?? inheritedProperties?[name]) is JsonObject field) target[name] = Generate(field);
        if (schema["anyOf"] is JsonArray anyOf) Fill(target, anyOf[0]!.AsObject(), properties);
        if (schema["allOf"] is JsonArray allOf)
            foreach (var condition in allOf.OfType<JsonObject>())
            {
                if (condition["if"] is JsonObject test && condition["then"] is JsonObject then)
                {
                    var matches = (test["properties"] as JsonObject ?? []).All(entry => entry.Value?["const"] is not { } value || JsonNode.DeepEquals(value, target[entry.Key]));
                    if (matches && then["properties"] is JsonObject nested)
                        foreach (var entry in nested) if (target[entry.Key] is JsonObject nestedTarget) Fill(nestedTarget, entry.Value!.AsObject());
                }
            }
    }

    private static JsonSchema Schema(string tool) => JsonSchema.FromText(HelpdeskToolDefinitions.Create().Single(item => item.ProtocolTool.Name == tool).ProtocolTool.InputSchema.GetRawText());
    private static void AssertValid(JsonSchema schema, JsonObject arguments, McpOperationDefinition operation)
    {
        var result = schema.Evaluate(arguments, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, $"{operation.Tool}.{operation.Operation}: {arguments.ToJsonString()}\n{JsonSerializer.Serialize(result)}");
    }
}
