using System.Net;
using System.Security.Cryptography;
using Helpdesk.Shared.DTOs.PublicAccess;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace HelpDesk.NewWeb.Services;

internal static class PublicBrowserEndpoints
{
    internal static void MapPublicBrowserEndpoints(this IEndpointRouteBuilder app)
    {
        var methods = new[] { HttpMethods.Get, HttpMethods.Head };
        app.MapMethods("/public/link", methods, (HttpContext http, IAntiforgery antiforgery) =>
            Entry(http, antiforgery)).AllowAnonymous();
        app.MapMethods("/view-ticket/{trackingId?}", methods, (HttpContext http, IAntiforgery antiforgery, string? trackingId) =>
            Entry(http, antiforgery, PublicLinkPurpose.Ticket, trackingId)).AllowAnonymous();
        app.MapMethods("/change-approval/{trackingId}", methods, (HttpContext http, IAntiforgery antiforgery, string trackingId) =>
            Entry(http, antiforgery, PublicLinkPurpose.ChangeView, trackingId)).AllowAnonymous();
        app.MapMethods("/request-approval/{trackingId}/{taskId}", methods,
            (HttpContext http, IAntiforgery antiforgery, string trackingId, string taskId) =>
                Entry(http, antiforgery, PublicLinkPurpose.RequestApproval, trackingId, taskId)).AllowAnonymous();
        app.MapPost("/public/link", ExchangeAsync).AllowAnonymous();

        var access = app.MapGroup(PublicAccessProtocol.BrowserPrefix + "/{accessId:guid}").AllowAnonymous();
        access.MapPost("/change/decision", (Guid accessId, HttpContext http, IAntiforgery antiforgery, PublicBrowserAccess api) =>
            DecideAsync(accessId, http, antiforgery, api, "change"));
        access.MapPost("/request/decision", (Guid accessId, HttpContext http, IAntiforgery antiforgery, PublicBrowserAccess api) =>
            DecideAsync(accessId, http, antiforgery, api, "request"));
        access.MapGet("/asset/{reference}", ServeAssetAsync);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http, IAntiforgery antiforgery, PublicBrowserAccess api)
    {
        if (!await ValidateFormAsync(http, antiforgery))
            return Results.BadRequest();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var launch = form["launch"].ToString();
        LegacyPublicLink? legacy = null;
        if (launch.Length == 0 && Enum.TryParse<PublicLinkPurpose>(form["purpose"], out var purpose))
        {
            var task = form["task"].ToString();
            legacy = new(purpose, form["tracking"].ToString(), task.Length == 0 ? null : task,
                form["email"].ToString(), form["legacyToken"].ToString());
        }

        var browser = http.Request.Cookies[PublicBrowserAccess.BrowserCookie];
        var newBrowser = browser is not { Length: 43 };
        if (newBrowser)
            browser = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var exchange = new PublicLinkExchangeRequest(launch.Length == 0 ? null : launch, legacy, browser!);
        var replacement = form["action"] == "replace";

        try
        {
            using var response = await api.ExchangeAsync(http, exchange, replacement);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                http.Response.Headers.RetryAfter = response.Headers.RetryAfter?.ToString() ?? "30";
                return Entry(http, antiforgery, notice: "Too many requests. Wait a moment and try again.",
                    retry: exchange, statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (replacement && response.IsSuccessStatusCode)
                return Entry(http, antiforgery, notice: "If a replacement is available, it will be sent to the original recipient.");
            var grant = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<PublicLinkExchangeResponse>(http.RequestAborted) : null;
            if (grant is null || grant.Grant.Length is 0 or > 512)
                return Entry(http, antiforgery, notice: "This link has expired or has already been used.", retry: exchange);

            var path = PublicBrowserAccess.AccessPath(grant.AccessId);
            if (newBrowser)
                http.Response.Cookies.Append(PublicBrowserAccess.BrowserCookie, browser!,
                    Cookie("/", DateTimeOffset.UtcNow.AddHours(2)));
            http.Response.Cookies.Append(PublicBrowserAccess.GrantCookie, grant.Grant, Cookie(http.Request.PathBase + path, grant.ExpiresAt));
            var page = grant.Purpose switch
            {
                PublicLinkPurpose.ChangeView or PublicLinkPurpose.ChangeApproval => "change",
                PublicLinkPurpose.RequestApproval => "request",
                _ => "ticket"
            };
            return SeeOther(http, path + "/" + page);
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        {
            return Entry(http, antiforgery, notice: "This link could not be opened. Try again in a moment.", retry: exchange,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> DecideAsync(Guid accessId, HttpContext http, IAntiforgery antiforgery,
        PublicBrowserAccess api, string resource)
    {
        if (!await ValidateFormAsync(http, antiforgery))
            return Results.BadRequest();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var action = form["decision"].ToString();
        var reason = form["reason"].ToString().Trim();
        if (action is not ("approve" or "reject") || action == "reject" && reason.Length is < 1 or > 1000)
            return Results.BadRequest();
        using var response = await api.SendWithGrantAsync(http, accessId, HttpMethod.Post, $"{resource}/decision",
            JsonContent.Create(new PublicApprovalDecision(action == "approve", reason)));
        var suffix = response is { IsSuccessStatusCode: true } ? "" : "?review=unavailable";
        return SeeOther(http, $"{PublicBrowserAccess.AccessPath(accessId)}/{resource}{suffix}");
    }

    private static async Task<IResult> ServeAssetAsync(Guid accessId, string reference, HttpContext http, PublicBrowserAccess api)
    {
        if (reference.Length is 0 or > 1024 || !reference.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))
            return Results.NotFound();
        using var response = await api.SendWithGrantAsync(http, accessId, HttpMethod.Get, "asset/" + reference);
        if (response is not { IsSuccessStatusCode: true } || response.Content.Headers.ContentType is not { } contentType)
            return Results.NotFound();
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.ContentType = contentType.ToString();
        await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        return Results.Empty;
    }

    private static async Task<bool> ValidateFormAsync(HttpContext http, IAntiforgery antiforgery)
    {
        const int limit = 8192;
        if (!http.Request.HasFormContentType || http.Request.ContentLength > limit ||
            http.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
            return false;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
            body.MaxRequestBodySize = limit;
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static IResult Entry(HttpContext http, IAntiforgery antiforgery, PublicLinkPurpose? legacyPurpose = null,
        string? tracking = null, string? task = null, string? notice = null, PublicLinkExchangeRequest? retry = null, int statusCode = 200)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        http.Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; script-src 'nonce-{nonce}'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        if (HttpMethods.IsHead(http.Request.Method))
            return Results.Ok();
        var formToken = antiforgery.GetAndStoreTokens(http);
        var action = http.Request.PathBase + "/public/link";
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Open your RatelDesk link</title>
            <script nonce="{{nonce}}">
                const fragment = location.hash.slice(1);
                const parameters = new URLSearchParams(location.search);
                history.replaceState(null, '', {{System.Text.Json.JsonSerializer.Serialize(action)}});
                const setLaunch = value => {
                    const form = document.querySelector('form');
                    if (!/^[A-Za-z0-9_-]{43}$/.test(value)) return;
                    form.elements.launch.value = value;
                    form.hidden = false;
                    document.getElementById('email-instructions').hidden = true;
                };
                addEventListener('hashchange', () => {
                    const next = location.hash.slice(1);
                    history.replaceState(null, '', {{System.Text.Json.JsonSerializer.Serialize(action)}});
                    setLaunch(next);
                });
                document.addEventListener('DOMContentLoaded', () => {
                    const form = document.querySelector('form');
                    setLaunch(fragment);
                    if (!form.elements.launch.value && form.elements.purpose.value && parameters.has('token')) {
                        form.elements.legacyToken.value = parameters.get('token');
                        form.elements.email.value = parameters.get('email') || '';
                        if (!form.elements.tracking.value) form.elements.tracking.value = parameters.get('trackingId') || '';
                    }
                    if (form.elements.launch.value || form.elements.legacyToken.value) {
                        form.hidden = false;
                        document.getElementById('email-instructions').hidden = true;
                    }
                });
            </script></head><body><main><h1>Open your RatelDesk link</h1>
            <p role="status">{{Encode(notice)}}</p>
            <p id="email-instructions">Open the link in your RatelDesk email to continue.</p>
            <form method="post" action="{{Encode(action)}}" hidden>
                <input type="hidden" name="{{Encode(formToken.FormFieldName)}}" value="{{Encode(formToken.RequestToken)}}">
                <input type="hidden" name="launch" value="{{Encode(retry?.Launch)}}">
                <input type="hidden" name="purpose" value="{{Encode((retry?.Legacy?.Purpose ?? legacyPurpose)?.ToString())}}">
                <input type="hidden" name="tracking" value="{{Encode(retry?.Legacy?.TrackingId ?? tracking)}}">
                <input type="hidden" name="task" value="{{Encode(retry?.Legacy?.TaskId ?? task)}}">
                <input type="hidden" name="email" value="{{Encode(retry?.Legacy?.Email)}}">
                <input type="hidden" name="legacyToken" value="{{Encode(retry?.Legacy?.Token)}}">
                <button type="submit" name="action" value="open">Continue</button>
                {{(retry is null ? "" : """<button type="submit" name="action" value="replace">Email a replacement link</button>""")}}
            </form></main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static CookieOptions Cookie(string path, DateTimeOffset expires) =>
        new() { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = path, Expires = expires };

    private static IResult SeeOther(HttpContext http, string path)
    {
        http.Response.Headers.Location = http.Request.PathBase + path;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }
}
