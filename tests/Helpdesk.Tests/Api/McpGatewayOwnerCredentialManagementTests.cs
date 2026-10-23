using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Helpdesk.Shared.Auth;

namespace Helpdesk.Tests.Api;

public sealed partial class McpGatewayRealAccessPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_can_manage_only_owned_credential_metadata_while_issuance_remains_interactive(bool postgreSql)
    {
        await using var harness = postgreSql ? await GatewayAccessHarness.CreatePostgreSqlAsync() : await GatewayAccessHarness.CreateAsync();
        await harness.SetOwnerAdministratorAsync();
        var owner = await harness.IssueCredentialAsync(OwnerGrantRequest());
        var execution = await harness.DelegateAsync(owner);
        var target = await harness.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var otherOwner = await harness.CreateCredentialAsync("org-a", purpose: "api", ownerUserId: "other-owner");
        using var list = await harness.GetAsync(execution, "/api/v1/integration-credentials/");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True(list.Headers.CacheControl?.NoStore);
        var metadata = await list.Content.ReadFromJsonAsync<JsonArray>();
        Assert.Contains(metadata!, record => record!["id"]!.GetValue<Guid>() == target.Id);
        Assert.DoesNotContain(metadata!, record => record!["id"]!.GetValue<Guid>() == otherOwner.Id);
        Assert.DoesNotContain(owner.Bearer, metadata!.ToJsonString());
        Assert.DoesNotContain(target.Bearer, metadata.ToJsonString());

        using var forbiddenIssuance = await harness.SendAsync(execution, HttpMethod.Post, "/api/v1/integration-credentials/", OwnerGrantRequest());
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenIssuance.StatusCode);
        using var deniedForeign = await harness.SendAsync(execution, HttpMethod.Delete, $"/api/v1/integration-credentials/{otherOwner.Id:D}");
        Assert.Equal(HttpStatusCode.NotFound, deniedForeign.StatusCode);
        using var deniedForeignDelete = await harness.SendAsync(execution, HttpMethod.Delete, $"/api/v1/integration-credentials/{otherOwner.Id:D}/record");
        Assert.Equal(HttpStatusCode.NotFound, deniedForeignDelete.StatusCode);

        var targetExecution = await harness.DelegateAsync(target);
        using var ordinaryDenied = await harness.GetAsync(targetExecution, "/api/v1/integration-credentials/");
        Assert.Equal(HttpStatusCode.Forbidden, ordinaryDenied.StatusCode);
        using var revoked = await harness.SendAsync(execution, HttpMethod.Delete, $"/api/v1/integration-credentials/{target.Id:D}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        await harness.AssertDelegationStatusAsync(target, GatewayAccessHarness.ResourceUri, HttpStatusCode.Unauthorized);
        using var deleted = await harness.SendAsync(execution, HttpMethod.Delete, $"/api/v1/integration-credentials/{target.Id:D}/record");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var after = await harness.GetAsync<JsonArray>(execution, "/api/v1/integration-credentials/");
        Assert.DoesNotContain(after.Body!, record => record!["id"]!.GetValue<Guid>() == target.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_can_register_and_revise_incident_sources_while_ordinary_grants_stay_denied(bool postgreSql)
    {
        await using var harness = postgreSql ? await GatewayAccessHarness.CreatePostgreSqlAsync() : await GatewayAccessHarness.CreateAsync();
        await harness.SetOwnerAdministratorAsync();
        var owner = await harness.IssueCredentialAsync(OwnerGrantRequest());
        var execution = await harness.DelegateAsync(owner);
        var sourceCredential = await harness.CreateCredentialAsync("org-a", purpose: "api", permissions: HelpdeskPermissions.IncidentWrite);
        var ordinary = await harness.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var ordinaryExecution = await harness.DelegateAsync(ordinary);
        using var denied = await harness.GetAsync(ordinaryExecution, "/api/v1/integrations/netratel/sources/");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var registered = await harness.SendAsync(execution, HttpMethod.Post, "/api/v1/integrations/netratel/sources/", new
        {
            sourceInstanceId = Guid.NewGuid(), organizationId = "org-a", customerId = "customer-a", credentialId = sourceCredential.Id
        });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var source = await registered.Content.ReadFromJsonAsync<JsonObject>();
        var id = source!["sourceNamespaceId"]!.GetValue<Guid>();
        var revision = source["revision"]!.GetValue<long>();
        using var changed = await harness.SendAsync(execution, HttpMethod.Patch, $"/api/v1/integrations/netratel/sources/{id:D}", new { expectedRevision = revision, isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var stale = await harness.SendAsync(execution, HttpMethod.Patch, $"/api/v1/integrations/netratel/sources/{id:D}", new { expectedRevision = revision, isEnabled = true });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var binding = await harness.SendAsync(execution, HttpMethod.Put, $"/api/v1/integrations/netratel/sources/{id:D}/principal-bindings/{sourceCredential.Id:D}", new { expectedRevision = revision + 1, isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, binding.StatusCode);
        var sources = await harness.GetAsync<JsonArray>(execution, "/api/v1/integrations/netratel/sources/");
        var saved = Assert.Single(sources.Body!);
        Assert.Equal(id, saved!["sourceNamespaceId"]!.GetValue<Guid>());
        Assert.False(saved["isEnabled"]!.GetValue<bool>());
        Assert.Equal(revision + 2, saved["revision"]!.GetValue<long>());
        Assert.Equal("owner", saved["updatedBy"]!.GetValue<string>());
    }
}
