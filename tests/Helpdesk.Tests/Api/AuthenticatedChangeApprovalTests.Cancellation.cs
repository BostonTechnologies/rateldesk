using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
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
            using var lateSigned = await Approve();
            Assert.Equal(HttpStatusCode.Conflict, lateSigned.StatusCode);
            using var lateAuthenticated = await approver.PostAsync(path + "/approval/review", null);
            Assert.Equal(HttpStatusCode.Conflict, lateAuthenticated.StatusCode);
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

    [Theory]
    [InlineData(false, ChangeLifecycleState.Cancelled, "Change Cancelled. This change has been cancelled.")]
    [InlineData(true, ChangeLifecycleState.Cancelled, "Change Cancelled. This change has been cancelled.")]
    [InlineData(false, ChangeLifecycleState.ImplementedFailed, "Change Implemented - Failed")]
    [InlineData(true, ChangeLifecycleState.ImplementedFailed, "Change Implemented - Failed")]
    public async Task Closure_queues_the_configured_mail_once_with_the_actual_outcome(bool postgres,
        ChangeLifecycleState destination, string subject)
    {
        await using var fixture = await Fixture.StartAsync(postgres, recipients: true, extraCc: true);
        var cancelling = destination == ChangeLifecycleState.Cancelled;
        await fixture.WithDbAsync(async db =>
        {
            if (cancelling)
                db.EmailTemplates.Add(new EmailTemplate { Name = "ChangeUpdate",
                    Subject = "Change {{{CHANGE_APPROVAL_STATUS}}}. {{{UPDATE_MESSAGE}}}" });
            else
            {
                var change = await db.Changes.SingleAsync();
                change.LifecycleState = ChangeLifecycleState.ApprovedForImplementation;
                change.ChangeType = "Standard";
                change.ChangeTemplateJson = JsonSerializer.Serialize(new ChangeTemplateDto
                {
                    IsPreApproved = true, ExistingRunbookReference = "SOP-SYNTHETIC", ScopeOfChange = "Owned test",
                    AffectedSystems = ["Test service"], ImplementationSteps = ["Apply"], ValidationSteps = ["Verify"], RollbackPlan = "Restore"
                });
                (await db.EmailTemplates.SingleAsync(template => template.Name == "ChangeImplemented")).Subject =
                    "Change Implemented - {{{CHANGE_COMPLETION_STATE}}}";
            }
            await db.SaveChangesAsync();
        });
        using var manager = fixture.Client("owner-a");
        const string path = "/api/v1/changes/change/lifecycle";
        var request = new ChangeLifecycleRequest(destination, "Synthetic explanation");
        using var first = await manager.PostAsJsonAsync(path, request);
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        using var repeat = await manager.PostAsJsonAsync(path, request with { Comment = "Second explanation" });
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.False((await repeat.Content.ReadFromJsonAsync<ChangeDto>())!.LifecycleChanged);

        var emails = (await fixture.ReadEmailEffectsAsync())
            .Select(effect => MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload)).ToArray();
        Assert.Equal(["cc@example.test", "owner-a@example.test"], emails.SelectMany(email => email.Recipients).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(emails, email => Assert.Equal(subject, email.Subject));
    }
}
