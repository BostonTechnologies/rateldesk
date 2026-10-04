using Helpdesk.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Helpdesk.API.Authentication;

public sealed class IncidentCreateBoundaryRequirement : IAuthorizationRequirement
{
    public const string Policy = "IncidentCreateBoundary";
}

/// <summary>Preserves ordinary IncidentAccess (including its authentication schemes), with an explicit keyed alternative.</summary>
public sealed class IncidentCreateBoundaryHandler(IAuthorizationPolicyProvider policies,
    IIncidentReceiverAuthorization receiverAuthorization) : AuthorizationHandler<IncidentCreateBoundaryRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, IncidentCreateBoundaryRequirement requirement)
    {
        if (context.Resource is not HttpContext http) return;
        // Resolve after the authorization service/handler graph is constructed; constructor injection is circular.
        var evaluator = http.RequestServices.GetRequiredService<IPolicyEvaluator>();
        var keyed = http.Request.Headers.ContainsKey(IncidentReceiverContract.KeyHeader) ||
            http.Request.Headers.ContainsKey(IncidentReceiverContract.SourceHeader) || receiverAuthorization.RequiresKey(http.User);
        var policy = keyed ? await policies.GetDefaultPolicyAsync() : await policies.GetPolicyAsync("IncidentAccess");
        if (policy is null) return;
        var authenticated = await evaluator.AuthenticateAsync(policy, http);
        var authorized = await evaluator.AuthorizeAsync(policy, authenticated, http, http);
        if (authorized.Succeeded) context.Succeed(requirement);
        // Keyed requests still require current source/operation/mapping authorization in IncidentReceiver.
    }
}
