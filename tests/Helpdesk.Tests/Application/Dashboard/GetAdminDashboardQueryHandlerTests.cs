using Helpdesk.Application.Dashboard;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Tests.Application.Dashboard;

public class GetAdminDashboardQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsOnlyLiveCountsAndDoesNotForceZeroStateCards()
    {
        var incidents = new InMemoryRepository<Incident>();
        var requests = new InMemoryRepository<Request>();
        var changes = new InMemoryRepository<Change>();

        await incidents.CreateAsync(new Incident { Id = "inc-live", TrackingId = "INC-LIVE", Title = "Live", State = TicketState.New });
        await incidents.CreateAsync(new Incident { Id = "inc-done", TrackingId = "INC-DONE", Title = "Done", State = TicketState.Resolved });
        await requests.CreateAsync(new Request { Id = "req-live", TrackingId = "REQ-LIVE", Title = "Live", State = TicketState.InProgress });
        await requests.CreateAsync(new Request { Id = "req-done", TrackingId = "REQ-DONE", Title = "Done", State = TicketState.Resolved });
        await changes.CreateAsync(new Change { Id = "chg-live", TrackingId = "CHG-LIVE", Title = "Live", State = TicketState.Replied, LifecycleState = ChangeLifecycleState.PendingApproval });
        await changes.CreateAsync(new Change { Id = "chg-done", TrackingId = "CHG-DONE", Title = "Done", State = TicketState.New, LifecycleState = ChangeLifecycleState.ImplementedSuccess });
        await changes.CreateAsync(new Change { Id = "chg-cancelled", TrackingId = "CHG-CANCELLED", State = TicketState.New, LifecycleState = ChangeLifecycleState.Cancelled });
        await changes.CreateAsync(new Change { Id = "chg-failed", TrackingId = "CHG-FAILED", State = TicketState.InProgress, LifecycleState = ChangeLifecycleState.ImplementedFailed });

        var handler = new GetAdminDashboardQueryHandler(incidents, requests, changes);

        var result = await handler.Handle(new GetAdminDashboardQuery(), CancellationToken.None);

        Assert.Equal(new[] { TicketState.New }, result.IncidentCountsByState.Keys);
        Assert.Equal(1, result.IncidentCountsByState[TicketState.New]);
        Assert.DoesNotContain(TicketState.WaitingReply, result.IncidentCountsByState.Keys);
        Assert.DoesNotContain(TicketState.Resolved, result.IncidentCountsByState.Keys);

        Assert.Equal(new[] { TicketState.InProgress.ToString() }, result.RequestCountsByState.Keys);
        Assert.Equal(new[] { ChangeLifecycleState.PendingApproval.ToString() }, result.ChangeCountsByState.Keys);
        Assert.Equal(1, result.PendingChanges);
    }
}
