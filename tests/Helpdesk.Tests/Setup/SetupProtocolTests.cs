using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Helpdesk.AgentClient;
using Helpdesk.Mcp;
using Helpdesk.Mcp.Tools;
using Helpdesk.Setup;
using Xunit;

namespace Helpdesk.Tests.Setup;

public sealed class SetupProtocolTests
{
    [Fact]
    public async Task Pairing_rejects_other_advertised_origin_before_sending_credentials()
    {
        var handler = new Handler(_ => Json(new PairingCapabilities("RatelDesk", 1, true, "https://other.example", "https://other.example/account/integration-credentials/approve", 30, 90)));
        using var http = new HttpClient(handler);
        using var client = new IntegrationCredentialPairingClient("https://desk.example", http);
        await Assert.ThrowsAsync<AgentClientValidationException>(() => client.DiscoverAsync());
        Assert.Single(handler.Requests);
        Assert.Null(handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Disabled_capability_supports_manual_setup_with_null_discovery_links()
    {
        using var http = new HttpClient(new Handler(_ => Json(new { product = "RatelDesk", protocolVersion = 1, pairingSupported = false, publicOrigin = (string?)null, verificationUri = (string?)null })));
        using var client = new IntegrationCredentialPairingClient("https://desk.example", http);
        Assert.False((await client.DiscoverAsync())!.PairingSupported);
    }

    [Fact]
    public async Task Oversized_chunked_pairing_delivery_is_bounded_and_invalid_null_secret_is_rejected()
    {
        using var oversizedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 70000)))) }));
        using var oversized = new IntegrationCredentialPairingClient("https://desk.example", oversizedHttp);
        await Assert.ThrowsAsync<AgentClientValidationException>(() => oversized.PollAsync("private-capability"));
        using var nullHttp = new HttpClient(new Handler(_ => Json(new { status = "issued", pollIntervalSeconds = 5, credential = new { id = Guid.NewGuid(), secret = (string?)null, organizationId = "org-one", permissions = Array.Empty<string>() } })));
        using var invalid = new IntegrationCredentialPairingClient("https://desk.example", nullHttp);
        await Assert.ThrowsAsync<AgentClientValidationException>(() => invalid.PollAsync("private-capability"));
    }

    [Fact]
    public void Private_pairing_capabilities_and_delivery_are_redacted_by_default_string_representation()
    {
        var session = new PairingSession("device-private", "HUMAN-CODE", "https://desk.example/account/integration-credentials/approve", DateTimeOffset.UtcNow.AddMinutes(5), 5);
        var credential = new PairedIntegrationCredential(Guid.NewGuid(), "rdk_prefix", "rdk_private_secret", "api", "org-one", ["ticket.view"], DateTimeOffset.UtcNow.AddDays(30), null);
        Assert.DoesNotContain("device-private", session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("rdk_private_secret", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("rdk_private_secret", new PairingPollResult("issued", 5, credential).ToString(), StringComparison.Ordinal);
        Assert.Equal("labeldanger[red]", TerminalText.Sanitize("label\u001b\u009bdanger[red]\u202e"));
    }

    [Fact]
    public async Task Http_probe_reads_matching_sse_event_without_waiting_for_stream_end_and_calls_only_readonly_status()
    {
        var handler = new Handler(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            if (body.RootElement.GetProperty("method").GetString() == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            var result = id == 1 ? JsonSerializer.Serialize(Initialization()) : id == 2 ? JsonSerializer.Serialize(Catalog()) : "{\"structuredContent\":{\"success\":true,\"status\":\"ok\"}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new OpenAfterDataStream($"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}\n\n")) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var checks = await McpSetupDiagnostics.CheckHttpAsync(HttpProfile(), deadline.Token, http);
        Assert.Contains(checks, check => check.Name == "initialize" && check.Status == "passed");
        Assert.Contains(checks, check => check.Name == "authentication" && check.Status == "passed");
        Assert.Contains(checks, check => check.Name == "tool-result" && check.Status == "passed");
        Assert.Equal(4, handler.Requests.Count);
        using var listing = JsonDocument.Parse(handler.Requests[2].Body!);
        Assert.Equal("tools/list", listing.RootElement.GetProperty("method").GetString());
        Assert.True(JsonSerializer.Serialize(Catalog()).Length > 65536, "Exercise the real production catalog, not a toy fixture.");
        using var tool = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal("helpdesk_auth", tool.RootElement.GetProperty("params").GetProperty("name").GetString());
        Assert.Equal("status", tool.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("operation").GetString());
        Assert.False(tool.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("confirm").GetBoolean());
        Assert.All(handler.Requests, request => Assert.Equal("Bearer rdk_test_private", request.Authorization));
    }

    [Fact]
    public async Task Http_transport_success_does_not_claim_upstream_tool_success()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            if (body.RootElement.GetProperty("method").GetString() == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            return id == 1 ? Json(new { jsonrpc = "2.0", id, result = Initialization() }) : id == 2 ? Json(new { jsonrpc = "2.0", id, result = Catalog() }) :
                Json(new { jsonrpc = "2.0", id, result = new { isError = false, structuredContent = new { success = false, status = "access_denied" } } });
        }));
        var checks = await McpSetupDiagnostics.CheckHttpAsync(HttpProfile(), CancellationToken.None, http);
        Assert.Contains(checks, check => check.Name == "initialize" && check.Status == "passed");
        Assert.Contains(checks, check => check.Name == "tool-result" && check.Status == "failed");
    }

    [Fact]
    public async Task External_OAuth_metadata_is_bound_to_exact_resource_and_HTTPS_authority_without_existing_credentials()
    {
        var handler = new Handler(_ => Json(new { resource = "https://desk.example/mcp", authorizationServers = new[] { "https://auth.example/application/o/mcp/" }, scopesSupported = new[] { "mcp" } }));
        using var http = new HttpClient(handler);
        var configuration = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://desk.example", ClientTransport = "http", CredentialPurpose = "mcp", HttpClientAuthentication = "oauth", McpResourceUri = "https://desk.example/mcp", OAuthAuthorizationServers = ["https://auth.example/application/o/mcp/"], OAuthClientId = "public-imported-id" };
        var checks = await McpSetupDiagnostics.CheckHttpAsync(configuration, CancellationToken.None, http);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://desk.example/.well-known/oauth-protected-resource/mcp", request.Url.AbsoluteUri);
        Assert.Null(request.Authorization);
        Assert.Contains(checks, check => check.Name == "authentication" && check.Status == "unverified");
        Assert.DoesNotContain(checks, check => check.Name == "tool-result" && check.Status == "passed");
        using var mismatch = new OAuthProtectedResourceClient("https://desk.example", http);
        await Assert.ThrowsAsync<AgentClientValidationException>(() => mismatch.DiscoverAsync(["https://different.example/issuer"]));
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("http")]
    public async Task Unconfigured_distribution_help_and_nontty_setup_dispatch_before_serving_validation(string distribution)
    {
        var assembly = distribution == "stdio" ? typeof(HelpdeskMcpStdioHost).Assembly.Location : typeof(Helpdesk.Mcp.Http.Program).Assembly.Location;
        var help = await RunProcessAsync(assembly, "--help");
        Assert.Equal(0, help.Code); Assert.Contains("setup", help.Output, StringComparison.Ordinal); Assert.Empty(help.Error);
        var setup = await RunProcessAsync(assembly, "setup");
        Assert.Equal(78, setup.Code); Assert.Empty(setup.Output); Assert.Contains("TTY", setup.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", setup.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_detection_respects_explicit_prefix_options_and_never_treats_server_setting_values_as_setup()
    {
        Assert.True(SetupCommands.IsCommand(["--profile", "default", "setup"]));
        Assert.False(SetupCommands.IsCommand(["--Helpdesk:Mcp:Instance", "setup"]));
        Assert.False(SetupCommands.IsCommand([]));
    }

    private static object Initialization(string version = "2025-11-25") => new { protocolVersion = version, capabilities = new { }, serverInfo = new { name = "RatelDesk", version = "1" } };
    private static object Catalog() => new { tools = HelpdeskToolDefinitions.Create().Select(tool => tool.ProtocolTool).ToArray() };

    [Theory]
    [InlineData("unsupported-version")]
    [InlineData("missing-info")]
    [InlineData("bad-result")]
    [InlineData("bad-catalog")]
    [InlineData("oversized-catalog")]
    [InlineData("bad-iserror")]
    public async Task Malformed_protocol_or_catalog_never_reports_success(string failure)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            var method = body.RootElement.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            if (method == "initialize") return Json(new { jsonrpc = "2.0", id, result = failure switch
            {
                "unsupported-version" => Initialization("2099-01-01"), "missing-info" => (object)new { protocolVersion = "2025-11-25" },
                "bad-result" => "invalid", _ => Initialization()
            }});
            if (method == "tools/list")
            {
                if (failure == "oversized-catalog") return new(HttpStatusCode.OK) { Content = new StringContent(new string('x', McpSetupDiagnostics.MaximumResponseCharacters + 1)) };
                return Json(new { jsonrpc = "2.0", id, result = failure == "bad-catalog" ? (object)new { tools = new[] { new { name = "helpdesk_auth", inputSchema = new { type = "object" } } } } : Catalog() });
            }
            return Json(new { jsonrpc = "2.0", id, result = new { isError = "invalid", structuredContent = new { success = true } } });
        }));
        var checks = await McpSetupDiagnostics.CheckHttpAsync(HttpProfile(), default, http);
        Assert.Contains(checks, check => check.Status == "failed");
        Assert.DoesNotContain(checks, check => check.Name == "tool-result" && check.Status == "passed");
    }

    [Fact]
    public async Task Full_catalog_pagination_and_safe_downstream_failure_preserve_reason_without_payload()
    {
        var tools = HelpdeskToolDefinitions.Create().Select(tool => tool.ProtocolTool).ToArray();
        var handler = new Handler(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            var method = body.RootElement.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            if (method == "initialize") return Json(new { jsonrpc = "2.0", id, result = Initialization() });
            if (method == "tools/list") return id == 2 ? Json(new { jsonrpc = "2.0", id, result = new { tools = tools.Take(10), nextCursor = "page2" } }) : Json(new { jsonrpc = "2.0", id, result = new { tools = tools.Skip(10) } });
            return Json(new { jsonrpc = "2.0", id, result = new { isError = false, structuredContent = new
            { success = false, summary = "secret-raw-payload", correlationId = "0123456789abcdef", failure = new { code = "upstream_denied", upstreamStatus = 403 }, data = "private-data" } } });
        });
        using var http = new HttpClient(handler);
        var checks = await McpSetupDiagnostics.CheckHttpAsync(HttpProfile(), default, http);
        var failure = Assert.Single(checks, check => check.Name == "tool-result");
        Assert.Contains(checks, check => check.Name == "catalog" && check.Status == "passed");
        Assert.Equal("upstream_denied", failure.Code); Assert.Equal("api", failure.Layer); Assert.Equal(403, failure.UpstreamStatus);
        Assert.Equal("0123456789abcdef", failure.CorrelationId); Assert.True(failure.TransportSucceeded); Assert.False(failure.OperationSucceeded);
        Assert.NotNull(failure.NextAction); Assert.DoesNotContain("secret-raw-payload", JsonSerializer.Serialize(checks)); Assert.DoesNotContain("private-data", JsonSerializer.Serialize(checks));
        using var page2 = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal("page2", page2.RootElement.GetProperty("params").GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task Probe_credentials_and_sessions_are_request_scoped_on_a_shared_http_client()
    {
        var handler = new Handler(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            var method = body.RootElement.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            var id = body.RootElement.GetProperty("id").GetInt32();
            return Json(new { jsonrpc = "2.0", id, result = method == "initialize" ? Initialization() : method == "tools/list" ? Catalog() : (object)new { structuredContent = new { success = true } } });
        });
        using var http = new HttpClient(handler);
        var first = HttpProfile() with { IntegrationCredential = "rdk_first_private" };
        var second = HttpProfile() with { IntegrationCredential = "rdk_second_private" };
        var results = await Task.WhenAll(McpSetupDiagnostics.CheckHttpAsync(first, default, http), McpSetupDiagnostics.CheckHttpAsync(second, default, http));
        Assert.All(results, checks => Assert.Contains(checks, check => check.Name == "tool-result" && check.Status == "passed"));
        Assert.Equal(4, handler.Requests.Count(request => request.Authorization == "Bearer rdk_first_private"));
        Assert.Equal(4, handler.Requests.Count(request => request.Authorization == "Bearer rdk_second_private"));
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    private static AgentClientConfiguration HttpProfile() => AgentClientConfiguration.Empty with
    { ApiBaseUrl = "https://desk.example", CredentialTarget = "https://desk.example", CredentialMode = "integration", IntegrationCredential = "rdk_test_private", ClientTransport = "http", CredentialPurpose = "mcp", McpResourceUri = "https://desk.example/mcp" };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    private static async Task<(int Code, string Output, string Error)> RunProcessAsync(string assembly, string argument)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(assembly); start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("RATELDESK_", StringComparison.Ordinal) || name.StartsWith("Helpdesk__Mcp__", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(name);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output, await error);
    }
    private sealed record Request(Uri Url, string? Authorization, string? Body);
    private sealed class Handler(Func<Request, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = new Request(request.RequestUri!, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(snapshot); return response(snapshot);
        }
    }
    private sealed class OpenAfterDataStream(string data) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(data);
        private int _position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == _bytes.Length) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            var count = Math.Min(buffer.Length, _bytes.Length - _position); _bytes.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
        }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
