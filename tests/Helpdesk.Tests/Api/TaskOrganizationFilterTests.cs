using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Harness = Helpdesk.Tests.Api.AssigneeSearchEndpointsTests.Harness;

namespace Helpdesk.Tests.Api;

public sealed class TaskOrganizationFilterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_search_and_task_filter_use_the_complete_authorized_query_before_paging(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        await h.WithDb(async db =>
        {
            for (var index = 0; index < 125; index++)
            {
                var organization = new Organization { Id = $"tenant-{index:000}", Name = $"Synthetic tenant {index:000}" };
                db.Organizations.Add(organization);
                var request = new Request { Id = "request-" + organization.Id, OrganizationId = organization.Id, Title = "Synthetic request", TrackingId = "SYN-" + index };
                db.Requests.Add(request);
                db.RequestTasks.Add(new RequestTask { Id = "task-" + organization.Id, OrganizationId = organization.Id, RequestId = request.Id, Title = "Synthetic task", Status = RequestTaskStatus.Pending });
            }
            for (var index = 0; index < 55; index++) db.RequestTasks.Add(new RequestTask { Id = $"own-task-{index:000}", OrganizationId = "org", RequestId = "request", Title = "Own task", Status = RequestTaskStatus.Pending });
            db.RequestTasks.Add(new RequestTask { Id = "stale-mismatched-task", OrganizationId = "org", RequestId = "request-tenant-124", Title = "Foreign request title must stay private" });
            await db.SaveChangesAsync();
        });
        var late = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=task&dimension=customer&q=124");
        Assert.Equal("tenant-124", Assert.Single(late!.Items).Id);
        var filtered = await h.Client.GetFromJsonAsync<PagedResponse<RequestTaskListItemDto>>("/api/v1/request-tasks?customerOrgId=tenant-124&pageSize=25");
        Assert.Equal(1, filtered!.TotalCount);
        Assert.Equal("task-tenant-124", Assert.Single(filtered.Items).Id);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var denied = await h.Client.GetFromJsonAsync<PagedResponse<RequestTaskListItemDto>>("/api/v1/request-tasks?customerOrgId=tenant-124");
        Assert.Equal(0, denied!.TotalCount); Assert.Empty(denied.Items);
        Assert.Empty((await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=task&dimension=customer&q=124"))!.Items);
        var own = await h.Client.GetFromJsonAsync<PagedResponse<RequestTaskListItemDto>>("/api/v1/request-tasks?customerOrgId=org&page=2&pageSize=25");
        Assert.Equal(55, own!.TotalCount); Assert.Equal(25, own.Items.Count);
        Assert.All(own.Items, item => Assert.Equal("org", item.OrganizationId));
        Assert.DoesNotContain(own.Items, item => item.Id == "stale-mismatched-task");
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/v1/request-tasks?" + string.Join('&', Enumerable.Range(0, 21).Select(i => "customerOrgId=org" + i)))).StatusCode);
    }
}
