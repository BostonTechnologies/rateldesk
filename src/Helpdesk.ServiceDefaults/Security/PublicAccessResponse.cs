using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Helpdesk.ServiceDefaults.Security;

public static class PublicAccessResponse
{
    public static bool IsPublicLinkPath(PathString path) =>
        path.StartsWithSegments("/public/access") || path.StartsWithSegments("/public/link") ||
        path.StartsWithSegments("/view-ticket") || path.StartsWithSegments("/change-approval") ||
        path.StartsWithSegments("/request-approval") || path.StartsWithSegments("/api/v1/public/access") ||
        path.StartsWithSegments("/api/v1/tickets/public") || path.StartsWithSegments("/api/v1/changes/public") ||
        path.StartsWithSegments("/api/v1/request-approvals/public");

    public static void UsePublicLinkResponseHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (IsPublicLinkPath(context.Request.Path))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
        }
        await next(context);
    });
}
