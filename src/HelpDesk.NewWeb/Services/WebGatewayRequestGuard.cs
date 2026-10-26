using System.Threading.RateLimiting;
using Helpdesk.ServiceDefaults;
using Yarp.ReverseProxy.Model;

namespace HelpDesk.NewWeb.Services;

/// <summary>Optional transport paths always produce a protocol/routing result, never a Blazor document.</summary>
public static class WebGatewayRequestGuard
{
    public const string MetadataPath = WebGatewayPaths.MetadataPath;
    private static readonly ConcurrencyLimiter McpBodyLimiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 32,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });

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
            if (McpRequestBody.IsJsonPost(context.Request))
            {
                // Web validates before downstream MCP authentication. Bound the
                // number of public requests holding memory or temporary body files.
                using var lease = await McpBodyLimiter.AcquireAsync(1, context.RequestAborted);
                if (!lease.IsAcquired)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    await context.Response.WriteAsJsonAsync(new { title = "Too many concurrent MCP requests", status = 429, code = "too_many_mcp_requests" },
                        options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
                    return;
                }
                if (!await McpRequestBody.BufferAsync(context)) return;
                // Forward the completed body with its exact length. Incoming chunk
                // framing is hop-specific and must not consume the backend budget.
                context.Request.Headers.Remove("Transfer-Encoding");
                context.Request.ContentLength = context.Request.Body.Length;
                await next(context);
                return;
            }
            await next(context);
        });
    }
}
