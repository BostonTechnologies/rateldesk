using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Helpdesk.ServiceDefaults;

/// <summary>Applies the same decoded JSON body budget at the Web gateway and MCP host.</summary>
public static class McpRequestBody
{
    public const long DefaultMaximumBytes = 30_000_000;
    private const int MemoryThreshold = 1_048_576;
    private const int ReadBufferBytes = 8192;
    private const long AdditionalFramingBytes = 65_536;

    public static bool IsJsonPost(HttpRequest request) =>
        string.Equals(request.Path.Value?.TrimEnd('/'), "/mcp", StringComparison.OrdinalIgnoreCase) &&
        HttpMethods.IsPost(request.Method) &&
        request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    public static async Task<bool> BufferAsync(HttpContext context)
    {
        var request = context.Request;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        var maximumBytes = feature?.MaxRequestBodySize ?? DefaultMaximumBytes;

        // Kestrel counts HTTP/1 chunk prefixes, suffixes and extensions against
        // its body limit. Keep a separate finite wire budget while enforcing the
        // original configured limit on decoded bytes. Thirteen wire bytes cover
        // even one-byte chunks with eight hexadecimal size digits; the additional
        // allowance covers the final chunk and bounded extension overhead.
        var maximumWireBytes = maximumBytes <= (long.MaxValue - AdditionalFramingBytes) / 13
            ? maximumBytes * 13 + AdditionalFramingBytes
            : long.MaxValue;
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maximumWireBytes;

        if (request.ContentLength > maximumBytes)
            return await RejectAsync(context, maximumBytes, maximumWireBytes, "body");

        var bufferingLimit = maximumBytes < long.MaxValue ? maximumBytes + 1 : maximumBytes;
        request.EnableBuffering(bufferThreshold: MemoryThreshold, bufferLimit: bufferingLimit);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        long inspectedBytes = 0;
        try
        {
            while (true)
            {
                var remaining = maximumBytes - inspectedBytes;
                var readLength = remaining < ReadBufferBytes ? (int)remaining + 1 : ReadBufferBytes;
                var count = await request.Body.ReadAsync(buffer.AsMemory(0, readLength), context.RequestAborted);
                if (count == 0) break;
                inspectedBytes += count;
                if (inspectedBytes > maximumBytes)
                    return await RejectAsync(context, maximumBytes, maximumWireBytes, "body");
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return await RejectAsync(context, maximumBytes, maximumWireBytes, "transport");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        request.Body.Position = 0;
        return true;
    }

    private static async Task<bool> RejectAsync(HttpContext context, long maximumBytes, long maximumWireBytes, string limit)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "about:blank",
            title = "MCP request is too large",
            status = StatusCodes.Status413PayloadTooLarge,
            code = "request_too_large",
            limit,
            maximumBodyBytes = maximumBytes,
            maximumWireBytes
        });
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/problem+json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);

        // Commit the deliberate response before Kestrel drains an unread declared
        // body. The finite wire allowance also prevents an ordinary just-over-limit
        // upload from tripping the old limit during that drain and resetting TCP.
        await context.Response.CompleteAsync();
        return false;
    }
}
