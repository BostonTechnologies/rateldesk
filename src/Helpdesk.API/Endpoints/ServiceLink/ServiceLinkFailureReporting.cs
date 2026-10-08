using System.Security.Claims;
using Helpdesk.Application.Notifications;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.ServiceLink;

namespace Helpdesk.API.Endpoints.ServiceLink;

public static partial class ServiceLinkEndpoints
{
    private static async Task<IResult> FailureAsync(HttpContext http, int statusCode, string code)
    {
        // This ID is generated locally, never copied from an untrusted request header.
        var correlation = Guid.NewGuid().ToString("N");
        var stage = HttpMethods.IsGet(http.Request.Method) ? "status" :
            ServiceLinkFailure.NormalizeStage(http.Request.Path.Value?.TrimEnd('/').Split('/').LastOrDefault());
        var failure = ServiceLinkFailure.From(code, stage, correlation, statusCode);
        http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ServiceLinkFailure").LogWarning(
            "Service-link operation failed. Code={Code} Stage={Stage} CorrelationId={CorrelationId}",
            failure.Code, failure.Stage, failure.CorrelationId);

        // Only interactive commands produce personal notifications. Reads, peer protocol traffic
        // and the background reconciliation worker cannot flood the notification centre.
        var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (HttpMethods.IsPost(http.Request.Method) &&
            http.Request.Path.StartsWithSegments("/api/v1/admin/service-links") &&
            http.User.Identity?.IsAuthenticated == true &&
            http.User.FindFirst("service_principal_id") is null &&
            http.User.FindFirst("netratel_service_principal_id") is null &&
            http.User.FindFirst("netratel_integration_credential_id") is null &&
            http.User.FindFirst("integration_credential_id") is null && !string.IsNullOrWhiteSpace(actor))
        {
            try
            {
                await using var reportingScope = http.RequestServices.CreateAsyncScope();
                var notifications = reportingScope.ServiceProvider.GetService<INotificationService>();
                if (notifications is not null) await notifications.CreateNotificationAsync(new CreateNotificationRequest
                {
                    UserId = actor, Title = "Connection operation needs attention", Message = failure.Message +
                        $" Stage: {failure.Stage}. Reference: {failure.CorrelationId}.",
                    Severity = NotificationSeverity.Warning, Source = "ServiceLink", Category = "ServiceLink",
                    Reference = failure.Code, CorrelationId = correlation, Link = "/account/integration-credentials"
                    // TenantId deliberately remains absent: an unvalidated submitted organization is not authority.
                }, http.RequestAborted);
            }
            catch (Exception) when (!http.RequestAborted.IsCancellationRequested)
            {
                // Reporting must not replace the bounded original failure or expose exception text.
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ServiceLinkFailure").LogWarning(
                    "Service-link failure notification could not be saved. CorrelationId={CorrelationId}", correlation);
            }
        }
        return Results.Problem(statusCode: statusCode, title: failure.Message,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ServiceLinkFailure.ProtocolCode(code, statusCode), ["stage"] = failure.Stage, ["correlationId"] = failure.CorrelationId
            });
    }
}

internal static class ServiceLinkFailureReporting
{
    public static bool IsNetworkPolicyFailure(Exception exception)
    {
        // The policy is local code; use its local marker, never classify by peer/error text.
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current.Data.Contains("ServiceLink.NetworkPolicyRejected")) return true;
        return false;
    }
}
