using System.Net;
using System.Net.Http.Json;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

public sealed partial class AiOpsAndPolicyEndpointsTests
{
    [Theory]
    [InlineData("integration")]
    [InlineData("mcp")]
    [InlineData("gateway")]
    [InlineData("service")]
    [InlineData("machine")]
    [InlineData("service-purpose")]
    [InlineData("execution-purpose")]
    public async Task Machine_principals_cannot_read_or_rewrite_policy_administration_even_with_an_admin_role(string actor)
    {
        await using var h = await Harness.Create(false);
        h.Client.DefaultRequestHeaders.Authorization = new("Test", actor);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/admin/mcp-policies/directory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview",
            new McpPolicyDraft(h.Credential, "a", [HelpdeskPermissions.IncidentRead], DateTime.UtcNow.AddHours(1), 0, "Active"))).StatusCode);
        await h.WithDb(async db => Assert.Empty(await db.Set<McpAccessPolicy>().ToListAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editing_a_disabled_policy_retains_its_state_until_an_explicit_enable_review(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var expiry = new DateTime(DateTime.UtcNow.AddHours(1).Ticks / 10 * 10, DateTimeKind.Utc);
        var scope = new McpPolicyScopeDraft("Synthetic reader", "organization", "a", "Allow",
            [HelpdeskPermissions.IncidentRead], ["helpdesk_incidents.get", "helpdesk_incidents.list"], expiry, []);
        var draft = new McpPolicyDraft(h.Credential, "a", scope.Permissions, expiry, 0, "Active") { Scopes = [scope] };
        async Task<McpPolicyDto> Apply(McpPolicyDraft change)
        {
            var preview = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", change);
            preview.EnsureSuccessStatusCode();
            var confirmation = new ConfirmMcpPolicy((await preview.Content.ReadFromJsonAsync<McpPolicyPreview>())!.Confirmation);
            var confirmed = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", confirmation);
            confirmed.EnsureSuccessStatusCode();
            (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", confirmation)).EnsureSuccessStatusCode();
            return (await confirmed.Content.ReadFromJsonAsync<McpPolicyDto>())!;
        }
        var active = await Apply(draft);
        var disabled = await Apply(draft with { ExpectedRevision = active.Revision, State = "Disabled" });
        var narrower = scope with { Name = "Synthetic list only", Operations = ["helpdesk_incidents.list"] };
        var editedDraft = draft with { ExpectedRevision = disabled.Revision, State = "Disabled", Scopes = [narrower] };
        var edited = await Apply(editedDraft);
        Assert.Equal("Disabled", edited.State);
        Assert.Equal("helpdesk_incidents.list", Assert.Single(Assert.Single(edited.Scopes).Operations));
        await h.WithDb(async db =>
        {
            var saved = await db.Set<McpAccessPolicy>().SingleAsync();
            Assert.Equal("Disabled", saved.State);
            Assert.False(McpPolicyDefinitionService.AllowsRequest(saved,
                new() { McpResourceUri = "https://helpdesk.example.test/mcp" }, "GET", "/api/v1/incidents/", "/api/v1/incidents/", DateTime.UtcNow));
            Assert.Equal(3, await db.Set<McpAccessPolicyAudit>().CountAsync());
        });
        var enabled = await Apply(editedDraft with { ExpectedRevision = edited.Revision, State = "Active" });
        Assert.Equal("Active", enabled.State);
        Assert.Equal(4, enabled.Revision);
        Assert.Equal(narrower.Name, Assert.Single(enabled.Scopes).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_a_saved_policy_handles_expired_children_and_a_deleted_source_credential(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var expiry = new DateTime(DateTime.UtcNow.AddHours(1).Ticks / 10 * 10, DateTimeKind.Utc);
        var expiredChild = new DateTime(DateTime.UtcNow.AddHours(-1).Ticks / 10 * 10, DateTimeKind.Utc);
        var scope = new McpPolicyScopeDraft("Synthetic historical policy", "organization", "a", "Allow",
            [HelpdeskPermissions.IncidentRead], ["helpdesk_incidents.get"], expiry,
            [new("Expired exception", "Deny", ["helpdesk_incidents.get"], expiredChild)]);
        await h.WithDb(async db =>
        {
            var row = new McpAccessPolicy { CredentialId = h.Credential, Revision = 1 };
            McpPolicyDefinitionService.Apply(row, new() { McpResourceUri = "https://helpdesk.example.test/mcp" }, [scope], "Active");
            db.Add(row); await db.SaveChangesAsync();
        });
        await h.WithIdentity(async db =>
        {
            db.IntegrationCredentials.Remove(await db.IntegrationCredentials.SingleAsync(x => x.Id == h.Credential));
            await db.SaveChangesAsync();
        });
        var draft = new McpPolicyDraft(h.Credential, "a", scope.Permissions, expiry, 1, "Disabled") { Scopes = [scope] };
        foreach (var state in new[] { "Disabled", "Revoked" })
        {
            var preview = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", draft with { State = state });
            preview.EnsureSuccessStatusCode();
            var confirmed = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", new ConfirmMcpPolicy((await preview.Content.ReadFromJsonAsync<McpPolicyPreview>())!.Confirmation));
            confirmed.EnsureSuccessStatusCode();
            var saved = (await confirmed.Content.ReadFromJsonAsync<McpPolicyDto>())!;
            Assert.Equal(state, saved.State);
            Assert.Equal(expiredChild, Assert.Single(Assert.Single(saved.Scopes).Exceptions).ExpiresUtc);
            draft = draft with { ExpectedRevision = saved.Revision };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_policy_review_nested_edit_and_deletion_preserve_audit_and_deny_access(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        h.Client.DefaultRequestHeaders.Authorization = new("Test", "owner");
        var directory = await h.Client.GetFromJsonAsync<McpPolicyDirectory>("/api/v1/admin/mcp-policies/directory");
        var credential = Assert.Single(directory!.Credentials);
        Assert.Equal(h.Credential, credential.Id);
        Assert.Equal("owner", credential.OwnerName);
        Assert.Equal("A", credential.OrganizationName);
        Assert.Contains(directory.Operations, operation => operation.Key == "helpdesk_incidents.get");
        var expiry = new DateTime(DateTime.UtcNow.AddHours(1).Ticks / 10 * 10, DateTimeKind.Utc);
        var scope = new McpPolicyScopeDraft("Synthetic reader", "organization", "a", "Allow",
            [HelpdeskPermissions.IncidentRead], ["helpdesk_incidents.get", "helpdesk_raw.get_incident", "helpdesk_incidents.list"], expiry,
            [new("No individual details", "Deny", ["helpdesk_raw.get_incident"], expiry)]);
        var draft = new McpPolicyDraft(h.Credential, "a", scope.Permissions, expiry, 0, "Active") { Scopes = [scope] };
        var previewResponse = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", draft);
        previewResponse.EnsureSuccessStatusCode();
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpPolicyPreview>())!;
        h.Client.DefaultRequestHeaders.Authorization = new("Test", "admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", new ConfirmMcpPolicy(preview.Confirmation))).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new("Test", "owner");
        var confirmed = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", new ConfirmMcpPolicy(preview.Confirmation));
        confirmed.EnsureSuccessStatusCode();
        var saved = (await confirmed.Content.ReadFromJsonAsync<McpPolicyDto>())!;
        Assert.Single(Assert.Single(saved.Scopes).Exceptions);
        var savedDirectory = await h.Client.GetFromJsonAsync<McpPolicyDirectory>("/api/v1/admin/mcp-policies/directory");
        Assert.Equal(1, Assert.Single(savedDirectory!.Credentials).PolicyRevision);
        await h.WithDb(async db =>
        {
            var row = await db.Set<McpAccessPolicy>().SingleAsync();
            var source = new Helpdesk.Infrastructure.Identity.IntegrationCredential { Id = h.Credential, McpResourceUri = credential.Resource };
            Assert.True(McpPolicyDefinitionService.AllowsRequest(row, source, "GET", "/api/v1/incidents/", "/api/v1/incidents/", DateTime.UtcNow));
            Assert.False(McpPolicyDefinitionService.AllowsRequest(row, source, "GET", "/api/v1/incidents/example", "/api/v1/incidents/{id}", DateTime.UtcNow));
        });

        var deletePreview = await h.Client.PostAsJsonAsync($"/api/v1/admin/mcp-policies/{h.Credential}/delete/preview", new McpPolicyDeleteDraft(1));
        deletePreview.EnsureSuccessStatusCode();
        var staleDeletion = new ConfirmMcpPolicy((await deletePreview.Content.ReadFromJsonAsync<McpPolicyPreview>())!.Confirmation);
        var changed = draft with { ExpectedRevision = 1, Scopes = [scope with { Exceptions = [] }] };
        var changePreview = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", changed);
        changePreview.EnsureSuccessStatusCode();
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", new ConfirmMcpPolicy((await changePreview.Content.ReadFromJsonAsync<McpPolicyPreview>())!.Confirmation))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", staleDeletion)).StatusCode);
        var freshDelete = await h.Client.PostAsJsonAsync($"/api/v1/admin/mcp-policies/{h.Credential}/delete/preview", new McpPolicyDeleteDraft(2));
        freshDelete.EnsureSuccessStatusCode();
        var deletion = new ConfirmMcpPolicy((await freshDelete.Content.ReadFromJsonAsync<McpPolicyPreview>())!.Confirmation);
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", deletion)).EnsureSuccessStatusCode();
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", deletion)).EnsureSuccessStatusCode();
        Assert.Empty((await h.Client.GetFromJsonAsync<McpPolicyDto[]>("/api/v1/admin/mcp-policies"))!);
        var history = Assert.Single((await h.Client.GetFromJsonAsync<McpPolicyDto[]>("/api/v1/admin/mcp-policies?includeRetired=true"))!);
        Assert.Equal("Deleted", history.State);
        Assert.Equal(3, history.Revision);
        Assert.Equal(3, (await h.Client.GetFromJsonAsync<McpPolicyAuditDto[]>($"/api/v1/admin/mcp-policies/{h.Credential}/audit"))!.Length);
        await h.WithDb(async db =>
        {
            var row = await db.Set<McpAccessPolicy>().SingleAsync();
            Assert.False(McpPolicyDefinitionService.AllowsRequest(row, new() { McpResourceUri = credential.Resource }, "GET", "/api/v1/incidents/", null, DateTime.UtcNow));
            Assert.Equal(3, await db.Set<McpAccessPolicyAudit>().CountAsync());
        });
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", changed with { ExpectedRevision = 3 })).StatusCode);
    }

    [Fact]
    public async Task Legacy_policy_history_is_hidden_by_default_and_can_be_deleted_after_expiry()
    {
        await using var h = await Harness.Create(false);
        await h.WithDb(async db =>
        {
            db.Add(new McpAccessPolicy { CredentialId = h.Credential, OrganizationId = "a", PermissionsJson = "[\"Incident.Read\"]", State = "Revoked", ExpiresUtc = DateTime.UtcNow.AddDays(-1), Revision = 1 });
            await db.SaveChangesAsync();
        });
        Assert.Empty((await h.Client.GetFromJsonAsync<McpPolicyDto[]>("/api/v1/admin/mcp-policies"))!);
        var preview = await h.Client.PostAsJsonAsync($"/api/v1/admin/mcp-policies/{h.Credential}/delete/preview", new McpPolicyDeleteDraft(1));
        preview.EnsureSuccessStatusCode();
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", new ConfirmMcpPolicy((await preview.Content.ReadFromJsonAsync<McpPolicyPreview>())!.Confirmation))).EnsureSuccessStatusCode();
        var row = Assert.Single((await h.Client.GetFromJsonAsync<McpPolicyDto[]>("/api/v1/admin/mcp-policies?includeRetired=true"))!);
        Assert.Equal("Deleted", row.State);
        Assert.Empty(row.Scopes);
    }
}
