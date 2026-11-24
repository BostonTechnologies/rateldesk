using System.Net;
using System.Net.Http.Json;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.Models;
using Helpdesk.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

public sealed partial class AuthenticatedChangeApprovalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_and_signed_final_approval_preserve_the_committed_winner(bool postgres, bool approvalWins)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        await fixture.WithDbAsync(async db =>
        {
            (await db.Changes.SingleAsync()).EmailExclusionReason = TicketEmailExclusionReason.MarketingSpam;
            await db.SaveChangesAsync();
        });
        using var manager = fixture.Client("owner-a");
        using var approver = fixture.Client("owner-b");
        var grant = await fixture.GrantAsync("owner-b");
        const string path = "/api/v1/changes/change";
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync(path + "/approval/review", null)).StatusCode);
        var cancellation = new ChangeLifecycleRequest(ChangeLifecycleState.Cancelled, "Synthetic cancellation",
            ChangeLifecycleState.PendingApproval);

        fixture.Commits.Fail = true;
        using var rolledBack = await manager.PostAsJsonAsync(path + "/lifecycle", cancellation);
        Assert.Equal(HttpStatusCode.InternalServerError, rolledBack.StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
        fixture.Commits.Fail = false;

        Task<HttpResponseMessage> Cancel() => manager.PostAsJsonAsync(path + "/lifecycle", cancellation);
        Task<HttpResponseMessage> Approve() => PublicLinkFixture.SendAsync(approver, grant, "change/decision", new(true));
        fixture.Commits.HoldNext = true;
        var winner = Task.Run(() => approvalWins ? Approve() : Cancel());
        Task<HttpResponseMessage> loser;
        try
        {
            await fixture.Commits.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
            fixture.Commits.ObserveCompetitor = true;
            loser = Task.Run(() => approvalWins ? Cancel() : Approve());
            await (postgres ? fixture.Commits.CompetingRead.Task : fixture.Commits.CompetingStart.Task)
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { fixture.Commits.Release.TrySetResult(); }

        Assert.Equal(HttpStatusCode.OK, (await winner).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await loser).StatusCode);
        if (approvalWins)
        {
            await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 1);
        }
        else
        {
            var retries = await Task.WhenAll(Cancel(), Cancel());
            Assert.Contains(retries, response => response.StatusCode == HttpStatusCode.OK);
            Assert.All(retries, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            using var edit = await manager.PutAsJsonAsync(path, new UpdateChangeDto
                { ExpectedLifecycleState = ChangeLifecycleState.PendingApproval, Priority = TicketPriority.Critical });
            Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
            await fixture.AssertStoredAsync(1, ChangeLifecycleState.Cancelled, 2, 2);
            await fixture.WithDbAsync(async db =>
            {
                var change = await db.Changes.SingleAsync();
                Assert.Equal(TicketState.Resolved, change.State);
                Assert.Equal("Synthetic cancellation", change.AdministrativeClosureReason);
            });
        }
    }
}
