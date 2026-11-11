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
    private const string CallbackRejectedReasonAcknowledgementPending = "AcknowledgementPending";

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
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

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
        IDomainEventPublisher domainEvents,
        ICorrelationContext correlationContext,
        AutomationExecutionOutcomeService outcomes,
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

        var resolution = await ResolveManagedTaskAsync(db, dto, registration!, correlationId, ct);
        if (resolution.AcknowledgementPending)
        {
            await PublishRejectedAsync(domainEvents, CallbackRejectedReasonAcknowledgementPending, callerClientId, correlationId, ct);
            httpContext.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var task = resolution.Task;
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

        var applied = await outcomes.ApplyAsync(task, dto, incomingStatus, correlationId, callerClientId, ct);
        if (!applied)
        {
            httpContext.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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
        return normalized is "running" or "succeeded" or "failed" or "cancelled" or "unknown" ? normalized : null;
    }

    private static string GetCorrelationId(ICorrelationContext correlationContext) => correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    private static bool HasTaskIdentifier(OrchestrationCallbackDto dto) => !string.IsNullOrWhiteSpace(dto.RequestTaskId) || !string.IsNullOrWhiteSpace(dto.ExecutionId) || !string.IsNullOrWhiteSpace(dto.OrchestrationRunId) || !string.IsNullOrWhiteSpace(dto.OrchestrationRequestId) || !string.IsNullOrWhiteSpace(dto.RequestId);

    private static async Task<(RequestTask? Task, bool AcknowledgementPending)> ResolveManagedTaskAsync(
        HelpdeskDbContext db, OrchestrationCallbackDto dto, ServicePrincipalRegistration registration,
        string correlationId, CancellationToken ct)
    {
        var id = NormalizeIdentifier(dto.RequestTaskId);
        var execution = FirstNonEmpty(dto.ExecutionId, dto.OrchestrationRunId);
        var request = FirstNonEmpty(dto.OrchestrationRequestId, dto.RequestId);
        // Managed callbacks cannot create a missing correlation or use a client ID as a user identity.
        if (id is null || execution is null || request is null) return (null, false);
        if (registration.MappingId is not { } mappingId) return (null, false);
        var mapping = mappingId.ToString("D");
        var tasks = db.RequestTasks.Where(x => x.Id == id && x.OrganizationId == registration.OrganizationId &&
            x.OrchestrationLinkId == mapping && x.OrchestrationPeerInstanceId == registration.PeerInstanceId && x.OrchestrationLinkRevision == registration.MappingRevision);
        var task = await tasks.FirstOrDefaultAsync(ct);
        if (task is null || task.Type != RequestTaskType.Automation ||
            !await db.Requests.AnyAsync(x => x.Id == task.RequestId && x.OrganizationId == registration.OrganizationId, ct))
            return (null, false);

        // Start persists the scoped attempt before invoking the provider. A fast
        // callback may precede its ingest response, but cannot establish remote IDs.
        // Ask for the same callback again only for that exact in-flight attempt.
        if (task.Status == RequestTaskStatus.InProgress
            && string.Equals(task.AutomationAttemptCorrelationId, correlationId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(task.AutomationAttemptCorrelationId)
            && string.IsNullOrWhiteSpace(task.OrchestrationExternalRequestId)
            && string.IsNullOrWhiteSpace(task.OrchestrationExternalRunId)
            && string.IsNullOrWhiteSpace(task.OrchestratorExecutionId)
            && NormalizeStatus(dto.Status) is not null
            && MatchesOrEmpty(dto.ExecutionId, execution)
            && MatchesOrEmpty(dto.OrchestrationRunId, execution)
            && MatchesOrEmpty(dto.RequestId, request)
            && MatchesOrEmpty(dto.OrchestrationRequestId, request))
        {
            return (null, true);
        }

        if (string.IsNullOrWhiteSpace(task.OrchestrationExternalRequestId) ||
            !string.Equals(task.OrchestrationExternalRequestId, request, StringComparison.Ordinal) ||
            !new[] { task.OrchestratorExecutionId, task.OrchestrationExternalRunId }.Any(x => !string.IsNullOrEmpty(x) && x == execution))
            return (null, false);
        return (task, false);
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
