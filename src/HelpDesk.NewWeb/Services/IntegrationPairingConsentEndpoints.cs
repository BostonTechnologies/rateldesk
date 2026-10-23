using System.Net;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.AspNetCore.Antiforgery;

namespace HelpDesk.NewWeb.Services;

public static class IntegrationPairingConsentEndpoints
{
    public static void MapIntegrationPairingConsentEndpoints(this IEndpointRouteBuilder app)
    {
        MapConsent(app, "/account/integration-credentials/pairing-consent", host: false);
        MapConsent(app, "/account/integration-credentials/host-consent", host: true);
    }

    private static void MapConsent(IEndpointRouteBuilder app, string route, bool host)
    {
        app.MapPost(route, async (HttpContext context, IAntiforgery antiforgery, IHttpClientFactory clients) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.HasFormContentType || context.Request.ContentLength > 16384)
                return Results.BadRequest();
            var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 16384;
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Reply(context, "form-expired", host); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var code = form["humanCode"].ToString();
            if (code.Length > 16) return Results.BadRequest();
            if (form["decision"] != "deny" && form["decision"] != "approve") return Results.BadRequest();
            var deny = form["decision"] == "deny";
            if (!deny && form["consent"] != "on") return Reply(context, "consent-required", host);
            int? lifetime = int.TryParse(form["lifetimeDays"], out var days) ? days : null;
            var family = host ? "host-proposals" : "pairing";
            try
            {
                using var response = deny
                    ? await clients.CreateClient("SystemPairingApi").PostAsJsonAsync($"api/v1/integration-credentials/{family}/deny",
                        new IntegrationPairingReviewRequest(code), context.RequestAborted)
                    : host
                        ? await clients.CreateClient("SystemPairingApi").PostAsJsonAsync($"api/v1/integration-credentials/{family}/approve",
                            new HostDeploymentApprovalRequest(code, form["proposalHash"].ToString(), form["sourceHash"].ToString(), true), context.RequestAborted)
                        : await clients.CreateClient("SystemPairingApi").PostAsJsonAsync($"api/v1/integration-credentials/{family}/approve",
                            new IntegrationPairingApprovalRequest(code, form["organizationId"].ToString(),
                                form["permissions"].Where(p => p is not null).Select(p => p!).ToArray(), lifetime, true)
                            {
                                GrantKind = string.IsNullOrWhiteSpace(form["grantKind"]) ? "ordinary" : form["grantKind"].ToString(),
                                OwnerAdministrationConsent = form["ownerAdministrationConsent"] == "on"
                            }, context.RequestAborted);
                return Reply(context, response.IsSuccessStatusCode ? deny ? "denied" : "approved" :
                    response.StatusCode == HttpStatusCode.TooManyRequests ? "slow-down" : "retry", host);
            }
            catch (Exception error) when (error is HttpRequestException ||
                error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            { return Reply(context, "retry", host); }
        }).RequireAuthorization();
    }
    private static IResult Reply(HttpContext context, string status, bool host)
    {
        var redirectUrl = $"/account/integration-credentials/approve?status={status}" + (host ? "&kind=host" : "");
        // Enhanced browser submission retains the review and exact selected grants on recoverable errors.
        return context.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            ? Results.Json(new { status, redirectUrl }) : Results.LocalRedirect(redirectUrl);
    }
}
