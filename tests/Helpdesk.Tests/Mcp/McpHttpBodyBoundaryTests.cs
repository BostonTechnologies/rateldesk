extern alias NewWeb;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Helpdesk.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewWeb::HelpDesk.NewWeb.Services;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace Helpdesk.Tests.Mcp;

public sealed partial class McpHttpHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kestrel_MCP_returns_413_when_a_proxy_client_uploads_before_reading(bool throughGateway)
    {
        await using var host = await McpSocketHost.CreateAsync(throughGateway, throughIngress: true);
        var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 75, method = "initialize", @params = InitializeParameters()
        }));
        foreach (var path in new[] { "/mcp", "/mcp/" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var socket = new TcpClient();
            await socket.ConnectAsync(host.Client.BaseAddress!.Host, host.Client.BaseAddress.Port, deadline.Token);
            await using var stream = socket.GetStream();
            var headers = $"POST {path} HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer rdk_local-a\r\n" +
                "Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
                "Content-Length: 30000001\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
            await stream.WriteAsync(message, deadline.Token);
            var padding = new byte[8192];
            Array.Fill(padding, (byte)' ');
            await stream.WriteAsync(padding, deadline.Token);
            // Model a sender pausing mid-upload beyond Kestrel's fixed five-second
            // post-response drain. The prefix keeps its request data rate above
            // Kestrel's normal minimum. Rejection must not start that drain early.
            using (var uploadPacing = new PeriodicTimer(TimeSpan.FromSeconds(7)))
                Assert.True(await uploadPacing.WaitForNextTickAsync(deadline.Token));
            for (var remaining = 30_000_001 - message.Length - padding.Length; remaining > 0;)
            {
                var count = Math.Min(remaining, padding.Length);
                await stream.WriteAsync(padding.AsMemory(0, count), deadline.Token);
                remaining -= count;
            }
            // Python http.client and other eager clients finish the upload before
            // reading. HttpClient's concurrent response reader masks early resets.
            using var reader = new StreamReader(stream);
            var response = await reader.ReadToEndAsync(deadline.Token);
            Assert.StartsWith("HTTP/1.1 413", response, StringComparison.Ordinal);
            using var problem = JsonDocument.Parse(response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
            Assert.Equal("request_too_large", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal("body", problem.RootElement.GetProperty("limit").GetString());
            Assert.Equal(30_000_000, problem.RootElement.GetProperty("maximumBodyBytes").GetInt64());
        }
        Assert.Equal(0, host.Outbound.ApiRequests);
        Assert.Equal(0, host.Outbound.TokenRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kestrel_MCP_body_boundary_is_equivalent_for_declared_and_chunked_requests(bool throughGateway)
    {
        await using var host = await McpSocketHost.CreateAsync(throughGateway);
        var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 71, method = "initialize", @params = InitializeParameters() });
        foreach (var path in new[] { "/mcp", "/mcp/" })
        foreach (var knownLength in new[] { false, true })
        foreach (var length in new[] { 29_990_208, 29_999_999, 30_000_000, 30_000_001 })
        {
            using var request = SocketRequest(path, new PaddedJsonContent(message, length, knownLength));
            using var response = await host.Client.SendAsync(request);
            if (length > McpRequestBody.DefaultMaximumBytes)
            {
                await AssertBodyLimitAsync(response, McpRequestBody.DefaultMaximumBytes);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var result = ParseMcpResponse(await response.Content.ReadAsStringAsync());
                Assert.Equal(71, result.RootElement.GetProperty("id").GetInt32());
                Assert.True(result.RootElement.GetProperty("result").TryGetProperty("serverInfo", out _));
            }
        }
        Assert.Equal(0, host.Outbound.ApiRequests);
        Assert.Equal(0, host.Outbound.TokenRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kestrel_MCP_preserves_smaller_limit_even_with_one_byte_chunks_and_expect_continue(bool throughGateway)
    {
        const int maximumBytes = 4096;
        await using var host = await McpSocketHost.CreateAsync(throughGateway, maximumBytes);
        var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 72, method = "initialize", @params = InitializeParameters() });
        foreach (var path in new[] { "/mcp", "/mcp/" })
        {
            using (var request = SocketRequest(path, new PaddedJsonContent(message, maximumBytes, knownLength: false, chunkBytes: 1)))
            using (var response = await host.Client.SendAsync(request))
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using (var request = SocketRequest(path, new PaddedJsonContent(message, maximumBytes + 1, knownLength: false, chunkBytes: 1)))
            using (var response = await host.Client.SendAsync(request))
                await AssertBodyLimitAsync(response, maximumBytes);

            var content = new PaddedJsonContent(message, maximumBytes + 1, knownLength: true);
            using var declared = SocketRequest(path, content);
            declared.Headers.ExpectContinue = true;
            using var rejection = await host.Client.SendAsync(declared);
            await AssertBodyLimitAsync(rejection, maximumBytes);
            Assert.Equal(0, content.BytesWritten);
        }
        Assert.Equal(0, host.Outbound.ApiRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kestrel_MCP_retains_large_duplicate_validation_and_malformed_JSON_behavior(bool throughGateway)
    {
        await using var host = await McpSocketHost.CreateAsync(throughGateway);
        const string duplicate = "{\"jsonrpc\":\"2.0\",\"id\":73,\"method\":\"tools/call\",\"params\":{\"name\":\"helpdesk_notifications\",\"arguments\":{\"operation\":\"summary\"},\"arguments\":{\"operation\":\"mark_read\",\"confirm\":true}}}";
        foreach (var path in new[] { "/mcp", "/mcp/" })
        foreach (var knownLength in new[] { false, true })
        {
            using var request = SocketRequest(path, new PaddedJsonContent(duplicate, 1_100_000, knownLength));
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = ParseMcpResponse(await response.Content.ReadAsStringAsync());
            Assert.Equal(73, result.RootElement.GetProperty("id").GetInt32());
            var rpcResult = result.RootElement.GetProperty("result");
            Assert.True(rpcResult.GetProperty("isError").GetBoolean());
            Assert.Equal("validation_failed", rpcResult.GetProperty("structuredContent").GetProperty("status").GetString());

            using var malformed = SocketRequest(path, new PaddedJsonContent("{\"jsonrpc\":", 1_100_000, knownLength));
            using var malformedResponse = await host.Client.SendAsync(malformed);
            Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        }
        Assert.Equal(0, host.Outbound.ApiRequests);
        Assert.Equal(0, host.Outbound.TokenRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Kestrel_MCP_still_bounds_excessive_chunk_extensions(bool throughGateway)
    {
        await using var host = await McpSocketHost.CreateAsync(throughGateway, maximumBytes: 1024);
        using var socket = new TcpClient();
        await socket.ConnectAsync(host.Client.BaseAddress!.Host, host.Client.BaseAddress.Port);
        await using var stream = socket.GetStream();
        // A tiny decoded body must not permit an unbounded chunk-extension stream.
        var request = "POST /mcp HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer rdk_local-a\r\n" +
            "Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
            "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n1;pad=" + new string('a', 80_000) + "\r\nx\r\n0\r\n\r\n";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
        using var reader = new StreamReader(stream);
        var response = await reader.ReadToEndAsync(deadline.Token);
        Assert.StartsWith("HTTP/1.1 413", response, StringComparison.Ordinal);
        var separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(separator > 0);
        using var problem = JsonDocument.Parse(response[(separator + 4)..]);
        Assert.Equal("request_too_large", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("transport", problem.RootElement.GetProperty("limit").GetString());
        Assert.Equal(1024, problem.RootElement.GetProperty("maximumBodyBytes").GetInt32());
        Assert.Equal(0, host.Outbound.ApiRequests);
    }

    [Fact]
    public async Task Kestrel_MCP_Web_bounds_concurrent_uploads_and_releases_completed_slots()
    {
        await using var host = await McpSocketHost.CreateAsync(throughGateway: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var waiting = new List<(TcpClient Socket, NetworkStream Stream, StreamReader Reader)>();
        try
        {
            for (var index = 0; index < 32; index++)
            {
                var socket = new TcpClient();
                await socket.ConnectAsync(host.Client.BaseAddress!.Host, host.Client.BaseAddress.Port, deadline.Token);
                var stream = socket.GetStream();
                var reader = new StreamReader(stream);
                waiting.Add((socket, stream, reader));
                var headers = "POST /mcp HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer rdk_local-a\r\n" +
                    "Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
                    "Transfer-Encoding: chunked\r\nExpect: 100-continue\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), deadline.Token);
                // A 100 response proves this request acquired a lease and began
                // reading its body. No timing delay is needed to fill the slots.
                Assert.Equal("HTTP/1.1 100 Continue", await reader.ReadLineAsync(deadline.Token));
                Assert.Equal("", await reader.ReadLineAsync(deadline.Token));
            }

            var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 74, method = "initialize", @params = InitializeParameters() });
            using (var overflow = SocketRequest("/mcp", new PaddedJsonContent(message, 512, knownLength: true)))
            using (var response = await host.Client.SendAsync(overflow, deadline.Token))
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

            var chunk = Encoding.ASCII.GetBytes($"{Encoding.UTF8.GetByteCount(message):X}\r\n{message}\r\n0\r\n\r\n");
            await waiting[0].Stream.WriteAsync(chunk, deadline.Token);
            var completed = await waiting[0].Reader.ReadToEndAsync(deadline.Token);
            Assert.StartsWith("HTTP/1.1 200", completed, StringComparison.Ordinal);
            Assert.Contains("serverInfo", completed, StringComparison.Ordinal);

            using var resumed = SocketRequest("/mcp", new PaddedJsonContent(message, 512, knownLength: true));
            using var accepted = await host.Client.SendAsync(resumed, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            foreach (var (socket, stream, reader) in waiting)
            {
                reader.Dispose();
                stream.Dispose();
                socket.Dispose();
            }
        }
    }

    private static HttpRequestMessage SocketRequest(string path, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "rdk_local-a");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    private static async Task AssertBodyLimitAsync(HttpResponseMessage response, long expectedMaximum)
    {
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(problem);
        Assert.Equal(413, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("request_too_large", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("body", problem.RootElement.GetProperty("limit").GetString());
        Assert.Equal(expectedMaximum, problem.RootElement.GetProperty("maximumBodyBytes").GetInt64());
    }

    private sealed class PaddedJsonContent : HttpContent
    {
        private readonly byte[] _message;
        private readonly int _length;
        private readonly bool _knownLength;
        private readonly int _chunkBytes;
        public long BytesWritten { get; private set; }

        public PaddedJsonContent(string message, int length, bool knownLength, int chunkBytes = 8192)
        {
            _message = Encoding.UTF8.GetBytes(message);
            _length = length;
            _knownLength = knownLength;
            _chunkBytes = chunkBytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            WriteAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            WriteAsync(stream, cancellationToken);

        private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var padding = new byte[_chunkBytes];
            Array.Fill(padding, (byte)' ');
            for (var remaining = _length - _message.Length; remaining > 0;)
            {
                var count = Math.Min(remaining, padding.Length);
                await stream.WriteAsync(padding.AsMemory(0, count), cancellationToken);
                BytesWritten += count;
                remaining -= count;
            }
            await stream.WriteAsync(_message, cancellationToken);
            BytesWritten += _message.Length;
        }

        protected override bool TryComputeLength(out long value)
        {
            value = _length;
            return _knownLength;
        }
    }

    private sealed class McpSocketHost : IAsyncDisposable
    {
        private readonly McpHostEnvironment _environment = new(gateway: true);
        private WebApplicationFactory<Helpdesk.Mcp.Http.Program>? _factory;
        private WebApplication? _gateway;
        private WebApplication? _ingress;
        private WebGatewayConfiguration? _gatewayConfiguration;
        public AgentClientBoundaryProbe Outbound { get; } = new();
        public HttpClient Client { get; private set; } = null!;

        public static async Task<McpSocketHost> CreateAsync(bool throughGateway, long maximumBytes = McpRequestBody.DefaultMaximumBytes,
            bool throughIngress = false)
        {
            var host = new McpSocketHost();
            try
            {
                host._factory = CreateFactory(host._environment.SigningKey, boundaryProbe: host.Outbound, delegationProbe: new GatewayDelegationProbe());
                host._factory.UseKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0);
                    options.Limits.MaxRequestBodySize = maximumBytes;
                });
                host._factory.StartServer();
                var address = host._factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                if (throughGateway)
                {
                    host._gatewayConfiguration = new WebGatewayConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ApiBaseUrl"] = "http://127.0.0.1:9/",
                        ["WebGateway:PublicOrigin"] = "https://desk.example/",
                        ["WebGateway:Mcp:Enabled"] = "true",
                        ["WebGateway:Mcp:Destination"] = address,
                        ["WebGateway:Mcp:AuthenticationMode"] = "gateway"
                    }).Build());
                    var builder = WebApplication.CreateBuilder();
                    builder.Logging.ClearProviders();
                    builder.WebHost.ConfigureKestrel(options =>
                    {
                        options.Listen(IPAddress.Loopback, 0);
                        options.Limits.MaxRequestBodySize = maximumBytes;
                    });
                    builder.Services.AddAuthorization();
                    builder.Services.AddReverseProxy().LoadFromConfig(host._gatewayConfiguration.EffectiveConfiguration.GetSection("ReverseProxy"));
                    host._gateway = builder.Build();
                    host._gateway.UseRouting();
                    host._gateway.UseWebGatewayRequestGuard(host._gatewayConfiguration.Options);
                    host._gateway.MapReverseProxy().AllowAnonymous();
                    await host._gateway.StartAsync();
                    address = host._gateway.Urls.Single();
                }
                if (throughIngress)
                {
                    var builder = WebApplication.CreateBuilder();
                    builder.Logging.ClearProviders();
                    builder.WebHost.ConfigureKestrel(options =>
                    {
                        options.Listen(IPAddress.Loopback, 0);
                        options.Limits.MaxRequestBodySize = 40_000_000;
                    });
                    builder.Services.AddReverseProxy().LoadFromMemory(
                        [new RouteConfig { RouteId = "mcp", ClusterId = "mcp", Match = new RouteMatch { Path = "/{**path}" } }],
                        [new ClusterConfig { ClusterId = "mcp", Destinations = new Dictionary<string, DestinationConfig>
                        {
                            ["host"] = new() { Address = address }
                        } }]);
                    host._ingress = builder.Build();
                    host._ingress.MapReverseProxy();
                    await host._ingress.StartAsync();
                    address = host._ingress.Urls.Single();
                }
                host.Client = new HttpClient(new SocketsHttpHandler { UseProxy = false, Expect100ContinueTimeout = TimeSpan.FromSeconds(30) })
                {
                    BaseAddress = new Uri(address),
                    Timeout = TimeSpan.FromSeconds(60)
                };
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (_ingress is not null) await _ingress.DisposeAsync();
            if (_gateway is not null) await _gateway.DisposeAsync();
            _gatewayConfiguration?.Dispose();
            if (_factory is not null) await _factory.DisposeAsync();
            _environment.Dispose();
        }
    }
}
