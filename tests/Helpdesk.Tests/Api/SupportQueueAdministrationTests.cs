using System.Net;
using System.Net.Http.Json;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

namespace Helpdesk.Tests.Api;

public sealed class SupportQueueAdministrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_administrator_manages_owned_group_and_coverage_without_cross_tenant_or_self_assignment(bool postgres)
    {
        await using var harness = await RequestLiveProgressTests.Harness.CreateAsync(postgres);
        await harness.WithDbAsync(async db =>
        {
            db.Organizations.Add(new Organization { Id = "foreign", Name = "Foreign customer", ItSupportOrganizationId = "live-org" });
            db.Users.Add(new User { Id = "foreign-user", Name = "Foreign contact", OrganizationId = "foreign" });
            db.SupportGroups.AddRange(new SupportGroup { Id = "owned", Name = "Local team", OwningOrganizationId = "live-org" },
                new SupportGroup { Id = "foreign-group", Name = "Other team", OwningOrganizationId = "foreign" });
            db.OrganizationSupportCoverages.Add(new OrganizationSupportCoverage
            {
                Id = "foreign-coverage", CustomerOrganizationId = "foreign", ProviderOrganizationId = "live-org",
                SupportGroupId = "owned", Role = SupportCoverageRole.Primary, IsEnabled = true
            });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "requester", OrganizationId = "live-org", RoleKey = ScopedRoleCatalog.TenantAdministrator
            });
            await db.SaveChangesAsync();
        });

        using var client = (await harness.ClientsAsync("requester")).CreateClient("HelpdeskApi");
        const string groups = "api/v1/support/groups/";
        const string coverage = "api/v1/support/coverage/";
        Assert.Equal("owned", Assert.Single((await client.GetFromJsonAsync<SupportGroupDto[]>(groups))!).Id);
        using var foreign = await client.GetAsync(groups + "foreign-group");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var foreignRoster = await client.GetAsync(groups + "foreign-group/members/");
        Assert.Equal(HttpStatusCode.NotFound, foreignRoster.StatusCode);

        using var create = await client.PostAsJsonAsync(groups,
            new CreateSupportGroupDto { OwningOrganizationId = "live-org", Name = "Service desk", IsEnabled = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var group = (await create.Content.ReadFromJsonAsync<SupportGroupDto>())!;
        using var foreignOwner = await client.PutAsJsonAsync(groups + group.Id,
            new UpdateSupportGroupDto { OwningOrganizationId = "foreign", Name = group.Name, IsEnabled = true });
        Assert.Equal(HttpStatusCode.Forbidden, foreignOwner.StatusCode);

        var members = groups + group.Id + "/members/";
        using var self = await client.PostAsJsonAsync(members, Member("requester"));
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        using var foreignMember = await client.PostAsJsonAsync(members, Member("foreign-user"));
        Assert.Equal(HttpStatusCode.BadRequest, foreignMember.StatusCode);
        using var existingStaff = await client.PostAsJsonAsync(members, Member("other-requester"));
        Assert.Equal(HttpStatusCode.Created, existingStaff.StatusCode);
        Assert.Equal("other-requester", Assert.Single((await client.GetFromJsonAsync<SupportGroupMemberDto[]>(members))!).UserId);

        Assert.Empty((await client.GetFromJsonAsync<OrganizationSupportCoverageDto[]>(coverage))!);
        using var foreignCoverage = await client.DeleteAsync(coverage + "foreign-coverage");
        Assert.Equal(HttpStatusCode.NotFound, foreignCoverage.StatusCode);
        using var share = await client.PostAsJsonAsync(coverage, Route("foreign", group.Id));
        Assert.Equal(HttpStatusCode.Forbidden, share.StatusCode);
        using var localCoverage = await client.PostAsJsonAsync(coverage, Route("live-org", group.Id));
        Assert.Equal(HttpStatusCode.Created, localCoverage.StatusCode);
        Assert.Equal(group.Id, Assert.Single((await client.GetFromJsonAsync<OrganizationSupportCoverageDto[]>(coverage))!).SupportGroupId);

        static CreateSupportGroupMemberDto Member(string userId) => new()
        {
            UserId = userId, Role = SupportGroupMemberRole.Member, Source = SupportGroupMemberSource.Manual, IsEnabled = true
        };
        static CreateOrganizationSupportCoverageDto Route(string customerId, string groupId) => new()
        {
            CustomerOrganizationId = customerId, ProviderOrganizationId = "live-org", SupportGroupId = groupId,
            Role = SupportCoverageRole.Primary, IsEnabled = true
        };
    }
}
