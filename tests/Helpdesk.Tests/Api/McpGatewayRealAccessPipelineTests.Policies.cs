using System.Net;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class McpGatewayRealAccessPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_policy_operation_rules_apply_to_real_execution_requests_and_existing_tokens(bool postgres)
    {
        await using var harness = postgres ? await GatewayAccessHarness.CreatePostgreSqlAsync() : await GatewayAccessHarness.CreateAsync();
        var credential = await harness.CreateCredentialAsync("org-a");
        var execution = await harness.DelegateAsync(credential);
        await harness.SetStructuredMcpPolicyAsync(credential.Id, "Active", false,
            ["helpdesk_auth.access", "helpdesk_self_service.requests", "helpdesk_self_service.get_request"],
            ["helpdesk_self_service.get_request"]);
        using var allowed = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var denied = await harness.GetAsync(execution, "/api/v1/self-service/requests/request-a");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("mcp_policy_denied", await denied.Content.ReadAsStringAsync());
        using var unrelated = await harness.GetAsync(execution, "/api/v1/requests/");
        Assert.Equal(HttpStatusCode.Forbidden, unrelated.StatusCode);
        await harness.SetStructuredMcpPolicyAsync(credential.Id, "Deleted", false,
            ["helpdesk_auth.access", "helpdesk_self_service.requests"], []);
        using var afterDelete = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Forbidden, afterDelete.StatusCode);
        await harness.AssertDelegationStatusAsync(credential, GatewayAccessHarness.ResourceUri, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_policy_preserves_explicit_owner_admin_only_for_allowed_operations(bool postgres)
    {
        await using var harness = postgres ? await GatewayAccessHarness.CreatePostgreSqlAsync() : await GatewayAccessHarness.CreateAsync();
        await harness.SetOwnerAdministratorAsync();
        var credential = await harness.CreateCredentialAsync("org-a", IntegrationCredentialGrantKinds.OwnerAdministrationPurpose,
            HelpdeskPermissions.HelpdeskAdmin);
        await harness.SetStructuredMcpPolicyAsync(credential.Id, "Active", true, ["helpdesk_auth.status"], []);
        var execution = await harness.DelegateAsync(credential);
        var status = await harness.GetAsync<CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True(status.Body!.IsHelpdeskAdmin);
        using var denied = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await harness.SetOwnerAdministratorAsync(false);
        using var demoted = await harness.GetAsync(execution, "/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, demoted.StatusCode);
    }

    internal sealed partial class GatewayAccessHarness
    {
        public async Task SetStructuredMcpPolicyAsync(Guid credentialId, string state, bool server, string[] operations, string[] denied)
        {
            await using var services = application.Services.CreateAsyncScope();
            var identity = services.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var credential = await identity.IntegrationCredentials.AsNoTracking().SingleAsync(x => x.Id == credentialId);
            var db = services.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var row = await db.Set<McpAccessPolicy>().SingleOrDefaultAsync(x => x.CredentialId == credentialId);
            if (row is null) { row = new() { CredentialId = credentialId }; db.Add(row); }
            var expiry = DateTime.UtcNow.AddHours(1);
            var policy = new McpPolicyScopeDraft("Synthetic base", server ? "server" : "organization", server ? null : credential.OrganizationId,
                "Allow", credential.Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries), operations, expiry,
                denied.Length == 0 ? [] : [new("Synthetic exception", "Deny", denied, expiry)]);
            Assert.Null(McpPolicyDefinitionService.ValidateScopes(credential, [policy], DateTime.UtcNow));
            McpPolicyDefinitionService.Apply(row, credential, [policy], state);
            row.Revision++;
            await db.SaveChangesAsync();
        }
    }
}
