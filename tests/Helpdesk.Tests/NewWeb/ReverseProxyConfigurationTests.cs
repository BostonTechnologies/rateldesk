extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewWeb::HelpDesk.NewWeb.Services;
using Xunit;

namespace Helpdesk.Tests.NewWeb;

public sealed class ReverseProxyConfigurationTests
{
    [Fact]
    public void Shipped_defaults_resolve_one_local_API_destination_and_preserve_routes()
    {
        var source = new ConfigurationBuilder().AddJsonFile(Path.Combine(TestEnvironment.RepositoryRoot,
            "src", "HelpDesk.NewWeb", "appsettings.json")).Build();
        using var gateway = new WebGatewayConfiguration(source);

        Assert.False(source.GetSection("ReverseProxy").Exists());
        Assert.Equal("http://127.0.0.1:8222/", gateway.Options.ApiBaseUri.AbsoluteUri);
        Assert.Equal(gateway.Options.ApiBaseUri.AbsoluteUri, gateway.EffectiveConfiguration[ApiDestination]);
        Assert.Equal(7, gateway.EffectiveConfiguration.GetSection("ReverseProxy:Routes").GetChildren().Count());
        Assert.Null(gateway.EffectiveConfiguration["ReverseProxy:Routes:mcpRoute:ClusterId"]);
    }

    [Fact]
    public void Explicit_API_clients_and_advanced_proxy_destinations_can_intentionally_diverge()
    {
        using var gateway = new WebGatewayConfiguration(Configuration(new Dictionary<string, string?>()
        {
            ["ApiBaseUrl"] = "http://api:8222/",
            ["ReverseProxy:Clusters:apiCluster:Destinations:custom:Address"] = "https://split-api.example/",
            ["ReverseProxy:Routes:customRoute:ClusterId"] = "apiCluster",
            ["ReverseProxy:Routes:customRoute:Match:Path"] = "/custom"
        }));

        Assert.Equal("http://api:8222/", gateway.Options.ApiBaseUri.AbsoluteUri);
        Assert.Null(gateway.EffectiveConfiguration[ApiDestination]);
        Assert.Equal("https://split-api.example/", gateway.EffectiveConfiguration["ReverseProxy:Clusters:apiCluster:Destinations:custom:Address"]);
        Assert.Equal(8, gateway.EffectiveConfiguration.GetSection("ReverseProxy:Routes").GetChildren().Count());
    }

    [Fact]
    public void Collections_replace_per_source_layer_and_route_identity_is_case_insensitive()
    {
        var source = Configuration(new Dictionary<string, string?>()
        {
            ["ReverseProxy:Routes:api-openapi-route:Transforms:0:PathRemovePrefix"] = "/old",
            ["ReverseProxy:Routes:api-openapi-route:Transforms:1:RequestHeader"] = "X-Old",
            ["ReverseProxy:Routes:api-openapi-route:Transforms:1:Set"] = "old",
            ["ReverseProxy:Clusters:apiCluster:Destinations:first:Address"] = "http://first:8222/",
            ["ReverseProxy:Clusters:apiCluster:Destinations:second:Address"] = "http://second:8222/"
        }, new Dictionary<string, string?>()
        {
            ["ReverseProxy:Routes:API-OPENAPI-ROUTE:Transforms:0:PathRemovePrefix"] = "/api",
            ["ReverseProxy:Clusters:APICLUSTER:Destinations:replacement:Address"] = "https://split.example/"
        });
        using var gateway = new WebGatewayConfiguration(source);

        var transforms = gateway.EffectiveConfiguration.GetSection("ReverseProxy:Routes:api-openapi-route:Transforms");
        Assert.Single(transforms.GetChildren());
        Assert.Equal("/api", transforms["0:PathRemovePrefix"]);
        var destinations = gateway.EffectiveConfiguration.GetSection("ReverseProxy:Clusters:apiCluster:Destinations");
        Assert.Equal("replacement", Assert.Single(destinations.GetChildren()).Key);
        Assert.Equal("/api/openapi/{**catch-all}", gateway.EffectiveConfiguration["ReverseProxy:Routes:api-openapi-route:Match:Path"]);
    }

    [Fact]
    public void Explicit_removal_lists_are_distinct_from_disabled_generation()
    {
        using var gateway = new WebGatewayConfiguration(Configuration(new Dictionary<string, string?>()
        {
            ["WebGateway:RemoveRoutes:0"] = "email-brand-route",
            ["ReverseProxy:Routes:manualMcp:ClusterId"] = "manualCluster",
            ["ReverseProxy:Routes:manualMcp:Match:Path"] = "/mcp/{**path}",
            ["ReverseProxy:Clusters:manualCluster:Destinations:manual:Address"] = "http://mcp-http:8223/"
        }));
        Assert.Null(gateway.EffectiveConfiguration["ReverseProxy:Routes:email-brand-route:ClusterId"]);
        Assert.Equal("manualCluster", gateway.EffectiveConfiguration["ReverseProxy:Routes:manualMcp:ClusterId"]);
        Assert.False(gateway.Options.McpEnabled);
    }

    [Fact]
    public void Numeric_custom_identities_preserve_generated_routes_and_cluster_objects()
    {
        using var gateway = new WebGatewayConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:1:ClusterId"] = "1",
            ["ReverseProxy:Routes:1:Match:Path"] = "/custom",
            ["ReverseProxy:Clusters:1:Destinations:1:Address"] = "https://custom.example/"
        }));
        Assert.Equal(8, gateway.EffectiveConfiguration.GetSection("ReverseProxy:Routes").GetChildren().Count());
        Assert.Equal(2, gateway.EffectiveConfiguration.GetSection("ReverseProxy:Clusters").GetChildren().Count());
    }

    [Theory]
    [InlineData("gateway", false)]
    [InlineData("authentik", true)]
    public void Generated_metadata_matches_ingress_mode_and_streaming_settings(string mode, bool metadata)
    {
        using var gateway = new WebGatewayConfiguration(Configuration(Mcp("http://mcp-http:8223/", mode)));
        Assert.Equal("/mcp/{**catch-all}", gateway.EffectiveConfiguration["ReverseProxy:Routes:mcpRoute:Match:Path"]);
        Assert.Equal(metadata, gateway.EffectiveConfiguration.GetSection("ReverseProxy:Routes:mcpMetadataRoute").Exists());
        if (metadata)
            Assert.Equal(WebGatewayRequestGuard.MetadataPath, gateway.EffectiveConfiguration["ReverseProxy:Routes:mcpMetadataRoute:Match:Path"]);
        Assert.Equal("false", gateway.EffectiveConfiguration["ReverseProxy:Clusters:mcpCluster:HttpRequest:AllowResponseBuffering"]);
        Assert.Equal("00:05:00", gateway.EffectiveConfiguration["ReverseProxy:Clusters:mcpCluster:HttpRequest:ActivityTimeout"]);
        Assert.Equal("https://desk.example/mcp", gateway.Options.PublicMcpResource!.AbsoluteUri);
    }

    [Fact]
    public void Existing_resource_URL_supplies_a_trusted_origin_and_explicit_split_resource_is_preserved()
    {
        var values = Mcp("http://mcp-http:8223/");
        values.Remove("WebGateway:PublicOrigin");
        values["WebGateway:Mcp:PublicResourceUri"] = "https://resource.example/mcp";
        using var inferred = new WebGatewayConfiguration(Configuration(values));
        Assert.Equal("https://resource.example/", inferred.Options.PublicOrigin!.AbsoluteUri);

        values["WebGateway:PublicOrigin"] = "https://desk.example/";
        using var split = new WebGatewayConfiguration(Configuration(values));
        Assert.Equal("https://desk.example/", split.Options.PublicOrigin!.AbsoluteUri);
        Assert.Equal("https://resource.example/mcp", split.Options.PublicMcpResource!.AbsoluteUri);
    }

    [Theory]
    [InlineData("ApiBaseUrl", "https://secret@api.example/")]
    [InlineData("WebGateway:PublicOrigin", "http://desk.example")]
    [InlineData("WebGateway:PublicOrigin", "https://desk.example/private")]
    [InlineData("WebGateway:Mcp:PublicResourceUri", "https://desk.example/other")]
    [InlineData("WebGateway:Mcp:Destination", "ftp://mcp.example/")]
    [InlineData("WebGateway:Mcp:AuthenticationMode", "automatic")]
    [InlineData("WebGateway:Mcp:ActivityTimeout", "00:00:00")]
    public void Invalid_targets_and_modes_fail_without_echoing_values(string key, string value)
    {
        var values = Mcp("http://mcp-http:8223/");
        values[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => new WebGatewayConfiguration(Configuration(values)));
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_proxy_reload_is_supported_generated_options_stay_frozen_and_invalid_URL_keeps_previous()
    {
        var source = Configuration(new Dictionary<string, string?>() { [ApiDestination] = "http://first:8222/" });
        using var gateway = new WebGatewayConfiguration(source);
        source[ApiDestination] = "http://second:8222/";
        source["ApiBaseUrl"] = "http://changed-client:8222/";
        source.Reload();
        Assert.Equal("http://second:8222/", gateway.EffectiveConfiguration[ApiDestination]);
        Assert.Equal("http://api:8222/", gateway.Options.ApiBaseUri.AbsoluteUri);
        source[ApiDestination] = "https://secret@unexpected.example/";
        source.Reload();
        Assert.Equal("http://second:8222/", gateway.EffectiveConfiguration[ApiDestination]);
    }

    [Fact]
    public void Hard_disabled_proxy_reload_handles_rootless_custom_route_patterns()
    {
        var source = Configuration(new Dictionary<string, string?>
        {
            ["WebGateway:Mcp:HardDisable"] = "true",
            ["ReverseProxy:Routes:custom:ClusterId"] = "apiCluster",
            ["ReverseProxy:Routes:custom:Match:Path"] = "/custom"
        });
        using var gateway = new WebGatewayConfiguration(source);
        source["ReverseProxy:Routes:custom:Match:Path"] = "broken/{**path}";
        source.Reload();
        Assert.Equal("broken/{**path}", gateway.EffectiveConfiguration["ReverseProxy:Routes:custom:Match:Path"]);
        source["ReverseProxy:Routes:custom:Match:Path"] = "mcp{suffix}";
        source.Reload();
        Assert.Equal("mcp{suffix}", gateway.EffectiveConfiguration["ReverseProxy:Routes:custom:Match:Path"]);
        source["ReverseProxy:Routes:custom:Match:Path"] = "mcp/{**path}";
        source.Reload();
        Assert.False(gateway.EffectiveConfiguration.GetSection("ReverseProxy:Routes:custom").Exists());
    }

    [Fact]
    public async Task Allowlist_preserves_V1_pairing_execution_OpenAPI_assets_and_Web_local_docs()
    {
        await using var backend = await StartBackend(app => app.Run(context => context.Response.WriteAsync(context.Request.Path.Value ?? string.Empty)));
        await using var gateway = await Gateway.Start(Configuration(new Dictionary<string, string?>() { ["ApiBaseUrl"] = backend.Urls.Single() }));
        foreach (var (publicPath, backendPath) in new[]
        {
            ("/api/v1/integration-credentials", "/api/v1/integration-credentials"),
            ("/api/v1/mcp/execution-token", "/api/v1/mcp/execution-token"),
            ("/api/v1/integration-credentials/pairing/capabilities", "/api/v1/integration-credentials/pairing/capabilities"),
            ("/api/openapi/v1.json", "/openapi/v1.json"),
            ("/api/incidents/1/file", "/api/incidents/1/file"),
            ("/api/worklogs/1/file", "/api/worklogs/1/file"),
            ("/api/email-templates/1/asset", "/api/email-templates/1/asset"),
            ("/api/tenants/1/branding", "/api/tenants/1/branding"),
            ("/email-brand/1/logo", "/email-brand/1/logo")
        })
            Assert.Equal(backendPath, await gateway.Client.GetStringAsync(publicPath));
        foreach (var path in new[] { "/api/v1/integration-credentials/pairing/start", "/api/v1/integration-credentials/pairing/poll", "/api/v1/mcp/execution-token" })
        {
            using var response = await gateway.Client.PostAsJsonAsync(path, new { test = true });
            Assert.Equal(path, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal("Web API documentation", await gateway.Client.GetStringAsync("/api/docs"));
        Assert.DoesNotContain(gateway.Configuration.EffectiveConfiguration.GetSection("ReverseProxy:Routes").GetChildren(),
            route => route["Match:Path"] == "/api/{**catch-all}");
    }

    [Theory]
    [InlineData("gateway", HttpStatusCode.NotFound)]
    [InlineData("authentik", HttpStatusCode.OK)]
    public async Task Discovery_is_exact_and_absent_in_gateway_mode(string mode, HttpStatusCode expected)
    {
        await using var backend = await StartBackend(app => app.Run(context => context.Response.WriteAsync(context.Request.Path.Value ?? string.Empty)));
        await using var gateway = await Gateway.Start(Configuration(Mcp(backend.Urls.Single(), mode)));
        using var response = await gateway.Client.GetAsync(WebGatewayRequestGuard.MetadataPath);
        Assert.Equal(expected, response.StatusCode);
        using var unknown = await gateway.Client.GetAsync(WebGatewayRequestGuard.MetadataPath + "/unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("Blazor", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MCP_forwards_all_methods_paths_bearer_and_protocol_headers_without_Web_credentials()
    {
        await using var backend = await StartBackend(app => app.Run(async context =>
            await context.Response.WriteAsJsonAsync(new
            {
                path = context.Request.Path.Value,
                method = context.Request.Method,
                authorization = context.Request.Headers.Authorization.ToString(),
                protocol = context.Request.Headers["Mcp-Protocol-Version"].ToString(),
                session = context.Request.Headers["Mcp-Session-Id"].ToString(),
                accept = context.Request.Headers.Accept.ToString(),
                contentType = context.Request.ContentType,
                body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted)
            })));
        await using var gateway = await Gateway.Start(Configuration(Mcp(backend.Urls.Single())));
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, method == HttpMethod.Post ? "/mcp" : "/mcp/session")
            { Content = new StringContent("{\"jsonrpc\":\"2.0\"}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new("Bearer", "caller-rdk-test-value");
            request.Headers.Add("Cookie", "__Host-RatelDesk.Local=unrelated-browser-session");
            request.Headers.Add("Mcp-Protocol-Version", "2025-06-18");
            request.Headers.Add("Mcp-Session-Id", "test-session");
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            Assert.Equal(method == HttpMethod.Post ? "/mcp" : "/mcp/session", result!["path"]);
            Assert.Equal(method.Method, result["method"]);
            Assert.Equal("Bearer caller-rdk-test-value", result["authorization"]);
            Assert.Equal("2025-06-18", result["protocol"]);
            Assert.Equal("test-session", result["session"]);
            Assert.Contains("text/event-stream", result["accept"], StringComparison.Ordinal);
            Assert.StartsWith("application/json", result["contentType"], StringComparison.Ordinal);
            Assert.Equal("{\"jsonrpc\":\"2.0\"}", result["body"]);
        }
    }

    [Fact]
    public async Task MCP_delivers_incremental_streaming_and_propagates_cancellation_to_backend()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await StartBackend(app => app.Run(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data:first\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await finish.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { canceled.TrySetResult(); }
        }));
        await using var gateway = await Gateway.Start(Configuration(Mcp(backend.Urls.Single())));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await gateway.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        var first = new byte[12];
        await stream.ReadExactlyAsync(first, cancellation.Token);
        Assert.Equal("data:first\n\n", Encoding.UTF8.GetString(first));
        Assert.False(finish.Task.IsCompleted); // Delivery happened before backend completion.
        cancellation.Cancel();
        stream.Dispose();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disabled_manual_and_hard_disabled_MCP_are_explicit_and_never_HTML()
    {
        await using var backend = await StartBackend(app => app.Run(context => context.Response.WriteAsync("manual MCP")));
        var values = new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:manualMcp:ClusterId"] = "manualCluster",
            ["ReverseProxy:Routes:manualMcp:Match:Path"] = "/mcp/{**path}",
            ["ReverseProxy:Clusters:manualCluster:Destinations:manual:Address"] = backend.Urls.Single()
        };
        await using (var manual = await Gateway.Start(Configuration(values)))
            Assert.Equal("manual MCP", await manual.Client.GetStringAsync("/mcp"));
        values["WebGateway:Mcp:HardDisable"] = "true";
        await using (var disabled = await Gateway.Start(Configuration(values)))
        {
            using var response = await disabled.Client.GetAsync("/mcp");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("Blazor", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await using var unselected = await Gateway.Start(Configuration());
        using var unavailable = await unselected.Client.GetAsync("/mcp/unknown");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        Assert.Equal("application/problem+json", unavailable.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Selected_unavailable_backend_returns_proxy_failure_without_destination_switching()
    {
        await using var backend = await StartBackend(app => app.Run(context => context.Response.WriteAsync("up")));
        var selected = backend.Urls.Single();
        await backend.StopAsync();
        await using var gateway = await Gateway.Start(Configuration(Mcp(selected)));
        using var response = await gateway.Client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("Blazor", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(selected.TrimEnd('/') + "/", gateway.Configuration.Options.McpDestination!.AbsoluteUri);
    }

    [Fact]
    public async Task Full_Web_preserves_local_docs_rejects_unknown_API_and_resolves_all_clients_once()
    {
        using var factory = new WebApplicationFactory<WebGatewayConfiguration>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Authentication:Mode", "Local");
            // Minimal-host settings must be available while Program resolves its
            // immutable gateway options; ConfigureAppConfiguration runs afterward.
            // Keep the process-wide loopback safety defaults for other tests intact.
            builder.UseSetting("ApiBaseUrl", "http://selected-api.example:8222/");
            builder.UseSetting("WebGateway:PublicOrigin", "https://trusted.example/");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://spoofed.example"), AllowAutoRedirect = true });
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "untrusted.example");
        using var docs = await client.GetAsync("/api/docs");
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
        Assert.Contains("scalar", await docs.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var unknown = await client.GetAsync("/api/not-public");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var mcp = await client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.NotFound, mcp.StatusCode);
        Assert.Equal("application/problem+json", mcp.Content.Headers.ContentType!.MediaType);
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        foreach (var name in new[] { "HelpdeskApi", "NetclawPairingApi", "SystemPairingApi", "HelpdeskApiStreaming", "SystemApi", "SystemApiNoAuth" })
            Assert.Equal("http://selected-api.example:8222/", clients.CreateClient(name).BaseAddress!.AbsoluteUri);
        Assert.Equal("https://trusted.example/mcp", factory.Services.GetRequiredService<ResolvedWebGatewayOptions>().PublicMcpResource!.AbsoluteUri);
    }

    [Fact]
    public async Task Image_entrypoint_consumes_shell_safe_environment_overrides_in_a_real_Web_process()
    {
        var dockerfile = await File.ReadAllTextAsync(Path.Combine(TestEnvironment.RepositoryRoot, "docker", "web", "Dockerfile"));
        var entrypoint = dockerfile.Split('\n').Single(line => line.StartsWith("ENTRYPOINT ", StringComparison.Ordinal));
        var arguments = JsonSerializer.Deserialize<string[]>(entrypoint["ENTRYPOINT ".Length..])!;
        Assert.Equal(new[] { "dotnet", "HelpDesk.NewWeb.dll" }, arguments);
        await using var backend = await StartBackend(app => app.Run(context => context.Response.WriteAsync("MCP environment override")));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? arguments[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(typeof(WebGatewayConfiguration).Assembly.Location)!
        };
        start.ArgumentList.Add(typeof(WebGatewayConfiguration).Assembly.Location);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("WebGateway__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ReverseProxy__", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        foreach (var name in new[] { "ASPNETCORE_HTTPS_PORT", "HTTPS_PORT", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTPS_PORTS" })
            start.Environment.Remove(name);
        var keys = Path.Combine(Path.GetTempPath(), "rateldesk-gateway-tests", Guid.NewGuid().ToString("N"));
        start.Environment["DataProtection__KeyRingPath"] = keys;
        start.Environment["Authentication__Mode"] = "Local";
        start.Environment["ApiBaseUrl"] = "http://127.0.0.1:9/";
        start.Environment["WebGateway__PublicOrigin"] = "https://desk.example/";
        start.Environment["WebGateway__Mcp__Enabled"] = "true";
        start.Environment["WebGateway__Mcp__Destination"] = "http://127.0.0.1:9/";
        start.Environment["WebGateway__Mcp__AuthenticationMode"] = "gateway";
        start.Environment["ReverseProxy__Clusters__mcpCluster__Destinations__customMcp__Address"] = backend.Urls.Single();
        start.Environment["ReverseProxy__Routes__mcpRoute__Match__Path"] = "/mcp/overridden";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
            using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            HttpResponseMessage? response = null;
            while (response is null)
            {
                startupDeadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    Assert.Fail($"Web exited during startup (code {process.ExitCode}). {await stderr}");
                try { response = await client.GetAsync("/mcp/overridden", startupDeadline.Token); }
                catch (HttpRequestException) { await Task.Delay(100, startupDeadline.Token); }
            }
            Assert.NotNull(response);
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("MCP environment override", await response.Content.ReadAsStringAsync());
            }
            using var unselectedPath = await client.GetAsync("/mcp", startupDeadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, unselectedPath.StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr);
            if (Directory.Exists(keys)) Directory.Delete(keys, recursive: true);
        }
    }

    private const string ApiDestination = "ReverseProxy:Clusters:apiCluster:Destinations:api1:Address";

    private static IConfigurationRoot Configuration(params Dictionary<string, string?>[] layers)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://api:8222/" });
        foreach (var layer in layers) builder.AddInMemoryCollection(layer);
        return builder.Build();
    }

    private static Dictionary<string, string?> Mcp(string destination, string mode = "gateway") => new()
    {
        ["WebGateway:PublicOrigin"] = "https://desk.example/",
        ["WebGateway:Mcp:Enabled"] = "true",
        ["WebGateway:Mcp:Destination"] = destination,
        ["WebGateway:Mcp:AuthenticationMode"] = mode
    };

    private static async Task<WebApplication> StartBackend(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        return app;
    }

    private sealed class Gateway(WebApplication app, WebGatewayConfiguration configuration) : IAsyncDisposable
    {
        public WebGatewayConfiguration Configuration { get; } = configuration;
        public HttpClient Client { get; } = app.GetTestClient();

        public static async Task<Gateway> Start(IConfiguration source)
        {
            var configuration = new WebGatewayConfiguration(source);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthorization();
            builder.Services.AddReverseProxy().LoadFromConfig(configuration.EffectiveConfiguration.GetSection("ReverseProxy"));
            var app = builder.Build();
            app.UseRouting();
            app.UseWebGatewayRequestGuard(configuration.Options);
            app.MapGet("/api/docs", () => "Web API documentation");
            app.MapReverseProxy().AllowAnonymous();
            app.MapFallback(() => Results.Content("<html>Blazor fallback</html>", "text/html"));
            await app.StartAsync();
            return new Gateway(app, configuration);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            Configuration.Dispose();
        }
    }
}
