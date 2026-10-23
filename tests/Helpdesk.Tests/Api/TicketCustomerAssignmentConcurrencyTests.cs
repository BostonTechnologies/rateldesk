using System.Net;
using System.Net.Http.Json;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

public sealed partial class TicketCustomerAssignmentEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requester_assignment_from_a_stale_change_read_preserves_the_concurrently_committed_lifecycle(bool postgres)
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync(postgres: postgres);
        await SeedRequesterRaceAsync(harness);
        harness.Barrier.HoldNext = true;
        var assignment = Task.Run(() => AssignRequesterAsync(harness, "customer-1"));
        try
        {
            await harness.Barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await harness.WithDbAsync(async db =>
            {
                var change = await db.Changes.SingleAsync();
                change.LifecycleState = ChangeLifecycleState.ApprovedForImplementation;
                change.State = TicketState.PendingApproval;
                change.ImplementorUserId = change.AssignedToId = "reviewed-implementor";
                change.ApproverUserIds = ["reviewed-owner"];
                db.ChangeApprovals.Add(new ChangeApproval { Id = "reviewed-decision", ChangeId = change.Id,
                    ApproverId = "reviewed-owner", ApproverEmail = "owner@example.test", Status = ChangeApprovalStatus.Reviewed });
                await db.SaveChangesAsync();
            });
        }
        finally { harness.Barrier.Release.TrySetResult(); }
        using var response = await assignment;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = Assert.IsType<Change>(await harness.GetTicketAsync("changes", "change-1"));
        Assert.Equal("customer-1", saved.CustomerId);
        Assert.Equal("first@example.test", saved.RequesterEmail);
        Assert.Equal(ChangeLifecycleState.ApprovedForImplementation, saved.LifecycleState);
        Assert.Equal(TicketState.PendingApproval, saved.State);
        Assert.Equal("reviewed-implementor", saved.ImplementorUserId);
        Assert.Equal(saved.ImplementorUserId, saved.AssignedToId);
        Assert.Equal(new[] { "reviewed-owner" }, saved.ApproverUserIds);
        await harness.WithDbAsync(async db => Assert.Equal(ChangeApprovalStatus.Reviewed, (await db.ChangeApprovals.SingleAsync()).Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_requester_assignments_commit_one_customer_without_replacing_the_winner(bool postgres)
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync(postgres: postgres);
        await SeedRequesterRaceAsync(harness);
        harness.Barrier.HoldNext = true;
        var first = Task.Run(() => AssignRequesterAsync(harness, "customer-1"));
        try
        {
            await harness.Barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var winner = await AssignRequesterAsync(harness, "customer-2");
            Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        }
        finally { harness.Barrier.Release.TrySetResult(); }
        using var loser = await first;
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        var ticket = await harness.GetTicketAsync("changes", "change-1");
        Assert.Equal("customer-2", ticket!.CustomerId);
        Assert.Equal("second@example.test", ticket.RequesterEmail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requester_assignment_rechecks_the_organization_after_the_authorized_ticket_read(bool postgres)
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync(role: "Technician", postgres: postgres);
        await SeedRequesterRaceAsync(harness);
        harness.Barrier.HoldNext = true;
        var assignment = Task.Run(() => AssignRequesterAsync(harness, "customer-1"));
        try
        {
            await harness.Barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await harness.WithDbAsync(async db =>
            {
                var change = await db.Changes.SingleAsync();
                change.OrganizationId = "org-2";
                await db.SaveChangesAsync();
            });
        }
        finally { harness.Barrier.Release.TrySetResult(); }
        using var response = await assignment;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var ticket = await harness.GetTicketAsync("changes", "change-1");
        Assert.Equal("org-2", ticket!.OrganizationId);
        Assert.Null(ticket.CustomerId);
        Assert.Null(ticket.RequesterEmail);
    }

    public enum CustomerDrift { Disabled, Organization, Email }

    [Theory]
    [InlineData(false, CustomerDrift.Disabled)]
    [InlineData(false, CustomerDrift.Organization)]
    [InlineData(false, CustomerDrift.Email)]
    [InlineData(true, CustomerDrift.Disabled)]
    [InlineData(true, CustomerDrift.Organization)]
    [InlineData(true, CustomerDrift.Email)]
    public async Task Requester_assignment_checks_current_customer_state_at_the_conditional_write(bool postgres, CustomerDrift drift)
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync(postgres: postgres);
        await SeedRequesterRaceAsync(harness);
        harness.Barrier.HoldWrite = true;
        var assignment = Task.Run(() => AssignRequesterAsync(harness, "customer-1"));
        try
        {
            // The actual EF command has been constructed after both repository
            // reads. Hold before executing it, without an open reader/transaction.
            await harness.Barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await harness.WithDbAsync(async db =>
            {
                var customer = await db.Customers.SingleAsync(item => item.Id == "customer-1");
                switch (drift)
                {
                    case CustomerDrift.Disabled: customer.IsEnabled = false; break;
                    case CustomerDrift.Organization: customer.OrganizationId = "org-2"; break;
                    case CustomerDrift.Email: customer.Email = "changed@example.test"; break;
                    default: throw new ArgumentOutOfRangeException(nameof(drift));
                }
                await db.SaveChangesAsync();
            });
        }
        finally { harness.Barrier.Release.TrySetResult(); }
        using var response = await assignment;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var ticket = await harness.GetTicketAsync("changes", "change-1");
        Assert.Null(ticket!.CustomerId);
        Assert.Null(ticket.RequesterEmail);
    }

    private static async Task SeedRequesterRaceAsync(TicketCustomerAssignmentHarness harness)
    {
        await harness.SeedTicketAsync("changes", "change-1", "org-1");
        await harness.SeedCustomerAsync("customer-1", "org-1", "First requester", "first@example.test");
        await harness.SeedCustomerAsync("customer-2", "org-1", "Second requester", "second@example.test");
    }

    private static Task<HttpResponseMessage> AssignRequesterAsync(TicketCustomerAssignmentHarness harness, string customer) =>
        harness.Client.PostAsJsonAsync("/api/v1/changes/change-1/customer", new { CustomerId = customer });
}
