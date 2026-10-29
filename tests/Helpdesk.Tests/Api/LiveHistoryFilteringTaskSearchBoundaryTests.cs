using System.Net;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;

namespace Helpdesk.Tests.Api;

public sealed partial class LiveHistoryFilteringEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task_search_enforces_published_200_character_boundary_for_admin_and_scoped_reader(bool scoped)
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(scoped
            ? ScopedTicketProfile(new ScopedPermissionGrant(HelpdeskPermissions.RequestRead, "org-1")) : null);
        if (scoped) harness.UseRole(HelpdeskPermissions.RequestRead);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/v1/request-tasks/?q=" + new string('x', 200))).StatusCode);
        var rejected = await harness.Client.GetAsync("/api/v1/request-tasks/?q=" + new string('x', 201));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("200", await rejected.Content.ReadAsStringAsync());
    }
}
