using Helpdesk.AgentClient;
using Helpdesk.Mcp;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Http.Authorization;
using Helpdesk.Mcp.Http.Configuration;
using Helpdesk.Mcp.Http.Observability;
using Helpdesk.Mcp.Prompts;
using Helpdesk.Mcp.Resources;
using Helpdesk.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Authentication;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using Helpdesk.Setup;

namespace Helpdesk.Mcp.Http;

public sealed partial class Program
{
    public static void Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Out.WriteLine("RatelDesk HTTP MCP host\n\nExplicit commands before server validation: setup [--mode host|client] [--target http-host|http-client|cli|stdio] [--url URL] [--profile NAME|--config PATH] [--export-directory DIR] [--monochrome]; host preflight|approval-check|verify [--export-directory DIR]; status; config validate; doctor [--network] [--authenticate]; profile list/show/remove; auth status/logout. Bare setup connects a personal HTTP client. Run ./setup-http-mcp.sh on the Docker host for deployment discovery and apply. Normal startup serves HTTP without prompting.");
            return;
        }
        if (args is ["--version"])
        {
            Console.Out.WriteLine($"rateldesk-mcp-http {typeof(Program).Assembly.GetName().Version}");
            return;
        }
        if (SetupCommands.IsCommand(args))
        {
            Environment.ExitCode = SetupCommands.InvokeAsync(args, SetupTransport.Http).GetAwaiter().GetResult();
            return;
        }
        const string McpScheme = "HelpdeskMcp";
        const string JwtScheme = "HelpdeskMcpJwt";
        const string PolicyName = "HelpdeskMcpAccess";

        var builder = WebApplication.CreateBuilder(args);
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();
        builder.Services.AddServiceDiscovery();

        var httpOptions = builder.Configuration.GetSection(HelpdeskMcpHttpOptions.SectionName).Get<HelpdeskMcpHttpOptions>() ?? new HelpdeskMcpHttpOptions();
        var authenticationMode = httpOptions.AuthenticationMode.Trim().ToLowerInvariant();
        var usesGatewayCredentials = string.Equals(authenticationMode, "gateway", StringComparison.Ordinal);
        var configurationPath = string.IsNullOrWhiteSpace(httpOptions.ConfigurationPath)
            ? Environment.GetEnvironmentVariable(HelpdeskMcpTarget.ConfigurationEnvironmentVariable)
            : httpOptions.ConfigurationPath;
        var agentConfiguration = AgentClientConfigurationResolver.LoadMcp(builder.Configuration, configurationPath);
        var target = HelpdeskMcpTarget.Resolve(agentConfiguration, configurationPath, httpOptions.Instance, httpOptions.ExpectedApiBaseUrl);

        builder.Services.AddSingleton<IValidateOptions<HelpdeskMcpHttpOptions>, HelpdeskMcpHttpOptionsValidator>();
        builder.Services.AddOptions<HelpdeskMcpHttpOptions>()
            .Bind(builder.Configuration.GetSection(HelpdeskMcpHttpOptions.SectionName))
            .ValidateOnStart();
        var usesGatewayDownstream = string.Equals(agentConfiguration.CredentialMode?.Trim(), "gateway", StringComparison.OrdinalIgnoreCase);
        if (usesGatewayCredentials && !usesGatewayDownstream)
            throw new AgentClientValidationException("Local HTTP MCP gateway mode requires credentialMode=gateway and does not accept a deployment credential.");
        if (!usesGatewayCredentials && usesGatewayDownstream)
            throw new AgentClientValidationException("credentialMode=gateway requires HTTP gateway ingress so each API execution identity is supplied by its authenticated request.");

        AuthentikMcpOptions? authOptions = null;
        if (!usesGatewayCredentials)
        {
            builder.Services.AddSingleton<IValidateOptions<AuthentikMcpOptions>, AuthentikMcpOptionsValidator>();
            builder.Services.AddOptions<AuthentikMcpOptions>()
                .Bind(builder.Configuration.GetSection(AuthentikMcpOptions.SectionName))
                .ValidateOnStart();
            authOptions = builder.Configuration.GetSection(AuthentikMcpOptions.SectionName).Get<AuthentikMcpOptions>() ?? new AuthentikMcpOptions();
        }

        var hostContext = target.ToHostContext("http", httpOptions.PublicResourceUri) with
        {
            IngressAuthenticationMode = authenticationMode
        };
        var resourceMetadataUri = usesGatewayCredentials
            ? null
            : new Uri(new Uri(hostContext.ResourceUri), "/.well-known/oauth-protected-resource/mcp");
        var protectedResourceMetadata = usesGatewayCredentials
            ? null
            : new ProtectedResourceMetadata
            {
                Resource = hostContext.ResourceUri,
                AuthorizationServers = [$"{authOptions!.Authority.TrimEnd('/')}/"],
                ScopesSupported = authOptions.RequiredScopes,
                BearerMethodsSupported = ["header"]
            };
        IServiceProvider? applicationServices = null;
        var resourceTarget = new HelpdeskResources(
            () => applicationServices?.GetRequiredService<IHelpdeskAgentClient>()
                ?? throw new InvalidOperationException("Helpdesk MCP resources were invoked before the application service provider was available."),
            hostContext);

        builder.Services.AddHelpdeskMcpCore(agentConfiguration, new ReadOnlyHelpdeskMcpConfigurationSurface(agentConfiguration), hostContext);
        var authentication = builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = usesGatewayCredentials ? GatewayDelegationAuthenticationHandler.SchemeName : McpScheme;
            options.DefaultChallengeScheme = usesGatewayCredentials ? GatewayDelegationAuthenticationHandler.SchemeName : McpScheme;
        });
        if (usesGatewayCredentials)
        {
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddHttpClient(GatewayDelegationAuthenticationHandler.DelegationClientName, client =>
            {
                client.BaseAddress = new Uri(hostContext.CanonicalApiBaseUrl);
                // The authentication handler owns one linked deadline which
                // includes both headers and body consumption.
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
            builder.Services.RemoveAll<IHelpdeskAgentClient>();
            builder.Services.AddSingleton<IAgentAccessTokenProvider, GatewayExecutionTokenProvider>();
            builder.Services.AddSingleton<IHelpdeskAgentClient>(provider => new HelpdeskAgentClient(
                agentConfiguration,
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(),
                provider.GetRequiredService<IAgentAccessTokenProvider>()));
            authentication.AddScheme<AuthenticationSchemeOptions, GatewayDelegationAuthenticationHandler>(
                GatewayDelegationAuthenticationHandler.SchemeName,
                _ => { });
        }
        else
        {
            authentication.AddJwtBearer(JwtScheme, options =>
            {
                options.Authority = authOptions!.Authority;
                options.Audience = authOptions.Audience;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authOptions.Authority.TrimEnd('/'),
                    ValidateAudience = true,
                    ValidAudience = authOptions.Audience.TrimEnd('/'),
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true
                };
            })
            .AddMcp(McpScheme, "Helpdesk MCP", options =>
            {
                options.ForwardAuthenticate = JwtScheme;
                options.ResourceMetadataUri = resourceMetadataUri!;
                options.ResourceMetadata = protectedResourceMetadata!;
            });
        }
        builder.Services.AddSingleton<IAuthorizationHandler, RequiredMcpClaimsHandler>();
        builder.Services.AddAuthorization(options => options.AddPolicy(PolicyName, policy =>
        {
            policy.AddAuthenticationSchemes(usesGatewayCredentials ? GatewayDelegationAuthenticationHandler.SchemeName : McpScheme);
            policy.RequireAuthenticatedUser();
            if (!usesGatewayCredentials)
            {
                policy.Requirements.Add(new RequiredMcpClaimsRequirement(
                    authOptions!.RequiredScopes.ToHashSet(StringComparer.Ordinal),
                    authOptions.RequiredGroups.ToHashSet(StringComparer.Ordinal)));
            }
        }));
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("HelpdeskMcp", limiter =>
        {
            limiter.PermitLimit = 32;
            limiter.QueueLimit = 64;
            limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        }));
        builder.Services.AddSingleton<McpToolInvocationAudit>();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(HelpdeskToolDefinitions.Create())
            .WithResources(resourceTarget)
            .WithPromptsFromAssembly(typeof(HelpdeskPrompts).Assembly)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
            {
                var services = context.Services ?? throw new InvalidOperationException("MCP request context does not have a service provider.");
                return services.GetRequiredService<McpToolInvocationAudit>().InvokeAsync(
                    (request, ct) => SetupVerificationAuthorization.InvokeToolAsync(
                        request.User, request.Params.Name, request.Params.Arguments, token => next(request, token), ct),
                    context, cancellationToken);
            })
                .AddReadResourceFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddGetPromptFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddListResourcesFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddListResourceTemplatesFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddListPromptsFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddCompleteFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddSubscribeToResourcesFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddUnsubscribeFromResourcesFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); })
                .AddSetLoggingLevelFilter(next => (context, ct) => { SetupVerificationAuthorization.RequireOrdinary(context.User); return next(context, ct); }))
            .AddAuthorizationFilters();

        var app = builder.Build();
        applicationServices = app.Services;
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp") && context.Request.Headers.Origin is { Count: > 0 } origin &&
                !httpOptions.AllowedOrigins.Contains(origin.ToString(), StringComparer.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready");
        if (protectedResourceMetadata is not null)
            app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(protectedResourceMetadata)).AllowAnonymous();
        app.MapMcp("/mcp").RequireAuthorization(PolicyName).RequireRateLimiting("HelpdeskMcp");
        app.Run();
    }
}
