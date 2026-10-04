using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.ServiceIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Authentication;

public sealed class ServiceOrchestrationCallbackRequirement : IAuthorizationRequirement;

/// <summary>A dedicated service alternative at the callback boundary; ordinary application JWTs are excluded.</summary>
public sealed class ServiceOrchestrationCallbackHandler(IServicePrincipalRegistry registry,
    IOptions<OrchestrationM2MOptions> legacy) : AuthorizationHandler<ServiceOrchestrationCallbackRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ServiceOrchestrationCallbackRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (ServicePrincipalRegistry.IsMachinePrincipal(context.User))
        {
            var ct = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
            if (await registry.ResolvePrincipalAsync(context.User, ServiceIdentityScopes.Callback, ct) is not null)
                context.Succeed(requirement);
            return;
        }
        var caller = context.User.FindFirst("azp")?.Value ?? context.User.FindFirst("client_id")?.Value;
        if (legacy.Value.AllowedCallerClientIds.Any(x => string.Equals(x.Trim(), caller, StringComparison.OrdinalIgnoreCase)))
            context.Succeed(requirement);
    }
}

public static class ServiceOrchestrationCallbackRegistration
{
    public static IServiceCollection AddServiceOrchestrationCallbacks(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, ServiceOrchestrationCallbackHandler>();
        services.Configure<AuthorizationOptions>(options => options.AddPolicy("OrchestrationM2MOnly", policy =>
        {
            policy.AddAuthenticationSchemes("OrchestrationM2M", ServiceIdentityAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new ServiceOrchestrationCallbackRequirement());
        }));
        return services;
    }
}
