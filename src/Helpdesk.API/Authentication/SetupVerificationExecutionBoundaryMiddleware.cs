using Microsoft.AspNetCore.Authentication;

namespace Helpdesk.API.Authentication;

/// <summary>
/// A temporary execution identity cannot invoke even otherwise anonymous None-mapped routes.
/// Authentication still verifies the current source credential before applying this boundary.
/// </summary>
public sealed class SetupVerificationExecutionBoundaryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            authorization["Bearer ".Length..].Trim().StartsWith(McpExecutionTokenService.TokenPrefix, StringComparison.Ordinal))
        {
            await context.AuthenticateAsync(McpExecutionAuthenticationHandler.SchemeName);
            if (context.Items.ContainsKey(McpExecutionAuthenticationHandler.SetupOperationDeniedItem))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        await next(context);
    }
}
