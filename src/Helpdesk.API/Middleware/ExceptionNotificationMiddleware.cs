using Helpdesk.Application.Notifications;
using Helpdesk.Application.Events;
using Helpdesk.Shared.DTOs.Notification;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Timeouts;

namespace Helpdesk.API.Middleware;

public class ExceptionNotificationMiddleware(RequestDelegate next, ILogger<ExceptionNotificationMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<ExceptionNotificationMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context, INotificationService notifications)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested &&
            context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested != true)
        {
            // Classify before the Error logger: it asynchronously persists notifications
            // independently of the aborted request's cancellation token.
            _logger.LogDebug("Request aborted at {Path}. CorrelationId={CorrelationId} ResponseStarted={ResponseStarted}",
                context.Request.Path, context.Items[CorrelationConstants.HttpContextItemKey], context.Response.HasStarted);
            throw;
        }
        catch (Exception ex)
        {
            var correlationId = context.Items[CorrelationConstants.HttpContextItemKey] as string;
            var smokeScenarioId = context.Items[CorrelationConstants.SmokeScenarioHttpContextItemKey] as string;
            var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
            var spanId = System.Diagnostics.Activity.Current?.SpanId.ToString();
            var tenantId = ResolveTenantId(context.User);
            var cause = ex switch
            {
                TimeoutException => "dependency_timeout",
                OperationCanceledException when context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested == true => "request_timeout",
                OperationCanceledException => "unexpected_cancellation",
                _ => "server_fault"
            };

            _logger.LogError(
                ex,
                "Unhandled exception at path {Path}. CorrelationId={CorrelationId} TraceId={TraceId} SpanId={SpanId} TenantId={TenantId} SmokeScenario={SmokeScenario} Cause={Cause} RequestAborted={RequestAborted} ExceptionTokenCancelled={ExceptionTokenCancelled} ResponseStarted={ResponseStarted}",
                context.Request.Path,
                correlationId,
                traceId,
                spanId,
                tenantId,
                smokeScenarioId, cause, context.RequestAborted.IsCancellationRequested,
                ex is OperationCanceledException cancellation && cancellation.CancellationToken.IsCancellationRequested,
                context.Response.HasStarted);

            try
            {
                var message = $"Unhandled exception at '{context.Request.Path}'.{Environment.NewLine}" +
                              $"CorrelationId: {correlationId ?? "unknown"}{Environment.NewLine}" +
                              $"TraceId: {traceId ?? "unknown"}{Environment.NewLine}" +
                              $"SpanId: {spanId ?? "unknown"}{Environment.NewLine}" +
                              $"TenantId: {tenantId ?? "unknown"}{Environment.NewLine}" +
                              $"SmokeScenario: {smokeScenarioId ?? "none"}{Environment.NewLine}" +
                              $"Cause: {cause}; RequestAborted: {context.RequestAborted.IsCancellationRequested}; ResponseStarted: {context.Response.HasStarted}{Environment.NewLine}" +
                              $"Message: {ex.Message}{Environment.NewLine}" +
                              $"StackTrace: {ex.StackTrace}";

                await notifications.CreateNotificationAsync(new CreateNotificationRequest
                {
                    Title = "UnhandledException",
                    Message = message,
                    Severity = NotificationSeverity.Critical,
                    Source = nameof(ExceptionNotificationMiddleware),
                    Category = "System",
                    TenantId = tenantId
                }, context.RequestAborted);
            }
            catch
            {
                // Swallow to keep exception pipeline stable.
            }

            throw;
        }
    }

    private static string? ResolveTenantId(ClaimsPrincipal user) =>
        FirstClaim(user, "organization_id", "tenant_id", "allowed_organization_id", "tid");

    private static string? FirstClaim(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
