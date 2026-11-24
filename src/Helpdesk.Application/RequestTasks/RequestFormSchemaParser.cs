using System.Text.Json;
using Helpdesk.Shared.DTOs.RequestForm;

namespace Helpdesk.Application.RequestTasks;

public sealed class RequestFormSchemaParser : IRequestFormSchemaParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public RequestFormSchemaModel Parse(JsonDocument schema)
    {
        if (schema.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new RequestFormSchemaModel();
        }

        var result = new RequestFormSchemaModel();

        if (schema.RootElement.TryGetProperty("fields", out var fieldsNode) && fieldsNode.ValueKind == JsonValueKind.Array)
        {
            var fields = JsonSerializer.Deserialize<List<FormField>>(fieldsNode.GetRawText(), SerializerOptions);
            if (fields is not null)
            {
                if (fields.Any(field => field is null))
                {
                    throw new JsonException("Request form fields must be objects.");
                }

                foreach (var field in fields.Where(field =>
                             string.Equals(field.Type, "helpdeskData", StringComparison.OrdinalIgnoreCase)))
                {
                    field.Type = "text";
                }

                result.Fields = fields;
            }
        }

        result.Tasks = ParseTasks(schema.RootElement);

        return result;
    }

    private static List<RequestTaskTemplateModel> ParseTasks(JsonElement root)
    {
        if (!root.TryGetProperty("tasks", out var tasksNode) || tasksNode.ValueKind != JsonValueKind.Array)
        {
            return new List<RequestTaskTemplateModel>();
        }

        var tasks = new List<RequestTaskTemplateModel>();
        foreach (var item in tasksNode.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var model = new RequestTaskTemplateModel
            {
                Id = TryReadGuid(item, "id"),
                Name = TryReadString(item, "name") ?? "New Task",
                Description = TryReadString(item, "description"),
                Order = TryReadInt(item, "order") ?? 0,
                Type = NormalizeType(item),
                AutoStart = TryReadBool(item, "autoStart") ?? true,
                DefaultAssigneeId = TryReadString(item, "defaultAssigneeId"),
                TeamOrRole = TryReadString(item, "teamOrRole"),
                OrchestratorJobName = TryReadString(item, "orchestratorJobName"),
                PayloadMapping = TryReadMapping(item, "payloadMapping"),
                ExpectedRuntimeMinutes = TryReadInt(item, "expectedRuntimeMinutes"),
                GraceRuntimeMinutes = TryReadInt(item, "graceRuntimeMinutes"),
                DependsOn = TryReadGuidList(item, "dependsOn") ?? new List<Guid>(),
                ConditionExpression = TryReadString(item, "conditionExpression"),
                TaskSlaMinutes = TryReadInt(item, "taskSlaMinutes"),
                EscalateAfterMinutes = TryReadInt(item, "escalateAfterMinutes"),
                EscalationUserId = TryReadString(item, "escalationUserId"),
                EscalationRole = TryReadString(item, "escalationRole"),
                IsCritical = TryReadBool(item, "isCritical") ?? false,
                MaxRetries = TryReadInt(item, "maxRetries"),
                RetryDelayMinutes = TryReadInt(item, "retryDelayMinutes"),
                FailurePolicy = TryReadString(item, "failurePolicy"),
                ApprovalAllowedDays = TryReadInt(item, "approvalAllowedDays"),
                ApprovalApprovers = TryReadApprovalApprovers(item, "approvalApprovers")
            };

            tasks.Add(model);
        }

        return tasks;
    }

    private static string NormalizeType(JsonElement item)
    {
        if (!item.TryGetProperty("type", out var typeNode))
        {
            return "manual";
        }

        if (typeNode.ValueKind == JsonValueKind.String)
        {
            var value = typeNode.GetString();
            if (string.Equals(value, "automation", StringComparison.OrdinalIgnoreCase))
            {
                return "automation";
            }

            if (string.Equals(value, "approval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "get approval", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "get-approval", StringComparison.OrdinalIgnoreCase))
            {
                return "approval";
            }

            return "manual";
        }

        if (typeNode.ValueKind == JsonValueKind.Number && typeNode.TryGetInt32(out var num))
        {
            return num switch
            {
                2 => "automation",
                3 => "approval",
                _ => "manual"
            };
        }

        return "manual";
    }

    private static Guid TryReadGuid(JsonElement item, string propertyName)
    {
        var value = TryReadString(item, propertyName);
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    private static int? TryReadInt(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return node.TryGetInt32(out var value) ? value : null;
    }

    private static bool? TryReadBool(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var node))
        {
            return null;
        }

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? TryReadString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return node.GetString();
    }

    private static Dictionary<string, string>? TryReadMapping(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in node.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                mapping[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return mapping.Count == 0 ? null : mapping;
    }

    private static List<Guid>? TryReadGuidList(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var node))
        {
            return null;
        }

        if (node.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<Guid>();
        foreach (var entry in node.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String
                && Guid.TryParse(entry.GetString(), out var parsed))
            {
                values.Add(parsed);
                continue;
            }

            values.Add(Guid.Empty);
        }

        return values;
    }

    private static List<RequestTaskApprovalApproverModel> TryReadApprovalApprovers(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return new List<RequestTaskApprovalApproverModel>();
        }

        var approvers = JsonSerializer.Deserialize<List<RequestTaskApprovalApproverModel>>(node.GetRawText(), SerializerOptions)
            ?? new List<RequestTaskApprovalApproverModel>();
        return approvers
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .Select(x => new RequestTaskApprovalApproverModel
            {
                Source = NormalizeApproverSource(x.Source),
                Id = x.Id?.Trim() ?? string.Empty,
                Name = x.Name?.Trim() ?? string.Empty,
                Email = x.Email.Trim(),
                OrganizationId = string.IsNullOrWhiteSpace(x.OrganizationId) ? null : x.OrganizationId.Trim(),
                OrganizationName = string.IsNullOrWhiteSpace(x.OrganizationName) ? null : x.OrganizationName.Trim()
            })
            .DistinctBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeApproverSource(string? source)
        => string.Equals(source, "User", StringComparison.OrdinalIgnoreCase)
            ? "User"
            : "Customer";
}
