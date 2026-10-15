using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Harness = Helpdesk.Tests.Api.AssigneeSearchEndpointsTests.Harness;

namespace Helpdesk.Tests.Api;

public sealed class TicketListFiltersTests
{
    [Theory]
    [InlineData(false, "incidents")][InlineData(false, "requests")][InlineData(false, "changes")]
    [InlineData(true, "incidents")][InlineData(true, "requests")][InlineData(true, "changes")]
    public async Task Every_dimension_filters_before_paging_and_preserves_authorization(bool postgres, string module)
    {
        await using var h = await Harness.Create(postgres);
        await Seed(h, module);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var all = await Get(h, module, "view=all&page=1&pageSize=10&q=synthetic");
        Assert.Equal(42, all.GetProperty("totalCount").GetInt32()); Assert.Equal(10, Items(all).Length);
        var second = await Get(h, module, "view=all&page=2&pageSize=10&q=synthetic");
        Assert.Empty(Items(all).Intersect(Items(second)));
        Assert.Equal(0, Total(await Get(h, module, "view=all&customerOrgId=other")));
        var forId = module == "changes" ? "late" : "contact";
        Assert.Equal(42, Total(await Get(h, module, $"view=all&q=synthetic&customerOrgId=org&forId={forId}")));
        Assert.Equal(0, Total(await Get(h, module, "view=all&forId=not-a-contact")));
        Assert.Equal(21, Total(await Get(h, module, "view=all&q=synthetic&unassigned=true")));
        Assert.Equal(21, Total(await Get(h, module, "view=all&q=synthetic&assigneeId=external")));
        Assert.Equal(42, Total(await Get(h, module, "view=all&q=synthetic&assigneeId=external&unassigned=true")));
        Assert.Equal(21, Total(await Get(h, module, "view=all&q=synthetic&priority=High")));
        Assert.Equal(42, Total(await Get(h, module, "view=all&q=synthetic&priority=High&priority=Low")));
        Assert.Equal(0, Total(await Get(h, module, "view=all&q=synthetic&priority=High&assigneeId=external")));
        Assert.Equal(21, Total(await Get(h, module, "view=all&q=synthetic&priority=High&unassigned=true")));
        var active = module == "changes" ? "Draft" : "New";
        var closed = module == "changes" ? "ImplementedSuccess" : "Resolved";
        Assert.Equal(21, Total(await Get(h, module, $"q=synthetic&stateValues={closed}")));
        Assert.Equal(42, Total(await Get(h, module, $"q=synthetic&stateValues={closed}&stateValues={active}")));
        Assert.Equal(21, Total(await Get(h, module, "q=synthetic&view=historic")));
        Assert.Equal(21, Total(await Get(h, module, "q=synthetic")));
        Assert.Equal("row-00", Items(await Get(h, module, "view=all&q=synthetic&sort=trackingId&descending=false&pageSize=1"))[0]);
        Assert.Equal("row-41", Items(await Get(h, module, "view=all&q=synthetic&sort=trackingId&descending=true&pageSize=1"))[0]);
        var capped = await Get(h, module, "view=all&q=synthetic&pageSize=9999");
        Assert.Equal(42, Items(capped).Length); Assert.Equal(100, capped.GetProperty("pageSize").GetInt32());
        Assert.Empty(Items(await Get(h, module, "view=all&page=2147483647&pageSize=100")));
        Assert.Equal(0, Total(await Get(h, module, "view=all&q=%25%5F")));
        var customer = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>($"/api/v1/ticketing/filter-options?module={module.TrimEnd('s')}&dimension=customer&q=Other");
        Assert.Empty(customer!.Items);
        var requester = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>($"/api/v1/ticketing/filter-options?module={module.TrimEnd('s')}&dimension=for&q={(module == "changes" ? "Zulu" : "Contact")}");
        Assert.Equal(forId, Assert.Single(requester!.Items).Id);
    }

    [Theory]
    [InlineData(false, "incidents")][InlineData(false, "requests")][InlineData(false, "changes")]
    [InlineData(true, "incidents")][InlineData(true, "requests")][InlineData(true, "changes")]
    public async Task UTC_half_open_boundaries_created_alternative_and_invalid_queries(bool postgres, string module)
    {
        await using var h = await Harness.Create(postgres); await Seed(h, module);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var range = "view=all&q=synthetic&from=2026-03-29T00%3A00%3A00Z&to=2026-03-29T01%3A00%3A00Z";
        Assert.Equal(21, Total(await Get(h, module, range)));
        Assert.Equal(0, Total(await Get(h, module, range + "&timeField=created")));
        Assert.Equal(21, Total(await Get(h, module, "view=all&q=synthetic&from=2026-03-29T02%3A00%3A00%2B02%3A00&to=2026-03-29T03%3A00%3A00%2B02%3A00")));
        foreach (var invalid in new[] { "sort=sql", "stateValues=999", "priority=999", "timeField=closed", "from=2026-01-01", "from=2026-03-29T01:00:00Z&to=2026-03-29T00:00:00Z", "unassigned=maybe", "q=" + new string('a', 201) })
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync($"/api/v1/{module}?{invalid}")).StatusCode);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Filter_lookups_are_bounded_and_share_assignment_eligibility(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var first = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=request&dimension=assignee");
        Assert.Equal(20, first!.Items.Count); Assert.True(first.HasMore);
        var late = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=request&dimension=assignee&q=surname");
        Assert.Equal("late", Assert.Single(late!.Items).Id);
        var incident = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=incident&dimension=assignee");
        Assert.Equal("external", Assert.Single(incident!.Items).Id);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        var denied = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=request&dimension=assignee&customerOrgId=other");
        Assert.Empty(denied!.Items);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Contributor_identity_and_module_grants_scope_both_tickets_and_facets(bool postgres)
    {
        await using var h = await Harness.Create(postgres); await Seed(h, "incidents");
        await h.WithDb(async db => {
            db.Customers.Add(new Customer { Id = "another-contact", Name = "Another contact", Email = "another@example.test", OrganizationId = "org" });
            db.Incidents.Add(new Incident { Id = "another-ticket", Title = "synthetic other owner", TrackingId = "SYN-OTHER", OrganizationId = "org", CustomerId = "another-contact" });
            await db.SaveChangesAsync();
        });
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "own");
        Assert.Equal(42, Total(await Get(h, "incidents", "view=all&q=synthetic")));
        Assert.Equal(0, Total(await Get(h, "incidents", "view=all&forId=another-contact")));
        var own = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=incident&dimension=for");
        Assert.Equal("contact", Assert.Single(own!.Items).Id);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "request-only");
        Assert.Equal(0, Total(await Get(h, "incidents", "view=all&q=synthetic")));
        var denied = await h.Client.GetFromJsonAsync<TicketFilterOptionsDto>("/api/v1/ticketing/filter-options?module=incident&dimension=customer");
        Assert.Empty(denied!.Items);
    }

    private static int Total(JsonElement response) => response.GetProperty("totalCount").GetInt32();
    private static string[] Items(JsonElement response) => response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
    private static async Task<JsonElement> Get(Harness h, string module, string query)
    {
        var response = await h.Client.GetAsync($"/api/v1/{module}?summaryOnly=true&{(query.Contains("page=") ? "" : "page=1&")}{query}");
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static Task Seed(Harness h, string module) => h.WithDb(async db => {
        db.Customers.AddRange(new Customer { Id = "contact", Name = "Contact", Email = "contact@example.test", OrganizationId = "org" }, new Customer { Id = "secret-contact", Name = "Secret", Email = "secret@example.test", OrganizationId = "other" });
        for (var i = 0; i < 43; i++)
        {
            Ticket ticket = module switch { "incidents" => new Incident(), "requests" => new Helpdesk.Shared.Models.Request(), _ => new Change { RequestedForUserId = "late", ImplementorUserId = i % 2 == 0 ? null : "external", LifecycleState = i % 2 == 0 ? ChangeLifecycleState.Draft : ChangeLifecycleState.ImplementedSuccess } };
            ticket.Id = $"row-{i:D2}"; ticket.Title = "synthetic ticket"; ticket.TrackingId = $"SYN-{i:D2}";
            ticket.OrganizationId = i == 42 ? "other" : "org"; ticket.CustomerId = i == 42 ? "secret-contact" : "contact";
            ticket.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); ticket.UpdatedAt = new DateTime(2026, 3, 29, i % 2, 0, 0, DateTimeKind.Utc);
            ticket.AssignedToId = i % 2 == 0 ? null : "external"; ticket.Priority = i % 2 == 0 ? TicketPriority.High : TicketPriority.Low; ticket.State = i % 2 == 0 ? TicketState.New : TicketState.Resolved;
            db.Add(ticket);
        }
        await db.SaveChangesAsync();
    });
}
