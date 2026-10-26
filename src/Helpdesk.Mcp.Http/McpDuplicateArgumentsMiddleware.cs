using Helpdesk.Mcp.Tools;
using Helpdesk.ServiceDefaults;

namespace Helpdesk.Mcp.Http;

/// <summary>Validates duplicate argument envelopes before the MCP SDK binds HTTP requests.</summary>
public sealed class McpDuplicateArgumentsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!McpRequestBody.IsJsonPost(request))
        {
            await next(context);
            return;
        }

        if (!await McpRequestBody.BufferAsync(context)) return;
        var normalized = await McpDuplicateArgumentsGuard.NormalizeAsync(request.Body, context.RequestAborted);
        request.Body.Position = 0;
        if (normalized is not null)
        {
            var body = new MemoryStream(normalized, writable: false);
            context.Response.RegisterForDispose(body);
            request.Body = body;
            request.ContentLength = normalized.Length;
        }
        await next(context);
    }
}
