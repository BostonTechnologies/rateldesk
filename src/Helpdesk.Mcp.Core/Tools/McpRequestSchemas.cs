using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.DTOs.Category;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;

namespace Helpdesk.Mcp.Tools;

/// <summary>API DTO properties plus the deliberately narrower MCP adapter contracts.</summary>
internal static class McpRequestSchemas
{
    public static JsonObject Create(McpOperationDefinition definition)
    {
        var schema = Object();
        schema["description"] = $"{definition.RequestContract} for {definition.Tool} operation {definition.Operation}. All operation-specific fields belong inside this request object, never at the tool root.";
        var fields = (JsonObject)schema["properties"]!;
        var required = (JsonArray)schema["required"]!;
        foreach (var name in Identifiers(definition.PathTemplate).Distinct())
            Field(fields, required, name, name == "tenantId" ? new JsonObject { ["type"] = "integer", ["minimum"] = 1 } : name is "notificationId" or "categoryId" ? TypeSchema(typeof(Guid)) : Text(), true);
        var op = definition.Operation;
        var tool = definition.Tool;

        if (op == "list" || definition.RequestContract == "ListRequest") AddList(fields, tool, op);
        switch (definition.RequestContract)
        {
            case "SearchRequest":
                if (tool == "helpdesk_services" && op == "search_items")
                {
                    fields["query"] = Text(); fields["q"] = Text(); fields["pageSize"] = Integer(1, 25); fields["includeTotal"] = Boolean();
                    schema["anyOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("query") }, new JsonObject { ["required"] = new JsonArray("q") });
                }
                else
                {
                    fields["query"] = Text(); fields["q"] = Text(); fields["limit"] = Integer(1, 25); fields["pageSize"] = Integer(1, 25);
                    fields["limit"]!["description"] = "Alias of pageSize; bounded result count, maximum25. TotalCount describes returned bounded rows, not full filtered dataset.";
                    schema["anyOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("query") }, new JsonObject { ["required"] = new JsonArray("q") });
                }
                break;
            case "LogSearchRequest":
                fields["since"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = long.MaxValue, ["description"] = "Sequence cursor: return records whose numeric id is greater than since. This is not a timestamp." };
                fields["correlationId"] = Text(); fields["correlationId"]!["pattern"] = "\\S"; fields["correlationId"]!["description"] = "Exact stored Activity TraceId or SpanId selector; an application X-Correlation-Id is a different identifier.";
                fields["contains"] = Text(); fields["contains"]!["pattern"] = "\\S";
                fields["level"] = Named("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None");
                fields["limit"] = Integer(1, 100);
                schema["anyOf"] = new JsonArray(new[] { "since", "correlationId", "contains" }.Select(name => (JsonNode)new JsonObject { ["required"] = new JsonArray(name) }).ToArray());
                break;
            case "ConfigKeyRequest": case "ConfigValueRequest":
                Field(fields, required, "key", Named("apiBaseUrl", "authentikScope"), true);
                if (op == "set") Field(fields, required, "value", Text(), true);
                break;
            case "NotificationMarkReadRequest":
                Field(fields, required, "ids", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = TypeSchema(typeof(Guid)) }, true); break;
            case "BulkRequestTaskActionRequest":
                Field(fields, required, "ids", Strings(1), true); break;
            case "TicketStateRequest": case "BulkTicketStateRequest":
                Field(fields, required, "newState", EnumSchema(typeof(TicketState), names: true), true);
                fields["comment"] = Text(false);
                if (op == "bulk_state") Field(fields, required, "ids", Strings(1), true);
                break;
            case "TicketAssignmentRequest": case "ConfiguredAgentAssignmentRequest":
                Field(fields, required, "ids", Strings(1), true);
                if (op == "assign") fields["assignedToId"] = Nullable(Text());
                else fields["agentUserEmail"] = Text();
                break;
            case "IncidentWorklogRequest": case "TicketWorklogRequest":
                Field(fields, required, "hours", new JsonObject { ["type"] = "number", ["minimum"] = 0 }, true);
                Field(fields, required, "notes", Text(), true); fields["isInternalNote"] = Boolean(); break;
            case "IncidentCloseRequest":
                Field(fields, required, "closureNote", Text(), true); fields["isInternalNote"] = Boolean(); break;
            case "TicketCountRequest":
                fields["ticketType"] = Named("incidents", "requests", "changes"); break;
            case "ChangeLifecycleRequest":
                Field(fields, required, "lifecycleState", EnumSchema(typeof(ChangeLifecycleState), names: true), true); fields["comment"] = Text(false); break;
            case "AiAssistantWorklogRequest":
                foreach (var name in new[] { "eventId", "ticketId", "correlationId", "message" }) Field(fields, required, name, Text(), true);
                fields["invocationId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
                Field(fields, required, "ticketType", Named("incidents", "requests", "changes"), true);
                Field(fields, required, "status", Named(Enum.GetNames<AiInvestigationStatus>()), true);
                foreach (var name in new[] { "severity", "metadataJson", "artifactReferencesJson", "runReference" }) fields[name] = Nullable(Text(false));
                break;
            case "RequestTaskCreateRequest":
                foreach (var name in new[] { "title", "description", "requestId", "organizationId" }) Field(fields, required, name, Text(), true);
                fields["customerId"] = Nullable(Text()); fields["priority"] = Named(Enum.GetNames<TicketPriority>());
                fields["linkedAssetIds"] = Strings(); fields["attachments"] = Strings(); fields["dueDate"] = Date(); break;
            case "RequestTaskUpdateRequest":
                foreach (var name in new[] { "title", "description", "assignedToId" }) fields[name] = Text();
                fields["priority"] = Named(Enum.GetNames<TicketPriority>());
                fields["linkedAssetIds"] = Strings(); fields["attachments"] = Strings(); fields["dueDate"] = Date();
                fields["clearAssignment"] = Boolean(); fields["clearDueDate"] = Boolean();
                schema["anyOf"] = new JsonArray(new[] { "title", "description", "priority", "assignedToId", "linkedAssetIds", "attachments", "dueDate", "clearAssignment", "clearDueDate" }.Select(name => (JsonNode)new JsonObject
                {
                    ["required"] = new JsonArray(name),
                    ["properties"] = name.StartsWith("clear", StringComparison.Ordinal) ? new JsonObject { [name] = new JsonObject { ["const"] = true } } : new JsonObject()
                }).ToArray());
                schema["allOf"] = new JsonArray(ExclusiveClear("clearAssignment", "assignedToId"), ExclusiveClear("clearDueDate", "dueDate"));
                break;
        }

        if (tool is "helpdesk_incidents" or "helpdesk_requests" or "helpdesk_changes")
        {
            var body = op switch
            {
                "create" or "bulk_create" => tool switch { "helpdesk_incidents" => typeof(CreateIncidentDto), "helpdesk_requests" => typeof(CreateRequestDto), _ => typeof(CreateChangeDto) },
                "update" => tool switch { "helpdesk_incidents" => typeof(UpdateIncidentDto), "helpdesk_requests" => typeof(UpdateRequestDto), _ => typeof(UpdateChangeDto) },
                _ => null
            };
            if (body is not null)
            {
                var target = op == "bulk_create" ? Object() : schema;
                AddDto(target, body);
                var targetFields = (JsonObject)target["properties"]!;
                var targetRequired = (JsonArray)target["required"]!;
                targetFields["priority"] = Named(Enum.GetNames<TicketPriority>());
                if (targetFields.ContainsKey("state")) targetFields["state"] = Nullable(EnumSchema(typeof(TicketState), names: true));
                if (targetFields.ContainsKey("lifecycleState")) targetFields["lifecycleState"] = Nullable(EnumSchema(typeof(ChangeLifecycleState), names: true));
                if (op is "create" or "bulk_create")
                {
                    foreach (var name in new[] { "title", "description", "organizationId" }) Require(targetRequired, name);
                    if (tool != "helpdesk_changes") Require(targetRequired, "customerId");
                    else
                    {
                        foreach (var name in new[] { "requestedForUserId", "implementorUserId", "implementationStartAt", "implementationEndAt", "changeType", "changeTemplate" }) Require(targetRequired, name);
                        targetFields["changeType"] = Named("Standard", "Normal", "Emergency");
                        targetFields["changeTemplate"] = ChangeTemplate();
                        target["allOf"] = ChangeConditions();
                    }
                    foreach (var name in targetRequired.Select(node => node!.GetValue<string>()).ToArray())
                        if (targetFields[name] is JsonObject field)
                        {
                            RemoveNull(field);
                            if (field["type"]?.ToString() == "string") field["minLength"] = 1;
                        }
                }
                else if (tool == "helpdesk_changes")
                {
                    targetFields["changeTemplate"] = Nullable(ChangeTemplate());
                    targetFields["changeType"] = Nullable(Named("Standard", "Normal", "Emergency"));
                    target["allOf"] = ChangeConditions();
                }
                if (op == "bulk_create") Field(fields, required, "items", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 25, ["items"] = target }, true);
            }
        }
        if (tool == "helpdesk_admin_mutations") AddAdmin(schema, op);
        foreach (var name in required.Select(node => node!.GetValue<string>()))
            if (fields[name] is JsonObject field)
            {
                RemoveNull(field);
                if (field["type"]?.ToString() == "string") field["minLength"] = 1;
            }
        return schema;
    }

    public static string? ValidateSupportedFields(JsonObject schema, JsonElement? value, string path = "request")
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return $"{path} must be an object.";
        var element = value.Value;
        if (element.ValueKind != JsonValueKind.Object) return $"{path} must be an object.";
        var properties = (JsonObject)schema["properties"]!;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return $"{path}.{property.Name} is duplicated.";
            if (properties[property.Name] is not JsonObject field) return $"{path}.{property.Name} is not supported for this operation.";
            if (property.Value.ValueKind == JsonValueKind.Object && field["properties"] is JsonObject && field["additionalProperties"]?.GetValue<bool>() == false)
            {
                var error = ValidateSupportedFields(field, property.Value, $"{path}.{property.Name}");
                if (error is not null) return error;
            }
        }
        return null;
    }

    private static void AddAdmin(JsonObject schema, string operation)
    {
        var fields = (JsonObject)schema["properties"]!;
        var required = (JsonArray)schema["required"]!;
        if (operation.StartsWith("delete_", StringComparison.Ordinal) || operation.StartsWith("customer_", StringComparison.Ordinal) || operation == "provision_user") return;
        var type = operation switch
        {
            "create_organization" or "update_organization" => typeof(Organization),
            "create_customer" or "update_customer" => typeof(Customer),
            "create_user" => typeof(CreateUserRequest), "update_user" => typeof(UpdateUserRequest),
            "create_category" => typeof(CreateTicketCategoryDto), "update_category" => typeof(UpdateTicketCategoryDto),
            "create_service" => typeof(CreateServiceDto), "update_service" => typeof(UpdateServiceDto),
            "create_request_form" or "create_service_request_form" => typeof(CreateRequestFormDto), "update_request_form" => typeof(UpdateRequestFormDto),
            "create_self_service_request" => typeof(SubmitSelfServiceRequestDto),
            _ => null
        };
        if (type is not null) AddDto(schema, type);
        if (operation is "create_role" or "update_role")
        {
            Field(fields, required, "name", Text(), true);
            Field(fields, required, "permissions", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["uniqueItems"] = true, ["items"] = Named(Helpdesk.Shared.Auth.HelpdeskPermissions.AssignablePermissions) }, true);
            schema["allOf"] = RolePermissionConditions();
            if (operation == "create_role")
            {
                fields["key"] = Nullable(Text(false));
                fields["key"]!["description"] = "Optional custom. key using letters, numbers, periods or hyphens, at most 128 characters after trimming; omitted/blank generates a key from name.";
                Field(fields, required, "ownerOrganizationId", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["pattern"] = "\\S", ["description"] = "Required enabled owner organization; scoped role authority and delegable permission intersection apply." }, true);
            }
        }
        if (operation == "update_organization_branding")
            foreach (var name in new[] { "brandName", "logoUrl", "footerHtml", "primaryColor", "fromName", "replyTo" }) fields[name] = Nullable(Text(false));
        if (operation is "create_organization" or "update_organization" or "create_customer" or "update_customer" or "create_user" or "update_user" or "create_category" or "update_category" or "create_service" or "update_service")
            Require(required, "name");
        if (operation is "create_customer" or "update_customer") { Require(required, "email"); Require(required, "organizationId"); }
        if (operation is "create_user" or "update_user") { Require(required, "email"); Require(required, "role"); }
        if (operation == "create_self_service_request")
        {
            Require(required, "requestFormId");
            schema["dependentRequired"] = new JsonObject { ["requestedForPersonId"] = new JsonArray("requestedForPersonSource") };
        }
        fields.Remove("password");
        if (operation == "create_service") fields.Remove("parentServiceId"); // API uses ParentId; ParentServiceId is ignored by this create handler.
        // These are identity/model response fields, never accepted as MCP edits.
        if (type == typeof(Organization)) { fields.Remove("orchestrationTenantLinkedAtUtc"); }
        if (operation.StartsWith("update_", StringComparison.Ordinal)) fields.Remove("id");
    }

    private static void AddList(JsonObject fields, string tool, string operation)
    {
        fields["page"] = Integer(1); fields["pageSize"] = Integer(1, 100);
        if (tool is "helpdesk_incidents" or "helpdesk_requests" or "helpdesk_changes" or "helpdesk_request_tasks")
        {
            const string rule = "Search sent as API q. query/q aliases accept null or blank as unfiltered; equivalent trimmed values deduplicate; conflicting nonempty values reject. Filtering precedes totals and paging.";
            foreach (var alias in new[] { "query", "q" }) fields[alias] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 200, ["description"] = rule };
            fields["includeTotal"] = Boolean(); fields["historicOnly"] = Boolean();
            if (tool == "helpdesk_request_tasks")
            {
                foreach (var name in new[] { "assignedToId", "requestId", "serviceId" }) fields[name] = Text();
                fields["assignedToMe"] = Boolean(); fields["status"] = EnumSchema(typeof(RequestTaskStatus), true); fields["type"] = EnumSchema(typeof(RequestTaskType), true);
            }
            else
            {
                foreach (var name in new[] { "activeOnly", "summaryOnly", "unassigned", "descending" }) fields[name] = Boolean();
                fields["state"] = EnumSchema(typeof(TicketState), true); fields["view"] = Named("all", "active", "historic");
                foreach (var name in new[] { "customerOrgId", "forId", "assigneeId" }) fields[name] = Repeated(Text());
                fields["stateValues"] = Repeated(EnumSchema(tool == "helpdesk_changes" ? typeof(ChangeLifecycleState) : typeof(TicketState), true));
                fields["priority"] = Repeated(EnumSchema(typeof(TicketPriority), true));
                fields["timeField"] = Named("updated", "created"); fields["sort"] = Named("updated", "created", "trackingId", "subject", "priority", "state");
                fields["from"] = Date(); fields["to"] = Date();
                if (tool == "helpdesk_incidents") { fields["organizationId"] = Text(); fields["excludeId"] = Text(); fields["requesterEmail"] = Text(); }
                if (tool == "helpdesk_requests") fields["aiOnly"] = Boolean();
                if (tool == "helpdesk_changes") fields["lifecycleState"] = EnumSchema(typeof(ChangeLifecycleState), true);
            }
        }
        else if (tool == "helpdesk_self_service") { if (operation == "request_users") { fields.Remove("page"); fields["query"] = Text(false); } else fields["q"] = Text(false); }
        else if (tool == "helpdesk_notifications")
        {
            if (operation == "list")
            {
                foreach (var name in new[] { "search", "source", "category", "sortBy", "sortDir" }) fields[name] = Text(false);
                fields["severity"] = EnumSchema(typeof(Helpdesk.Shared.DTOs.Notification.NotificationSeverity), true);
                fields["from"] = Date(); fields["to"] = Date();
            }
            else { fields.Clear(); if (operation == "unread_errors") fields["take"] = Integer(1, 100); }
        }
        else if (tool == "helpdesk_categories")
        {
            fields.Clear(); fields["type"] = EnumSchema(typeof(Helpdesk.Shared.Enums.TicketCategoryType), true);
            fields["search"] = new JsonObject { ["type"] = "string", ["maxLength"] = 120 }; fields["ids"] = Text(); fields["take"] = Integer(1, 100); fields["allScopes"] = Boolean(); fields["includeInactive"] = Boolean();
            // tenantId is currently filtered by the legacy adapter; do not advertise an ignored field.
        }
        else { fields.Clear(); /* Legacy CRUD list routes do not apply paging/search. */ }
    }

    private static void AddDto(JsonObject schema, Type type)
    {
        var fields = (JsonObject)schema["properties"]!; var required = (JsonArray)schema["required"]!;
        var nullability = new NullabilityInfoContext();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite && property.GetCustomAttribute<JsonIgnoreAttribute>() is null))
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var field = TypeSchema(property.PropertyType);
            if (System.Nullable.GetUnderlyingType(property.PropertyType) is not null || nullability.Create(property).ReadState == NullabilityState.Nullable) field = Nullable(field);
            fields[name] = field;
            if (property.GetCustomAttribute<RequiredAttribute>() is not null) Require(required, name);
        }
    }

    private static JsonObject TypeSchema(Type type)
    {
        type = System.Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return Text(false);
        if (type == typeof(Guid)) return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return Date();
        if (type == typeof(bool)) return Boolean();
        if (type.IsEnum) return EnumSchema(type, names: false);
        if (type == typeof(int) || type == typeof(long)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(decimal) || type == typeof(double)) return new JsonObject { ["type"] = "number" };
        if (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(type.GetGenericArguments()[0]) };
        var schema = Object(); AddDto(schema, type); return schema;
    }

    private static JsonObject ChangeTemplate()
    {
        var schema = Object(); AddDto(schema, typeof(ChangeTemplateDto));
        var fields = (JsonObject)schema["properties"]!; var required = (JsonArray)schema["required"]!;
        foreach (var name in new[] { "scopeOfChange", "affectedSystems", "implementationSteps", "validationSteps" }) Require(required, name);
        fields["scopeOfChange"] = Text();
        foreach (var name in new[] { "affectedSystems", "implementationSteps", "validationSteps" }) fields[name] = Strings(1);
        fields["preChangeChecks"] = Strings(); // Only Normal requires nonempty checks (the type-specific condition below).
        schema["anyOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("rollbackPlan"), ["properties"] = new JsonObject { ["rollbackPlan"] = Text() } }, new JsonObject { ["required"] = new JsonArray("rollbackReference"), ["properties"] = new JsonObject { ["rollbackReference"] = Text() } });
        return schema;
    }

    private static JsonArray ChangeConditions() => new(new[]
    {
        ("Standard", new[] { "isPreApproved", "existingRunbookReference" }),
        ("Normal", new[] { "businessJustification", "impactAssessment", "riskAssessment", "preChangeChecks", "testingPlan" }),
        ("Emergency", new[] { "emergencyReason", "businessImpactIfNotImplemented", "immediateRiskAssessment", "postChangeValidation" })
    }.Select(item => (JsonNode)new JsonObject
    {
        ["if"] = new JsonObject { ["required"] = new JsonArray("changeType"), ["properties"] = new JsonObject { ["changeType"] = new JsonObject { ["const"] = item.Item1 } } },
        ["then"] = new JsonObject { ["properties"] = new JsonObject { ["changeTemplate"] = new JsonObject
        {
            ["required"] = new JsonArray(item.Item2.Select(name => JsonValue.Create(name)).ToArray()),
            ["properties"] = new JsonObject(item.Item2.Select(name => new KeyValuePair<string, JsonNode?>(name, name == "isPreApproved" ? new JsonObject { ["const"] = true } : name == "preChangeChecks" ? Strings(1) : Text())))
        } } }
    }).ToArray());

    private static JsonArray RolePermissionConditions()
    {
        var conditions = new JsonArray();
        var permissions = Helpdesk.Shared.Auth.HelpdeskPermissions.AssignablePermissions;
        foreach (var permission in permissions.Where(permission => !Helpdesk.Shared.Auth.RoleDefinitionCatalog.SatisfiesDependencies([permission])))
        {
            var dependency = permissions.Single(candidate => candidate != permission && Helpdesk.Shared.Auth.RoleDefinitionCatalog.SatisfiesDependencies([permission, candidate]));
            conditions.Add(new JsonObject
            {
                ["if"] = new JsonObject { ["required"] = new JsonArray("permissions"), ["properties"] = new JsonObject { ["permissions"] = new JsonObject { ["contains"] = new JsonObject { ["const"] = permission } } } },
                ["then"] = new JsonObject { ["properties"] = new JsonObject { ["permissions"] = new JsonObject { ["contains"] = new JsonObject { ["const"] = dependency } } } }
            });
        }
        return conditions;
    }

    private static JsonObject ExclusiveClear(string clear, string value) => new() { ["not"] = new JsonObject { ["required"] = new JsonArray(clear, value), ["properties"] = new JsonObject { [clear] = new JsonObject { ["const"] = true } } } };
    private static JsonObject Object() => new() { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = new JsonObject(), ["required"] = new JsonArray() };
    private static JsonObject Text(bool nonempty = true) => nonempty ? new() { ["type"] = "string", ["minLength"] = 1 } : new() { ["type"] = "string" };
    private static JsonObject Integer(int minimum, int? maximum = null) { var result = new JsonObject { ["type"] = "integer", ["minimum"] = minimum }; if (maximum.HasValue) result["maximum"] = maximum.Value; return result; }
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Date() => new() { ["type"] = "string", ["format"] = "date-time" };
    private static JsonObject Strings(int minimum = 0) => new() { ["type"] = "array", ["minItems"] = minimum, ["items"] = Text() };
    private static JsonObject Named(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray()) };
    private static JsonObject EnumSchema(Type type, bool names)
    {
        var values = new JsonArray();
        if (names) foreach (var name in Enum.GetNames(type)) values.Add(name);
        foreach (var value in Enum.GetValues(type)) values.Add(Convert.ToInt32(value));
        return new JsonObject { ["enum"] = values };
    }
    private static JsonObject Repeated(JsonObject item) => new() { ["oneOf"] = new JsonArray(item.DeepClone(), new JsonObject { ["type"] = "array", ["maxItems"] = 20, ["items"] = item }) };
    private static JsonObject Nullable(JsonObject field)
    {
        if (field["type"] is JsonValue type) field["type"] = new JsonArray(type.GetValue<string>(), "null");
        else if (field["enum"] is JsonArray values) values.Add((JsonNode?)null);
        return field;
    }
    private static void RemoveNull(JsonObject field)
    {
        if (field["type"] is JsonArray types) field["type"] = types.First(value => value!.GetValue<string>() != "null")!.DeepClone();
        if (field["enum"] is JsonArray values) for (var i = values.Count - 1; i >= 0; i--) if (values[i] is null) values.RemoveAt(i);
    }
    private static void Field(JsonObject fields, JsonArray required, string name, JsonObject field, bool mandatory) { fields[name] = field; if (mandatory) Require(required, name); }
    private static void Require(JsonArray required, string name) { if (!required.Any(value => value!.GetValue<string>() == name)) required.Add(name); }
    internal static IEnumerable<string> Identifiers(string path)
    {
        for (var position = 0; position < path.Length;)
        {
            var open = path.IndexOf('{', position); if (open < 0) yield break;
            var close = path.IndexOf('}', open + 1); if (close < 0) yield break;
            yield return path[(open + 1)..close]; position = close + 1;
        }
    }
}
