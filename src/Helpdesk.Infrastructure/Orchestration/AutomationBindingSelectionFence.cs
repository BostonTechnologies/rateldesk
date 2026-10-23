using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Orchestration;

internal sealed class AutomationBindingSelectionChangedException()
    : IntegrationProviderConfigurationConflictException("The automation binding changed while its remote target was being read. Refresh the binding before trying again.", "binding_selection_changed");

internal static class AutomationBindingSelectionFence
{
    public static async Task SaveAsync(HelpdeskDbContext db, IEnumerable<AutomationBinding> bindings, Action applyChanges, CancellationToken cancellationToken)
    {
        // The remote call has already finished. Hold only a short persistence transaction:
        // checking the original selection with UPDATE also locks it until local writes commit.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        foreach (var binding in bindings.DistinctBy(x => x.Id).OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            var original = db.Entry(binding).OriginalValues;
            var organizationId = original.GetValue<string>(nameof(AutomationBinding.OrganizationId));
            var connectionId = original.GetValue<Guid?>(nameof(AutomationBinding.SystemConnectionId));
            var definitionId = original.GetValue<string>(nameof(AutomationBinding.OrchestrationRequestDefinitionId));
            var jobId = original.GetValue<string?>(nameof(AutomationBinding.OrchestrationJobDefinitionId));
            var enabled = original.GetValue<bool>(nameof(AutomationBinding.Enabled));
            var matched = await db.AutomationBindings.IgnoreQueryFilters()
                .Where(x => x.Id == binding.Id && x.OrganizationId == organizationId && x.SystemConnectionId == connectionId
                    && x.OrchestrationRequestDefinitionId == definitionId && x.OrchestrationJobDefinitionId == jobId && x.Enabled == enabled)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.UpdatedAtUtc, x => x.UpdatedAtUtc), cancellationToken);
            if (matched != 1) throw new AutomationBindingSelectionChangedException();
        }
        applyChanges();
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }
}
