using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Pairing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;

namespace Helpdesk.API.Endpoints.Orchestration;

public static class OrchestrationCallbackEndpoints
{
    private const string CallbackRejectedReasonInvalidToken = "InvalidToken";
    private const string CallbackRejectedReasonWrongAudience = "WrongAudience";
    private const string CallbackRejectedReasonExpired = "Expired";
    private const string CallbackRejectedReasonClientNotAllowed = "ClientNotAllowed";
    private const string CallbackRejectedReasonMissingClaim = "MissingClaim";
    private const string CallbackRejectedReasonMissingTaskIdentifier = "MissingTaskIdentifier";
    private const string CallbackRejectedReasonTaskNotFound = "TaskNotFound";
    private const string CallbackRejectedReasonInvalidStatus = "InvalidStatus";
    private const string CallbackRejectedReasonIdentifierMismatch = "IdentifierMismatch";

    public static void MapExternalOrchestrationCallbackEndpoints(this IEndpointRouteBuilder app)
    {
        MapRoutes(app, "/api/v1/orchestration/provider", "Orchestration provider");
    }

    private static void MapRoutes(IEndpointRouteBuilder app, string prefix, string tag)
    {
        var group = app.MapGroup(prefix).WithTags(tag);

        group.MapPost("/callback", HandleCallbackAsync)
            .RequireAuthorization("PairingCallbackOnly")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/m2m/ping", async (
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var registration = await httpContext.RequestServices.GetRequiredService<IServicePrincipalRegistry>().ResolvePrincipalAsync(httpContext.User, ServiceIdentityScopes.Callback, ct);
            if (registration is null) return Results.StatusCode(403);
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { service = "Helpdesk.API", client_id = registration.ClientId, organization_id = registration.OrganizationId,
                peer_instance_id = registration.PeerInstanceId, peer_tenant_id = registration.PeerTenantId, mapping_id = registration.MappingId, mapping_revision = registration.MappingRevision });
        })
        .RequireAuthorization("PairingCallbackOnly")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> HandleCallbackAsync(
        OrchestrationCallbackDto dto,
        HttpContext httpContext,
        HelpdeskDbContext db,
        IRequestTaskStateService requestTaskStateService,
        IDomainEventPublisher domainEvents,
        ICorrelationContext correlationContext,
        IRequestSender sender,
        [FromServices] IFailurePolicyEngine failurePolicyEngine,
        CancellationToken ct)
    {
        var correlationId = GetCorrelationId(correlationContext);
        httpContext.Response.Headers.CacheControl = "no-store";
        var registration = await httpContext.RequestServices.GetRequiredService<IServicePrincipalRegistry>().ResolvePrincipalAsync(httpContext.User, ServiceIdentityScopes.Callback, ct);
        var callerClientId = registration?.ClientId;
        if (string.IsNullOrWhiteSpace(callerClientId))
        {
            await PublishRejectedAsync(
                domainEvents,
                CallbackRejectedReasonMissingClaim,
                null,
                correlationId,
                ct);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (registration is null)
        {
            await PublishRejectedAsync(
                domainEvents,
                CallbackRejectedReasonClientNotAllowed,
                callerClientId,
                correlationId,
                ct);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!HasTaskIdentifier(dto))
        {
            await PublishRejectedAsync(
                domainEvents,
                CallbackRejectedReasonMissingTaskIdentifier,
                callerClientId,
                correlationId,
                ct);
            return Results.BadRequest();
        }

        var task = await ResolveManagedTaskAsync(db, dto, registration!, ct);
        if (task is null)
        {
            await PublishRejectedAsync(
                domainEvents,
                CallbackRejectedReasonTaskNotFound,
                callerClientId,
                correlationId,
                ct);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var incomingStatus = NormalizeStatus(dto.Status);
        if (incomingStatus is null)
        {
            await PublishRejectedAsync(
                domainEvents,
                CallbackRejectedReasonInvalidStatus,
                callerClientId,
                correlationId,
                ct);
            return Results.BadRequest();
        }

        if (!IdentifiersMatch(task, dto))
        {
            await PublishRejectedAsync(domainEvents, CallbackRejectedReasonIdentifierMismatch, callerClientId, correlationId, ct);
            return Results.BadRequest();
        }

        await domainEvents.PublishAsync(
            new OrchestrationCallbackReceivedEvent(
                RequestId: task.RequestId,
                TaskId: task.Id,
                ExecutionId: dto.ExecutionId ?? task.OrchestratorExecutionId ?? task.OrchestrationExternalRunId ?? task.Id,
                Status: incomingStatus,
                CallerClientId: callerClientId,
                TenantId: task.OrganizationId,
                Timestamp: DateTimeOffset.UtcNow,
                CorrelationId: correlationId),
            ct);

        var now = DateTimeOffset.UtcNow;
        var callbackResultJson = BuildCallbackResultJson(dto);
        task.OrchestrationExternalRequestId = FirstNonEmpty(task.OrchestrationExternalRequestId, dto.OrchestrationRequestId, dto.RequestId);
        task.OrchestrationExternalRunId = FirstNonEmpty(task.OrchestrationExternalRunId, dto.OrchestrationRunId, dto.ExecutionId);
        task.OrchestratorExecutionId = FirstNonEmpty(task.OrchestratorExecutionId, dto.ExecutionId, dto.OrchestrationRunId);
        task.LastAutomationStatus = incomingStatus;
        task.LastAutomationUpdatedAt = now;

        switch (task.Status, incomingStatus)
        {
            case (RequestTaskStatus.InProgress, "running"):
                if (dto.StartedAtUtc.HasValue && task.StartedAt is null)
                {
                    task.StartedAt = dto.StartedAtUtc;
                }
                if (!string.IsNullOrWhiteSpace(callbackResultJson))
                {
                    task.ResultJson = callbackResultJson;
                }
                task.UpdatedAt = now.UtcDateTime;
                await db.SaveChangesAsync(ct);
                if (dto.ExpectedRuntimeExceeded == true)
                {
                    await AppendAutomationWorklogAsync(sender, task, dto, "expected-runtime-exceeded", callerClientId, ct);
                }
                await domainEvents.PublishAsync(
                    new RequestTaskAutomationRunningEvent(
                        task.Id,
                        task.RequestId,
                        task.OrchestratorExecutionId,
                        task.OrganizationId,
                        now,
                        correlationId,
                        callerClientId),
                    ct);
                return Results.NoContent();

            case (RequestTaskStatus.InProgress, "succeeded"):
                task.Status = RequestTaskStatus.Completed;
                task.State = TicketState.Resolved;
                task.CompletedAt = dto.CompletedAtUtc ?? now;
                task.FailureReason = null;
                task.ResultJson = callbackResultJson;
                task.UpdatedAt = now.UtcDateTime;
                await db.SaveChangesAsync(ct);

                await AppendAutomationWorklogAsync(sender, task, dto, incomingStatus, callerClientId, ct);
                await domainEvents.PublishAsync(
                    new RequestTaskCompletedEvent(
                        task.Id,
                        task.RequestId,
                        task.OrganizationId,
                        now,
                        correlationId),
                    ct);
                await domainEvents.PublishAsync(
                    new RequestTaskAutomationCompletedEvent(
                        task.Id,
                        task.RequestId,
                        task.OrchestratorExecutionId,
                        task.OrganizationId,
                        now,
                        correlationId,
                        callerClientId),
                    ct);
                await requestTaskStateService.EvaluateParentRequestState(task.RequestId, ct);
                return Results.NoContent();

            case (RequestTaskStatus.InProgress, "failed"):
                task.Status = RequestTaskStatus.Failed;
                task.State = TicketState.OnHold;
                task.CompletedAt = dto.CompletedAtUtc ?? task.CompletedAt;
                task.FailureReason = BuildFailureReason(dto);
                task.ResultJson = callbackResultJson;
                task.UpdatedAt = now.UtcDateTime;
                await db.SaveChangesAsync(ct);

                await AppendAutomationWorklogAsync(sender, task, dto, incomingStatus, callerClientId, ct);
                await domainEvents.PublishAsync(
                    new RequestTaskFailedEvent(
                        task.Id,
                        task.RequestId,
                        task.OrganizationId,
                        now,
                        correlationId),
                    ct);
                await domainEvents.PublishAsync(
                    new RequestTaskAutomationFailedEvent(
                        task.Id,
                        task.RequestId,
                        task.OrchestratorExecutionId,
                        dto.Message ?? "Automation callback failed.",
                        task.OrganizationId,
                        now,
                        correlationId,
                        callerClientId),
                    ct);
                await failurePolicyEngine.OnTaskFailedAsync(task.RequestId, task.Id, ct);
                return Results.NoContent();

            case (RequestTaskStatus.Completed, "succeeded"):
            case (RequestTaskStatus.Failed, "failed"):
                return Results.NoContent();

            case (RequestTaskStatus.Completed, _):
            case (RequestTaskStatus.Failed, _):
                return Results.NoContent();
        }

        return Results.NoContent();
    }

    public static async Task PublishRejectedAsync(
        IDomainEventPublisher domainEvents,
        string reasonCode,
        string? callerClientId,
        string correlationId,
        CancellationToken ct)
    {
        await domainEvents.PublishAsync(
            new OrchestrationCallbackRejectedEvent(
                ReasonCode: reasonCode,
                CallerClientId: callerClientId,
                TenantId: null,
                Timestamp: DateTimeOffset.UtcNow,
                CorrelationId: correlationId),
            ct);
    }

    public static string ResolveRejectedReasonFromException(Exception? ex)
    {
        if (ex is null)
        {
            return CallbackRejectedReasonInvalidToken;
        }

        return ex switch
        {
            Microsoft.IdentityModel.Tokens.SecurityTokenInvalidAudienceException => CallbackRejectedReasonWrongAudience,
            Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException => CallbackRejectedReasonExpired,
            _ => CallbackRejectedReasonInvalidToken
        };
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "running" or "succeeded" or "failed" ? normalized : null;
    }

    private static string GetCorrelationId(ICorrelationContext correlationContext) => correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    private static bool HasTaskIdentifier(OrchestrationCallbackDto dto) => !string.IsNullOrWhiteSpace(dto.RequestTaskId) || !string.IsNullOrWhiteSpace(dto.ExecutionId) || !string.IsNullOrWhiteSpace(dto.OrchestrationRunId) || !string.IsNullOrWhiteSpace(dto.OrchestrationRequestId) || !string.IsNullOrWhiteSpace(dto.RequestId);

    private static async Task<RequestTask?> ResolveManagedTaskAsync(HelpdeskDbContext db, OrchestrationCallbackDto dto,
        ServicePrincipalRegistration registration, CancellationToken ct)
    {
        var id = NormalizeIdentifier(dto.RequestTaskId);
        var execution = FirstNonEmpty(dto.ExecutionId, dto.OrchestrationRunId);
        var request = FirstNonEmpty(dto.OrchestrationRequestId, dto.RequestId);
        // Managed callbacks cannot create a missing correlation or use a client ID as a user identity.
        if (id is null || execution is null || request is null) return null;
        if (registration.MappingId is not { } mappingId) return null;
        var mapping = mappingId.ToString("D");
        var tasks = db.RequestTasks.Where(x => x.Id == id && x.OrganizationId == registration.OrganizationId &&
            x.OrchestrationLinkId == mapping && x.OrchestrationPeerInstanceId == registration.PeerInstanceId && x.OrchestrationLinkRevision == registration.MappingRevision);
        var task = await tasks.FirstOrDefaultAsync(ct);
        if (task is null || task.Type != RequestTaskType.Automation ||
            string.IsNullOrWhiteSpace(task.OrchestrationExternalRequestId) ||
            !string.Equals(task.OrchestrationExternalRequestId, request, StringComparison.Ordinal) ||
            !new[] { task.OrchestratorExecutionId, task.OrchestrationExternalRunId }.Any(x => !string.IsNullOrEmpty(x) && x == execution) ||
            !await db.Requests.AnyAsync(x => x.Id == task.RequestId && x.OrganizationId == registration.OrganizationId, ct)) return null;
        return task;
    }

    private static bool IdentifiersMatch(RequestTask task, OrchestrationCallbackDto dto)
    {
        var requestTaskId = NormalizeIdentifier(dto.RequestTaskId);
        if (!string.IsNullOrWhiteSpace(requestTaskId)
            && !string.Equals(task.Id, requestTaskId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!MatchesOrEmpty(dto.ExecutionId, task.OrchestratorExecutionId, task.OrchestrationExternalRunId))
        {
            return false;
        }

        if (!MatchesOrEmpty(dto.OrchestrationRunId, task.OrchestrationExternalRunId, task.OrchestratorExecutionId))
        {
            return false;
        }

        if (!MatchesOrEmpty(dto.OrchestrationRequestId, task.OrchestrationExternalRequestId))
        {
            return false;
        }

        if (!MatchesOrEmpty(dto.RequestId, task.OrchestrationExternalRequestId))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesOrEmpty(string? candidate, params string?[] knownValues)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }

        var normalizedCandidate = candidate.Trim();
        var populated = knownValues
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (populated.Count == 0)
        {
            return true;
        }

        return populated.Any(x => string.Equals(x, normalizedCandidate, StringComparison.Ordinal));
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (Guid.TryParse(trimmed, out var guid))
        {
            return guid.ToString("N");
        }

        return trimmed;
    }

    private static string BuildCallbackResultJson(OrchestrationCallbackDto dto)
    {
        var payload = new
        {
            requestId = dto.RequestId,
            requestTaskId = dto.RequestTaskId,
            executionId = dto.ExecutionId,
            orchestrationRequestId = dto.OrchestrationRequestId,
            orchestrationRunId = dto.OrchestrationRunId,
            status = dto.Status,
            message = dto.Message,
            resultJson = dto.ResultJson,
            errorJson = dto.ErrorJson,
            worklogSummary = dto.WorklogSummary,
            startedAtUtc = dto.StartedAtUtc,
            completedAtUtc = dto.CompletedAtUtc,
            expectedRuntimeExceeded = dto.ExpectedRuntimeExceeded,
            expectedRuntimeSeconds = dto.ExpectedRuntimeSeconds,
            graceSeconds = dto.GraceSeconds,
            hardTimeoutSeconds = dto.HardTimeoutSeconds
        };

        return JsonSerializer.Serialize(payload);
    }

    private static async Task AppendAutomationWorklogAsync(
        IRequestSender sender,
        RequestTask task,
        OrchestrationCallbackDto dto,
        string status,
        string callerClientId,
        CancellationToken ct)
    {
        var worklogText = BuildWorklogMessage(task, dto, status, callerClientId);
        await sender.Send(
            new CreateWorkLogCommand(
                task.RequestId,
                0,
                worklogText,
                null,
                callerClientId,
                NotifyCustomer: false),
            ct);
    }

    private static string BuildWorklogMessage(
        RequestTask task,
        OrchestrationCallbackDto dto,
        string status,
        string callerClientId)
    {
        var lines = new List<string>
        {
            $"Automation task '{task.Name}' reported '{status}'.",
            $"Source: {callerClientId}"
        };

        var bindingId = FirstNonEmpty(task.AutomationBindingId);
        if (!string.IsNullOrWhiteSpace(bindingId))
        {
            lines.Add($"Binding: {bindingId}");
        }

        var orchestrationRequestId = FirstNonEmpty(dto.OrchestrationRequestId, dto.RequestId, task.OrchestrationExternalRequestId);
        if (!string.IsNullOrWhiteSpace(orchestrationRequestId))
        {
            lines.Add($"External orchestration Request: {orchestrationRequestId}");
        }

        var orchestrationRunId = FirstNonEmpty(dto.OrchestrationRunId, dto.ExecutionId, task.OrchestrationExternalRunId, task.OrchestratorExecutionId);
        if (!string.IsNullOrWhiteSpace(orchestrationRunId))
        {
            lines.Add($"External orchestration Run: {orchestrationRunId}");
        }

        if (!string.IsNullOrWhiteSpace(dto.WorklogSummary))
        {
            lines.Add(string.Empty);
            lines.Add(dto.WorklogSummary.Trim());
        }
        else if (dto.ExpectedRuntimeExceeded == true)
        {
            lines.Add(string.Empty);
            lines.Add("Expected runtime exceeded; waiting until the hard timeout before failing the task.");
        }
        else if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var failureReason = BuildFailureReason(dto);
            if (!string.IsNullOrWhiteSpace(failureReason))
            {
                lines.Add(string.Empty);
                lines.Add(failureReason);
            }
        }

        AppendRuntimePolicyLines(lines, dto);

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFailureReason(OrchestrationCallbackDto dto)
    {
        var baseReason = FirstNonEmpty(dto.Message, ExtractErrorMessage(dto.ErrorJson), "Automation callback failed.")!;
        if (IsHardTimeout(dto))
        {
            return string.Join(
                " ",
                baseReason,
                $"Expected runtime: {FormatSeconds(dto.ExpectedRuntimeSeconds)}.",
                $"Grace: {FormatSeconds(dto.GraceSeconds)}.",
                $"Hard timeout: {FormatSeconds(dto.HardTimeoutSeconds)}.");
        }

        return baseReason;
    }

    private static bool IsHardTimeout(OrchestrationCallbackDto dto)
        => string.Equals(dto.Status, "failed", StringComparison.OrdinalIgnoreCase)
           && (dto.ExpectedRuntimeExceeded == true
               || ContainsTimeout(dto.Message)
               || ContainsTimeout(dto.ErrorJson));

    private static bool ContainsTimeout(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    private static void AppendRuntimePolicyLines(List<string> lines, OrchestrationCallbackDto dto)
    {
        if (dto.ExpectedRuntimeSeconds is null && dto.GraceSeconds is null && dto.HardTimeoutSeconds is null)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"Expected runtime: {FormatSeconds(dto.ExpectedRuntimeSeconds)}");
        lines.Add($"Grace runtime: {FormatSeconds(dto.GraceSeconds)}");
        lines.Add($"Hard timeout: {FormatSeconds(dto.HardTimeoutSeconds)}");
    }

    private static string FormatSeconds(int? seconds)
        => seconds.HasValue ? $"{seconds.Value / 60} min" : "not supplied";

    private static string? ExtractErrorMessage(string? errorJson)
    {
        if (string.IsNullOrWhiteSpace(errorJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(errorJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("message", out var messageNode)
                    && messageNode.ValueKind == JsonValueKind.String)
                {
                    return messageNode.GetString();
                }

                if (document.RootElement.TryGetProperty("error", out var errorNode)
                    && errorNode.ValueKind == JsonValueKind.String)
                {
                    return errorNode.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Agents can supply a plain-text error instead of a JSON object.
            return errorJson;
        }

        return errorJson;
    }

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
}
