using System.Net;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.AspNetCore.Antiforgery;

namespace HelpDesk.NewWeb.Services;

public static class IntegrationPairingConsentEndpoints
{
    public static void MapIntegrationPairingConsentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/integration-credentials/pairing-consent", async (HttpContext context,
            IAntiforgery antiforgery, IHttpClientFactory clients) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.HasFormContentType || context.Request.ContentLength > 16384)
                return Results.BadRequest();
            var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 16384;
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Redirect("form-expired"); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var code = form["humanCode"].ToString();
            if (code.Length > 16) return Results.BadRequest();
            if (form["decision"] != "deny" && form["decision"] != "approve") return Results.BadRequest();
            var deny = form["decision"] == "deny";
            if (!deny && form["consent"] != "on") return Redirect("consent-required");
            int? lifetime = int.TryParse(form["lifetimeDays"], out var days) ? days : null;
            try
            {
                using var response = deny
                    ? await clients.CreateClient("SystemPairingApi").PostAsJsonAsync("api/v1/integration-credentials/pairing/deny",
                        new IntegrationPairingReviewRequest(code), context.RequestAborted)
                    : await clients.CreateClient("SystemPairingApi").PostAsJsonAsync("api/v1/integration-credentials/pairing/approve",
                        new IntegrationPairingApprovalRequest(code, form["organizationId"].ToString(),
                            form["permissions"].Where(p => p is not null).Select(p => p!).ToArray(), lifetime, true), context.RequestAborted);
                return Redirect(response.IsSuccessStatusCode ? deny ? "denied" : "approved" :
                    response.StatusCode == HttpStatusCode.TooManyRequests ? "slow-down" : "retry");
            }
            catch (Exception error) when (error is HttpRequestException ||
                error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            { return Redirect("retry"); }
        }).RequireAuthorization();
    }
    private static IResult Redirect(string status) => Results.LocalRedirect($"/account/integration-credentials/approve?status={status}");
}
