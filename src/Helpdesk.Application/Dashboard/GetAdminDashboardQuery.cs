// File: src/Helpdesk.Application/Dashboard/GetAdminDashboardQuery.cs

using Helpdesk.Shared.DTOs.Dashboard;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Changes;

namespace Helpdesk.Application.Dashboard;

public record GetAdminDashboardQuery : IRequest<AdminDashboardDto>;

public class GetAdminDashboardQueryHandler(
    IRepository<Incident> incidents,
    IRepository<Request> requests,
    IRepository<Change> changes)
    : IRequestHandler<GetAdminDashboardQuery, AdminDashboardDto>
{
    public async Task<AdminDashboardDto> Handle(GetAdminDashboardQuery request, CancellationToken cancellationToken)
    {
        var allIncidents = (await incidents.GetAllAsync()).ToList();
        var incidentCountsByState = allIncidents
            .Where(i => i.State != TicketState.Resolved)
            .GroupBy(i => i.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var incidentsToday = allIncidents.Count(i => i.CreatedAt.Date == DateTime.UtcNow.Date);

        var unassignedIncidents = allIncidents.Count(i => i.AssignedToId == null && i.State != TicketState.Resolved);

        var allChanges = (await changes.GetAllAsync()).ToList();
        var liveChanges = allChanges
            .Where(c => !ChangeLifecycle.IsTerminal(c.LifecycleState ?? ChangeLifecycleState.Draft))
            .ToList();
        var pendingChanges = liveChanges.Count;
        var changeCountsByState = liveChanges
            .GroupBy(c => c.LifecycleState ?? ChangeLifecycleState.Draft)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var allRequests = (await requests.GetAllAsync()).ToList();
        var requestCountsByState = allRequests
            .Where(r => r.State != TicketState.Resolved)
            .GroupBy(r => r.State)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        return new AdminDashboardDto
        {
            IncidentsToday = incidentsToday,
            UnassignedIncidents = unassignedIncidents,
            PendingChanges = pendingChanges,
            IncidentCountsByState = incidentCountsByState,
            RequestCountsByState = requestCountsByState,
            ChangeCountsByState = changeCountsByState
        };
    }

}
