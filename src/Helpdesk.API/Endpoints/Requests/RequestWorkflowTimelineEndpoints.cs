using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Requests;

public static class RequestWorkflowTimelineEndpoints
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "clientSecret",
        "token",
        "authorization",
        "password",
        "payloadJson"
    };

    public static void MapRequestWorkflowTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/requests/{requestId}/workflow-timeline", async (
            [FromRoute] string requestId,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            ClaimsPrincipal user,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] bool? includePayload,
            [FromQuery] string? correlationId,
            [FromQuery] string? categories,
            [FromQuery] DateTimeOffset? sinceUtc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return Results.BadRequest("Invalid request id.");
            }

            var request = await db.Requests.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.Id == requestId)
                .Select(r => new { r.Id, r.OrganizationId, r.TrackingId })
                .FirstOrDefaultAsync(ct);

            if (request is null)
            {
                return Results.Problem("Request not found", statusCode: 404);
            }

            var access = await accessService.ResolveAsync(user, ct);
            if (!access.CanManageRequest(request.OrganizationId))
            {
                return Results.Forbid();
            }

            var safePage = Math.Max(page ?? 1, 1);
            var safePageSize = Math.Clamp(pageSize ?? 50, 1, 200);
            var skip = (safePage - 1) * safePageSize;
            var categoryFilters = ParseCsv(categories);

            var query = db.Notifications.IgnoreQueryFilters().AsNoTracking()
                .Where(n => n.Category != null && EF.Functions.Like(n.Category, "DomainEvent%"))
                .Where(n => EF.Functions.Like(n.Message, $"%\"requestId\":\"{requestId}\"%"));

            if (!string.IsNullOrWhiteSpace(request.OrganizationId))
            {
                query = query.Where(n => n.TenantId == request.OrganizationId);
            }

            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                query = query.Where(n => n.CorrelationId == correlationId);
            }

            if (sinceUtc.HasValue)
            {
                var since = sinceUtc.Value.UtcDateTime;
                query = query.Where(n => n.CreatedUtc >= since);
            }

            if (categoryFilters.Count > 0)
            {
                query = query.Where(n => categoryFilters.Contains(n.Title));
            }

            var totalCount = await query.CountAsync(ct);
            var notifications = await query
                .OrderByDescending(n => n.CreatedUtc)
                .Skip(skip)
                .Take(safePageSize)
                .Select(n => new
                {
                    n.Id,
                    n.CreatedUtc,
                    n.Title,
                    n.Message,
                    n.CorrelationId,
                    n.TenantId
                })
                .ToListAsync(ct);

            var canViewPayload = includePayload == true && access.IsHelpdeskAdmin;
            var items = new List<WorkflowTimelineItemDto>(notifications.Count);
            foreach (var notification in notifications)
            {
                var parsed = ParsePayload(notification.Message);
                var eventType = string.IsNullOrWhiteSpace(parsed.EventType) ? notification.Title : parsed.EventType;
                items.Add(new WorkflowTimelineItemDto
                {
                    NotificationId = notification.Id,
                    CreatedAt = DateTime.SpecifyKind(notification.CreatedUtc, DateTimeKind.Utc),
                    Category = eventType,
                    Title = BuildTitle(eventType, parsed),
                    Severity = ResolveSeverity(eventType),
                    CorrelationId = notification.CorrelationId,
                    RequestId = requestId,
                    TaskId = parsed.TaskId,
                    TrackingId = parsed.TrackingId ?? request.TrackingId,
                    ExecutionId = parsed.ExecutionId,
                    PayloadJson = canViewPayload ? RedactPayload(notification.Message) : null
                });
            }

            return Results.Ok(new PagedResponse<WorkflowTimelineItemDto>
            {
                Page = safePage,
                PageSize = safePageSize,
                TotalCount = totalCount,
                Items = items
            });
        })
        .WithTags("Requests")
        .RequireAuthorization();
    }

    private static string BuildTitle(string eventType, WorkflowEventPayload payload)
    {
        var taskNameSuffix = string.IsNullOrWhiteSpace(payload.TaskName) ? string.Empty : $": {payload.TaskName}";
        return eventType switch
        {
            "DomainEvent.RequestTask.Unblocked" => $"Task unblocked{taskNameSuffix}",
            "DomainEvent.RequestTask.Blocked" => $"Task blocked{taskNameSuffix}",
            "DomainEvent.RequestTask.Skipped" => $"Task skipped{taskNameSuffix}",
            "DomainEvent.RequestTask.Started" => $"Task started{taskNameSuffix}",
            "DomainEvent.RequestTask.Completed" => $"Task completed{taskNameSuffix}",
            "DomainEvent.RequestTask.Failed" => $"Task failed{taskNameSuffix}",
            "DomainEvent.RequestTask.RetryScheduled" => $"Retry scheduled{taskNameSuffix}",
            "DomainEvent.RequestTask.RetryTriggered" => $"Retry triggered{taskNameSuffix}",
            "DomainEvent.RequestTask.Escalated" => $"Task escalated{taskNameSuffix}",
            "DomainEvent.RequestTask.SlaBreached" => $"Task SLA breached{taskNameSuffix}",
            "DomainEvent.RequestTask.AutomationSubmitted" => $"Automation submitted{taskNameSuffix}",
            "DomainEvent.RequestTask.AutomationRunning" => $"Automation running{taskNameSuffix}",
            "DomainEvent.RequestTask.AutomationCompleted" => $"Automation completed{taskNameSuffix}",
            "DomainEvent.RequestTask.AutomationFailed" => $"Automation failed{taskNameSuffix}",
            "DomainEvent.RequestTask.AutomationSubmitRejected" => $"Automation submission rejected{taskNameSuffix}",
            "DomainEvent.RequestTask.AutomationSubmitUncertain" => $"Automation submission uncertain{taskNameSuffix}",
            "DomainEvent.Workflow.Evaluated" => "Workflow evaluated",
            "DomainEvent.Workflow.Progressed" => "Workflow progressed",
            "DomainEvent.Workflow.RequestBlockedByTaskFailure" => $"Request blocked by task failure{taskNameSuffix}",
            "DomainEvent.Workflow.RequestFailedByCriticalTask" => $"Request failed by critical task{taskNameSuffix}",
            "DomainEvent.Workflow.TaskFailureIgnored" => $"Task failure ignored{taskNameSuffix}",
            _ => eventType
        };
    }

    private static string ResolveSeverity(string eventType)
    {
        if (eventType is "DomainEvent.Workflow.RequestFailedByCriticalTask"
            or "DomainEvent.RequestTask.Failed"
            or "DomainEvent.RequestTask.AutomationFailed"
            or "DomainEvent.RequestTask.AutomationSubmitUncertain")
        {
            return "Error";
        }

        if (eventType is "DomainEvent.RequestTask.Escalated"
            or "DomainEvent.RequestTask.RetryScheduled"
            or "DomainEvent.RequestTask.Blocked"
            or "DomainEvent.Workflow.RequestBlockedByTaskFailure"
            or "DomainEvent.RequestTask.AutomationSubmitRejected")
        {
            return "Warning";
        }

        return "Info";
    }

    private static HashSet<string> ParseCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static WorkflowEventPayload ParsePayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return WorkflowEventPayload.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new WorkflowEventPayload
            {
                EventType = TryGetString(root, "eventType"),
                TaskName = TryGetString(root, "taskName"),
                TrackingId = TryGetString(root, "trackingId"),
                ExecutionId = TryGetString(root, "executionId"),
                TaskId = TryGetIdentifier(root, "taskId")
            };
        }
        catch
        {
            return WorkflowEventPayload.Empty;
        }
    }

    private static string? RedactPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var cleaned = RedactElement(doc.RootElement);
            var payload = JsonSerializer.Serialize(cleaned);
            return payload.Length > 4000 ? payload[..4000] : payload;
        }
        catch
        {
            return null;
        }
    }

    private static object? RedactElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => RedactObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(RedactElement).ToList(),
            JsonValueKind.String => Truncate(element.GetString()),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static Dictionary<string, object?> RedactObject(JsonElement element)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (IsSensitiveKey(property.Name))
            {
                map[property.Name] = "***REDACTED***";
                continue;
            }

            map[property.Name] = RedactElement(property.Value);
        }

        return map;
    }

    private static bool IsSensitiveKey(string key)
    {
        if (SensitiveKeys.Contains(key))
        {
            return true;
        }

        return Regex.IsMatch(key, "(secret|token|password|authorization)", RegexOptions.IgnoreCase);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length <= 500 ? value : $"{value[..500]}...";
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return node.GetString();
    }

    private static string? TryGetIdentifier(JsonElement root, string propertyName)
    {
        return TryGetString(root, propertyName);
    }

    private sealed class WorkflowEventPayload
    {
        public static WorkflowEventPayload Empty { get; } = new();
        public string? EventType { get; init; }
        public string? TaskName { get; init; }
        public string? TaskId { get; init; }
        public string? TrackingId { get; init; }
        public string? ExecutionId { get; init; }
    }
}
