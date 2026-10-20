using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Http.Observability;
using Helpdesk.Mcp.Http.Authorization;
using Helpdesk.Mcp.Tools;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.WebUtilities;
using Helpdesk.Tests.Api;
using Helpdesk.Setup;
using Helpdesk.Shared.Auth;
using Helpdesk.API.Ops;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Helpdesk.Tests.Mcp;

[CollectionDefinition(nameof(McpHttpHostCollection), DisableParallelization = true)]
public sealed class McpHttpHostCollection;

[Collection(nameof(McpHttpHostCollection))]
public sealed partial class McpHttpHostTests
{
    private const string Authority = "https://auth.example";
    private const string ResourceUri = "https://helpdesk-dev.example/mcp";

    [Fact]
    public async Task Environment_only_gateway_process_starts_without_file_deployment_credential_or_prompt()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var assembly = typeof(Helpdesk.Mcp.Http.Program).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(assembly)!
        };
        start.ArgumentList.Add(assembly);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("RATELDESK_MCP_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Helpdesk__Mcp__", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Authentication__AuthentikMcp__", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Helpdesk__Mcp__Outbound__ApiBaseUrl"] = "http://api:8222/";
        start.Environment["Helpdesk__Mcp__Outbound__CredentialMode"] = "gateway";
        start.Environment["Helpdesk__Mcp__Instance"] = "north-west";
        start.Environment["Helpdesk__Mcp__ExpectedApiBaseUrl"] = "http://api:8222/";
        start.Environment["Helpdesk__Mcp__PublicResourceUri"] = "https://desk.example/mcp";
        start.Environment["Helpdesk__Mcp__AllowedOrigins__0"] = "https://desk.example";
        start.Environment["Helpdesk__Mcp__AuthenticationMode"] = "gateway";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["RATELDESK_CREDENTIAL_MODE"] = "integration";
        start.Environment["RATELDESK_API_BASE_URL"] = "https://unrelated-cli.example";
        start.Environment["RATELDESK_INTEGRATION_CREDENTIAL"] = "rdk_unrelated_cli_identity";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Timeout.InfiniteTimeSpan };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            HttpResponseMessage? live = null;
            while (live is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) Assert.Fail($"HTTP MCP exited during startup (code {process.ExitCode}). {await stderr}");
                try { live = await client.GetAsync("/health/live", deadline.Token); }
                catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
            }
            using (live) Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.False(process.HasExited);
            using var initialize = await client.PostAsync("/mcp", McpRequest("initialize"), deadline.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, initialize.StatusCode);
            Assert.DoesNotContain(initialize.Headers.WwwAuthenticate, challenge => challenge.Parameter?.Contains("oauth-protected-resource", StringComparison.Ordinal) == true);
            using var metadata = await client.GetAsync("/.well-known/oauth-protected-resource/mcp", deadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(stdout, stderr);
        }
        var logs = await stdout + await stderr;
        Assert.DoesNotContain("Connect a client", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[Ctrl+N", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("rdk_unrelated_cli_identity", logs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData(" Gateway ")]
    public void External_ingress_rejects_gateway_downstream_configuration_before_serving(string credentialMode)
    {
        using var environment = new McpHostEnvironment(gateway: true);
        Environment.SetEnvironmentVariable("Helpdesk__Mcp__AuthenticationMode", "authentik");
        var configuration = JsonNode.Parse(File.ReadAllText(environment.ConfigurationPath))!.AsObject();
        configuration["credentialMode"] = credentialMode;
        File.WriteAllText(environment.ConfigurationPath, configuration.ToJsonString());
        using var factory = CreateFactory(environment.SigningKey);

        var error = Assert.Throws<AgentClientValidationException>(() => CreateClient(factory));
        Assert.Contains("requires HTTP gateway ingress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_request_is_challenged_with_protected_resource_metadata()
    {
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey);
        using var client = CreateClient(factory);

        using var response = await client.PostAsync("/mcp", McpRequest("initialize"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains(response.Headers.WwwAuthenticate, header =>
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            header.Parameter?.Contains("resource_metadata=\"https://helpdesk-dev.example/.well-known/oauth-protected-resource/mcp\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Metadata_and_authorized_initialize_use_the_canonical_resource()
    {
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey);
        using var client = CreateClient(factory);

        using var metadata = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        var metadataBody = await metadata.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Contains(ResourceUri, metadataBody, StringComparison.Ordinal);
        Assert.Contains($"{Authority}/", metadataBody, StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = McpRequest("initialize")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]));
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_identity_missing_a_required_group_is_forbidden()
    {
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey);
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = McpRequest("initialize")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["another-group"]));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_signature_issuer_or_audience_is_unauthorized()
    {
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey);
        using var client = CreateClient(factory);
        using var unrelatedSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await AssertUnauthorizedAsync(client, CreateToken(unrelatedSigningKey, ["helpdesk.mcp"], ["ai-assistant"]));
        await AssertUnauthorizedAsync(client, CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"], issuer: "https://other-auth.example"));
        await AssertUnauthorizedAsync(client, CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"], audience: "https://other-helpdesk.example/mcp"));
    }

    [Fact]
    public async Task Authenticated_discovery_and_redacted_configuration_never_expose_secrets()
    {
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey);
        using var client = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);

        using var tools = await HttpCallAsync(client, bearer, 1, "tools/list", new { });
        using var configuration = await HttpCallAsync(client, bearer, 2, "tools/call", ToolCallParameters("helpdesk_config", "show"));

        Assert.True(tools.RootElement.TryGetProperty("result", out _));
        var configurationOutput = configuration.RootElement.GetRawText();
        Assert.DoesNotContain("test-only-password", configurationOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(bearer, configurationOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_http_discovery_distinguishes_configured_execution_identity_from_verified_authority()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var client = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);

        using var capabilities = await HttpCallAsync(client, bearer, 1, "tools/call", ToolCallParameters("helpdesk_capabilities", "get"));
        using var catalog = await HttpCallAsync(client, bearer, 2, "tools/call", ToolCallParameters("helpdesk_schema", "list"));
        var identity = StructuredContent(capabilities.RootElement).GetProperty("data").GetProperty("configuredIdentity");
        Assert.Equal("authentik", identity.GetProperty("ingressAuthenticationMode").GetString());
        Assert.Equal("authentik", identity.GetProperty("downstreamCredentialMode").GetString());
        Assert.Equal("separately_configured_shared_principal", identity.GetProperty("executionIdentity").GetString());
        Assert.False(identity.GetProperty("ingressCallerUsedForApi").GetBoolean());
        Assert.False(identity.GetProperty("inboundTokenForwarded").GetBoolean());
        Assert.False(identity.GetProperty("authorityVerified").GetBoolean());
        Assert.Equal("requires_current_api_helpdesk_admin", identity.GetProperty("instanceAdministration").GetString());
        Assert.Equal("requires_exact_authentik_ai_agent", identity.GetProperty("exactAiScheme").GetString());
        Assert.Equal("helpdesk_auth.access", identity.GetProperty("accessVerificationOperation").GetString());
        Assert.Equal(identity.GetRawText(), StructuredContent(catalog.RootElement).GetProperty("data").GetProperty("configuredIdentity").GetRawText());
        foreach (var resource in new[] { "helpdesk://server/status", "helpdesk://capabilities" })
        {
            using var response = await HttpCallAsync(client, bearer, 3, "resources/read", new { uri = resource });
            using var body = JsonDocument.Parse(response.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!);
            Assert.Equal(identity.GetRawText(), body.RootElement.GetProperty("configuredIdentity").GetRawText());
        }
        Assert.Equal(0, outbound.TokenRequests);
        Assert.Equal(0, outbound.ApiRequests);
        Assert.DoesNotContain("test-only-password", identity.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_http_access_reads_production_API_authority_with_a_separate_synthetic_provider_principal(bool administrator)
    {
        // Production API authentication and role projection; only provider signing
        // material and its token endpoint are synthetic. These are separate tokens.
        using var api = new SignedOidcApi("Hybrid");
        await api.SeedLinkedUserAsync();
        var downstreamToken = administrator
            ? api.Token(subject: "synthetic-administrator", roles: ["external-administrators"])
            : api.Token();
        var requests = new ConcurrentQueue<string>();
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: () =>
            new SyntheticProviderTokenHandler(downstreamToken, api.Factory.Server.CreateHandler(), requests));
        using var client = CreateClient(factory);
        var firstCaller = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"], subject: "first-ingress-caller");
        var secondCaller = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"], subject: "second-ingress-caller");

        foreach (var caller in new[] { firstCaller, secondCaller })
        {
            using var response = await HttpCallAsync(client, caller, 1, "tools/call", ToolCallParameters("helpdesk_auth", "access"));
            var result = StructuredContent(response.RootElement);
            Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
            var access = result.GetProperty("data");
            Assert.Equal(administrator, access.GetProperty("isHelpdeskAdmin").GetBoolean());
            if (administrator)
                Assert.Contains(HelpdeskPermissions.HelpdeskAdmin, access.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));
            else
            {
                Assert.Equal("contact-a", access.GetProperty("customerId").GetString());
                Assert.Equal(["tenant-a"], access.GetProperty("allowedOrganizationIds").EnumerateArray().Select(value => value.GetString()));
                Assert.DoesNotContain("tenant-b", access.GetProperty("allowedOrganizationIds").EnumerateArray().Select(value => value.GetString()));
            }
            Assert.DoesNotContain(caller, result.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(downstreamToken, result.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal(["/api/v1/auth/me", "/api/v1/auth/me"], requests.ToArray());
    }

    [Fact]
    public async Task External_http_AI_status_and_bounded_logs_require_the_real_exact_API_scheme_and_separate_token()
    {
        const string aiIssuer = "https://provider.example.test/application/o/ai-reporter/";
        const string aiAudience = "synthetic-ai-api";
        const string aiGroup = "synthetic-ai-reporters";
        const string marker = "Synthetic exact AI HTTP diagnostic";
        using var provider = new SignedOidcApi("Hybrid");
        using var api = provider.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:AuthentikAiAgent:Authority"] = aiIssuer,
                ["Authentication:AuthentikAiAgent:Audience"] = aiAudience,
                ["Authentication:AuthentikAiAgent:RequiredGroups:0"] = aiGroup
            }));
            builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>("AuthentikAiAgent", options =>
            {
                var discovery = new OpenIdConnectConfiguration { Issuer = aiIssuer };
                discovery.SigningKeys.Add(provider.Key);
                options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
            }));
        });
        string traceId;
        using (var activity = new Activity("owned-native-transport-regression").Start())
        {
            traceId = activity.TraceId.ToString();
            api.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Helpdesk.NativeTransport")
                .LogWarning("{Marker} token=synthetic-transport-secret", marker);
        }
        using var environment = new McpHostEnvironment();
        var ingressToken = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        var validToken = provider.Token(issuer: aiIssuer, audience: aiAudience, roleClaim: "groups", roles: [aiGroup]);
        foreach (var (token, permitted) in new[]
        {
            (validToken, true),
            (provider.Token(issuer: aiIssuer + "foreign/", audience: aiAudience, roleClaim: "groups", roles: [aiGroup]), false),
            (provider.Token(issuer: aiIssuer, audience: ResourceUri, roleClaim: "groups", roles: [aiGroup]), false),
            (provider.Token(issuer: aiIssuer, audience: aiAudience, roleClaim: "groups", roles: ["other-group"]), false),
            (provider.Token(issuer: aiIssuer, audience: aiAudience, roleClaim: "groups", roles: [aiGroup], expired: true), false),
            (provider.Token(roles: ["external-administrators"]), false)
        })
        {
            var paths = new ConcurrentQueue<string>();
            using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: () =>
                new SyntheticProviderTokenHandler(token, api.Server.CreateHandler(), paths));
            using var client = CreateClient(factory);
            using var status = await HttpCallAsync(client, ingressToken, 1, "tools/call", ToolCallParameters("helpdesk_auth", "status"));
            var statusResult = StructuredContent(status.RootElement);
            Assert.Equal(permitted, statusResult.GetProperty("success").GetBoolean());
            if (permitted)
            {
                Assert.Equal("ai_agent", statusResult.GetProperty("data").GetProperty("authMode").GetString());
                Assert.DoesNotContain(HelpdeskPermissions.HelpdeskAdmin, statusResult.GetProperty("data").GetProperty("roles").EnumerateArray().Select(value => value.GetString()));
            }
            else
                Assert.Equal(401, statusResult.GetProperty("failure").GetProperty("upstreamStatus").GetInt32());
            using var logs = await HttpCallAsync(client, ingressToken, 2, "tools/call", ToolCallParameters("helpdesk_logs", new { operation = "search", request = new { contains = marker, correlationId = traceId, limit = 1 } }));
            var logResult = StructuredContent(logs.RootElement);
            Assert.Equal(permitted, logResult.GetProperty("success").GetBoolean());
            if (permitted)
            {
                var data = logResult.GetProperty("data");
                Assert.True(data.GetProperty("redacted").GetBoolean());
                var item = Assert.Single(data.GetProperty("items").EnumerateArray());
                Assert.Equal(marker + " token=[REDACTED]", item.GetProperty("message").GetString());
                Assert.Equal(traceId, item.GetProperty("traceId").GetString());
                using var direct = api.CreateClient();
                direct.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
                using var independent = await direct.GetAsync("/api/v1/ops/ai-agent/logs?contains=" + Uri.EscapeDataString(marker) + "&correlationId=" + traceId);
                independent.EnsureSuccessStatusCode();
                using var apiLogs = JsonDocument.Parse(await independent.Content.ReadAsStringAsync());
                Assert.Equal(data.GetProperty("items").GetRawText(), apiLogs.RootElement.GetProperty("items").GetRawText());
            }
            else
                Assert.Equal(401, logResult.GetProperty("failure").GetProperty("upstreamStatus").GetInt32());
            Assert.Equal(["/api/v1/auth/ai-agent/status", "/api/v1/ops/ai-agent/logs"], paths.ToArray());
        }
    }

    [Fact]
    public async Task HTTP_orchestration_selection_uses_exact_optional_queries_and_keeps_test_confirmation()
    {
        // A synthetic downstream responder observes adapter requests. API mapping
        // and tenant authorization are tested separately against the real API.
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var client = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        var selection = new { organizationId = "synthetic-org & scope", connectionId = "11111111-2222-3333-4444-555555555555" };
        const string query = "?organizationId=synthetic-org%20%26%20scope&connectionId=11111111-2222-3333-4444-555555555555";
        var id = 1;
        foreach (var (operation, path) in new[]
        {
            ("orchestration", "/api/v1/admin/orchestration"),
            ("orchestration_jobs", "/api/v1/admin/orchestration/catalog/jobs"),
            ("orchestration_tenants", "/api/v1/admin/orchestration/catalog/tenants"),
            ("orchestration_request_definitions", "/api/v1/admin/orchestration/catalog/request-definitions")
        })
        {
            using var omitted = await HttpCallAsync(client, bearer, id++, "tools/call", ToolCallParameters("helpdesk_connectivity", operation));
            Assert.True(StructuredContent(omitted.RootElement).GetProperty("success").GetBoolean());
            Assert.Equal("GET " + path, outbound.ApiPathsAndQueries.Last());
            using var selected = await HttpCallAsync(client, bearer, id++, "tools/call", ToolCallParameters("helpdesk_connectivity", new { operation, request = selection }));
            Assert.True(StructuredContent(selected.RootElement).GetProperty("success").GetBoolean());
            Assert.Equal("GET " + path + query, outbound.ApiPathsAndQueries.Last());
        }

        var beforePreview = outbound.ApiRequests;
        using var preview = await HttpCallAsync(client, bearer, id++, "tools/call", ToolCallParameters("helpdesk_connectivity", new { operation = "orchestration_test", request = selection }));
        Assert.Equal("confirmation_required", StructuredContent(preview.RootElement).GetProperty("status").GetString());
        Assert.Equal(beforePreview, outbound.ApiRequests);
        using var confirmed = await HttpCallAsync(client, bearer, id++, "tools/call", ToolCallParameters("helpdesk_connectivity", new { operation = "orchestration_test", request = selection, confirm = true }));
        Assert.True(StructuredContent(confirmed.RootElement).GetProperty("success").GetBoolean());
        Assert.Equal("POST /api/v1/admin/orchestration/test" + query, outbound.ApiPathsAndQueries.Last());

        var beforeInvalid = outbound.ApiRequests;
        foreach (var json in new[] { """{"organizationId":" "}""", """{"organizationId":1}""", """{"connectionId":"not-a-uuid"}""", """{"connectionId":"00000000-0000-0000-0000-000000000000"}""", """{"connectionId":[]}""", """{"apiBaseUrl":"https://other.example.test"}""", """{"organizationId":"a","organizationId":"b"}""" })
        {
            using var invalid = await HttpCallAsync(client, bearer, id++, "tools/call", ToolCallParameters("helpdesk_connectivity", new { operation = "orchestration_jobs", request = JsonSerializer.Deserialize<JsonElement>(json) }));
            Assert.Equal("validation_failed", StructuredContent(invalid.RootElement).GetProperty("status").GetString());
        }
        using var invalidBindings = await HttpCallAsync(client, bearer, id, "tools/call", ToolCallParameters("helpdesk_connectivity", new { operation = "orchestration_bindings", request = selection }));
        Assert.Equal("validation_failed", StructuredContent(invalidBindings.RootElement).GetProperty("status").GetString());
        Assert.Equal(beforeInvalid, outbound.ApiRequests);
    }

    [Fact]
    public async Task Inbound_mcp_bearer_is_never_forwarded_to_the_helpdesk_api()
    {
        using var environment = new McpHostEnvironment();
        using var audit = new AuditLogCollector();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, audit, boundaryProbe: outbound);
        using var client = CreateClient(factory);
        var inboundBearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);

        using var response = await HttpCallAsync(client, inboundBearer, 1, "tools/call", ToolCallParameters("helpdesk_auth", "status"));

        Assert.Equal("completed", StructuredContent(response.RootElement).GetProperty("status").GetString());
        Assert.Equal("Bearer outbound-agent-token", outbound.ApiAuthorization);
        Assert.DoesNotContain(inboundBearer, outbound.ApiAuthorization, StringComparison.Ordinal);
        Assert.Equal(1, outbound.TokenRequests);
        Assert.Equal(1, outbound.ApiRequests);
        var auditMessage = Assert.Single(audit.Messages, message => message.StartsWith("MCP tool invocation completed.", StringComparison.Ordinal));
        Assert.Contains("CorrelationId=corr-123", auditMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paired_gateway_callers_receive_request_scoped_execution_credentials()
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: delegation);
        using var client = CreateClient(factory);

        var calls = await Task.WhenAll(
            HttpCallAsync(client, "rdk_local-a", 1, "tools/call", ToolCallParameters("helpdesk_auth", "status")),
            HttpCallAsync(client, "rdk_local-b", 2, "tools/call", ToolCallParameters("helpdesk_auth", "status")));
        using var first = calls[0];
        using var second = calls[1];

        Assert.All(calls, response => Assert.Equal("completed", StructuredContent(response.RootElement).GetProperty("status").GetString()));
        Assert.Equal(2, delegation.Requests);
        Assert.DoesNotContain(outbound.ApiAuthorizations, value => value.Contains("rdk_local-a", StringComparison.Ordinal));
        Assert.DoesNotContain(outbound.ApiAuthorizations, value => value.Contains("rdk_local-b", StringComparison.Ordinal));
        Assert.Equal(["Bearer rdx_local-a", "Bearer rdx_local-b"], outbound.ApiAuthorizations.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Gateway_mcp_host_uses_a_real_authority_for_delegation_and_current_identity()
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync();
        await authority.SetOwnerAdministratorAsync();
        var credential = await authority.CreateCredentialAsync("org-b");
        using var environment = new McpHostEnvironment(
            apiBaseUrl: "https://api.example",
            publicResourceUri: McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri,
            gateway: true);
        using var factory = CreateFactory(
            environment.SigningKey,
            apiHandlerFactory: authority.CreateHandler,
            delegationHandlerFactory: authority.CreateHandler);
        using var client = CreateClient(factory, McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri);

        using var identity = await HttpCallAsync(client, credential.Bearer, 1, "tools/call", ToolCallParameters("helpdesk_auth", "status"));
        var identityData = StructuredContent(identity.RootElement).GetProperty("data");
        Assert.Equal("owner", identityData.GetProperty("userId").GetString());
        Assert.Contains("org-b", identityData.GetProperty("allowedOrganizationIds").EnumerateArray().Select(value => value.GetString()));
        Assert.False(identityData.GetProperty("isHelpdeskAdmin").GetBoolean());
        using var accessResponse = await HttpCallAsync(client, credential.Bearer, 2, "tools/call", ToolCallParameters("helpdesk_auth", "access"));
        var accessData = StructuredContent(accessResponse.RootElement).GetProperty("data");
        Assert.False(accessData.GetProperty("isHelpdeskAdmin").GetBoolean());
        Assert.Equal(["org-b"], accessData.GetProperty("allowedOrganizationIds").EnumerateArray().Select(value => value.GetString()));
        using var capabilities = await HttpCallAsync(client, credential.Bearer, 3, "tools/call", ToolCallParameters("helpdesk_capabilities", "get"));
        var configured = StructuredContent(capabilities.RootElement).GetProperty("data").GetProperty("configuredIdentity");
        Assert.Equal("gateway", configured.GetProperty("ingressAuthenticationMode").GetString());
        Assert.Equal("request_credential_owner", configured.GetProperty("executionIdentity").GetString());
        Assert.True(configured.GetProperty("ingressCallerUsedForApi").GetBoolean());
        Assert.Equal("requires_explicit_owner_administration_grant", configured.GetProperty("instanceAdministration").GetString());
        Assert.Equal("requires_explicit_owner_administration_grant", configured.GetProperty("aiOperationAuthority").GetString());
        Assert.Equal("denied_by_bounded_credential_policy", configured.GetProperty("exactAiScheme").GetString());
        using var administration = await HttpCallAsync(client, credential.Bearer, 4, "tools/call", ToolCallParameters("helpdesk_connectivity", "orchestration"));
        Assert.Equal(403, StructuredContent(administration.RootElement).GetProperty("failure").GetProperty("upstreamStatus").GetInt32());

        await authority.SetCredentialRevokedAsync(credential.Id);
        using var revokedRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = McpToolCall("helpdesk_auth", "status")
        };
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
        revokedRequest.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var revoked = await client.SendAsync(revokedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task Gateway_incident_search_uses_the_real_api_adapter_catalog_and_credential_boundary()
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync(unscopedData: true);
        await authority.SeedIncidentSearchAsync();
        var credential = await authority.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var resource = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri;
        using var environment = new McpHostEnvironment(apiBaseUrl: "https://api.example", publicResourceUri: resource, gateway: true);
        using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: authority.CreateHandler, delegationHandlerFactory: authority.CreateHandler);
        using var client = CreateClient(factory, resource);

        using var initialize = await HttpCallAsync(client, credential.Bearer, 1, "initialize", InitializeParameters());
        Assert.Equal("2025-03-26", initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        using var catalog = await HttpCallAsync(client, credential.Bearer, 2, "tools/list", new { });
        var catalogTools = catalog.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(HelpdeskToolDefinitions.Create().Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal),
            catalogTools.Select(tool => tool.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        var incidentTool = Assert.Single(catalogTools,
            tool => tool.GetProperty("name").GetString() == "helpdesk_incidents");
        Assert.Contains("request.query", incidentTool.GetProperty("description").GetString(), StringComparison.Ordinal);

        using var match = await HttpCallAsync(client, credential.Bearer, 3, "tools/call", ToolCallParameters("helpdesk_incidents", new
        {
            operation = "list", request = new { query = "needle", page = 1, pageSize = 1, includeTotal = true }
        }));
        var matchResult = StructuredContent(match.RootElement);
        Assert.True(matchResult.GetProperty("success").GetBoolean());
        var matchData = matchResult.GetProperty("data");
        Assert.Equal(2, matchData.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, matchData.GetProperty("page").GetInt32());
        Assert.Equal(1, matchData.GetProperty("pageSize").GetInt32());
        Assert.Equal("incident-beta", Assert.Single(matchData.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());

        using var noMatch = await HttpCallAsync(client, credential.Bearer, 4, "tools/call", ToolCallParameters("helpdesk_incidents", new
        {
            operation = "list", request = new { query = "impossible-incident-search-193", includeTotal = true }
        }));
        var noMatchResult = StructuredContent(noMatch.RootElement);
        Assert.True(noMatchResult.GetProperty("success").GetBoolean());
        Assert.Equal(0, noMatchResult.GetProperty("data").GetProperty("totalCount").GetInt32());
        Assert.Empty(noMatchResult.GetProperty("data").GetProperty("items").EnumerateArray());

        using var literal = await HttpCallAsync(client, credential.Bearer, 5, "tools/call", ToolCallParameters("helpdesk_incidents", new
        {
            operation = "list", request = new { query = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.LiteralIncidentSearch }
        }));
        var literalData = StructuredContent(literal.RootElement).GetProperty("data");
        Assert.Equal(1, literalData.GetProperty("totalCount").GetInt32());
        Assert.Equal("incident-alpha", Assert.Single(literalData.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        var apiQueries = authority.IncidentListQueries.Select(QueryHelpers.ParseQuery).ToArray();
        Assert.Equal(3, apiQueries.Length);
        Assert.All(apiQueries, query =>
        {
            Assert.False(query.ContainsKey("query"));
            Assert.Single(query["q"]);
        });
        Assert.Equal("needle", apiQueries[0]["q"].ToString());
        Assert.Equal("impossible-incident-search-193", apiQueries[1]["q"].ToString());
        Assert.Equal(McpGatewayRealAccessPipelineTests.GatewayAccessHarness.LiteralIncidentSearch, apiQueries[2]["q"].ToString());

        using var detail = await HttpCallAsync(client, credential.Bearer, 6, "tools/call", ToolCallParameters("helpdesk_incidents", new
        {
            operation = "get", request = new { incidentId = "incident-beta" }
        }));
        Assert.True(StructuredContent(detail.RootElement).GetProperty("success").GetBoolean());
        Assert.Equal("incident-beta", StructuredContent(detail.RootElement).GetProperty("data").GetProperty("id").GetString());
        using var foreign = await HttpCallAsync(client, credential.Bearer, 7, "tools/call", ToolCallParameters("helpdesk_incidents", new
        {
            operation = "get", request = new { incidentId = "incident-foreign" }
        }));
        Assert.False(StructuredContent(foreign.RootElement).GetProperty("success").GetBoolean());
        Assert.Equal("forbidden", StructuredContent(foreign.RootElement).GetProperty("status").GetString());
        using var denied = await HttpCallAsync(client, credential.Bearer, 8, "tools/call", ToolCallParameters("helpdesk_requests", new
        {
            operation = "list", request = new { query = "needle" }
        }));
        Assert.False(StructuredContent(denied.RootElement).GetProperty("success").GetBoolean());
        Assert.Equal("forbidden", StructuredContent(denied.RootElement).GetProperty("status").GetString());
        using var status = await HttpCallAsync(client, credential.Bearer, 9, "tools/call", ToolCallParameters("helpdesk_auth", "status"));
        var identity = StructuredContent(status.RootElement).GetProperty("data");
        Assert.Equal("owner", identity.GetProperty("userId").GetString());
        Assert.Equal("org-a", Assert.Single(identity.GetProperty("allowedOrganizationIds").EnumerateArray()).GetString());
        Assert.Equal(HelpdeskPermissions.IncidentRead, Assert.Single(identity.GetProperty("permissions").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Real_temporary_verifier_checks_full_catalog_and_status_but_cannot_call_other_none_operations()
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync();
        var credential = await authority.CreateCredentialAsync("org-b", "mcp-setup");
        var resource = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri;
        using var environment = new McpHostEnvironment(apiBaseUrl: "https://api.example", publicResourceUri: resource, gateway: true);
        using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: authority.CreateHandler, delegationHandlerFactory: authority.CreateHandler);
        using var client = CreateClient(factory, resource);
        var profile = AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://helpdesk.example", CredentialTarget = "https://helpdesk.example", CredentialMode = "integration",
            IntegrationCredential = credential.Bearer, CredentialPurpose = "mcp", ClientTransport = "http", McpResourceUri = resource
        };
        var checks = await McpSetupDiagnostics.CheckHttpAsync(profile, default, client);
        Assert.Contains(checks, check => check.Name == "catalog" && check.Status == "passed");
        Assert.Contains(checks, check => check.Name == "tool-result" && check.Status == "passed");
        foreach (var (tool, operation) in new[] { ("helpdesk_capabilities", "get"), ("helpdesk_health", "get"), ("helpdesk_config", "show"), ("helpdesk_auth", "access") })
        {
            using var rejected = await HttpCallAsync(client, credential.Bearer, 50, "tools/call", ToolCallParameters(tool, operation));
            Assert.False(rejected.RootElement.TryGetProperty("error", out _));
            Assert.True(rejected.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Equal("setup_verification_operation_denied", StructuredContent(rejected.RootElement).GetProperty("failure").GetProperty("code").GetString());
        }
        using var prompts = await HttpCallAsync(client, credential.Bearer, 51, "prompts/list", new { });
        Assert.True(prompts.RootElement.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task Real_sdk_verifier_denial_is_a_known_tool_failure_before_the_handler_and_business_api(string protocolVersion)
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync(unscopedData: true);
        await authority.SeedIncidentSearchAsync();
        var verifier = await authority.CreateCredentialAsync("org-a", "mcp-setup");
        var ordinary = await authority.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var resource = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri;
        using var environment = new McpHostEnvironment(apiBaseUrl: "https://api.example", publicResourceUri: resource, gateway: true);
        using var audit = new AuditLogCollector();
        // The authority fixture shares one open in-memory SQLite connection.
        // Legacy SDK handshakes legitimately overlap initialize/initialized/GET;
        // independent EF contexts cannot register SQLite functions concurrently
        // on that same connection. Serialize only this fixture's API requests.
        using var authorityGate = new SemaphoreSlim(1, 1);
        HttpMessageHandler AuthorityHandler() => new SerializedAuthorityHandler(authority.CreateHandler(), authorityGate);
        var businessHandlers = 0;
        using var factory = CreateFactory(environment.SigningKey, audit,
            apiHandlerFactory: AuthorityHandler, delegationHandlerFactory: AuthorityHandler,
            configureServices: services => services.AddMcpServer().WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, ct) =>
            {
                if (context.Params.Name == "helpdesk_incidents") Interlocked.Increment(ref businessHandlers);
                return next(context, ct);
            })));
        using var client = CreateClient(factory, resource);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", verifier.Bearer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(resource)
        }, client), new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: deadline.Token);
        Assert.Equal(protocolVersion, mcp.NegotiatedProtocolVersion);
        var catalog = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Equal(HelpdeskToolDefinitions.Create().Count, catalog.Count);
        var status = await mcp.CallToolAsync("helpdesk_auth", new Dictionary<string, object?> { ["operation"] = "status" }, cancellationToken: deadline.Token);
        Assert.True(status.StructuredContent!.Value.GetProperty("success").GetBoolean());

        var arguments = new Dictionary<string, object?> { ["operation"] = "list", ["request"] = new { query = "needle" } };
        var denied = await mcp.CallToolAsync("helpdesk_incidents", arguments, cancellationToken: deadline.Token);
        Assert.True(denied.IsError);
        Assert.False(denied.StructuredContent!.Value.GetProperty("success").GetBoolean());
        Assert.Equal("setup_verification_operation_denied", denied.StructuredContent.Value.GetProperty("failure").GetProperty("code").GetString());
        Assert.False(denied.StructuredContent.Value.GetProperty("failure").GetProperty("retryable").GetBoolean());
        using var fallback = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(denied.Content)).Text);
        Assert.Equal("setup_verification_operation_denied", fallback.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, fallback.RootElement.GetProperty("data").ValueKind);
        Assert.Equal(0, businessHandlers);
        Assert.Empty(authority.IncidentListQueries);
        var denialAudit = Assert.Single(audit.Messages, message => message.Contains("Tool=helpdesk_incidents", StringComparison.Ordinal));
        Assert.Contains("ProtocolSucceeded=True", denialAudit, StringComparison.Ordinal);
        Assert.Contains("OperationSucceeded=False", denialAudit, StringComparison.Ordinal);
        Assert.Contains("ResultStatus=forbidden", denialAudit, StringComparison.Ordinal);
        Assert.Contains("Operation=setup_verification_operation_denied", denialAudit, StringComparison.Ordinal);
        Assert.All(audit.Messages, message =>
        {
            Assert.DoesNotContain(verifier.Bearer, message, StringComparison.Ordinal);
            Assert.DoesNotContain("needle", message, StringComparison.Ordinal);
        });

        // The identical valid request remains available to an explicitly permitted ordinary client.
        using var allowed = await HttpCallAsync(client, ordinary.Bearer, 70, "tools/call", ToolCallParameters("helpdesk_incidents", arguments));
        Assert.True(StructuredContent(allowed.RootElement).GetProperty("success").GetBoolean());
        Assert.Equal(1, businessHandlers);
        Assert.Single(authority.IncidentListQueries);

        await authority.SetCredentialRevokedAsync(verifier.Id);
        using var revokedRequest = CreateGatewayRequest();
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", verifier.Bearer);
        using var revoked = await client.SendAsync(revokedRequest, deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.DoesNotContain("setup_verification_operation_denied", await revoked.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_rejected_selectors_are_redacted_in_production_sdk_audit()
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync();
        var verifier = await authority.CreateCredentialAsync("org-b", "mcp-setup");
        var ordinary = await authority.CreateCredentialAsync("org-b");
        var resource = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri;
        using var environment = new McpHostEnvironment(apiBaseUrl: "https://api.example", publicResourceUri: resource, gateway: true);
        using var audit = new AuditLogCollector();
        using var authorityGate = new SemaphoreSlim(1, 1);
        HttpMessageHandler AuthorityHandler() => new SerializedAuthorityHandler(authority.CreateHandler(), authorityGate);
        var handlers = 0;
        using var factory = CreateFactory(environment.SigningKey, audit,
            apiHandlerFactory: AuthorityHandler, delegationHandlerFactory: AuthorityHandler,
            configureServices: services => services.AddMcpServer().WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, ct) =>
            {
                Interlocked.Increment(ref handlers);
                return next(context, ct);
            })));
        using var client = CreateClient(factory, resource);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", verifier.Bearer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(resource)
        }, client), new McpClientOptions { ProtocolVersion = "2025-03-26" }, cancellationToken: deadline.Token);

        var status = await mcp.CallToolAsync("helpdesk_auth", new Dictionary<string, object?> { ["operation"] = "status" }, cancellationToken: deadline.Token);
        Assert.True(status.StructuredContent!.Value.GetProperty("success").GetBoolean());
        Assert.Contains(audit.Messages, message => message.Contains("Tool=helpdesk_auth Operation=status", StringComparison.Ordinal));
        Assert.Equal(1, handlers);

        foreach (var (tool, selector, expectedTool) in new[]
        {
            ("helpdesk_auth", "private-ticket-marker", "helpdesk_auth"),
            ("helpdesk_incidents", "private-business-marker", "helpdesk_incidents"),
            ("private-tool-marker", "private-selector-marker", "unknown")
        })
        {
            var previousCount = audit.Messages.Count;
            var denied = await mcp.CallToolAsync(tool, new Dictionary<string, object?> { ["operation"] = selector }, cancellationToken: deadline.Token);
            Assert.True(denied.IsError);
            Assert.Equal("setup_verification_operation_denied", denied.StructuredContent!.Value.GetProperty("failure").GetProperty("code").GetString());
            Assert.False(denied.StructuredContent.Value.GetProperty("success").GetBoolean());
            Assert.Equal(1, handlers);
            Assert.Empty(authority.IncidentListQueries);
            var message = Assert.Single(audit.Messages.Skip(previousCount));
            Assert.Contains("Tool=" + expectedTool + " Operation=setup_verification_operation_denied", message, StringComparison.Ordinal);
            Assert.Contains("ProtocolSucceeded=True", message, StringComparison.Ordinal);
            Assert.Contains("OperationSucceeded=False", message, StringComparison.Ordinal);
            Assert.Contains("ResultStatus=forbidden", message, StringComparison.Ordinal);
        }
        Assert.All(audit.Messages, message =>
        {
            Assert.DoesNotContain("private-", message, StringComparison.Ordinal);
            Assert.DoesNotContain(verifier.Bearer, message, StringComparison.Ordinal);
        });

        using var allowed = await HttpCallAsync(client, ordinary.Bearer, 70, "tools/call", ToolCallParameters("helpdesk_capabilities", "get"));
        Assert.True(StructuredContent(allowed.RootElement).GetProperty("success").GetBoolean());
        Assert.Equal(2, handlers);
        Assert.Contains(audit.Messages, message => message.Contains("Tool=helpdesk_capabilities Operation=get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_http_json_is_a_protocol_error_and_not_a_known_verifier_denial()
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync();
        var verifier = await authority.CreateCredentialAsync("org-b", "mcp-setup");
        var resource = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri;
        using var environment = new McpHostEnvironment(apiBaseUrl: "https://api.example", publicResourceUri: resource, gateway: true);
        using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: authority.CreateHandler, delegationHandlerFactory: authority.CreateHandler);
        using var client = CreateClient(factory, resource);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", verifier.Bearer);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var error = JsonDocument.Parse(body);
        Assert.True(error.RootElement.TryGetProperty("error", out _));
        Assert.False(error.RootElement.TryGetProperty("result", out _));
        Assert.DoesNotContain("setup_verification_operation_denied", body, StringComparison.Ordinal);
        Assert.Empty(authority.IncidentListQueries);
    }

    [Fact]
    public async Task Paired_gateway_mode_does_not_advertise_oauth_metadata()
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe();
        using var factory = CreateFactory(environment.SigningKey, delegationProbe: delegation);
        using var client = CreateClient(factory);

        using var metadata = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = McpRequest("initialize") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "rdk_local-a");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var initialize = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    public async Task Paired_gateway_does_not_fall_back_when_delegation_dependency_rejects_or_is_unavailable(
        HttpStatusCode statusCode,
        HttpStatusCode expectedStatusCode)
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: delegation);
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = ToolCallParameters("helpdesk_auth", "status")
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "rdk_local-a");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, delegation.Requests);
        Assert.Equal(0, outbound.ApiRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    public async Task Paired_gateway_maps_credential_and_authorization_failures_without_exposing_dependency_content(
        HttpStatusCode delegationStatus,
        HttpStatusCode expectedStatus)
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe((_, _) => Task.FromResult(new HttpResponseMessage(delegationStatus)
        {
            Content = new StringContent("sensitive upstream body", Encoding.UTF8)
        }));
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: delegation);
        using var client = CreateClient(factory);
        using var response = await client.SendAsync(CreateGatewayRequest());

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.DoesNotContain("sensitive", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        if (expectedStatus == HttpStatusCode.Unauthorized)
            Assert.Contains(response.Headers.WwwAuthenticate, header => string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
        else
            Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal(0, outbound.ApiRequests);
    }

    [Fact]
    public async Task Paired_gateway_sanitizes_and_bounds_delegation_retry_after()
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return Task.FromResult(response);
        });
        using var factory = CreateFactory(environment.SigningKey, delegationProbe: delegation);
        using var client = CreateClient(factory);
        using var response = await client.SendAsync(CreateGatewayRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("60", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"accessToken\":\"")]
    public async Task Paired_gateway_rejects_invalid_or_oversized_delegation_bodies_without_fallback(string body)
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var responseBody = body == "{\"accessToken\":\""
            ? body + new string('x', 17 * 1024) + "\"}"
            : body;
        var delegation = new GatewayDelegationProbe((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8)
        }));
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: delegation);
        using var client = CreateClient(factory);
        using var request = CreateGatewayRequest();

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, delegation.Requests);
        Assert.Equal(0, outbound.ApiRequests);
    }

    [Fact]
    public async Task Paired_gateway_binds_its_exchange_deadline_to_response_headers_and_body()
    {
        using var environment = new McpHostEnvironment(gateway: true, delegationTimeoutSeconds: 1);
        var headersNeverArrive = new GatewayDelegationProbe((_, cancellationToken) =>
            AwaitCancellationAsync<HttpResponseMessage>(cancellationToken));
        var outbound = new AgentClientBoundaryProbe();
        using var headersFactory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: headersNeverArrive);
        using var headersClient = CreateClient(headersFactory);
        using var headersResponse = await headersClient.SendAsync(CreateGatewayRequest());

        Assert.Equal(HttpStatusCode.GatewayTimeout, headersResponse.StatusCode);
        Assert.Equal(0, outbound.ApiRequests);

        var bodyNeverCompletes = new GatewayDelegationProbe((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledReadStream())
        }));
        using var bodyFactory = CreateFactory(environment.SigningKey, delegationProbe: bodyNeverCompletes);
        using var bodyClient = CreateClient(bodyFactory);
        using var bodyResponse = await bodyClient.SendAsync(CreateGatewayRequest());

        Assert.Equal(HttpStatusCode.GatewayTimeout, bodyResponse.StatusCode);
    }

    [Fact]
    public async Task Paired_gateway_refused_delegation_connection_does_not_fall_back()
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("refused")));
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound, delegationProbe: delegation);
        using var client = CreateClient(factory);
        using var response = await client.SendAsync(CreateGatewayRequest());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, delegation.Requests);
        Assert.Equal(0, outbound.ApiRequests);
    }

    [Fact]
    public async Task Paired_gateway_propagates_caller_cancellation()
    {
        using var environment = new McpHostEnvironment(gateway: true);
        var delegation = new GatewayDelegationProbe((_, cancellationToken) =>
            AwaitCancellationAsync<HttpResponseMessage>(cancellationToken));
        using var factory = CreateFactory(environment.SigningKey, delegationProbe: delegation);
        using var client = CreateClient(factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(CreateGatewayRequest(), cancellation.Token));
        Assert.Equal(1, delegation.Requests);
    }

    [Fact]
    public async Task Http_health_resource_uses_the_registered_outbound_agent_client()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var client = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);

        using var response = await HttpCallAsync(client, bearer, 1, "resources/read", new { uri = "helpdesk://health" });

        Assert.True(response.RootElement.TryGetProperty("result", out _));
        Assert.Equal(1, outbound.TokenRequests);
        Assert.Equal(3, outbound.ApiRequests);
    }

    [Fact]
    public async Task Remote_configuration_mutation_is_rejected_without_persisting_or_calling_upstream()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new OutboundCallProbe();
        using var factory = CreateFactory(environment.SigningKey, outboundProbe: outbound);
        using var client = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        var before = File.ReadAllText(environment.ConfigurationPath);

        using var set = await HttpCallAsync(client, bearer, 1, "tools/call", ToolCallParameters("helpdesk_config", new
        {
            operation = "set",
            confirm = true,
            request = new { key = "apiBaseUrl", value = "https://api.example" }
        }));
        using var unset = await HttpCallAsync(client, bearer, 2, "tools/call", ToolCallParameters("helpdesk_config", new
        {
            operation = "unset",
            confirm = true,
            request = new { key = "authentikScope" }
        }));

        Assert.Equal("validation_failed", StructuredContent(set.RootElement).GetProperty("status").GetString());
        Assert.Equal("validation_failed", StructuredContent(unset.RootElement).GetProperty("status").GetString());
        Assert.Equal(before, File.ReadAllText(environment.ConfigurationPath));
        Assert.Equal(0, outbound.CallCount);
    }

    [Fact]
    public async Task Independent_dev_and_prod_hosts_are_concurrently_isolated()
    {
        const string devApi = "https://dev-api.example";
        const string prodApi = "https://prod-api.example";
        const string devResource = "https://helpdesk-dev.example/mcp";
        const string prodResource = "https://helpdesk-prod.example/mcp";
        using var devSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var prodSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var devEnvironment = new McpHostEnvironment("dev", devApi, devResource);
        using var devFactory = CreateFactory(devSigningKey, jwt: new McpJwtValidation(devResource));
        using var devClient = CreateClient(devFactory, devResource);
        using var prodEnvironment = new McpHostEnvironment("prod", prodApi, prodResource);
        using var prodFactory = CreateFactory(prodSigningKey, jwt: new McpJwtValidation(prodResource));
        using var prodClient = CreateClient(prodFactory, prodResource);
        var devBearer = CreateToken(devSigningKey, ["helpdesk.mcp"], ["ai-assistant"], audience: devResource);
        var prodBearer = CreateToken(prodSigningKey, ["helpdesk.mcp"], ["ai-assistant"], audience: prodResource);

        var calls = await Task.WhenAll(
            HttpCallAsync(devClient, devBearer, 1, "tools/call", ToolCallParameters("helpdesk_capabilities", "get")),
            HttpCallAsync(prodClient, prodBearer, 1, "tools/call", ToolCallParameters("helpdesk_capabilities", "get")));
        using var devResponse = calls[0];
        using var prodResponse = calls[1];
        var devData = StructuredContent(devResponse.RootElement).GetProperty("data");
        var prodData = StructuredContent(prodResponse.RootElement).GetProperty("data");

        Assert.Equal("dev", devData.GetProperty("instance").GetString());
        Assert.Equal(devApi, devData.GetProperty("apiBaseUrl").GetString());
        Assert.Equal(devResource, devData.GetProperty("resourceUri").GetString());
        Assert.Equal("prod", prodData.GetProperty("instance").GetString());
        Assert.Equal(prodApi, prodData.GetProperty("apiBaseUrl").GetString());
        Assert.Equal(prodResource, prodData.GetProperty("resourceUri").GetString());
        await AssertUnauthorizedAsync(devClient, prodBearer);
        await AssertUnauthorizedAsync(prodClient, devBearer);

        var devConfigBefore = File.ReadAllText(devEnvironment.ConfigurationPath);
        var prodConfigBefore = File.ReadAllText(prodEnvironment.ConfigurationPath);
        using var devConfigMutation = await HttpCallAsync(devClient, devBearer, 2, "tools/call", ToolCallParameters("helpdesk_config", new
        {
            operation = "set",
            confirm = true,
            request = new { key = "apiBaseUrl", value = prodApi }
        }));
        using var prodConfigMutation = await HttpCallAsync(prodClient, prodBearer, 2, "tools/call", ToolCallParameters("helpdesk_config", new
        {
            operation = "set",
            confirm = true,
            request = new { key = "apiBaseUrl", value = devApi }
        }));
        var devConfigResult = StructuredContent(devConfigMutation.RootElement);
        var prodConfigResult = StructuredContent(prodConfigMutation.RootElement);
        Assert.Equal("validation_failed", devConfigResult.GetProperty("status").GetString());
        Assert.Contains("active dev MCP endpoint", devConfigResult.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("validation_failed", prodConfigResult.GetProperty("status").GetString());
        Assert.Contains("active prod MCP endpoint", prodConfigResult.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal(devConfigBefore, File.ReadAllText(devEnvironment.ConfigurationPath));
        Assert.Equal(prodConfigBefore, File.ReadAllText(prodEnvironment.ConfigurationPath));
    }

    [Fact]
    public async Task Authenticated_tool_calls_emit_bounded_audit_without_bearer_data()
    {
        using var environment = new McpHostEnvironment();
        using var audit = new AuditLogCollector();
        using var factory = CreateFactory(environment.SigningKey, audit);
        using var client = CreateClient(factory);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = McpToolCall("helpdesk_capabilities", "get")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auditMessage = Assert.Single(audit.Messages, message => message.StartsWith("MCP tool invocation completed.", StringComparison.Ordinal));
        Assert.Contains("Instance=dev", auditMessage, StringComparison.Ordinal);
        Assert.Contains("Transport=http", auditMessage, StringComparison.Ordinal);
        Assert.Contains("Tool=helpdesk_capabilities", auditMessage, StringComparison.Ordinal);
        Assert.Contains("Operation=get", auditMessage, StringComparison.Ordinal);
        Assert.Contains("Caller=ai-assistant-client", auditMessage, StringComparison.Ordinal);
        Assert.Contains("Success=True", auditMessage, StringComparison.Ordinal);
        Assert.Contains("ResultStatus=completed", auditMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(bearer, auditMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("test-only-password", auditMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", auditMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Raw_http_and_stdio_ticket_pagination_rejects_invalid_wire_arguments_before_business_IO()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var http = CreateClient(factory);
        var backendBuilder = WebApplication.CreateSlimBuilder();
        backendBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        backendBuilder.Logging.ClearProviders();
        await using var backend = backendBuilder.Build();
        var stdioCalls = 0;
        backend.Run(context =>
        {
            Interlocked.Increment(ref stdioCalls);
            return context.Response.WriteAsync("{}");
        });
        await backend.StartAsync();
        var address = backend.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var stdio = StdioMcpClient.Start(address, "rdk_synthetic-pagination");
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        using var initialized = await stdio.CallAsync(1, "initialize", InitializeParameters());
        Assert.True(initialized.RootElement.TryGetProperty("result", out _));
        await stdio.NotifyAsync("notifications/initialized", new { });
        var id = 2;
        // These helpers write JSON-RPC directly; there is no MCP client/schema
        // validator preventing invalid cases from reaching either running host.
        foreach (var tool in new[] { "helpdesk_incidents", "helpdesk_requests", "helpdesk_changes", "helpdesk_request_tasks" })
        foreach (var json in new[] { """{"page":0,"pageSize":10000}""", """{"page":null}""", """{"pageSize":"1"}""", """{"page":1.5}""", """{"pageSize":true}""", """{"page":[]}""", """{"pageSize":{}}""", """{"page":1000001}""", """{"pageSize":101}""", """{"page":2147483648}""", """{"page":1,"page":1}""", """{"pageSize":1,"pageSize":null}""", """{"Page":0,"PAGESIZE":10000}""", """{"page":1,"PAGE":0}""" })
        {
            var parameters = ToolCallParameters(tool, new { operation = "list", request = JsonSerializer.Deserialize<JsonElement>(json) });
            using var httpResult = await HttpCallAsync(http, bearer, id++, "tools/call", parameters);
            using var stdioResult = await stdio.CallAsync(id++, "tools/call", parameters);
            foreach (var response in new[] { httpResult, stdioResult })
            {
                var content = StructuredContent(response.RootElement);
                Assert.False(content.GetProperty("success").GetBoolean());
                Assert.Equal("validation_failed", content.GetProperty("status").GetString());
                Assert.Contains("request.page", content.GetProperty("summary").GetString(), StringComparison.Ordinal);
            }
        }
        Assert.Equal(0, outbound.ApiRequests);
        Assert.Equal(0, outbound.TokenRequests);
        Assert.Equal(0, Volatile.Read(ref stdioCalls));
    }

    [Fact]
    public async Task Stdio_and_authenticated_http_discovery_are_protocol_equivalent()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new OutboundCallProbe();
        using var factory = CreateFactory(environment.SigningKey, outboundProbe: outbound);
        using var http = CreateClient(factory);
        await using var stdio = StartStdioHost();
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);

        using var stdioInitialize = await stdio.CallAsync(1, "initialize", InitializeParameters());
        using var httpInitialize = await HttpCallAsync(http, bearer, 1, "initialize", InitializeParameters());
        Assert.True(stdioInitialize.RootElement.TryGetProperty("result", out _));
        Assert.True(httpInitialize.RootElement.TryGetProperty("result", out _));
        await stdio.NotifyAsync("notifications/initialized", new { });

        using var stdioTools = await stdio.CallAsync(2, "tools/list", new { });
        using var httpTools = await HttpCallAsync(http, bearer, 2, "tools/list", new { });
        AssertDefinitionParity(stdioTools.RootElement, httpTools.RootElement, "tools", "name");

        using var stdioResources = await stdio.CallAsync(3, "resources/list", new { });
        using var httpResources = await HttpCallAsync(http, bearer, 3, "resources/list", new { });
        AssertDefinitionParity(stdioResources.RootElement, httpResources.RootElement, "resources", "uri");

        using var stdioPrompts = await stdio.CallAsync(4, "prompts/list", new { });
        using var httpPrompts = await HttpCallAsync(http, bearer, 4, "prompts/list", new { });
        AssertDefinitionParity(stdioPrompts.RootElement, httpPrompts.RootElement, "prompts", "name");

        using var stdioSchema = await stdio.CallAsync(5, "tools/call", ToolCallParameters("helpdesk_schema", "list"));
        using var httpSchema = await HttpCallAsync(http, bearer, 5, "tools/call", ToolCallParameters("helpdesk_schema", "list"));
        var stdioCatalog = StructuredContent(stdioSchema.RootElement);
        var httpCatalog = StructuredContent(httpSchema.RootElement);
        Assert.Equal(stdioCatalog.GetProperty("data").GetProperty("catalogRevision").GetString(), httpCatalog.GetProperty("data").GetProperty("catalogRevision").GetString());
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(stdioCatalog.GetProperty("data").GetProperty("operations").GetRawText()),
            JsonNode.Parse(httpCatalog.GetProperty("data").GetProperty("operations").GetRawText())));

        var toolNames = httpTools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("helpdesk_mutations", toolNames);
        Assert.Contains("helpdesk_incidents", toolNames);
        var incidentOperations = httpCatalog.GetProperty("data").GetProperty("operations").EnumerateArray()
            .Where(operation => operation.GetProperty("tool").GetString() == "helpdesk_incidents")
            .Select(operation => operation.GetProperty("operation").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("add_worklog", incidentOperations);
        Assert.Contains("close", incidentOperations);

        using var stdioMutation = await stdio.CallAsync(6, "tools/call", ToolCallParameters("helpdesk_incidents", new
        {
            operation = "close",
            request = new { incidentId = "INC-123", closureNote = "Resolved by test." }
        }));
        using var httpMutation = await HttpCallAsync(http, bearer, 6, "tools/call", ToolCallParameters("helpdesk_incidents", new
        {
            operation = "close",
            request = new { incidentId = "INC-123", closureNote = "Resolved by test." }
        }));
        Assert.Equal("confirmation_required", StructuredContent(stdioMutation.RootElement).GetProperty("status").GetString());
        Assert.Equal("confirmation_required", StructuredContent(httpMutation.RootElement).GetProperty("status").GetString());
        Assert.Equal(0, outbound.CallCount);
    }

    [Fact]
    public async Task Real_Mcp_client_constructs_affected_preview_payloads_from_discovery_without_upstream_writes()
    {
        using var environment = new McpHostEnvironment();
        var outbound = new AgentClientBoundaryProbe();
        using var factory = CreateFactory(environment.SigningKey, boundaryProbe: outbound);
        using var http = CreateClient(factory);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(ResourceUri)
        }, http), cancellationToken: deadline.Token);
        var discovery = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
        foreach (var (tool, operation) in new[]
        {
            ("helpdesk_changes", "lifecycle"), ("helpdesk_changes", "bulk_state"), ("helpdesk_changes", "assign"),
            ("helpdesk_admin_mutations", "create_role"), ("helpdesk_admin_mutations", "update_role"),
            ("helpdesk_admin_mutations", "create_self_service_request"), ("helpdesk_request_tasks", "update")
        })
        {
            var schemaElement = discovery.Single(item => item.Name == tool).JsonSchema;
            var arguments = McpExecutableSchemaTests.ArgumentsFromDiscovery(schemaElement, operation);
            arguments["confirm"] = false;
            var validator = Json.Schema.JsonSchema.FromText(schemaElement.GetRawText());
            Assert.True(validator.Evaluate(arguments).IsValid, arguments.ToJsonString());
            var result = await mcp.CallToolAsync(tool, arguments.Deserialize<Dictionary<string, object?>>()!, cancellationToken: deadline.Token);
            Assert.Equal("confirmation_required", result.StructuredContent!.Value.GetProperty("status").GetString());
            arguments.Remove("confirm");
            Assert.True(validator.Evaluate(arguments).IsValid);
            result = await mcp.CallToolAsync(tool, arguments.Deserialize<Dictionary<string, object?>>()!, cancellationToken: deadline.Token);
            Assert.Equal("confirmation_required", result.StructuredContent!.Value.GetProperty("status").GetString());
        }
        Assert.Equal(0, outbound.ApiRequests);
    }

    [Fact]
    public async Task Notification_summary_over_HTTP_and_stdio_uses_real_authenticated_scoped_counts()
    {
        await using var fixture = await NotificationSummaryFixture.CreateAsync(false);
        await using var api = await fixture.StartApiBridgeAsync();
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: fixture.CreateHandler);
        using var http = CreateClient(factory);
        await using var stdio = StdioMcpClient.Start(NotificationSummaryFixture.Address(api), fixture.Token);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        using var initialized = await stdio.CallAsync(1, "initialize", InitializeParameters());
        Assert.True(initialized.RootElement.TryGetProperty("result", out _));
        await stdio.NotifyAsync("notifications/initialized", new { });
        using var httpInitialized = await HttpCallAsync(http, bearer, 1, "initialize", InitializeParameters());
        using var stdioBefore = await stdio.CallAsync(2, "tools/call", ToolCallParameters("helpdesk_notifications", "summary"));
        using var httpBefore = await HttpCallAsync(http, bearer, 2, "tools/call", ToolCallParameters("helpdesk_notifications", "summary"));
        AssertCounts(stdioBefore, 7, 5, 3);
        AssertCounts(httpBefore, 7, 5, 3);
        Assert.Equal(2, fixture.Paths.Count(path => path == "/api/v1/notifications/summary"));
        Assert.DoesNotContain("/api/v1/notifications/error-summary", fixture.Paths);

        var snapshot = await fixture.PersistenceSnapshotAsync();
        using var preview = await HttpCallAsync(http, bearer, 3, "tools/call", ToolCallParameters("helpdesk_notifications", new
        {
            operation = "mark_read", request = new { ids = fixture.MarkIds }
        }));
        Assert.Equal("confirmation_required", StructuredContent(preview.RootElement).GetProperty("status").GetString());
        Assert.Equal(snapshot, await fixture.PersistenceSnapshotAsync());
        using var confirmed = await HttpCallAsync(http, bearer, 4, "tools/call", ToolCallParameters("helpdesk_notifications", new
        {
            operation = "mark_read", request = new { ids = fixture.MarkIds }, confirm = true
        }));
        Assert.Equal(2, StructuredContent(confirmed.RootElement).GetProperty("data").GetProperty("updated").GetInt32());
        using var stdioAfter = await stdio.CallAsync(3, "tools/call", ToolCallParameters("helpdesk_notifications", "summary"));
        using var httpAfter = await HttpCallAsync(http, bearer, 5, "tools/call", ToolCallParameters("helpdesk_notifications", "summary"));
        AssertCounts(stdioAfter, 7, 3, 1);
        AssertCounts(httpAfter, 7, 3, 1);
        await fixture.AssertInaccessibleRowsUnchangedAsync();

        static void AssertCounts(JsonDocument response, int total, int unread, int errors)
        {
            var content = StructuredContent(response.RootElement);
            Assert.True(content.GetProperty("success").GetBoolean(), content.GetRawText());
            NotificationSummaryHttpTests.AssertCounters(JsonNode.Parse(content.GetProperty("data").GetRawText())!, total, unread, errors);
        }
    }

    [Fact]
    public async Task Timeline_effort_fields_survive_HTTP_and_stdio_transport_for_all_ticket_families()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.Run(async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://api.example.test" + context.Request.Path);
            request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
            using var response = TimelineTransportPayload.Respond(request);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await api.StartAsync();
        using var environment = new McpHostEnvironment();
        using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: TimelineTransportPayload.CreateHandler);
        using var http = CreateClient(factory);
        await using var stdio = StdioMcpClient.Start(NotificationSummaryFixture.Address(api), TimelineTransportPayload.Token);
        var bearer = CreateToken(environment.SigningKey, ["helpdesk.mcp"], ["ai-assistant"]);
        using var initialize = await stdio.CallAsync(1, "initialize", InitializeParameters());
        await stdio.NotifyAsync("notifications/initialized", new { });
        using var httpInitialize = await HttpCallAsync(http, bearer, 1, "initialize", InitializeParameters());
        var id = 2;
        foreach (var family in new[] { "incidents", "requests", "changes" })
        {
            var arguments = new JsonObject
            {
                ["operation"] = "timeline",
                ["request"] = new JsonObject { [family[..^1] + "Id"] = "ticket-wire" }
            };
            var parameters = new { name = "helpdesk_" + family, arguments };
            using var fromStdio = await stdio.CallAsync(id, "tools/call", parameters);
            using var fromHttp = await HttpCallAsync(http, bearer, id++, "tools/call", parameters);
            foreach (var response in new[] { fromStdio, fromHttp })
            {
                var content = StructuredContent(response.RootElement);
                Assert.True(content.GetProperty("success").GetBoolean(), content.GetRawText());
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(TimelineTransportPayload.Json), JsonNode.Parse(content.GetProperty("data").GetRawText())));
            }
        }
    }

    [Fact]
    public async Task Bounded_real_Mcp_gateway_reads_keep_schema_results_and_tenant_scope_stable()
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync(unscopedData: true);
        await authority.SeedIncidentSearchAsync();
        var credential = await authority.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var resource = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri;
        using var environment = new McpHostEnvironment(apiBaseUrl: "https://api.example", publicResourceUri: resource, gateway: true);
        using var authorityGate = new SemaphoreSlim(1, 1);
        HttpMessageHandler Handler() => new SerializedAuthorityHandler(authority.CreateHandler(), authorityGate);
        using var factory = CreateFactory(environment.SigningKey, apiHandlerFactory: Handler, delegationHandlerFactory: Handler);
        using var http = CreateClient(factory, resource);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
        var extended = int.TryParse(Environment.GetEnvironmentVariable("RATELDESK_MCP_READ_SOAK_SECONDS"), out var seconds) && seconds > 0;
        seconds = Math.Clamp(seconds, 1, 900);
        var rounds = extended ? seconds : 4;
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(extended ? seconds + 60 : 30));
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(resource) }, http), cancellationToken: overall.Token);
        var discovery = await mcp.ListToolsAsync(cancellationToken: overall.Token);
        var incidentSchema = discovery.Single(tool => tool.Name == "helpdesk_incidents").JsonSchema;
        var listArguments = McpExecutableSchemaTests.ArgumentsFromDiscovery(incidentSchema, "list");
        listArguments["request"]!["query"] = "needle";
        listArguments["request"]!["page"] = 1; listArguments["request"]!["pageSize"] = 1; listArguments["request"]!["includeTotal"] = true;
        var validator = Json.Schema.JsonSchema.FromText(incidentSchema.GetRawText());
        Assert.True(validator.Evaluate(listArguments).IsValid);
        string? catalogHash = null;
        var calls = 0;
        var duration = Stopwatch.StartNew();
        for (var round = 0; round < rounds; round++)
        {
            using var callDeadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            callDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var list = await mcp.CallToolAsync("helpdesk_incidents", listArguments.Deserialize<Dictionary<string, object?>>()!, cancellationToken: callDeadline.Token);
            var listResponse = list.StructuredContent!.Value;
            Assert.True(listResponse.GetProperty("success").GetBoolean());
            var page = listResponse.GetProperty("data");
            Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
            var row = Assert.Single(page.GetProperty("items").EnumerateArray());
            var id = row.GetProperty("id").GetString(); Assert.Equal("incident-beta", id);
            var getArguments = McpExecutableSchemaTests.ArgumentsFromDiscovery(incidentSchema, "get");
            getArguments["request"]!["incidentId"] = id;
            Assert.True(validator.Evaluate(getArguments).IsValid);
            var get = await mcp.CallToolAsync("helpdesk_incidents", getArguments.Deserialize<Dictionary<string, object?>>()!, cancellationToken: callDeadline.Token);
            Assert.True(get.StructuredContent!.Value.GetProperty("success").GetBoolean());
            Assert.Equal(id, get.StructuredContent.Value.GetProperty("data").GetProperty("id").GetString());
            var capabilities = await mcp.CallToolAsync("helpdesk_capabilities", new Dictionary<string, object?> { ["operation"] = "get" }, cancellationToken: callDeadline.Token);
            Assert.True(capabilities.StructuredContent!.Value.GetProperty("success").GetBoolean());
            var metadata = capabilities.StructuredContent.Value.GetProperty("data");
            Assert.Equal(McpOperationCatalog.Revision, metadata.GetProperty("catalogRevision").GetString());
            var currentHash = metadata.GetProperty("catalogHash").GetString();
            Assert.NotEmpty(currentHash!); catalogHash ??= currentHash; Assert.Equal(catalogHash, currentHash);
            calls += 3;
            if (extended)
            {
                var pause = TimeSpan.FromSeconds(round + 1) - duration.Elapsed;
                if (pause > TimeSpan.Zero) await Task.Delay(pause, overall.Token);
            }
        }
        Assert.Equal(rounds * 3, calls);
        Assert.True(calls >= 12 || extended);
        Assert.Equal(rounds, authority.IncidentListQueries.Count);
    }

    private static WebApplicationFactory<Helpdesk.Mcp.Http.Program> CreateFactory(ECDsa signingKey, AuditLogCollector? audit = null, OutboundCallProbe? outboundProbe = null, McpJwtValidation? jwt = null, AgentClientBoundaryProbe? boundaryProbe = null, GatewayDelegationProbe? delegationProbe = null, Func<HttpMessageHandler>? apiHandlerFactory = null, Func<HttpMessageHandler>? delegationHandlerFactory = null, Action<IServiceCollection>? configureServices = null)
        => new WebApplicationFactory<Helpdesk.Mcp.Http.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                if (audit is not null)
                    services.AddSingleton<ILogger<McpToolInvocationAudit>>(_ => audit.CreateLogger());
                if (boundaryProbe is not null)
                {
                    services.AddHttpClient(Helpdesk.AgentClient.HelpdeskAgentClient.ApiHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(boundaryProbe.CreateHandler);
                    services.AddHttpClient(Helpdesk.AgentClient.HelpdeskAgentClient.AuthHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(boundaryProbe.CreateHandler);
                }
                else if (outboundProbe is not null)
                {
                    services.AddHttpClient(Helpdesk.AgentClient.HelpdeskAgentClient.ApiHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(outboundProbe.CreateHandler);
                    services.AddHttpClient(Helpdesk.AgentClient.HelpdeskAgentClient.AuthHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(outboundProbe.CreateHandler);
                }
                else if (apiHandlerFactory is not null)
                {
                    services.AddHttpClient(Helpdesk.AgentClient.HelpdeskAgentClient.ApiHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(apiHandlerFactory);
                    services.AddHttpClient(Helpdesk.AgentClient.HelpdeskAgentClient.AuthHttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(apiHandlerFactory);
                }
                if (delegationProbe is not null)
                {
                    services.AddHttpClient(GatewayDelegationAuthenticationHandler.DelegationClientName)
                        .ConfigurePrimaryHttpMessageHandler(delegationProbe.CreateHandler);
                }
                else if (delegationHandlerFactory is not null)
                {
                    services.AddHttpClient(GatewayDelegationAuthenticationHandler.DelegationClientName)
                        .ConfigurePrimaryHttpMessageHandler(delegationHandlerFactory);
                }

                services.PostConfigure<JwtBearerOptions>("HelpdeskMcpJwt", options =>
                {
                    options.RequireHttpsMetadata = false;
                    var authority = jwt?.Authority ?? Authority;
                    var resourceUri = jwt?.ResourceUri ?? ResourceUri;
                    var configuration = new OpenIdConnectConfiguration { Issuer = authority };
                    configuration.SigningKeys.Add(new ECDsaSecurityKey(signingKey));
                    options.Configuration = configuration;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = authority,
                        ValidateAudience = true,
                        ValidAudience = resourceUri,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new ECDsaSecurityKey(signingKey),
                        ClockSkew = TimeSpan.Zero
                    };
                });
                configureServices?.Invoke(services);
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Helpdesk.Mcp.Http.Program> factory, string resourceUri = ResourceUri)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(new Uri(resourceUri).GetLeftPart(UriPartial.Authority))
        });

    private static StringContent McpRequest(string method) => new(
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "helpdesk-tests", version = "1.0" }
            }
        }),
        Encoding.UTF8,
        "application/json");

    private static object InitializeParameters() => new
    {
        protocolVersion = "2025-03-26",
        capabilities = new { },
        clientInfo = new { name = "helpdesk-tests", version = "1.0" }
    };

    private static object ToolCallParameters(string tool, string operation) => new
    {
        name = tool,
        arguments = new { operation }
    };

    private static object ToolCallParameters(string tool, object arguments) => new
    {
        name = tool,
        arguments
    };

    private static StringContent McpToolCall(string tool, string operation) => new(
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = tool,
                arguments = new { operation }
            }
        }),
        Encoding.UTF8,
        "application/json");

    private static async Task<JsonDocument> HttpCallAsync(HttpClient client, string bearer, int id, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return ParseMcpResponse(responseBody);
    }

    private static HttpRequestMessage CreateGatewayRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = ToolCallParameters("helpdesk_auth", "status")
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "rdk_local-a");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    private static Task<T> AwaitCancellationAsync<T>(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    private static JsonDocument ParseMcpResponse(string responseBody)
    {
        if (responseBody.TrimStart().StartsWith("event:", StringComparison.Ordinal))
        {
            var payload = string.Join(
                "\n",
                responseBody.Split('\n')
                    .Select(line => line.TrimEnd('\r'))
                    .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(line => line["data:".Length..].TrimStart()));
            return JsonDocument.Parse(payload);
        }

        return JsonDocument.Parse(responseBody);
    }

    private static void AssertDefinitionParity(JsonElement stdio, JsonElement http, string collectionName, string nameProperty)
    {
        var stdioDefinitions = Definitions(stdio, collectionName, nameProperty);
        var httpDefinitions = Definitions(http, collectionName, nameProperty);
        Assert.Equal(stdioDefinitions.Keys.Order(), httpDefinitions.Keys.Order());
        foreach (var name in stdioDefinitions.Keys)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(stdioDefinitions[name].GetRawText()), JsonNode.Parse(httpDefinitions[name].GetRawText())), $"MCP {collectionName} definition differs for {name}.");
    }

    private static Dictionary<string, JsonElement> Definitions(JsonElement response, string collectionName, string nameProperty)
        => response.GetProperty("result").GetProperty(collectionName).EnumerateArray()
            .ToDictionary(item => item.GetProperty(nameProperty).GetString()!, item => item, StringComparer.Ordinal);

    private static JsonElement StructuredContent(JsonElement response)
        => response.GetProperty("result").GetProperty("structuredContent");

    private static async Task AssertUnauthorizedAsync(HttpClient client, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = McpRequest("initialize") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string CreateToken(ECDsa signingKey, string[] scopes, string[] groups, string? issuer = null, string? audience = null, string subject = "ai-assistant-client")
    {
        var token = new JwtSecurityToken(
            issuer: issuer ?? Authority,
            audience: audience ?? ResourceUri,
            claims:
            [
                new Claim("sub", subject),
                new Claim("scope", string.Join(' ', scopes)),
                .. groups.Select(group => new Claim("groups", group))
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new ECDsaSecurityKey(signingKey), SecurityAlgorithms.EcdsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class McpHostEnvironment : IDisposable
    {
        private const string ConfigurationPathVariable = "RATELDESK_MCP_CONFIG";
        private const string InstanceVariable = "RATELDESK_MCP_INSTANCE";
        private const string DevApiVariable = "RATELDESK_MCP_DEV_API_BASE_URL";
        private const string ProdApiVariable = "RATELDESK_MCP_PROD_API_BASE_URL";
        private const string PublicResourceVariable = "Helpdesk__Mcp__PublicResourceUri";
        private const string AllowedOriginVariable = "Helpdesk__Mcp__AllowedOrigins__0";
        private const string AuthenticationModeVariable = "Helpdesk__Mcp__AuthenticationMode";
        private const string DelegationTimeoutVariable = "Helpdesk__Mcp__DelegationTimeoutSeconds";
        private const string AuthorityVariable = "Authentication__AuthentikMcp__Authority";
        private const string AudienceVariable = "Authentication__AuthentikMcp__Audience";
        private const string ScopeVariable = "Authentication__AuthentikMcp__RequiredScopes__0";
        private const string GroupVariable = "Authentication__AuthentikMcp__RequiredGroups__0";
        private readonly string? _previousConfigurationPath = Environment.GetEnvironmentVariable(ConfigurationPathVariable);
        private readonly string? _previousInstance = Environment.GetEnvironmentVariable(InstanceVariable);
        private readonly string? _previousDevApi = Environment.GetEnvironmentVariable(DevApiVariable);
        private readonly string? _previousProdApi = Environment.GetEnvironmentVariable(ProdApiVariable);
        private readonly string? _previousPublicResource = Environment.GetEnvironmentVariable(PublicResourceVariable);
        private readonly string? _previousAllowedOrigin = Environment.GetEnvironmentVariable(AllowedOriginVariable);
        private readonly string? _previousAuthenticationMode = Environment.GetEnvironmentVariable(AuthenticationModeVariable);
        private readonly string? _previousDelegationTimeout = Environment.GetEnvironmentVariable(DelegationTimeoutVariable);
        private readonly string? _previousAuthority = Environment.GetEnvironmentVariable(AuthorityVariable);
        private readonly string? _previousAudience = Environment.GetEnvironmentVariable(AudienceVariable);
        private readonly string? _previousScope = Environment.GetEnvironmentVariable(ScopeVariable);
        private readonly string? _previousGroup = Environment.GetEnvironmentVariable(GroupVariable);
        private readonly string _configurationPath = Path.Combine(Path.GetTempPath(), $"helpdesk-mcp-http-{Guid.NewGuid():N}.json");

        public McpHostEnvironment(string instance = "dev", string apiBaseUrl = "https://api.example", string publicResourceUri = ResourceUri, bool gateway = false, int delegationTimeoutSeconds = 20)
        {
            File.WriteAllText(_configurationPath, JsonSerializer.Serialize(new
            {
                apiBaseUrl,
                credentialMode = gateway ? "gateway" : "authentik",
                authentikTokenUrl = gateway ? null : "https://auth.example/token",
                authentikClientId = gateway ? null : "helpdesk-mcp-test",
                authentikUsername = gateway ? null : "helpdesk-mcp-test",
                authentikAppPassword = gateway ? null : "test-only-password"
            }));
            Environment.SetEnvironmentVariable(ConfigurationPathVariable, _configurationPath);
            Environment.SetEnvironmentVariable(InstanceVariable, instance);
            Environment.SetEnvironmentVariable(instance == "dev" ? DevApiVariable : ProdApiVariable, apiBaseUrl);
            Environment.SetEnvironmentVariable(PublicResourceVariable, publicResourceUri);
            Environment.SetEnvironmentVariable(AllowedOriginVariable, "https://client.example");
            Environment.SetEnvironmentVariable(AuthenticationModeVariable, gateway ? "gateway" : "authentik");
            Environment.SetEnvironmentVariable(DelegationTimeoutVariable, delegationTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(AuthorityVariable, Authority);
            Environment.SetEnvironmentVariable(AudienceVariable, publicResourceUri);
            Environment.SetEnvironmentVariable(ScopeVariable, "helpdesk.mcp");
            Environment.SetEnvironmentVariable(GroupVariable, "ai-assistant");
        }

        public ECDsa SigningKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string ConfigurationPath => _configurationPath;

        public void Dispose()
        {
            SigningKey.Dispose();
            File.Delete(_configurationPath);
            Environment.SetEnvironmentVariable(ConfigurationPathVariable, _previousConfigurationPath);
            Environment.SetEnvironmentVariable(InstanceVariable, _previousInstance);
            Environment.SetEnvironmentVariable(DevApiVariable, _previousDevApi);
            Environment.SetEnvironmentVariable(ProdApiVariable, _previousProdApi);
            Environment.SetEnvironmentVariable(PublicResourceVariable, _previousPublicResource);
            Environment.SetEnvironmentVariable(AllowedOriginVariable, _previousAllowedOrigin);
            Environment.SetEnvironmentVariable(AuthenticationModeVariable, _previousAuthenticationMode);
            Environment.SetEnvironmentVariable(DelegationTimeoutVariable, _previousDelegationTimeout);
            Environment.SetEnvironmentVariable(AuthorityVariable, _previousAuthority);
            Environment.SetEnvironmentVariable(AudienceVariable, _previousAudience);
            Environment.SetEnvironmentVariable(ScopeVariable, _previousScope);
            Environment.SetEnvironmentVariable(GroupVariable, _previousGroup);
        }
    }

    private sealed class AuditLogCollector : IDisposable
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger<McpToolInvocationAudit> CreateLogger() => new AuditLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class AuditLogger(ConcurrentQueue<string> messages) : ILogger<McpToolInvocationAudit>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class OutboundCallProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public HttpMessageHandler CreateHandler() => new ProbeHandler(this);

        private sealed class ProbeHandler(OutboundCallProbe probe) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref probe._callCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    private sealed class SerializedAuthorityHandler(HttpMessageHandler inner, SemaphoreSlim gate) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try { return await base.SendAsync(request, cancellationToken); }
            finally { gate.Release(); }
        }
    }

    private sealed class SyntheticProviderTokenHandler(string downstreamToken, HttpMessageHandler apiHandler, ConcurrentQueue<string> paths)
        : DelegatingHandler(apiHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == "https://auth.example/token")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { access_token = downstreamToken, expires_in = 300 }), Encoding.UTF8, "application/json")
                });
            Assert.Equal("Bearer " + downstreamToken, request.Headers.Authorization?.ToString());
            paths.Enqueue(request.RequestUri!.AbsolutePath);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class AgentClientBoundaryProbe
    {
        private int _tokenRequests;
        private int _apiRequests;
        private string? _apiAuthorization;
        private readonly ConcurrentQueue<string> _apiAuthorizations = new();
        private readonly ConcurrentQueue<string> _apiPathsAndQueries = new();

        public int TokenRequests => Volatile.Read(ref _tokenRequests);
        public int ApiRequests => Volatile.Read(ref _apiRequests);
        public string? ApiAuthorization => Volatile.Read(ref _apiAuthorization);
        public IReadOnlyCollection<string> ApiAuthorizations => _apiAuthorizations.ToArray();
        public IReadOnlyCollection<string> ApiPathsAndQueries => _apiPathsAndQueries.ToArray();

        public HttpMessageHandler CreateHandler() => new ProbeHandler(this);

        private sealed class ProbeHandler(AgentClientBoundaryProbe probe) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (string.Equals(request.RequestUri?.Host, "auth.example", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref probe._tokenRequests);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"outbound-agent-token\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
                    });
                }

                Interlocked.Increment(ref probe._apiRequests);
                probe._apiPathsAndQueries.Enqueue(request.Method + " " + request.RequestUri!.PathAndQuery);
                Volatile.Write(ref probe._apiAuthorization, request.Headers.Authorization?.ToString());
                if (request.Headers.Authorization is not null)
                    probe._apiAuthorizations.Enqueue(request.Headers.Authorization.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.RequestUri!.AbsolutePath == "/api/v1/metrics/workflows"
                        ? new StringContent("# TYPE helpdesk_tasks_in_progress gauge\nhelpdesk_tasks_in_progress 2\n", Encoding.UTF8, "text/plain")
                        : new StringContent("{\"correlationId\":\"corr-123\"}", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    private sealed class GatewayDelegationProbe
    {
        private int _requests;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _responseFactory;

        public GatewayDelegationProbe(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? responseFactory = null)
        {
            _responseFactory = responseFactory;
        }

        public int Requests => Volatile.Read(ref _requests);

        public HttpMessageHandler CreateHandler() => new ProbeHandler(this);

        private sealed class ProbeHandler(GatewayDelegationProbe probe) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref probe._requests);
                if (probe._responseFactory is not null)
                    return probe._responseFactory(request, cancellationToken);
                var inbound = request.Headers.TryGetValues("Authorization", out var authorizationValues)
                    ? authorizationValues.SingleOrDefault()?.Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase)
                    : null;
                var resource = request.Headers.GetValues("X-RatelDesk-Mcp-Resource").SingleOrDefault();
                if (string.IsNullOrWhiteSpace(inbound) || string.IsNullOrWhiteSpace(resource))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                var suffix = inbound["rdk_".Length..];
                var json = JsonSerializer.Serialize(new
                {
                    accessToken = $"rdx_{suffix}",
                    expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                    userId = $"user-{suffix}",
                    name = $"User {suffix}",
                    primaryOrganizationId = $"org-{suffix}",
                    permissions = new[] { "Incident.Read" },
                    allowedOrganizationIds = new[] { $"org-{suffix}" }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        }
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            AwaitCancellationAsync<int>(cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(AwaitCancellationAsync<int>(cancellationToken));
    }

    private sealed record McpJwtValidation(string ResourceUri, string Authority = McpHttpHostTests.Authority);

    private sealed class StdioMcpClient(Process process, string configurationPath) : IAsyncDisposable
    {
        private readonly Process _process = process;
        private readonly string _configurationPath = configurationPath;

        public static StdioMcpClient Start(string apiBaseUrl = "https://api.example", string? integrationCredential = null)
        {
            var configurationPath = Path.Combine(Path.GetTempPath(), $"rateldesk-mcp-stdio-{Guid.NewGuid():N}.json");
            new AgentClientConfigurationStore(configurationPath).Save(integrationCredential is not null
                ? AgentClientConfiguration.Empty with { ApiBaseUrl = apiBaseUrl, CredentialMode = "integration", IntegrationCredential = integrationCredential }
                : new AgentClientConfiguration(
                apiBaseUrl,
                "https://auth.example/token",
                "helpdesk-mcp-test",
                "helpdesk-mcp-test",
                "test-only-password",
                "openid profile email",
                null));
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(Helpdesk.Mcp.HelpdeskMcpStdioHost).Assembly.Location);
            start.Environment["RATELDESK_MCP_CONFIG"] = configurationPath;
            start.Environment["RATELDESK_MCP_INSTANCE"] = "dev";
            start.Environment["RATELDESK_MCP_DEV_API_BASE_URL"] = apiBaseUrl;
            start.Environment["RATELDESK_API_BASE_URL"] = "https://api.example";
            start.Environment["RATELDESK_AUTHENTIK_TOKEN_URL"] = "https://auth.example/token";
            start.Environment["RATELDESK_AUTHENTIK_CLIENT_ID"] = "helpdesk-mcp-test";
            start.Environment["RATELDESK_AUTHENTIK_USERNAME"] = "helpdesk-mcp-test";
            start.Environment["RATELDESK_AUTHENTIK_APP_PASSWORD"] = "test-only-password";
            return new StdioMcpClient(Process.Start(start) ?? throw new InvalidOperationException("Could not start the Helpdesk MCP stdio host."), configurationPath);
        }

        public async Task<JsonDocument> CallAsync(int id, string method, object parameters)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(string.IsNullOrWhiteSpace(line), $"The stdio MCP host did not respond to '{method}'.");
            return JsonDocument.Parse(line!);
        }

        public async Task NotifyAsync(string method, object parameters)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            _process.Dispose();
            File.Delete(_configurationPath);
        }
    }

    private static StdioMcpClient StartStdioHost() => StdioMcpClient.Start();
}
