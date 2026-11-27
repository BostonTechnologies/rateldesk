using System.Text.Json;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed partial class IntegrationCredentialSetupProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_repairs_original_full_access_and_retains_custom_policy_secret_expiry_and_scope(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var clock = new Clock();
        var request = Request(clock);
        // The historical preset predates queue transfer. Its permission fingerprint is frozen.
        var permissions = HelpdeskPermissions.AssignablePermissions.Where(permission => permission != HelpdeskPermissions.TicketQueueTransfer)
            .Order(StringComparer.Ordinal).ToArray();
        // Seed the catalog the old release wrote, including its HTTP-only boundary.
        using var historical = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(TestEnvironment.RepositoryRoot, "tools", "ci", "beta10-operation-ids.json")));
        var historicalKeys = historical.RootElement.GetProperty("operationIds").EnumerateArray()
            .Select(id => id.GetString()).Append("helpdesk_email_settings.delete_rule").ToHashSet(StringComparer.Ordinal);
        var originalOperations = McpPolicyDefinitionService.Operations.Select(x => x.Key)
            .Where(historicalKeys.Contains).ToArray();
        Assert.Equal("E0F019CC1038A79E959248906ADD61779D8E68133D8912525DD65EC1117D1DED",
            McpPolicyDefinitionService.CatalogFingerprint(originalOperations));
        var original = request.Policies.Single() with { Name = "Connection · All access", Permissions = permissions,
            Operations = originalOperations };
        request = request with { Credential = request.Credential with { Permissions = permissions }, Policies = [original] };
        await using var identity = fixture.Identity();
        await using var domain = fixture.Open();
        var setup = Service(identity, domain, fixture, clock);
        var delivery = await setup.CreateAsync(request, Principal(), default);
        await setup.AcknowledgeAsync(request.RequestId, Principal(), default);
        var customRequest = request with { RequestId = Guid.NewGuid(), Policies = [original with
        { Operations = ["helpdesk_incidents.list"] }] };
        var custom = await setup.CreateAsync(customRequest, Principal(), default);
        await setup.AcknowledgeAsync(customRequest.RequestId, Principal(), default);
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleAsync(c => c.Id == delivery.Credential.Id);
        var hash = credential.SecretHash;
        identity.ChangeTracker.Clear(); domain.ChangeTracker.Clear();
        var repair = new McpAllAccessReconciliation(domain, identity, clock);
        await repair.ReconcileAsync();
        var row = await domain.Set<McpAccessPolicy>().AsNoTracking().SingleAsync(p => p.CredentialId == credential.Id);
        Assert.Equal(2, row.Revision);
        var repaired = McpPolicyDefinitionDocumentReader.Read(row.DefinitionJson)!.Scopes.Single();
        Assert.True(repaired.AllAccess);
        Assert.Contains("helpdesk_tickets.relations", repaired.Operations);
        var current = await identity.IntegrationCredentials.AsNoTracking().SingleAsync(c => c.Id == credential.Id);
        Assert.Equal(hash, current.SecretHash); Assert.Equal(credential.ExpiresAtUtc, current.ExpiresAtUtc);
        Assert.Equal(credential.OrganizationId, current.OrganizationId); Assert.Equal(credential.McpResourceUri, current.McpResourceUri);
        Assert.Equal(1, (await domain.Set<McpAccessPolicy>().AsNoTracking().SingleAsync(p => p.CredentialId == custom.Credential.Id)).Revision);
        var auditCount = await domain.Set<McpAccessPolicyAudit>().CountAsync();
        await repair.ReconcileAsync();
        Assert.Equal(auditCount, await domain.Set<McpAccessPolicyAudit>().CountAsync());
        Assert.True(McpPolicyDefinitionService.AllowsRequest(row, current, "GET", "/api/v1/incidents", "/api/v1/incidents", clock.GetUtcNow().UtcDateTime));
        identity.ChangeTracker.Clear();
        current.Permissions = HelpdeskPermissions.IncidentRead;
        identity.IntegrationCredentials.Update(current); await identity.SaveChangesAsync();
        await repair.ReconcileAsync();
        Assert.Equal(HelpdeskPermissions.IncidentRead, (await identity.IntegrationCredentials.AsNoTracking().SingleAsync(c => c.Id == credential.Id)).Permissions);
        row = await domain.Set<McpAccessPolicy>().AsNoTracking().SingleAsync(p => p.CredentialId == credential.Id);
        Assert.Null(McpPolicyDefinitionDocumentReader.Read(row.DefinitionJson)!.Scopes.Single().PermissionCatalogSnapshot);
    }
}
