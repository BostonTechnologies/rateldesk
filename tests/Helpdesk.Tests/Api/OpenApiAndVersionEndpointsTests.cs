using System.Net;
using System.Text.Json;
using Helpdesk.API;
using Helpdesk.API.Endpoints.Authentication;
using Microsoft.AspNetCore.Authorization;
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
        Assert.Equal("opaque rdx execution token", schemes.GetProperty("McpExecution").GetProperty("bearerFormat").GetString());
        Assert.Contains("mcp-owner", schemes.GetProperty("McpExecution").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("cookie", schemes.GetProperty("LocalSession").GetProperty("in").GetString());

        var integrationCredentialsEndpoint = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == "/api/v1/integration-credentials" &&
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        Assert.Contains(integrationCredentialsEndpoint.Metadata.OfType<IAuthorizeData>(),
            metadata => metadata.Policy == IntegrationCredentialEndpoints.OwnerManagementPolicy);

        Assert.Empty(SecuritySchemes(document, "/api/v1/setup/status", "get"));
        Assert.Empty(SecuritySchemes(document, "/api/v1/tickets/public/view", "get"));
        Assert.Equal(["JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, "/api/v1/integration-credentials", "get"));
        // Creating another account credential still requires the interactive session
        // policy even though the enclosing group permits owner execution for lifecycle actions.
        Assert.Equal(["JwtBearer", "LocalSession"], SecuritySchemes(document, "/api/v1/integration-credentials", "post"));
        Assert.Equal(["JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, "/api/v1/integration-credentials/{credentialId}", "delete"));
        Assert.Equal(["ServiceIdentity"], SecuritySchemes(document, "/api/v1/orchestration/provider/m2m/ping", "get"));
        Assert.Equal(["AiAgentJwt", "McpExecution"], SecuritySchemes(document, "/api/v1/auth/ai-agent/status", "get"));
        Assert.Equal(["AiAgentJwt", "McpExecution"], SecuritySchemes(document, "/api/v1/ops/ai-agent/logs", "get"));
        Assert.Equal(["AiAgentJwt", "McpExecution"], SecuritySchemes(document, "/api/v1/ai-assistant/investigations/{invocationId}/worklog", "post"));
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, "/api/v1/incidents", "get"));
        Assert.Equal(["McpIntegrationCredential"], SecuritySchemes(document, "/api/v1/mcp/execution-token", "post"));
        Assert.Equal(["IntegrationCredential", "McpExecution"], SecuritySchemes(document, "/api/v1/integration-credentials/self/revoke", "post"));
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
        // the preserved Netclaw administration and business APIs, and the three
        // scoped assignee search/current-label/change-implementor operations, and
        // eight browser-approved integration-credential pairing operations,
        // ticket filter suggestions and three bounded pending-email operations
        // (search, organization lookup and confirmed actions), six host deployment
        // approval operations and durable integration-credential record deletion,
        // plus single-change ticket state and external orchestration connectivity testing.
        // Beta.8 adds exactly six methods: authenticated task approval read/approve/reject,
        // authenticated Change approval read/review, and authenticated dataset row upsert.
        // Beta.11 adds the administrator-authorized inbound email rule deletion operation.
        // Beta.13 adds five MCP policy and seven AI investigation/guidance operations.
        // Reviewed credential access adds three interactive-session operations: inspect,
        // review an exact permission change, and apply the confirmed change.
        // Beta.14 adds one instance-administrator orphan attachment metadata deletion.
        // Reviewed MCP setup adds options, atomic creation and delivery acknowledgement;
        // policy management adds the named directory and revision-bound deletion preview.
        // Debug also exposes the authorized /__debug/me endpoint. Keep both inventories
        // explicit so additions or omissions require a reviewed taxonomy update.
#if DEBUG
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/__debug/me", out _));
        Assert.Equal(445, operationCount);
#else
        Assert.Equal(444, operationCount);
#endif
        var paths = document.RootElement.GetProperty("paths");
        foreach (var (path, operationId, tag) in new[]
        {
            ("/api/v1/changes/{id}/state", "UpdateChangeTicketState", "Changes"),
            ("/api/v1/admin/orchestration/test", "TestOrchestrationConnectivity", "External Orchestration")
        })
        {
            Assert.True(paths.TryGetProperty(path, out var route), $"Missing documented operation: POST {path}");
            Assert.True(route.TryGetProperty("post", out var operation));
            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.Equal([tag], operation.GetProperty("tags").EnumerateArray().Select(value => value.GetString()).OfType<string>().ToArray());
            Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, path, "post"));
        }
        const string inboundRulePath = "/api/v1/inbound-email-rules/{id}";
        Assert.True(paths.TryGetProperty(inboundRulePath, out var inboundRuleRoute));
        Assert.True(inboundRuleRoute.TryGetProperty("delete", out var inboundRuleDeletion));
        Assert.Equal(["Inbound Email Rules"], inboundRuleDeletion.GetProperty("tags").EnumerateArray().Select(value => value.GetString()).OfType<string>().ToArray());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, inboundRulePath, "delete"));
        var inboundRuleEndpoint = Assert.Single(_factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == inboundRulePath &&
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);
        Assert.Null(inboundRuleEndpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Contains(inboundRuleEndpoint.Metadata.OfType<IAuthorizeData>(), metadata => metadata.Policy == "HelpdeskAdmin");
        const string orphanAttachmentPath = "/api/v1/attachments/{id}/orphan-metadata";
        Assert.True(paths.TryGetProperty(orphanAttachmentPath, out var orphanAttachmentRoute));
        Assert.True(orphanAttachmentRoute.TryGetProperty("delete", out var orphanAttachmentDeletion));
        Assert.Equal(["Attachments"], orphanAttachmentDeletion.GetProperty("tags").EnumerateArray().Select(value => value.GetString()).OfType<string>().ToArray());
        Assert.Equal("Remove one orphan attachment metadata record", orphanAttachmentDeletion.GetProperty("summary").GetString());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, orphanAttachmentPath, "delete"));
        var orphanAttachmentEndpoint = Assert.Single(_factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == "/api/v1/attachments/{id:guid}/orphan-metadata" &&
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true);
        Assert.Null(orphanAttachmentEndpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Contains(orphanAttachmentEndpoint.Metadata.OfType<IAuthorizeData>(), metadata => metadata.Policy == "HelpdeskAdmin");
        var identityGroup = Assert.Single(document.RootElement.GetProperty("x-tagGroups").EnumerateArray(),
            group => group.GetProperty("name").GetString() == "Identity & Access");
        Assert.Contains(identityGroup.GetProperty("tags").EnumerateArray(),
            tag => tag.GetString() == "Integration Credential Pairing");
        Assert.Contains(identityGroup.GetProperty("tags").EnumerateArray(),
            tag => tag.GetString() == "MCP Deployment Approval");
        var credentialPairingOperations = new[]
        {
            ("capabilities", "get", false),
            ("start", "post", false),
            ("poll", "post", false),
            ("ack", "post", false),
            ("cancel", "post", false),
            ("review", "post", true),
            ("approve", "post", true),
            ("deny", "post", true)
        };
        foreach (var (action, method, requiresSession) in credentialPairingOperations)
        {
            var path = $"/api/v1/integration-credentials/pairing/{action}";
            Assert.True(paths.TryGetProperty(path, out var pairingPath), $"Missing documented credential pairing path: {path}");
            Assert.True(pairingPath.TryGetProperty(method, out var pairingOperation));
            Assert.Equal(["Integration Credential Pairing"], pairingOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
            Assert.Equal(requiresSession ? new[] { "JwtBearer", "LocalSession" } : [], SecuritySchemes(document, path, method));
        }
        var selfServiceOperations = new[]
        {
            ("/api/v1/integration-credentials/host-proposals/start", "post", "MCP Deployment Approval", false),
            ("/api/v1/integration-credentials/host-proposals/poll", "post", "MCP Deployment Approval", false),
            ("/api/v1/integration-credentials/host-proposals/cancel", "post", "MCP Deployment Approval", false),
            ("/api/v1/integration-credentials/host-proposals/review", "post", "MCP Deployment Approval", true),
            ("/api/v1/integration-credentials/host-proposals/approve", "post", "MCP Deployment Approval", true),
            ("/api/v1/integration-credentials/host-proposals/deny", "post", "MCP Deployment Approval", true),
            ("/api/v1/integration-credentials/{credentialId}/record", "delete", "Integration Credentials", true),
            ("/api/v1/integration-credentials/{credentialId}/access", "get", "Integration Credentials", true),
            ("/api/v1/integration-credentials/{credentialId}/access/review", "post", "Integration Credentials", true),
            ("/api/v1/integration-credentials/{credentialId}/access/apply", "post", "Integration Credentials", true),
            ("/api/v1/integration-credentials/setup-options", "get", "Integration Credentials", true),
            ("/api/v1/integration-credentials/setup", "post", "Integration Credentials", true),
            ("/api/v1/integration-credentials/setup/{requestId}/acknowledge", "post", "Integration Credentials", true)
        };
        foreach (var (path, method, expectedTag, requiresSession) in selfServiceOperations)
        {
            Assert.True(paths.TryGetProperty(path, out var selfServicePath), $"Missing documented self-service path: {path}");
            Assert.True(selfServicePath.TryGetProperty(method, out var selfServiceOperation));
            Assert.Equal([expectedTag], selfServiceOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
            var expectedSecurity = path.EndsWith("/record", StringComparison.Ordinal)
                ? new[] { "JwtBearer", "LocalSession", "McpExecution" }
                : requiresSession ? ["JwtBearer", "LocalSession"] : [];
            Assert.Equal(expectedSecurity, SecuritySchemes(document, path, method));
        }
        var policyManagementOperations = new[]
        {
            ("/api/v1/admin/mcp-policies", "get"),
            ("/api/v1/admin/mcp-policies/directory", "get"),
            ("/api/v1/admin/mcp-policies/credentials", "get"),
            ("/api/v1/admin/mcp-policies/{id}/audit", "get"),
            ("/api/v1/admin/mcp-policies/preview", "post"),
            ("/api/v1/admin/mcp-policies/{id}/delete/preview", "post"),
            ("/api/v1/admin/mcp-policies/confirm", "post")
        };
        foreach (var (path, method) in policyManagementOperations)
        {
            Assert.True(paths.TryGetProperty(path, out var policyPath), $"Missing documented policy management path: {path}");
            Assert.True(policyPath.TryGetProperty(method, out var policyOperation));
            Assert.Equal(["MCP Policies"], policyOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
            Assert.Equal(["JwtBearer", "LocalSession"], SecuritySchemes(document, path, method));
        }
        var workspaceOperations = new[]
        {
            ("/api/v1/ticketing/assignees/search", "get", "Ticket Lookups"),
            ("/api/v1/ticketing/assignees/current", "get", "Ticket Lookups"),
            ("/api/v1/changes/{id}/assignee", "post", "Changes"),
            ("/api/v1/ticketing/filter-options", "get", "Ticket Lookups"),
            ("/api/v1/timeline/pending", "get", "Timeline"),
            ("/api/v1/timeline/pending/organizations", "get", "Timeline"),
            ("/api/v1/timeline/pending/actions", "post", "Timeline")
        };
        foreach (var (path, method, expectedTag) in workspaceOperations)
        {
            Assert.True(paths.TryGetProperty(path, out var workspacePath), $"Missing documented workspace path: {path}");
            Assert.True(workspacePath.TryGetProperty(method, out var workspaceOperation));
            Assert.Equal([expectedTag], workspaceOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
            Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, path, method));
        }
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
                ? new[] { "JwtBearer", "LocalSession", "McpExecution" } : new[] { "IntegrationCredential", "ServiceIdentity" }, SecuritySchemes(document, path, method));
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
            Assert.Equal(["JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, path, method));
        }
        Assert.False(paths.TryGetProperty("/api/v1/admin/orchestration/test-draft", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/test-draft", out var netclawDraft));
        Assert.True(netclawDraft.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/pair-and-save", out var pairNetclaw));
        Assert.True(pairNetclaw.TryGetProperty("post", out var pairNetclawOperation));
        Assert.Equal(["Netclaw"], pairNetclawOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"],
            SecuritySchemes(document, "/api/v1/admin/netclaw/pair-and-save", "post"));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/legacy-sessions/unbound", out var unboundLegacySessions));
        Assert.True(unboundLegacySessions.TryGetProperty("get", out var listUnboundOperation));
        Assert.Equal(["Netclaw"], listUnboundOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"],
            SecuritySchemes(document, "/api/v1/admin/netclaw/legacy-sessions/unbound", "get"));
        Assert.True(paths.TryGetProperty("/api/v1/admin/netclaw/legacy-sessions/confirm-owner", out var confirmLegacyOwner));
        Assert.True(confirmLegacyOwner.TryGetProperty("post", out var confirmLegacyOwnerOperation));
        Assert.Equal(["Netclaw"], confirmLegacyOwnerOperation.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).OfType<string>().ToArray());
        Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"],
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

    [Fact]
    public async Task OpenApi_V1_Json_documents_all_six_owner_business_additions_with_their_actual_contracts()
    {
        using var client = _factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var endpoints = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var additions = new (string Path, string Method, string Tag, string Identifier, string? OperationId, bool HasBody)[]
        {
            ("/api/v1/request-tasks/{taskId}/approval", "get", "Request Approvals", "taskId", null, false),
            ("/api/v1/request-tasks/{taskId}/approval/approve", "post", "Request Approvals", "taskId", null, false),
            ("/api/v1/request-tasks/{taskId}/approval/reject", "post", "Request Approvals", "taskId", null, true),
            ("/api/v1/changes/{changeId}/approval", "get", "Changes", "changeId", "GetAuthenticatedChangeApproval", false),
            ("/api/v1/changes/{changeId}/approval/review", "post", "Changes", "changeId", "ReviewAuthenticatedChangeApproval", false),
            ("/api/v1/resources/datasets/{datasetId}/rows", "post", "Resources", "datasetId", "UpsertDatasetRowsAsOperator", true)
        };

        foreach (var (path, method, tag, identifier, operationId, hasBody) in additions)
        {
            Assert.True(paths.TryGetProperty(path, out var route), $"Missing added OpenAPI route: {method} {path}");
            Assert.True(route.TryGetProperty(method, out var operation), $"Missing added OpenAPI method: {method} {path}");
            Assert.Equal(tag, Assert.Single(operation.GetProperty("tags").EnumerateArray()).GetString());
            if (operationId is not null)
                Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.Equal(["IntegrationCredential", "JwtBearer", "LocalSession", "McpExecution"], SecuritySchemes(document, path, method));

            var endpoint = Assert.Single(endpoints, candidate => candidate.RoutePattern.RawText?.TrimEnd('/') == path &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method.ToUpperInvariant()) == true);
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.NotEmpty(endpoint.Metadata.OfType<IAuthorizeData>());
            if (identifier == "datasetId")
                Assert.Contains(endpoint.Metadata.OfType<IAuthorizeData>(), data => data.Policy == "DataManagementAccess");

            // Approval identity comes from the authenticated caller. A body/query approver
            // override would change that contract, so assert the complete parameter set.
            var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray());
            Assert.Equal(identifier, parameter.GetProperty("name").GetString());
            Assert.Equal("path", parameter.GetProperty("in").GetString());
            Assert.True(parameter.GetProperty("required").GetBoolean());
            Assert.Equal("string", ResolveSchema(document, parameter.GetProperty("schema")).GetProperty("type").GetString());
            Assert.Equal(hasBody, operation.TryGetProperty("requestBody", out var body));
            if (hasBody)
            {
                Assert.True(body.GetProperty("required").GetBoolean());
                Assert.Equal("application/json", Assert.Single(body.GetProperty("content").EnumerateObject()).Name);
            }
        }

        var rejection = ResolveSchema(document, paths.GetProperty("/api/v1/request-tasks/{taskId}/approval/reject")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var reason = Assert.Single(rejection.GetProperty("properties").EnumerateObject());
        Assert.Equal("reason", reason.Name);
        Assert.Equal("string", reason.Value.GetProperty("type").GetString());

        var upsert = ResolveSchema(document, paths.GetProperty("/api/v1/resources/datasets/{datasetId}/rows")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var upsertFields = upsert.GetProperty("properties");
        Assert.Equal(["deleteMissing", "rows"], upsertFields.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("boolean", upsertFields.GetProperty("deleteMissing").GetProperty("type").GetString());
        Assert.Equal("array", upsertFields.GetProperty("rows").GetProperty("type").GetString());
        var row = ResolveSchema(document, upsertFields.GetProperty("rows").GetProperty("items"));
        var rowFields = row.GetProperty("properties");
        Assert.Equal(["data", "externalKey", "sourceUpdatedAtUtc"], rowFields.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("string", rowFields.GetProperty("externalKey").GetProperty("type").GetString());
        Assert.Contains(row.GetProperty("required").EnumerateArray(), field => field.GetString() == "externalKey");
        Assert.Equal("object", ResolveSchema(document, rowFields.GetProperty("data")).GetProperty("type").GetString());
        Assert.Equal("date-time", rowFields.GetProperty("sourceUpdatedAtUtc").GetProperty("format").GetString());

        foreach (var (path, method) in new[]
        {
            ("/api/v1/request-approvals/public/{trackingId}/{taskId}", "get"),
            ("/api/v1/request-approvals/public/{trackingId}/{taskId}/approve", "post"),
            ("/api/v1/request-approvals/public/{trackingId}/{taskId}/reject", "post"),
            ("/api/v1/changes/public/approval", "get"),
            ("/api/v1/changes/public/approval/review", "post")
        })
        {
            Assert.Empty(SecuritySchemes(document, path, method));
            var endpoint = Assert.Single(endpoints, candidate => candidate.RoutePattern.RawText?.TrimEnd('/') == path &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method.ToUpperInvariant()) == true);
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        }
    }

    private static JsonElement ResolveSchema(JsonDocument document, JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            const string prefix = "#/components/schemas/";
            var value = reference.GetString()!;
            Assert.StartsWith(prefix, value, StringComparison.Ordinal);
            schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(value[prefix.Length..]);
        }
        return schema;
    }

    private static string[] SecuritySchemes(JsonDocument document, string path, string method)
    {
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty(path, out var pathItem),
            $"OpenAPI path '{path}' was not generated. Available paths: {string.Join(", ", paths.EnumerateObject().Select(item => item.Name))}");
        Assert.True(pathItem.TryGetProperty(method, out var operation),
            $"OpenAPI operation '{method}' was not generated for path '{path}'.");
        if (!operation.TryGetProperty("security", out var security))
            return [];
        // Every listed mechanism is an alternative, not an accidental AND requirement.
        var schemes = security.EnumerateArray()
            .Select(requirement => Assert.Single(requirement.EnumerateObject()).Name)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(schemes.Length, schemes.Distinct(StringComparer.Ordinal).Count());
        return schemes;
    }

}
