using System.Threading.RateLimiting;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Authentication;

public static class ServiceIdentityServiceCollectionExtensions
{
    public const string ManagementPolicy = "ServiceClientManagement";
    public const string VerifyPolicy = "ServiceLinkVerify";
    public const string ControlPolicy = "ServiceLinkControl";
    public const string SensitiveRateLimiter = "ServiceIssuerSensitive";

    public static IServiceCollection AddRatelDeskServiceIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceIdentityOptions>().Bind(configuration.GetSection(ServiceIdentityOptions.SectionName)).ValidateOnStart();
        // Normalize both flags on every options creation, including reload.
        bool AllowPrivateHttp() => configuration.GetValue<bool>("ServiceIdentity:AllowPrivateHttp") ||
            configuration.GetValue<bool>("ServiceLinks:AllowPrivateHttp");
        services.PostConfigure<ServiceIdentityOptions>(value => value.AllowPrivateHttp = AllowPrivateHttp());
        services.PostConfigure<ServiceLinkOptions>(value => value.AllowPrivateHttp = AllowPrivateHttp());
        services.AddSingleton<IValidateOptions<ServiceIdentityOptions>, ServiceIdentityOptionsValidator>();
        services.AddScoped<IServicePrincipalRegistry, ServicePrincipalRegistry>();
        services.AddScoped<ServiceSigningKeyStore>();
        services.AddScoped<ServiceAccessTokenService>();
        services.AddScoped<IServicePublicSettingsResolver, ServicePublicSettingsResolver>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ServiceIdentityAuthenticationHandler>(ServiceIdentityAuthenticationHandler.SchemeName, _ => { });
        services.AddScoped<IAuthorizationHandler, ServiceClientManagementHandler>();
        services.AddAuthorization(o =>
        {
            o.AddPolicy(ManagementPolicy, p => p.RequireAuthenticatedUser().AddRequirements(new ServiceClientManagementRequirement()));
            foreach (var (policy, scope) in new[] { (VerifyPolicy, ServiceIdentityScopes.Verify), (ControlPolicy, ServiceIdentityScopes.Control) })
                o.AddPolicy(policy, p => p.AddAuthenticationSchemes(ServiceIdentityAuthenticationHandler.SchemeName).RequireAuthenticatedUser().RequireAssertion(c => c.User.FindAll("scope").SelectMany(x => x.Value.Split(' ')).Contains(scope, StringComparer.Ordinal)));
        });
        services.AddRateLimiter(o => o.AddPolicy(SensitiveRateLimiter, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
        return services;
    }
}

public sealed class ServiceClientManagementRequirement : IAuthorizationRequirement;
public sealed class ServiceClientManagementHandler(IIntegrationCredentialOwnerResolver owners, ICurrentUserAccessService access)
    : AuthorizationHandler<ServiceClientManagementRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ServiceClientManagementRequirement requirement)
    {
        if (ServicePrincipalRegistry.IsMachinePrincipal(context.User) || context.User.HasClaim("auth_mode", "integration") ||
            await owners.ResolveAsync(context.User) is null) return;
        if ((await access.ResolveAsync(context.User)).IsHelpdeskAdmin) context.Succeed(requirement);
    }
}
