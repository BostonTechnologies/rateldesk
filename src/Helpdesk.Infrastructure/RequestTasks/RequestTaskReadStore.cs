using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.RequestTasks;

/// <summary>Scopes lifecycle/graph reads to the indexed parent, including background processing.</summary>
public sealed class RequestTaskReadStore(HelpdeskDbContext db) : IRequestTaskReadStore
{
    public async Task<IReadOnlyList<RequestTask>> GetForRequestAsync(string requestId, CancellationToken ct)
        => await db.RequestTasks.AsNoTracking().Where(task => task.RequestId == requestId)
            .OrderBy(task => task.Order).ThenBy(task => task.Id).ToListAsync(ct);
}
