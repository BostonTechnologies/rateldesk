using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.RequestTasks;

public sealed class RequestTaskEscalationCommit(HelpdeskDbContext db, IIngressEffectContext effects, MailboxOutboxStore outbox)
    : IRequestTaskEscalationCommit
{
    public async Task<bool> TryCommitAsync(RequestTask task, Func<Task> publish, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        using var capture = effects.Begin(Guid.NewGuid());
        effects.OrganizationId = task.OrganizationId;
        var updated = await db.RequestTasks.Where(row => row.Id == task.Id &&
            row.Status == RequestTaskStatus.InProgress && row.AdministrativeClosureReason == null &&
            row.AutomationStateVersion == task.AutomationStateVersion)
            .ExecuteUpdateAsync(fields => fields
                .SetProperty(row => row.Escalated, task.Escalated)
                .SetProperty(row => row.EscalatedAt, task.EscalatedAt)
                .SetProperty(row => row.SlaBreached, task.SlaBreached)
                .SetProperty(row => row.UpdatedAt, task.UpdatedAt)
                .SetProperty(row => row.AutomationStateVersion, row => row.AutomationStateVersion + 1), ct);
        if (updated == 0) return false;
        await publish();
        await outbox.FlushAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
