using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed partial class AiOpsAndPolicyEndpointsTests
{
    [Theory]
    [InlineData(HelpdeskPermissions.IncidentWrite)]
    [InlineData(HelpdeskPermissions.IncidentManager)]
    [InlineData(HelpdeskPermissions.RequestWrite)]
    [InlineData(HelpdeskPermissions.RequestManager)]
    [InlineData(HelpdeskPermissions.ChangeWrite)]
    [InlineData(HelpdeskPermissions.ChangeManager)]
    public async Task Investigation_organization_picker_returns_only_enabled_manageable_names(string permission)
    {
        await using var h = await Harness.Create(false);
        await h.WithDb(async db =>
        {
            var own = await db.Organizations.SingleAsync(x => x.Id == "a");
            own.ContactInfo = "Administrative contact data must not be exposed";
            db.Organizations.Add(new Organization { Id = "disabled", Name = "Disabled", IsEnabled = false });
            await db.SaveChangesAsync();
        });
        var access = Profile(false) with
        {
            AllowedOrganizationIds = new HashSet<string> { "a", "b", "disabled" },
            ScopedPermissionGrants = new HashSet<ScopedPermissionGrant>
            {
                new(permission, "a"), new(HelpdeskPermissions.IncidentRead, "b"), new(permission, "disabled")
            }
        };
        h.Access.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(access);
        var rows = (await h.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/ai/ops/organizations"))!;
        var row = Assert.Single(rows);
        Assert.Equal("a", row.GetProperty("id").GetString());
        Assert.Equal("A", row.GetProperty("name").GetString());
        Assert.Equal(new[] { "id", "name" }, row.EnumerateObject().Select(x => x.Name).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Organization_search_finds_late_granted_names_and_hydrates_only_visible_ids(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var grants = new HashSet<ScopedPermissionGrant>();
        await h.WithDb(async db =>
        {
            for (var i = 0; i < 73; i++)
            {
                var id = $"granted-{i:D2}";
                db.Organizations.Add(new Organization { Id = id, Name = $"Granted {i:D2}" });
                grants.Add(new ScopedPermissionGrant(HelpdeskPermissions.RequestWrite, id));
            }
            await db.SaveChangesAsync();
        });
        h.Access.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(Profile(false) with { ScopedPermissionGrants = grants });
        const string url = "/api/v1/ai/ops/organizations/search?";
        var first = await h.Client.GetFromJsonAsync<JsonElement>(url + "limit=10");
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal(73, first.GetProperty("totalEligible").GetInt32());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        foreach (var lookup in new[] { "q=72", "id=granted-72" })
        {
            var page = await h.Client.GetFromJsonAsync<JsonElement>(url + lookup);
            var row = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal("granted-72", row.GetProperty("id").GetString());
            Assert.Equal(new[] { "id", "name" }, row.EnumerateObject().Select(x => x.Name).Order());
        }
        var denied = await h.Client.GetFromJsonAsync<JsonElement>(url + "id=b");
        Assert.Empty(denied.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Investigation_organization_picker_excludes_scoped_users_without_management()
    {
        await using var h = await Harness.Create(false);
        h.Access.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(Profile(false) with
        {
            ScopedPermissionGrants = new HashSet<ScopedPermissionGrant> { new(HelpdeskPermissions.IncidentRead, "a") }
        });
        Assert.Empty((await h.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/ai/ops/organizations"))!);
    }

    [Fact]
    public async Task Investigation_organization_picker_allows_admin_enabled_organizations_in_name_order()
    {
        await using var h = await Harness.Create(false);
        await h.WithDb(async db =>
        {
            db.Organizations.Add(new Organization { Id = "disabled", Name = "Disabled", IsEnabled = false });
            await db.SaveChangesAsync();
        });
        var rows = (await h.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/ai/ops/organizations"))!;
        Assert.Equal(new[] { "a", "b" }, rows.Select(x => x.GetProperty("id").GetString()).ToArray());
    }
}
