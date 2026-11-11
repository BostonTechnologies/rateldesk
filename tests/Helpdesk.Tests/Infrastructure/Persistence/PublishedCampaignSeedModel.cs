using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

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

/// <summary>Seeds published schemas without applying the current model to historical rows.</summary>
internal class PublishedCampaignSeedContext(HelpdeskDbContext current) : HelpdeskDbContext(
    (DbContextOptions<HelpdeskDbContext>)current.GetService<IDbContextOptions>(),
    new AdminTenantContext(), new HttpContextAccessor())
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        PublishedCampaignSeedModel.Configure(builder);
    }

    private sealed class AdminTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
