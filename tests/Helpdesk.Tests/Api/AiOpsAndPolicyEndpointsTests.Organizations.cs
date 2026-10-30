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
