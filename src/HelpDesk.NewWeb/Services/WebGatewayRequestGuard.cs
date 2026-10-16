using Yarp.ReverseProxy.Model;

namespace HelpDesk.NewWeb.Services;

/// <summary>Optional transport paths always produce a protocol/routing result, never a Blazor document.</summary>
public static class WebGatewayRequestGuard
{
    public const string MetadataPath = WebGatewayPaths.MetadataPath;

    public static bool IsMcpPath(PathString path) => WebGatewayPaths.IsMcpPath(path.Value ?? "");

    public static IApplicationBuilder UseWebGatewayRequestGuard(this IApplicationBuilder app, ResolvedWebGatewayOptions options)
    {
        return app.Use(async (context, next) =>
        {
            if (IsMcpPath(context.Request.Path) &&
                (options.McpHardDisabled || context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>() is null))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { title = "MCP route is not configured", status = 404 },
                    options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
                return;
            }
            await next(context);
        });
    }
}
