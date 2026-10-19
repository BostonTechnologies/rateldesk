using System.Net;
using System.Net.Http.Headers;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Shared.Auth;
using Helpdesk.Tests.Api;
using ModelContextProtocol.Client;

namespace Helpdesk.Tests.Mcp;

public sealed partial class McpHttpHostTests
{
    [Fact]
    public async Task Real_owner_http_mcp_host_executes_admin_writes_preserves_ordinary_denials_and_revalidates_empty_scope()
    {
        await using var authority = await McpGatewayRealAccessPipelineTests.GatewayAccessHarness.CreateAsync(unscopedData: true);
        await authority.SetOwnerAdministratorAsync();
        var resource = McpGatewayRealAccessPipelineTests.GatewayAccessHarness.ResourceUri;
        var owner = await authority.IssueCredentialAsync(new IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest(
            "Synthetic owner SDK client", "mcp", "org-a", [HelpdeskPermissions.HelpdeskAdmin], 1)
        {
            McpResourceUri = resource, GrantKind = IntegrationCredentialGrantKinds.OwnerAdministration,
            OwnerAdministrationConsent = true
        });
        var ordinary = await authority.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        using var environment = new McpHostEnvironment(apiBaseUrl: "https://api.example", publicResourceUri: resource, gateway: true);
        using var audit = new AuditLogCollector();
        using var authorityGate = new SemaphoreSlim(1, 1);
        HttpMessageHandler Handler() => new SerializedAuthorityHandler(authority.CreateHandler(), authorityGate);
        using var factory = CreateFactory(environment.SigningKey, audit, apiHandlerFactory: Handler, delegationHandlerFactory: Handler);
        using var http = CreateClient(factory, resource);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Bearer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        { Endpoint = new Uri(resource) }, http), cancellationToken: deadline.Token);
        var catalog = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Contains(catalog, tool => tool.Name == "helpdesk_admin_mutations");
        var status = await mcp.CallToolAsync("helpdesk_auth", new Dictionary<string, object?> { ["operation"] = "status" }, cancellationToken: deadline.Token);
        var identity = status.StructuredContent!.Value.GetProperty("data");
        Assert.True(identity.GetProperty("isHelpdeskAdmin").GetBoolean());
        Assert.Equal("owner-administration", identity.GetProperty("grantKind").GetString());
        Assert.Equal(["org-a", "org-b"], identity.GetProperty("allowedOrganizationIds").EnumerateArray().Select(value => value.GetString()));
        var administration = await mcp.CallToolAsync("helpdesk_connectivity", new Dictionary<string, object?> { ["operation"] = "orchestration" }, cancellationToken: deadline.Token);
        Assert.True(administration.StructuredContent!.Value.GetProperty("success").GetBoolean());

        var createArguments = new Dictionary<string, object?>
        {
            ["operation"] = "create_role", ["confirm"] = false,
            ["request"] = new { name = "Owner SDK role", ownerOrganizationId = "org-b", permissions = new[] { HelpdeskPermissions.IncidentRead } }
        };
        var preview = await mcp.CallToolAsync("helpdesk_admin_mutations", createArguments, cancellationToken: deadline.Token);
        Assert.Equal("confirmation_required", preview.StructuredContent!.Value.GetProperty("status").GetString());
        createArguments["confirm"] = true;
        var created = await mcp.CallToolAsync("helpdesk_admin_mutations", createArguments, cancellationToken: deadline.Token);
        Assert.True(created.StructuredContent!.Value.GetProperty("success").GetBoolean());
        var roleId = created.StructuredContent.Value.GetProperty("data").GetProperty("id").GetString()!;
        var readback = await mcp.CallToolAsync("helpdesk_roles", new Dictionary<string, object?>
        { ["operation"] = "get", ["request"] = new { roleId } }, cancellationToken: deadline.Token);
        Assert.Equal("org-b", readback.StructuredContent!.Value.GetProperty("data").GetProperty("ownerOrganizationId").GetString());
        var deleted = await mcp.CallToolAsync("helpdesk_admin_mutations", new Dictionary<string, object?>
        { ["operation"] = "delete_role", ["confirm"] = true, ["request"] = new { roleId } }, cancellationToken: deadline.Token);
        Assert.True(deleted.StructuredContent!.Value.GetProperty("success").GetBoolean());

        using var ordinaryStatus = await HttpCallAsync(http, ordinary.Bearer, 61, "tools/call", ToolCallParameters("helpdesk_auth", "status"));
        Assert.False(StructuredContent(ordinaryStatus.RootElement).GetProperty("data").GetProperty("isHelpdeskAdmin").GetBoolean());
        using var denied = await HttpCallAsync(http, ordinary.Bearer, 62, "tools/call", ToolCallParameters("helpdesk_connectivity", "orchestration"));
        Assert.Equal(403, StructuredContent(denied.RootElement).GetProperty("failure").GetProperty("upstreamStatus").GetInt32());

        await authority.SetOrganizationEnabledAsync("org-a", false);
        await authority.SetOrganizationEnabledAsync("org-b", false);
        var noOrganizations = await mcp.CallToolAsync("helpdesk_auth", new Dictionary<string, object?> { ["operation"] = "access" }, cancellationToken: deadline.Token);
        var emptyAccess = noOrganizations.StructuredContent!.Value.GetProperty("data");
        Assert.True(emptyAccess.GetProperty("isHelpdeskAdmin").GetBoolean());
        Assert.Empty(emptyAccess.GetProperty("allowedOrganizationIds").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, emptyAccess.GetProperty("primaryOrganizationId").ValueKind);
        var stillAuthorized = await mcp.CallToolAsync("helpdesk_connectivity", new Dictionary<string, object?> { ["operation"] = "orchestration" }, cancellationToken: deadline.Token);
        Assert.True(stillAuthorized.StructuredContent!.Value.GetProperty("success").GetBoolean());

        await authority.SetOwnerAdministratorAsync(false);
        using var expiredAuthorityRequest = CreateGatewayRequest();
        expiredAuthorityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Bearer);
        using var expiredAuthority = await http.SendAsync(expiredAuthorityRequest, deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredAuthority.StatusCode);
        await authority.SetOwnerAdministratorAsync();
        var revoked = await mcp.CallToolAsync("helpdesk_auth", new Dictionary<string, object?>
        { ["operation"] = "revoke_self", ["confirm"] = true }, cancellationToken: deadline.Token);
        Assert.True(revoked.StructuredContent!.Value.GetProperty("success").GetBoolean());
        using var revokedRequest = CreateGatewayRequest();
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Bearer);
        using var revokedResponse = await http.SendAsync(revokedRequest, deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        Assert.All(audit.Messages, message =>
        {
            Assert.DoesNotContain(owner.Bearer, message, StringComparison.Ordinal);
            Assert.DoesNotContain(ordinary.Bearer, message, StringComparison.Ordinal);
            Assert.DoesNotContain("Owner SDK role", message, StringComparison.Ordinal);
        });
    }
}
