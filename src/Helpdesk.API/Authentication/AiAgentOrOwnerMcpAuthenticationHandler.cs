using System.Text.Encodings.Web;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Authentication;

/// <summary>Reuses the configured AI identity or the API's explicitly delegated owner identity.</summary>
public sealed class AiAgentOrOwnerMcpAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAuthenticationSchemeProvider schemes,
    ICurrentUserAccessService accessService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AiAgentOrOwnerMcp";
    public const string PolicyName = SchemeName;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        // This selects a validator only. A prefix never grants authority: that
        // validator verifies the token and, for MCP, reloads its durable grant.
        var ownerExecution = authorization["Bearer ".Length..].Trim()
            .StartsWith(McpExecutionTokenService.TokenPrefix, StringComparison.Ordinal);
        var selectedScheme = ownerExecution ? McpExecutionAuthenticationHandler.SchemeName : "AuthentikAiAgent";
        if (await schemes.GetSchemeAsync(selectedScheme) is null)
            return AuthenticateResult.Fail("The requested AI operation identity is not configured.");

        var result = await Context.AuthenticateAsync(selectedScheme).ConfigureAwait(false);
        if (!result.Succeeded || result.Principal is null)
            return AuthenticateResult.Fail("An authorized AI operation identity is required.");

        if (ownerExecution)
        {
            var principal = result.Principal;
            if (!principal.HasClaim("token_use", "mcp_execution") ||
                !principal.HasClaim("integration_purpose", IntegrationCredentialGrantKinds.OwnerAdministrationPurpose))
                return AuthenticateResult.Fail("Owner administration was not delegated to this MCP credential.");

            var access = await accessService.ResolveAsync(principal, Context.RequestAborted).ConfigureAwait(false);
            if (!access.IsHelpdeskAdmin)
                return AuthenticateResult.Fail("The delegated owner no longer has administrator authority.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(result.Principal, SchemeName));
    }
}

public static class AiAgentOrOwnerMcpServiceCollectionExtensions
{
    public static IServiceCollection AddAiAgentOrOwnerMcp(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AiAgentOrOwnerMcpAuthenticationHandler>(
            AiAgentOrOwnerMcpAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.AddPolicy(AiAgentOrOwnerMcpAuthenticationHandler.PolicyName,
            policy => policy.AddAuthenticationSchemes(AiAgentOrOwnerMcpAuthenticationHandler.SchemeName).RequireAuthenticatedUser()));
        return services;
    }
}
