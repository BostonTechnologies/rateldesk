using System.Net;
using System.Net.Http.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class NeutralTicketRelationTests
{
    [Fact]
    public async Task Concurrent_reverse_links_are_one_relation_and_foreign_organizations_stay_out_of_picker_counts()
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(true);
        await h.EditAsync(db =>
        {
            db.Organizations.Add(new() { Id = "foreign-org", Name = "Foreign synthetic organization", IsEnabled = true });
            db.Incidents.Add(new() { Id = "source", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, TrackingId = "INC-SOURCE", Title = "Source" });
            db.Requests.Add(new() { Id = "target", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, TrackingId = "REQ-TARGET", Title = "Target" });
            db.Requests.Add(new() { Id = "foreign", OrganizationId = "foreign-org", CustomerId = h.CustomerId, TrackingId = "REQ-PRIVATE", Title = "Private foreign label" });
            return Task.CompletedTask;
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => h.AdminAsync(HttpMethod.Post,
            i % 2 == 0 ? "/api/v1/tickets/source/relations/" : "/api/v1/tickets/target/relations/",
            new LinkTicketRequest(i % 2 == 0 ? "target" : "source"))));
        var ids = new List<Guid>();
        foreach (var response in results)
        {
            using (response) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); ids.Add((await response.Content.ReadFromJsonAsync<TicketRelationDto>())!.Id); }
        }
        Assert.Single(ids.Distinct());
        using var foreign = await h.AdminAsync(HttpMethod.Post, "/api/v1/tickets/source/relations/", new LinkTicketRequest("foreign"));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var choices = await h.AdminAsync(HttpMethod.Get, "/api/v1/tickets/source/relations/choices?type=requests&search=REQ-");
        var page = (await choices.Content.ReadFromJsonAsync<TicketLinkChoicePage>())!;
        Assert.Equal(1, page.TotalCount); Assert.Equal("target", Assert.Single(page.Items).Id);
        await h.EditAsync(async db => Assert.Equal(1, await db.TicketRelations.CountAsync()));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task All_ticket_pairs_are_reciprocal_neutral_and_reverse_idempotent(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres);
        var tickets = new Ticket[]
        {
            new Incident { Id = "i1", TrackingId = "INC-DEMO-001", Title = "Incident one", CcRecipients = ["one@example.test"] },
            new Incident { Id = "i2", TrackingId = "INC-DEMO-002", Title = "Incident two" },
            new Request { Id = "r1", TrackingId = "REQ-DEMO-001", Title = "Request one", CcRecipients = ["two@example.test"] },
            new Request { Id = "r2", TrackingId = "REQ-DEMO-002", Title = "Request two" },
            new Change { Id = "c1", TrackingId = "CHG-DEMO-001", Title = "Change one", LifecycleState = ChangeLifecycleState.ImplementationInProgress },
            new Change { Id = "c2", TrackingId = "CHG-DEMO-002", Title = "Change two" }
        };
        await h.EditAsync(db =>
        {
            foreach (var ticket in tickets) { ticket.OrganizationId = h.OrganizationId; ticket.CustomerId = h.CustomerId; db.Add(ticket); }
            return Task.CompletedTask;
        });
        var before = await h.CountsAsync();
        var pairs = new[] { ("i1", "i2"), ("i1", "r1"), ("i1", "c1"), ("r1", "r2"), ("r1", "c1"), ("c1", "c2") };
        foreach (var (source, target) in pairs)
        {
            using var first = await h.AdminAsync(HttpMethod.Post, $"/api/v1/tickets/{source}/relations/", new LinkTicketRequest(target));
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var relation = await first.Content.ReadFromJsonAsync<TicketRelationDto>();
            using var reverse = await h.AdminAsync(HttpMethod.Post, $"/api/v1/tickets/{target}/relations/", new LinkTicketRequest(source));
            Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
            Assert.Equal(relation!.Id, (await reverse.Content.ReadFromJsonAsync<TicketRelationDto>())!.Id);
            using var listed = await h.AdminAsync(HttpMethod.Get, $"/api/v1/tickets/{target}/relations/");
            Assert.Contains((await listed.Content.ReadFromJsonAsync<TicketRelationPage>())!.Items, item => item.Id == relation.Id);
        }
        await h.EditAsync(async db =>
        {
            Assert.Equal(6, await db.TicketRelations.CountAsync());
            Assert.All(await db.Tickets.Where(x => tickets.Select(t => t.Id).Contains(x.Id)).ToListAsync(), ticket =>
            { Assert.Equal(TicketState.New, ticket.State); Assert.Equal(0, ticket.Replies); Assert.Equal(0, ticket.TimeSpentHours); Assert.Null(ticket.AssignedToId); });
            Assert.Single((await db.Incidents.SingleAsync(x => x.Id == "i1")).CcRecipients);
            Assert.Single((await db.Requests.SingleAsync(x => x.Id == "r1")).CcRecipients);
        });
        Assert.Equal(before, await h.CountsAsync());
        using var self = await h.AdminAsync(HttpMethod.Post, "/api/v1/tickets/i1/relations/", new LinkTicketRequest("i1"));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        using var changes = await h.AdminAsync(HttpMethod.Get, "/api/v1/tickets/i1/relations/?pageSize=25");
        Assert.Contains((await changes.Content.ReadFromJsonAsync<TicketRelationPage>())!.Items,
            relation => relation.TargetTicketId == "c1" && relation.TargetLifecycleState == "ImplementationInProgress" || relation.SourceTicketId == "c1" && relation.SourceLifecycleState == "ImplementationInProgress");
    }
}
