using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.DTOs.Incident;

namespace Helpdesk.Tests.Api;

public sealed partial class McpGatewayRealAccessPipelineTests
{
    private static IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest OwnerGrantRequest() =>
        new("Synthetic owner agent", "mcp", "org-a", [HelpdeskPermissions.HelpdeskAdmin], 1)
        {
            McpResourceUri = GatewayAccessHarness.ResourceUri,
            GrantKind = IntegrationCredentialGrantKinds.OwnerAdministration,
            OwnerAdministrationConsent = true
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_global_administration_survives_disabled_default_and_no_enabled_organizations(bool postgreSql)
    {
        await using var harness = postgreSql ? await GatewayAccessHarness.CreatePostgreSqlAsync(unscopedData: true) : await GatewayAccessHarness.CreateAsync(unscopedData: true);
        await harness.SetOwnerAdministratorAsync();
        var credential = await harness.IssueCredentialAsync(OwnerGrantRequest());
        var ordinary = await harness.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var execution = await harness.DelegateAsync(credential);
        await harness.SetOrganizationEnabledAsync("org-a", false);
        await harness.SetOrganizationEnabledAsync("org-b", false);
        var access = await harness.GetAsync<CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.True(access.Body!.IsHelpdeskAdmin);
        Assert.Equal(IntegrationCredentialGrantKinds.OwnerAdministration, access.Body.GrantKind);
        Assert.Null(access.Body.PrimaryOrganizationId);
        Assert.Empty(access.Body.AllowedOrganizationIds);
        await harness.DelegateAsync(credential);
        await harness.AssertDelegationStatusAsync(ordinary, GatewayAccessHarness.ResourceUri, HttpStatusCode.Forbidden);
        using var settings = await harness.GetAsync(execution, "/api/v1/admin/orchestration/");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        await harness.SetOrganizationEnabledAsync("org-b", true);
        access = await harness.GetAsync<CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.Null(access.Body!.PrimaryOrganizationId);
        Assert.Equal(["org-b"], access.Body.AllowedOrganizationIds);
        using var created = await harness.SendAsync(execution, HttpMethod.Post, "/api/v1/admin/role-definitions/", new
        {
            Name = "Role after default organization was disabled", OwnerOrganizationId = "org-b",
            Permissions = new[] { HelpdeskPermissions.IncidentRead }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_owner_grant_uses_real_issuer_and_gateway_to_administer_multiple_organizations(bool postgreSql)
    {
        await using var harness = postgreSql
            ? await GatewayAccessHarness.CreatePostgreSqlAsync(unscopedData: true)
            : await GatewayAccessHarness.CreateAsync(unscopedData: true);
        await harness.SetOwnerAdministratorAsync();
        await harness.SeedIncidentSearchAsync();
        var credential = await harness.IssueCredentialAsync(OwnerGrantRequest());
        var execution = await harness.DelegateAsync(credential);
        var access = await harness.GetAsync<CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.True(access.Body!.IsHelpdeskAdmin);
        Assert.Contains(HelpdeskPermissions.HelpdeskAdmin, access.Body.Permissions);
        Assert.Equal(["org-a", "org-b"], access.Body.AllowedOrganizationIds);
        using var administration = await harness.GetAsync(execution, "/api/v1/admin/orchestration/");
        Assert.Equal(HttpStatusCode.OK, administration.StatusCode);
        var administrationOwner = await harness.GetAsync<JsonObject>(execution, "/test/administration-owner");
        Assert.Equal(HttpStatusCode.OK, administrationOwner.StatusCode);
        Assert.Equal("owner", administrationOwner.Body!["userId"]!.GetValue<string>());
        using var credentialAuthority = await harness.GetAsync(execution, "/test/credential-management");
        Assert.Equal(HttpStatusCode.Forbidden, credentialAuthority.StatusCode);
        var incidents = await harness.GetAsync<PagedResponse<IncidentDto>>(execution, "/api/v1/incidents/?includeTotal=true");
        Assert.Contains(incidents.Body!.Items, incident => incident.Id == "incident-alpha");
        Assert.Contains(incidents.Body.Items, incident => incident.Id == "incident-foreign");

        using var created = await harness.SendAsync(execution, HttpMethod.Post, "/api/v1/admin/role-definitions/", new
        {
            Name = "Owner-created role in second organization", OwnerOrganizationId = "org-b",
            Permissions = new[] { HelpdeskPermissions.IncidentRead }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var role = await created.Content.ReadFromJsonAsync<JsonObject>();
        var roleId = role!["id"]!.GetValue<string>();
        var fetched = await harness.GetAsync<JsonObject>(execution, $"/api/v1/admin/role-definitions/{roleId}");
        Assert.Equal("org-b", fetched.Body!["ownerOrganizationId"]!.GetValue<string>());
        using var deleted = await harness.SendAsync(execution, HttpMethod.Delete, $"/api/v1/admin/role-definitions/{roleId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The same owner's ordinary credential keeps its original tenant ceiling.
        var ordinary = await harness.IssueCredentialAsync(OwnerGrantRequest() with
        {
            GrantKind = IntegrationCredentialGrantKinds.Ordinary, OwnerAdministrationConsent = false,
            Permissions = [HelpdeskPermissions.IncidentRead]
        });
        var ordinaryExecution = await harness.DelegateAsync(ordinary);
        var ordinaryAccess = await harness.GetAsync<CurrentUserAccessDto>(ordinaryExecution, "/api/v1/auth/me");
        Assert.False(ordinaryAccess.Body!.IsHelpdeskAdmin);
        Assert.Equal(IntegrationCredentialGrantKinds.Ordinary, ordinaryAccess.Body.GrantKind);
        Assert.Equal(["org-a"], ordinaryAccess.Body.AllowedOrganizationIds);
        using var denied = await harness.GetAsync(ordinaryExecution, "/api/v1/admin/orchestration/");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var deniedConnection = await harness.GetAsync(ordinaryExecution, "/test/administration-owner");
        Assert.Equal(HttpStatusCode.Forbidden, deniedConnection.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_grant_issuer_requires_current_admin_mcp_purpose_exact_grant_and_explicit_consent(bool postgreSql)
    {
        await using var harness = postgreSql ? await GatewayAccessHarness.CreatePostgreSqlAsync() : await GatewayAccessHarness.CreateAsync();
        var unauthorized = await Assert.ThrowsAsync<CredentialGrantException>(() => harness.IssueCredentialAsync(OwnerGrantRequest()));
        Assert.Null(unauthorized.Errors);
        await harness.SetOwnerAdministratorAsync();
        foreach (var invalid in new[]
        {
            OwnerGrantRequest() with { OwnerAdministrationConsent = false },
            OwnerGrantRequest() with { Purpose = "api", McpResourceUri = null },
            OwnerGrantRequest() with { Permissions = [HelpdeskPermissions.IncidentRead] },
            OwnerGrantRequest() with { GrantKind = IntegrationCredentialGrantKinds.Ordinary },
            OwnerGrantRequest() with { McpResourceUri = "http://helpdesk.example/mcp" }
        })
            Assert.NotNull((await Assert.ThrowsAsync<CredentialGrantException>(() => harness.IssueCredentialAsync(invalid))).Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_grant_cached_execution_rechecks_admin_account_expiry_revocation_and_gateway_resource(bool postgreSql)
    {
        await using var harness = postgreSql ? await GatewayAccessHarness.CreatePostgreSqlAsync() : await GatewayAccessHarness.CreateAsync();
        await harness.SetOwnerAdministratorAsync();
        var credential = await harness.IssueCredentialAsync(OwnerGrantRequest());
        await harness.AssertDelegationStatusAsync(credential, "https://another.example/mcp", HttpStatusCode.Forbidden);
        var execution = await harness.DelegateAsync(credential);
        using var opaqueAtApi = await harness.GetAsync(credential.Bearer, "/api/v1/admin/orchestration/");
        Assert.Equal(HttpStatusCode.Unauthorized, opaqueAtApi.StatusCode);
        await harness.SetOwnerAdministratorAsync(false);
        await AssertUnauthenticatedAsync();
        await harness.SetOwnerAdministratorAsync();
        await harness.SetOwnerEnabledAsync(false);
        await AssertUnauthenticatedAsync();
        await harness.SetOwnerEnabledAsync(true);
        await harness.SetCredentialExpiredAsync(credential.Id);
        await AssertUnauthenticatedAsync();

        credential = await harness.IssueCredentialAsync(OwnerGrantRequest());
        execution = await harness.DelegateAsync(credential);
        using var revokeSelf = await harness.SendAsync(execution, HttpMethod.Post, "/api/v1/integration-credentials/self/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revokeSelf.StatusCode);
        await AssertUnauthenticatedAsync();
        var apiCredential = await harness.CreateCredentialAsync("org-a", purpose: "api");
        await harness.AssertDelegationStatusAsync(apiCredential, GatewayAccessHarness.ResourceUri, HttpStatusCode.Unauthorized);

        async Task AssertUnauthenticatedAsync()
        {
            using var denied = await harness.GetAsync(execution, "/api/v1/admin/orchestration/");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            await harness.AssertDelegationStatusAsync(credential, GatewayAccessHarness.ResourceUri, HttpStatusCode.Unauthorized);
        }
    }
}
