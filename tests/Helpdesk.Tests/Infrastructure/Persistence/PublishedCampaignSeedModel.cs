using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.Persistence;

/// <summary>Only historical seed contexts omit columns added by campaign #381.</summary>
internal static class PublishedCampaignSeedModel
{
    public static void Configure(ModelBuilder builder)
    {
        foreach (var name in new[] { nameof(Request.SelfServiceSubmissionId), nameof(Request.SelfServiceSubmitterId),
                     nameof(Request.SelfServiceSubmissionHash), nameof(Request.WorkflowStateVersion), nameof(Request.OperatorHold) })
            builder.Entity<Request>().Ignore(name);
        foreach (var name in new[] { nameof(RequestTask.AutomationDeadlineUtcTicks), nameof(RequestTask.AutomationNextCheckUtcTicks),
                     nameof(RequestTask.AutomationTimedOutAt), nameof(RequestTask.AutomationTimedOutAttemptId),
                     nameof(RequestTask.AutomationReconciliationLeaseId), nameof(RequestTask.AutomationReconciliationLeaseUntilUtcTicks),
                     nameof(RequestTask.AutomationContinuationPending), nameof(RequestTask.AutomationStateVersion) })
            builder.Entity<RequestTask>().Ignore(name);
        builder.Entity<Customer>().Ignore(customer => customer.IdentityUserId);
        // Domain retirement is integrated in the dependent Team batch. Its
        // historical rows must still be seeded through the published shape.
        builder.Entity<User>().Ignore("IsDeleted");
        builder.Entity<User>().Ignore("DeletedAtUtc");
    }
}
