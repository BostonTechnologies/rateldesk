using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Shared.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;

namespace HelpDesk.NewWeb.Services;

public static class LocalBrowserLoginEndpoints
{
    public static void MapLocalBrowserLoginEndpoints(this IEndpointRouteBuilder app, bool supportsLocalAccounts, string localCookieName)
    {
        app.MapPost("/local-login", async (HttpContext context, IHttpClientFactory clients, IAntiforgery antiforgery,
            IConfiguration configuration, ILoggerFactory loggers) =>
        {
            if (!supportsLocalAccounts)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            if (!await ValidateFormAsync(context, antiforgery))
                return Results.LocalRedirect("/login?status=form-expired");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var rememberMe = string.Equals(form["rememberMe"], "on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(form["rememberMe"], "true", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return Results.LocalRedirect("/login?status=Email%20and%20password%20are%20required");

            try
            {
                using var response = await clients.CreateClient("SystemApiNoAuth").PostAsJsonAsync(
                    "/api/v1/local-auth/login", new { email, password, rememberMe }, context.RequestAborted);
                ForwardLoginCookies(response, context, localCookieName, configuration);
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    var result = await response.Content.ReadFromJsonAsync<PasswordVerificationResult>(context.RequestAborted);
                    if (result?.RequiresTwoFactor == true && HasCookie(response, ChallengeCookieName(configuration)))
                        return Results.LocalRedirect("/login/two-factor");
                }
                else if (response.StatusCode == HttpStatusCode.NoContent && HasCookie(response, localCookieName))
                {
                    return Results.LocalRedirect("/home");
                }
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return Results.LocalRedirect("/login?status=Sign-in%20failed");
                }
                else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return Results.LocalRedirect("/login?status=rate-limited");
                }

                loggers.CreateLogger("LocalBrowserLogin").LogWarning(
                    "Local sign-in could not complete. API status {StatusCode}; request {RequestId}.",
                    (int)response.StatusCode, context.TraceIdentifier);
                return Results.LocalRedirect("/login?status=service-unavailable");
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException ||
                exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                loggers.CreateLogger("LocalBrowserLogin").LogWarning(
                    "Local sign-in service unavailable ({ExceptionType}); request {RequestId}.",
                    exception.GetType().Name, context.TraceIdentifier);
                return Results.LocalRedirect("/login?status=service-unavailable");
            }
        }).AllowAnonymous().RequireRateLimiting("LocalBrowserLogin");

        app.MapPost("/local-login/two-factor", async (HttpContext context, IHttpClientFactory clients, IAntiforgery antiforgery,
            IDataProtectionProvider protection, IConfiguration configuration, ILoggerFactory loggers) =>
        {
            if (!supportsLocalAccounts)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            if (!await ValidateFormAsync(context, antiforgery))
                return Results.LocalRedirect("/login?status=form-expired");
            if (ReadChallenge(context, configuration, protection) is null)
                return Results.LocalRedirect("/login?status=verification-expired");

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var code = form["code"].ToString();
            if (string.IsNullOrWhiteSpace(code))
                return Results.LocalRedirect("/login/two-factor?status=code-required");

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/local-auth/login/two-factor")
            {
                Content = JsonContent.Create(new { code })
            };
            var challengeCookieName = ChallengeCookieName(configuration);
            request.Headers.Add("Cookie", $"{challengeCookieName}={context.Request.Cookies[challengeCookieName]}");
            try
            {
                using var response = await clients.CreateClient("SystemApiNoAuth").SendAsync(request, context.RequestAborted);
                ForwardLoginCookies(response, context, localCookieName, configuration);
                if (response.StatusCode == HttpStatusCode.NoContent && HasCookie(response, localCookieName))
                    return Results.LocalRedirect("/home");
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return Results.LocalRedirect(HasCookie(response, challengeCookieName)
                        ? "/login?status=verification-expired"
                        : "/login/two-factor?status=invalid-code");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return Results.LocalRedirect("/login/two-factor?status=rate-limited");

                loggers.CreateLogger("LocalBrowserLogin").LogWarning(
                    "Local second-factor sign-in could not complete. API status {StatusCode}; request {RequestId}.",
                    (int)response.StatusCode, context.TraceIdentifier);
                return Results.LocalRedirect("/login/two-factor?status=service-unavailable");
            }
            catch (Exception exception) when (exception is HttpRequestException ||
                exception is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                loggers.CreateLogger("LocalBrowserLogin").LogWarning(
                    "Local second-factor service unavailable ({ExceptionType}); request {RequestId}.",
                    exception.GetType().Name, context.TraceIdentifier);
                return Results.LocalRedirect("/login/two-factor?status=service-unavailable");
            }
        }).AllowAnonymous().RequireRateLimiting("LocalBrowserLogin");
    }

    public static LocalSignInChallenge? ReadChallenge(HttpContext? context, IConfiguration configuration, IDataProtectionProvider protection)
    {
        var value = context?.Request.Cookies[ChallengeCookieName(configuration)];
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var payload = protection.CreateProtector(LocalSignInChallenge.ProtectionPurpose)
                .ToTimeLimitedDataProtector().Unprotect(value);
            return JsonSerializer.Deserialize<LocalSignInChallenge>(payload);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static string ChallengeCookieName(IConfiguration configuration) =>
        LocalSignInChallenge.CookieName(configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost"));

    private static bool HasCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
        cookies.Any(cookie => cookie.StartsWith(name + "=", StringComparison.Ordinal));

    private static void ForwardLoginCookies(HttpResponseMessage response, HttpContext context, string localCookieName, IConfiguration configuration)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return;
        var challengeCookieName = ChallengeCookieName(configuration);
        foreach (var cookie in cookies)
        {
            var name = cookie.Split('=', 2)[0];
            if (name == localCookieName || name == challengeCookieName ||
                name.StartsWith(localCookieName + "C", StringComparison.Ordinal) &&
                int.TryParse(name[(localCookieName.Length + 1)..], out _))
                context.Response.Headers.Append("Set-Cookie", cookie);
        }
    }

    private static async Task<bool> ValidateFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private sealed record PasswordVerificationResult(bool RequiresTwoFactor);
}
