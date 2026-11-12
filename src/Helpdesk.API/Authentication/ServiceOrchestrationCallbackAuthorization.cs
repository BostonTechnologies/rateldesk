using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.API.Background;
using Helpdesk.API.Endpoints.Orchestration;
using Microsoft.AspNetCore.Authorization;

namespace Helpdesk.API.Authentication;

public sealed class ServiceOrchestrationCallbackRequirement : IAuthorizationRequirement;

/// <summary>A dedicated service alternative at the callback boundary; ordinary application JWTs are excluded.</summary>
public sealed class ServiceOrchestrationCallbackHandler(IServicePrincipalRegistry registry) : AuthorizationHandler<ServiceOrchestrationCallbackRequirement>
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

    }
}

public static class ServiceOrchestrationCallbackRegistration
{
    public static IServiceCollection AddServiceOrchestrationCallbacks(this IServiceCollection services)
    {
        services.AddScoped<AutomationExecutionOutcomeService>();
        services.AddScoped<AutomationExecutionDeadlineProcessor>();
        services.AddScoped<IAuthorizationHandler, ServiceOrchestrationCallbackHandler>();
        services.Configure<AuthorizationOptions>(options => options.AddPolicy("PairingCallbackOnly", policy =>
        {
            policy.AddAuthenticationSchemes(ServiceIdentityAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new ServiceOrchestrationCallbackRequirement());
        }));
        return services;
    }
}
