using Helpdesk.Mcp.Tools;
using Microsoft.AspNetCore.Http.Features;

namespace Helpdesk.Mcp.Http;

/// <summary>Validates duplicate argument envelopes before the MCP SDK binds HTTP requests.</summary>
public sealed class McpDuplicateArgumentsMiddleware(RequestDelegate next)
{
    private const int MemoryThreshold = 1_048_576;
    private const long DefaultMaximumBodyBytes = 30_000_000;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!string.Equals(request.Path.Value?.TrimEnd('/'), "/mcp", StringComparison.OrdinalIgnoreCase) ||
            !HttpMethods.IsPost(request.Method) ||
            request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            await next(context);
            return;
        }

        // Content-Length is optional with streamed HTTP bodies. Inspect the entire
        // accepted body, using the server's finite limit or its normal default.
        var maximumBytes = context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize
            ?? DefaultMaximumBodyBytes;
        if (request.ContentLength > maximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var bufferingLimit = maximumBytes < long.MaxValue ? maximumBytes + 1 : maximumBytes;
        request.EnableBuffering(bufferThreshold: MemoryThreshold, bufferLimit: bufferingLimit);
        var buffer = new byte[8192];
        long inspectedBytes = 0;
        try
        {
            while (true)
            {
                var remaining = maximumBytes - inspectedBytes;
                var readLength = remaining < buffer.Length ? (int)remaining + 1 : buffer.Length;
                var count = await request.Body.ReadAsync(buffer.AsMemory(0, readLength), context.RequestAborted);
                if (count == 0) break;
                inspectedBytes += count;
                if (inspectedBytes > maximumBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        request.Body.Position = 0;
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
