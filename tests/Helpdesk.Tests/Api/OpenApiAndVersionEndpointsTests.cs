using System.Net;
using System.Text.Json;
using Helpdesk.API;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public class OpenApiAndVersionEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OpenApiAndVersionEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseIsolatedTestStorage();
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Production");
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "test",
                    ["Jwt:Audience"] = "test",
                    ["Jwt:Key"] = "test-key-123456789012345678901234",
                    ["AppBar:BuildVersion"] = "0.0.148"
                });
            });
        });
    }

    [Fact]
    public async Task OpenApi_V1_Json_IsServed()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"openapi\"", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/system/version", content, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(content);
        // The direct API document keeps Try It on its own root; the Web-hosted
        // Scalar configuration supplies the distinct /api proxy server below.
        var directServers = document.RootElement.GetProperty("servers");
        Assert.Equal(client.BaseAddress!.GetLeftPart(UriPartial.Authority) + "/", directServers[0].GetProperty("url").GetString());
        Assert.Equal("RatelDesk API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("info").GetProperty("version").GetString()));
        Assert.True(document.RootElement.TryGetProperty("x-tagGroups", out var groups));
        Assert.Contains(groups.EnumerateArray(), group => group.GetProperty("name").GetString() == "Ticketing");

        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("JWT", schemes.GetProperty("JwtBearer").GetProperty("bearerFormat").GetString());
        Assert.Equal("opaque rdk credential", schemes.GetProperty("IntegrationCredential").GetProperty("bearerFormat").GetString());
        Assert.Equal("paired opaque rdk credential", schemes.GetProperty("McpIntegrationCredential").GetProperty("bearerFormat").GetString());
        Assert.Equal("cookie", schemes.GetProperty("LocalSession").GetProperty("in").GetString());

        var integrationCredentialsEndpoint = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == "/api/v1/integration-credentials" &&
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        Assert.Contains(integrationCredentialsEndpoint.Metadata, metadata => metadata is Microsoft.AspNetCore.Authorization.IAuthorizeData);

        Assert.Empty(SecuritySchemes(document, "/api/v1/setup/status", "get"));
        Assert.Empty(SecuritySchemes(document, "/api/v1/tickets/public/view", "get"));
        Assert.Equal(["JwtBearer", "LocalSession"], SecuritySchemes(document, "/api/v1/integration-credentials", "get"));
        Assert.Equal(["ServiceIdentity"], SecuritySchemes(document, "/api/v1/orchestration/provider/m2m/ping", "get"));
        Assert.Equal(["AiAgentJwt"], SecuritySchemes(document, "/api/v1/auth/ai-agent/status", "get"));
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession"], SecuritySchemes(document, "/api/v1/incidents", "get"));
        Assert.Equal(["McpIntegrationCredential"], SecuritySchemes(document, "/api/v1/mcp/execution-token", "post"));
        Assert.Equal(["IntegrationCredential"], SecuritySchemes(document, "/api/v1/integration-credentials/self/revoke", "post"));
    }

    [Fact]
    public async Task OpenApi_V1_Json_classifies_every_documented_operation()
    {
        using var client = _factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var knownTags = document.RootElement.GetProperty("tags")
            .EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        var operationCount = 0;

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(item => item.Name is "delete" or "get" or "head" or "options" or "patch" or "post" or "put"))
            {
                operationCount++;
                if (!operation.Value.TryGetProperty("tags", out var tags) || tags.GetArrayLength() == 0)
                {
                    violations.Add($"{operation.Name.ToUpperInvariant()} {path.Name} is untagged");
                    continue;
                }

                foreach (var tag in tags.EnumerateArray().Select(item => item.GetString()).OfType<string>().Where(tag => !knownTags.Contains(tag)))
                    violations.Add($"{operation.Name.ToUpperInvariant()} {path.Name} uses unknown tag '{tag}'");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        // Release includes automatic service issuance, signed code pairing, named mappings,
        // and the preserved Netclaw administration and business APIs;
        // Debug also exposes the authorized /__debug/me endpoint. Keep both inventories
        // explicit so additions or omissions require a reviewed taxonomy update.
#if DEBUG
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/__debug/me", out _));
        Assert.Equal(393, operationCount);
#else
        Assert.Equal(392, operationCount);
#endif
        var paths = document.RootElement.GetProperty("paths");
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.Contains("service-link", StringComparison.Ordinal) || path.Name.Contains("service-clients", StringComparison.Ordinal));
        var receiverOperations = new[]
        {
            ("/api/v1/integrations/netratel/capabilities", "get"),
            ("/api/v1/integrations/netratel/incident-receipts/{key}", "get"),
            ("/api/v1/integrations/netratel/targets/validate", "post"),
            ("/api/v1/integrations/netratel/sources", "get"),
            ("/api/v1/integrations/netratel/sources", "post"),
            ("/api/v1/integrations/netratel/sources/{namespaceId}", "patch"),
            ("/api/v1/integrations/netratel/sources/{namespaceId}/principal-bindings/{credentialId}", "put")
        };
        foreach (var (path, method) in receiverOperations)
        {
            Assert.True(paths.TryGetProperty(path, out var receiverPath), $"Missing documented receiver path: {path}");
            Assert.True(receiverPath.TryGetProperty(method, out var receiverOperation));
            var expectedTag = path.Contains("/sources", StringComparison.Ordinal)
                ? "NetRatel Source Administration" : "NetRatel Incident Receiver";
            Assert.Equal([expectedTag], receiverOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
            Assert.Equal(path.Contains("/sources", StringComparison.Ordinal)
                ? new[] { "JwtBearer", "LocalSession" } : new[] { "IntegrationCredential", "ServiceIdentity" }, SecuritySchemes(document, path, method));
        }
        Assert.Empty(SecuritySchemes(document, "/connect/token", "post"));
        Assert.Empty(SecuritySchemes(document, "/api/pairing/v1/metadata", "get"));
        Assert.Empty(SecuritySchemes(document, "/api/pairing/v1/exchange", "post"));
        Assert.Equal(["PairingSetup"], SecuritySchemes(document, "/api/pairing/v1/directory", "get"));
        var connectionSetupOperations = new[]
        {
            ("/api/v1/admin/system-connections", "get"),
            ("/api/v1/admin/system-connections/code", "post"),
            ("/api/v1/admin/system-connections/pair", "post"),
            ("/api/v1/admin/system-connections/{pairId}/mappings/{mappingId}", "put"),
            ("/api/v1/admin/system-connections/{pairId}/mappings/{mappingId}/test", "post"),
            ("/api/v1/admin/system-connections/{pairId}/mappings/{mappingId}", "delete")
        };
        foreach (var (path, method) in connectionSetupOperations)
        {
            Assert.True(paths.TryGetProperty(path, out var setupPath), $"Missing documented connection setup path: {path}");
            Assert.True(setupPath.TryGetProperty(method, out var setupOperation));
            Assert.Equal(["System Connections"], setupOperation.GetProperty("tags").EnumerateArray().Select(item => item.GetString()).OfType<string>().ToArray());
            Assert.Equal(["JwtBearer", "LocalSession"], SecuritySchemes(document, path, method));
        }
        Assert.False(paths.TryGetProperty("/api/v1/admin/orchestration/test-draft", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/test-draft", out var netclawDraft));
        Assert.True(netclawDraft.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/pair-and-save", out var pairNetclaw));
        Assert.True(pairNetclaw.TryGetProperty("post", out var pairNetclawOperation));
        Assert.Equal(["Netclaw"], pairNetclawOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession"],
            SecuritySchemes(document, "/api/v1/admin/netclaw/pair-and-save", "post"));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/legacy-sessions/unbound", out var unboundLegacySessions));
        Assert.True(unboundLegacySessions.TryGetProperty("get", out var listUnboundOperation));
        Assert.Equal(["Netclaw"], listUnboundOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession"],
            SecuritySchemes(document, "/api/v1/admin/netclaw/legacy-sessions/unbound", "get"));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/legacy-sessions/confirm-owner", out var confirmLegacyOwner));
        Assert.True(confirmLegacyOwner.TryGetProperty("post", out var confirmLegacyOwnerOperation));
        Assert.Equal(["Netclaw"], confirmLegacyOwnerOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession"],
            SecuritySchemes(document, "/api/v1/admin/netclaw/legacy-sessions/confirm-owner", "post"));
        var pairEndpoint = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/v1/admin/netclaw/pair-and-save" &&
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        Assert.Contains(pairEndpoint.Metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>(),
            metadata => metadata.Policy == "HelpdeskAdmin");

        var pathOrder = paths.EnumerateObject().Select(path => path.Name).ToArray();
        Assert.Equal(pathOrder.Order(StringComparer.Ordinal), pathOrder);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            var methodOrder = path.Value.EnumerateObject()
                .Where(item => item.Name is "delete" or "get" or "head" or "options" or "patch" or "post" or "put")
                .Select(item => item.Name)
                .ToArray();
            Assert.Equal(methodOrder.Order(StringComparer.Ordinal), methodOrder);
        }

        var tagOrder = document.RootElement.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString())
            .OfType<string>()
            .ToArray();
        Assert.Equal(tagOrder.Order(StringComparer.Ordinal), tagOrder);
    }

    private static string[] SecuritySchemes(JsonDocument document, string path, string method)
    {
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty(path, out var pathItem),
            $"OpenAPI path '{path}' was not generated. Available paths: {string.Join(", ", paths.EnumerateObject().Select(item => item.Name))}");
        Assert.True(pathItem.TryGetProperty(method, out var operation),
            $"OpenAPI operation '{method}' was not generated for path '{path}'.");
        return operation.TryGetProperty("security", out var security)
            ? security.EnumerateArray()
                .SelectMany(requirement => requirement.EnumerateObject().Select(property => property.Name))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

}
