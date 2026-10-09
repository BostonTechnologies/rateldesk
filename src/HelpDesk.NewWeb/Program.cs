using FormCraft;
using FormCraft.ForMudBlazor;
using FormCraft.ForMudBlazor.Extensions;
using Gotho.BlazorPdf;
using HelpDesk.NewWeb;
using HelpDesk.NewWeb.Components;
using HelpDesk.NewWeb.Models;
using HelpDesk.NewWeb.Services;
using HelpDesk.NewWeb.Services.Search;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Pairing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MudBlazor.Services;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Claims;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var resolvedAuthenticationMode = WebAuthenticationMode.Resolve(builder.Configuration);
builder.Services.AddSingleton(resolvedAuthenticationMode);
var webAuthenticationMode = resolvedAuthenticationMode.Value;
const string localAuthenticationScheme = "RatelDeskLocal";
var webSupportsLocalAccounts = resolvedAuthenticationMode.SupportsLocalAccounts;
var webUsesOidc = resolvedAuthenticationMode.UsesOidc;
var webIsHybrid = resolvedAuthenticationMode.IsHybrid;
var localCookieName = builder.Configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost")
    ? "RatelDesk.Local"
    : "__Host-RatelDesk.Local";
var webDefaultScheme = webIsHybrid
    ? "RatelDeskWeb"
    : webSupportsLocalAccounts
        ? localAuthenticationScheme
        : CookieAuthenticationDefaults.AuthenticationScheme;

builder.AddServiceDefaults();

// The bounded browser roundtrip contains temporary proof values; never export its URI into request spans.
builder.Services.Configure<OpenTelemetry.Instrumentation.AspNetCore.AspNetCoreTraceInstrumentationOptions>(options =>
{
    var existingFilter = options.Filter;
    options.Filter = context => !context.Request.Path.StartsWithSegments("/account/integration-credentials/link")
        && (existingFilter?.Invoke(context) ?? true);
});

// DataProtection (shared ring for API + Web)
var dpSection = builder.Configuration.GetSection("DataProtection");
var keyRingPath = dpSection["KeyRingPath"]
                  ?? Path.Combine(Path.GetTempPath(), "rateldesk", "keys");

Directory.CreateDirectory(keyRingPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName(dpSection["ApplicationName"] ?? "Helpdesk-Keyring");

// Add MudBlazor services
builder.Services.AddMudServices();

builder.Services.AddBlazorPdfViewer();
builder.Services.AddFormCraft();
builder.Services.AddFormCraftMudBlazor();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllers();

builder.Services.AddAntiforgery();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("LocalBrowserLogin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddAuthorizationCore(options =>
{
    options.AddPolicy("DataManagementAccess", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin, HelpdeskPermissions.DataManagementAdmin);
    });
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<TokenAuthorizationHandler>();
builder.Services.AddTransient<SystemTokenAuthorizationHandler>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<CookieOidcSessionEvents>();
builder.Services.AddScoped<CookieLocalSessionEvents>();
builder.Services.AddSingleton<ISystemTokenService, SystemTokenService>();
builder.Services.AddScoped<IErrorLoggingService, ErrorLoggingService>();
builder.Services.AddScoped<IRequestService, RequestService>();
builder.Services.AddScoped<IRequestFormService, RequestFormService>();
builder.Services.AddScoped<IResourceDatasetService, ResourceDatasetService>();
builder.Services.AddScoped<IInboundEmailRuleAdminClient, InboundEmailRuleAdminClient>();
builder.Services.AddScoped<ISupportNotificationAdminClient, SupportNotificationAdminClient>();
builder.Services.AddScoped<ISystemNotificationApiClient, SystemNotificationApiClient>();
builder.Services.AddScoped<ITimelineApiClient, TimelineApiClient>();
builder.Services.AddScoped<TimelineStreamService>();
builder.Services.AddScoped<NotificationStreamService>();
builder.Services.AddSingleton<NotificationEventBus>();
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();
builder.Services.AddScoped<IGlobalSearchService, GlobalSearchService>();
builder.Services.AddScoped<IAppBarVersionApiClient, AppBarVersionApiClient>();
if (!string.Equals(webAuthenticationMode, "Local", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IValidateOptions<AuthentikOidcOptions>, AuthentikOidcOptionsValidator>();
    builder.Services.AddOptions<AuthentikOidcOptions>()
        .Bind(builder.Configuration.GetSection("Authentication:Authentik"))
        .ValidateOnStart();
}
builder.Services.AddSingleton<IValidateOptions<AuthentikAiAgentOptions>, AuthentikAiAgentOptionsValidator>();
builder.Services.AddOptions<AuthentikAiAgentOptions>()
    .Bind(builder.Configuration.GetSection("Authentication:AuthentikAiAgent"))
    .ValidateOnStart();
builder.Services.AddSingleton<IAuthentikAiAgentTokenValidator, AuthentikAiAgentTokenValidator>();
builder.Services.AddOptions<NotificationPageOptions>()
    .Bind(builder.Configuration.GetSection("Notifications"));

var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(apiBaseUrl))
{
    throw new InvalidOperationException("ApiBaseUrl is required. Set it through configuration or an environment variable.");
}

var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = webDefaultScheme;
    options.DefaultChallengeScheme = webUsesOidc ? "Authentik" : localAuthenticationScheme;
});

if (webUsesOidc)
{
    authentication.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "__Host-Helpdesk.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.EventsType = typeof(CookieOidcSessionEvents);
    });
}

if (webSupportsLocalAccounts)
{
    authentication.AddCookie(localAuthenticationScheme, options =>
    {
        options.Cookie.Name = localCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost") || builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.EventsType = typeof(CookieLocalSessionEvents);
    });
}

if (webIsHybrid)
{
    authentication.AddPolicyScheme("RatelDeskWeb", "RatelDesk browser session", options =>
    {
        options.ForwardDefaultSelector = context => context.Request.Cookies.ContainsKey(localCookieName)
            ? localAuthenticationScheme
            : CookieAuthenticationDefaults.AuthenticationScheme;
    });
}

if (webUsesOidc)
{
    authentication.AddOpenIdConnect("Authentik", options =>
{
    builder.Configuration.GetSection("Authentication:Authentik").Bind(options);
    var humanOidc = HumanOidcRuntimeOptionsResolver.Resolve(builder.Configuration);
    options.Authority = humanOidc.Authority;
    options.ClientId = humanOidc.ClientId;
    options.ClientSecret = humanOidc.ClientSecret;
    options.MapInboundClaims = false;
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
    options.GetClaimsFromUserInfoEndpoint = false;
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("email");
    options.Scope.Add("profile");
    options.Scope.Add("offline_access");
    if (!string.IsNullOrWhiteSpace(humanOidc.ApiScope))
        options.Scope.Add(humanOidc.ApiScope);

    options.TokenValidationParameters = new TokenValidationParameters
    {
        NameClaimType = "preferred_username",
        RoleClaimType = "roles"
    };

    options.CallbackPath = humanOidc.CallbackPath!;
    options.SignedOutCallbackPath = humanOidc.SignedOutCallbackPath!;
    options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    // (optional) belt & suspenders: build an HTTPS redirect using forwarded headers if present
    options.Events = new OpenIdConnectEvents
    {
        OnRedirectToIdentityProvider = ctx =>
        {
            var req = ctx.Request;
            var proto = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
            var host = req.Headers["X-Forwarded-Host"].FirstOrDefault() ?? req.Host.Value;
            var basePath = req.PathBase.ToString();
            ctx.ProtocolMessage.RedirectUri = $"{proto}://{host}{basePath}{options.CallbackPath}";
            return Task.CompletedTask;
        },
        OnTokenValidated = context => QueueUserProvisioningOnTokenValidatedAsync(context),
        OnRemoteFailure = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("ExternalSignIn")
                .LogWarning("External sign-in failed: {Category}.", SignInFailureDiagnostics.Category(context.Failure));
            context.HandleResponse();
            context.Response.Redirect("/login?status=Authentication%20failed");
            return Task.CompletedTask;
        }
    };
    });
}

// Web & System API clients
var helpdeskApiClient = builder.Services.AddHttpClient("HelpdeskApi", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(5);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
    .AddHttpMessageHandler<TokenAuthorizationHandler>();

// Pairing consumes a one-time code; a transient response must never replay this POST.
var netclawPairingApiClient = builder.Services.AddHttpClient("NetclawPairingApi", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(5);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
    .AddHttpMessageHandler<TokenAuthorizationHandler>();

// Mutations retain explicit operation IDs; automatic retries must not replace pairing codes.
var systemPairingApiClient = builder.Services.AddHttpClient("SystemPairingApi", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(60);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    .AddHttpMessageHandler<TokenAuthorizationHandler>();

var helpdeskApiStreamingClient = builder.Services.AddHttpClient("HelpdeskApiStreaming", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan;
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
    .AddHttpMessageHandler<TokenAuthorizationHandler>();

#pragma warning disable EXTEXP0001
helpdeskApiClient.RemoveAllResilienceHandlers();
netclawPairingApiClient.RemoveAllResilienceHandlers();
systemPairingApiClient.RemoveAllResilienceHandlers();
helpdeskApiClient.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);

    options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(4); // at least 2x attempt timeout (120s)
});

helpdeskApiStreamingClient.RemoveAllResilienceHandlers();
helpdeskApiStreamingClient.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(4);
});
#pragma warning restore EXTEXP0001

builder.Services.AddHttpClient("SystemApi", c =>
{
    c.BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute);
    c.Timeout = TimeSpan.FromSeconds(100);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
    .AddHttpMessageHandler<SystemTokenAuthorizationHandler>();

builder.Services.AddHttpClient("SystemApiNoAuth", c =>
{
    c.BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute);
    c.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });

builder.Services.AddSingleton<InstanceSetupStatusClient>();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = ctx.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("WebUnhandledException");
        logger.LogError(
            ex,
            "Unhandled web exception. Path={Path} RequestId={RequestId} TraceId={TraceId} User={User}",
            ctx.Request.Path,
            Activity.Current?.Id ?? ctx.TraceIdentifier,
            Activity.Current?.TraceId.ToString(),
            ctx.User.Identity?.Name ?? "anonymous");
        throw;
    }
});

var fwd = new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedFor
};

// Trust any proxy (dev-friendly). In prod, list the proxy IP(s) instead.
fwd.KnownIPNetworks.Clear();
fwd.KnownProxies.Clear();

// (Optional) lock to Traefik's IP
//fwd.KnownProxies.Add(IPAddress.Parse("192.168.1.65"));
app.UseForwardedHeaders(fwd);

app.UseHttpsRedirection();
app.UseMiddleware<FirstRunEntryMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value;
    if (string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, "/api/", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.Redirect("/api/docs/");
        return;
    }

    if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(path, PairingContract.Root + "/metadata", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/docs", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/openapi", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/incidents", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/worklogs", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/email-templates", StringComparison.OrdinalIgnoreCase) &&
        !ctx.Request.Path.StartsWithSegments("/api/tenants", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.MapScalarApiReference(
    "/api/docs",
    (options, context) =>
    {
        var request = context.Request;
        var openApiUrl = $"{request.Scheme}://{request.Host}{request.PathBase}/api/openapi/v1.json";
        options.AddDocument("v1", "RatelDesk API", openApiUrl, isDefault: true);
        options.Servers = new[] { new ScalarServer("/api") };
    }).AllowAnonymous();

app.MapReverseProxy().AllowAnonymous();

app.MapMethods("/hangfire/{**path}", ["GET", "POST", "PUT", "DELETE", "HEAD"], async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    ITokenService tokenService,
    IConfiguration configuration) =>
{
    if (!(context.User?.Identity?.IsAuthenticated ?? false) || !context.User.IsInRole("HelpdeskAdmin"))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var token = await tokenService.GetValidAccessTokenAsync();
    var localSessionCookie = context.User.HasClaim("auth_mode", "local")
        ? LocalSessionCookieForwarder.GetHeader(context, localCookieName)
        : null;
    if (string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(localSessionCookie))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var configuredApiBaseUrl = configuration["ApiBaseUrl"] ?? throw new InvalidOperationException("ApiBaseUrl is required.");
    var path = context.Request.RouteValues.TryGetValue("path", out var pathValue)
        ? pathValue?.ToString()
        : null;
    var targetPath = string.IsNullOrWhiteSpace(path) ? "hangfire" : $"hangfire/{path.TrimStart('/')}";
    var targetUri = new Uri(new Uri(configuredApiBaseUrl, UriKind.Absolute), $"{targetPath}{context.Request.QueryString}");

    using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
    if (!string.IsNullOrWhiteSpace(token))
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    else
    {
        requestMessage.Headers.TryAddWithoutValidation("Cookie", localSessionCookie);
        requestMessage.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
    }

    foreach (var header in context.Request.Headers)
    {
        if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
            || string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && context.Request.ContentLength > 0)
        {
            requestMessage.Content ??= new StreamContent(context.Request.Body);
            requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    if (context.Request.ContentLength > 0 && requestMessage.Content is null)
    {
        requestMessage.Content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
        {
            requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }
    }

    var client = httpClientFactory.CreateClient("SystemApiNoAuth");

    try
    {
        using var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        context.Response.StatusCode = (int)responseMessage.StatusCode;
        foreach (var header in responseMessage.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in responseMessage.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        context.Response.Headers.Remove("transfer-encoding");
        await responseMessage.Content.CopyToAsync(context.Response.Body);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // Hangfire issues short-lived AJAX requests that the browser may cancel while navigating.
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 499;
        }
    }
    catch (HttpRequestException ex) when (context.RequestAborted.IsCancellationRequested
        || ex.InnerException is IOException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 499;
        }
    }
}).RequireAuthorization();

app.MapStaticAssets();
app.MapControllers();

//app.MapGet("/oidc-config", (IOptions<HelpDesk.NewWeb.Models.OidcClientOptions> options) =>
//    Results.Json(options.Value));

app.MapGet("/login-authentik", async (HttpContext ctx) =>
{
    if (!webUsesOidc)
    {
        return Results.LocalRedirect("/login");
    }

    await ctx.ChallengeAsync("Authentik", new AuthenticationProperties { RedirectUri = "/home" });
    return Results.Empty;
});

app.MapGet("/login-azure", () => Results.LocalRedirect("/login-authentik"));

app.MapLocalBrowserLoginEndpoints(webSupportsLocalAccounts, localCookieName);
app.MapPairingMetadataEndpoint();

app.MapGet("/login-ai-agent", (IOptions<AuthentikAiAgentOptions> options) =>
{
    if (!options.Value.Enabled)
    {
        return Results.NotFound();
    }

    return Results.Text(
        "POST a valid Authentik AI agent token to /auth/ai-agent/exchange to establish a session for this dev instance.",
        "text/plain");
}).AllowAnonymous();

app.MapGet("/Account/Login", () => Results.LocalRedirect("/login"));
app.MapGet("/Account/AccessDenied", () => Results.LocalRedirect("/access-denied"));

app.MapPost("/auth/ai-agent/exchange", async (
    HttpContext ctx,
    AiAgentExchangeRequest? request,
    IOptions<AuthentikAiAgentOptions> options,
    IAuthentikAiAgentTokenValidator tokenValidator) =>
{
    if (!options.Value.Enabled)
    {
        return Results.NotFound();
    }

    var token = request?.Token;
    if (string.IsNullOrWhiteSpace(token))
    {
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader["Bearer ".Length..].Trim();
        }
    }

    if (string.IsNullOrWhiteSpace(token))
    {
        return Results.BadRequest(new { error = "missing_token" });
    }

    var validation = await tokenValidator.ValidateAsync(token, ctx.RequestAborted);
    var redirectUri = NormalizeLocalRedirect(request?.ReturnUrl, "/home");

    var properties = new AuthenticationProperties
    {
        IsPersistent = true,
        RedirectUri = redirectUri,
        ExpiresUtc = validation.ExpiresAt
    };

    properties.StoreTokens(
    [
        new AuthenticationToken { Name = "access_token", Value = token },
        new AuthenticationToken { Name = "expires_at", Value = validation.ExpiresAt.ToString("o") }
    ]);

    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, validation.Principal, properties);
    return Results.LocalRedirect(redirectUri);
}).AllowAnonymous();

app.MapGet("/auth/development", async (
    HttpContext ctx,
    IHostEnvironment environment,
    IConfiguration config,
    ISystemTokenService systemTokenService) =>
{
    if (!environment.IsDevelopment() || !config.GetValue<bool>("DevelopmentOperator:Enabled"))
        return Results.NotFound();

    var token = await systemTokenService.GetTokenAsync();
    var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
    var claims = jwt.Claims
        .Where(c => c.Type is not JwtRegisteredClaimNames.Exp
            and not JwtRegisteredClaimNames.Nbf
            and not JwtRegisteredClaimNames.Iat
            and not JwtRegisteredClaimNames.Aud
            and not JwtRegisteredClaimNames.Iss)
        .Select(c => new Claim(c.Type, c.Value))
        .ToList();

    var roleValues = claims
        .Where(c => c.Type == "roles")
        .Select(c => c.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var role in roleValues)
    {
        claims.Add(new Claim(ClaimTypes.Role, role));
    }

    if (claims.All(c => c.Type != ClaimTypes.Name))
    {
        var name = claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
            ?? claims.FirstOrDefault(c => c.Type == "name")?.Value
            ?? "Codex Local Operator";
        claims.Add(new Claim(ClaimTypes.Name, name));
    }

    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        claims,
        CookieAuthenticationDefaults.AuthenticationScheme,
        "preferred_username",
        ClaimTypes.Role));

    var properties = new AuthenticationProperties
    {
        IsPersistent = true,
        RedirectUri = "/home",
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30)
    };

    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    return Results.LocalRedirect("/home");
});

// Browser polling uses the Web session for both local and OIDC modes. API tokens
// remain on the server and anonymous callers receive 401 rather than an OIDC redirect.
app.MapGet("/session/access", async (HttpContext context, ITokenService tokens, IHttpClientFactory clients) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
    var token = await tokens.GetValidAccessTokenAsync();
    if (!string.IsNullOrWhiteSpace(token))
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    else if (context.User.HasClaim("auth_mode", "local") && LocalSessionCookieForwarder.GetHeader(context, localCookieName) is { } cookie)
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
    else
        return Results.Unauthorized();

    try
    {
        using var response = await clients.CreateClient("SystemApiNoAuth").SendAsync(request, context.RequestAborted);
        if (!response.IsSuccessStatusCode) return Results.StatusCode((int)response.StatusCode);
        return Results.Content(await response.Content.ReadAsStringAsync(context.RequestAborted), "application/json");
    }
    catch (HttpRequestException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous();

app.MapGet("/logout", async (HttpContext ctx) =>
{
    var isLocalSession = string.Equals(ctx.User.FindFirst("auth_mode")?.Value, "local", StringComparison.OrdinalIgnoreCase);
    if (webSupportsLocalAccounts)
    {
        await ctx.SignOutAsync(localAuthenticationScheme);
    }
    if (webUsesOidc)
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    if (isLocalSession || !webUsesOidc ||
        string.Equals(ctx.User.FindFirst("auth_mode")?.Value, "ai_agent", StringComparison.OrdinalIgnoreCase))
    {
        return Results.LocalRedirect("/login");
    }

    await ctx.SignOutAsync("Authentik", new AuthenticationProperties { RedirectUri = "/" });
    return Results.Empty;
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static Task QueueUserProvisioningOnTokenValidatedAsync(TokenValidatedContext context)
{
    var requestServices = context.HttpContext.RequestServices;
    var principal = context.Principal;
    if (principal is null)
        return Task.CompletedTask;

    return EnrichPrincipalAsync();

    async Task EnrichPrincipalAsync()
    {
        try
        {
            var provisioningService = requestServices.GetRequiredService<IUserProvisioningService>();
            var accessToken = context.TokenEndpointResponse?.AccessToken;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                requestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Provisioning")
                    .LogWarning("External sign-in failed: missing-api-access-token.");
                context.Fail("The identity provider did not issue an API access token.");
                return;
            }
            var access = await provisioningService.EnsureUserAccessAsync(principal, accessToken, context.HttpContext.RequestAborted);
            if (principal.Identity is ClaimsIdentity identity)
            {
                if (access is null)
                {
                    context.Fail("Account access could not be established.");
                    return;
                }

                WebAccessClaimsProjection.Apply(identity, access);
            }
        }
        catch (Exception ex)
        {
            var logger = requestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Provisioning");

            logger.LogWarning("External sign-in provisioning failed: {Category}.", SignInFailureDiagnostics.Category(ex));
            context.Fail("Account access could not be established.");
        }
    }
}

static string NormalizeLocalRedirect(string? value, string fallback)
{
    if (string.IsNullOrWhiteSpace(value)
        || !value.StartsWith("/", StringComparison.Ordinal)
        || value.StartsWith("//", StringComparison.Ordinal))
    {
        return fallback;
    }

    return value;
}

public sealed record AiAgentExchangeRequest(string? Token, string? ReturnUrl);
