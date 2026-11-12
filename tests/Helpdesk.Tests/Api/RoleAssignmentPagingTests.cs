using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Shared.Models;
using Harness = Helpdesk.Tests.Api.AssigneeSearchEndpointsTests.Harness;

namespace Helpdesk.Tests.Api;

public sealed class RoleAssignmentPagingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assignment_details_page_the_whole_authorized_collection_and_retain_legacy_array_contract(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        await h.WithDb(async db =>
        {
            db.Roles.AddRange(new Role { Id = "paged-role", Key = "paged-role", Name = "Synthetic built-in role", IsBuiltIn = true },
                new Role { Id = "foreign-role", Key = "foreign-role", Name = "Foreign custom role", OwnerOrganizationId = "other" });
            for (var index = 0; index < 233; index++)
            {
                var org = index < 230 ? "org" : "other";
                var user = new User { Id = $"assignment-person-{index:000}", Name = "Same synthetic name", Email = $"assignment{index}@example.test", OrganizationId = org };
                db.Users.Add(user);
                db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = user.Id, RoleKey = "paged-role", OrganizationId = org });
            }
            await db.SaveChangesAsync();
        });
        var legacy = await h.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/role-definitions/paged-role/assignments");
        Assert.Equal(JsonValueKind.Array, legacy.ValueKind); Assert.Equal(200, legacy.GetArrayLength());
        var all = await h.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/role-definitions/paged-role/assignments?paged=true&page=5&pageSize=50");
        Assert.Equal(233, all.GetProperty("totalCount").GetInt32()); Assert.Equal(33, all.GetProperty("items").GetArrayLength());
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "role-manager");
        var scoped = await h.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/role-definitions/paged-role/assignments?paged=true&page=99999&pageSize=50");
        Assert.Equal(230, scoped.GetProperty("totalCount").GetInt32()); Assert.Equal(5, scoped.GetProperty("page").GetInt32());
        Assert.Equal(30, scoped.GetProperty("items").GetArrayLength());
        Assert.All(scoped.GetProperty("items").EnumerateArray(), item => Assert.Equal("org", item.GetProperty("organizationId").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/admin/role-definitions/foreign-role/assignments?paged=true")).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/admin/role-definitions/paged-role/assignments?paged=true")).StatusCode);
    }
}
