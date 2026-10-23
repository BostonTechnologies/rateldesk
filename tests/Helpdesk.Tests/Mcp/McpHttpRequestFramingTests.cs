using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Helpdesk.Mcp.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed partial class McpHttpHostTests
{
    [Theory]
    [InlineData("/mcp", false, 0)]
    [InlineData("/mcp/", false, 0)]
    [InlineData("/mcp/", true, 0)]
    [InlineData("/mcp", false, 1_048_577)]
    [InlineData("/mcp", true, 1_048_577)]
    public async Task Duplicate_arguments_are_rejected_for_streamed_trailing_slash_and_large_requests(
        string path, bool knownLength, int paddingBytes)
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: delegation);
        using var http = CreateClient(factory);
        var raw = new string(' ', paddingBytes) +
            "{\"jsonrpc\":\"2.0\",\"id\":44,\"method\":\"tools/call\",\"params\":{\"name\":\"helpdesk_notifications\",\"arguments\":{\"operation\":\"summary\"},\"arguments\":{\"operation\":\"mark_read\",\"confirm\":true}}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = knownLength
                ? new StringContent(raw, Encoding.UTF8, "application/json")
                : new UnknownLengthJsonContent(raw)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "rdk_local-a");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = ParseMcpResponse(await response.Content.ReadAsStringAsync());
        Assert.Equal(44, result.RootElement.GetProperty("id").GetInt32());
        var content = result.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("validation_failed", content.GetProperty("status").GetString());
        Assert.Equal("validation_error", content.GetProperty("failure").GetProperty("code").GetString());
        Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(1, delegation.Requests);
        Assert.Equal(0, outbound.ApiRequests);
        Assert.Equal(0, outbound.TokenRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_large_initialize_request_remains_supported(bool knownLength)
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var http = CreateClient(factory);
        var raw = new string(' ', 1_048_577) + JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 45, method = "initialize", @params = InitializeParameters()
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = knownLength
                ? new StringContent(raw, Encoding.UTF8, "application/json")
                : new UnknownLengthJsonContent(raw)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]));
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = ParseMcpResponse(await response.Content.ReadAsStringAsync());
        Assert.Equal(45, result.RootElement.GetProperty("id").GetInt32());
        Assert.True(result.RootElement.GetProperty("result").TryGetProperty("serverInfo", out _));
        Assert.Equal(0, outbound.ApiRequests);
        Assert.Equal(0, outbound.TokenRequests);
    }

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UnknownLengthJsonContent(string raw)
        {
            _bytes = Encoding.UTF8.GetBytes(raw);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

public sealed class McpHttpRequestBodyLimitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_body_returns_413_without_invoking_the_next_handler(bool knownLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = knownLength ? 257 : null;
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new RequestBodyLimit { MaxRequestBodySize = 256 });
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 257)));
        context.Request.Body = body;
        var invoked = false;
        var middleware = new McpDuplicateArgumentsMiddleware(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Streamed_body_at_the_configured_limit_reaches_the_next_handler_unchanged()
    {
        var raw = new string(' ', 254) + "{}";
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(new RequestBodyLimit { MaxRequestBodySize = 256 });
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        context.Request.Body = body;
        string? received = null;
        var middleware = new McpDuplicateArgumentsMiddleware(async current =>
        {
            using var reader = new StreamReader(current.Request.Body, leaveOpen: true);
            received = await reader.ReadToEndAsync();
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(raw, received);
    }

    private sealed class RequestBodyLimit : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
