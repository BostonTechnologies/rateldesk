using System.Data;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Organization;

public static class OrganizationRetentionEndpoint
{
    public static async Task<IResult> GetAsync(string id, HelpdeskDbContext db, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return organization is null ? Results.NotFound() : Results.Ok(await AssessAsync(organization, db, ct));
    }

    public static async Task<IResult> RetireAsync(string id, HelpdeskDbContext db, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var organization = await db.Organizations.AsTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (organization is null) return Results.NotFound();
            var assessment = await AssessAsync(organization, db, ct);
            if (!assessment.CanRetire) return Conflict(assessment);
            if (organization.IsEnabled)
            {
                organization.IsEnabled = false;
                db.ActivityLogs.Add(new ActivityLog { RelatedEntityId = id, Message = "Organization retired with dependent history retained." });
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (Exception failure) when (IsDependencyRace(failure))
        {
            return ChangedDependencies();
        }
    }

    internal static bool IsDependencyRace(Exception failure) => failure is DbUpdateConcurrencyException ||
        failure is Npgsql.PostgresException { SqlState: "40001" or "40P01" } ||
        failure is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } ||
        failure is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23503" or "40001" or "40P01" } or
            Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 787 } or
            Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 } };

    internal static IResult ChangedDependencies() => Results.Conflict(new { code = "organization_dependency_conflict",
        message = "Dependencies changed or are being updated. Refresh the assessment before trying again; retained records have not been discarded." });

    public static IResult Conflict(OrganizationDeletionAssessmentDto assessment) => Results.Conflict(new
    {
        code = "organization_dependency_conflict",
        message = "Review the dependencies below. Retained history can be kept with a disabled organization; resolve active work before retirement.",
        assessment
    });

    public static async Task<OrganizationDeletionAssessmentDto> AssessAsync(Helpdesk.Shared.Models.Organization organization, HelpdeskDbContext db, CancellationToken ct)
    {
        var id = organization.Id;
        var nowMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dependencies = new List<OrganizationDependencyDto>();
        void Add(string code, int count, string message, string action, string? href = null, bool resolve = false)
        {
            if (count > 0) dependencies.Add(new(code, count, message, action, href, resolve));
        }
        var principals = db.Set<ServicePrincipalRegistration>().AsNoTracking().Where(x => x.OrganizationId == id);
        Add("service_principal_retention", await principals.CountAsync(ct), "Principal identities and revoked credential records retain audit attribution and credential denial.", "Keep the organization disabled to retain these identities.");
        Add("service_principal_active", await principals.CountAsync(x => x.Status != "revoked" && x.Status != "expired" && x.Status != "failed", ct), "Service principals still have an active or unfinished registration.", "Revoke or resolve these registrations in Integrations before retirement.", "/admin/integration", true);
        Add("support_relationship", await db.Organizations.CountAsync(x => x.ItSupportOrganizationId == id && x.Id != id, ct), "Other organizations use this support organization.", "Reassign their support routing before deletion or retirement.", "/admin/organizations", true);
        Add("ticket_history", await db.Tickets.CountAsync(x => x.OrganizationId == id, ct), "Ticket history retains its organization identity.", "Keep this organization disabled with its history.");
        Add("request_task_active", await db.RequestTasks.CountAsync(x => x.OrganizationId == id && x.Status == RequestTaskStatus.InProgress, ct), "Request tasks are still in progress.", "Resolve active execution and recovery outcomes before retirement.", "/tasks?customerOrgId=" + Uri.EscapeDataString(id), true);
        Add("user_membership", await db.Users.CountAsync(x => x.OrganizationId == id, ct), "Team records retain membership and historical identity.", "Review Team access and keep this organization disabled when retention is required.", "/admin/users");
        Add("customer_history", await db.Customers.CountAsync(x => x.OrganizationId == id, ct), "Customer records retain their organization identity.", "Use the supported customer access lifecycle or retain this organization disabled.", "/admin/customers");
        Add("role_assignments", await db.ScopedRoleAssignments.CountAsync(x => x.OrganizationId == id, ct), "Scoped role assignments still refer to this organization.", "Review organization membership and role assignments.", "/admin/organizations?section=members&organizationId=" + Uri.EscapeDataString(id));
        Add("owned_roles", await db.Roles.CountAsync(x => x.OwnerOrganizationId == id, ct), "Custom role definitions retain their owning organization.", "Review Roles & Permissions or retain the organization disabled.", "/admin/roles");
        Add("request_forms", await db.RequestForms.IgnoreQueryFilters().CountAsync(x => x.OrganizationId == id, ct), "Request forms retain this organization context.", "Review request services or retain the organization disabled.", "/admin/requests/services");
        Add("datasets", await db.DatasetDefinitions.CountAsync(x => x.OrganizationId == id, ct), "Dataset definitions and their retained rows belong to this organization.", "Use the supported dataset lifecycle or retain the organization disabled.", "/admin/resources/data-management");
        Add("support_coverage", await db.OrganizationSupportCoverages.CountAsync(x => x.IsEnabled && (x.CustomerOrganizationId == id || x.ProviderOrganizationId == id), ct), "Active support coverage uses this organization.", "Review and disable or reassign the affected support routes before retirement.", "/admin/support-routing", true);
        Add("asset_history", await db.Assets.CountAsync(x => x.OrganizationId == id, ct), "Assets retain this organization identity.", "Retain the organization or review assets through their supported lifecycle.", "/assets");
        var mailboxes = await db.EmailInboxSettings.AsNoTracking().Where(x => x.OrganizationId == id).ToListAsync(ct);
        foreach (var mailbox in mailboxes)
        {
            var href = $"/admin/email-settings/{mailbox.Id}/ingestion";
            Add("mailbox_active", !mailbox.Archived || mailbox.Enabled || mailbox.BackgroundSyncEnabled ? 1 : 0, "A mailbox is still active.", "Disable processing and archive the mailbox in Email Settings.", "/admin/email-settings", true);
            Add("mailbox_active_lease", await db.Set<MailboxLease>().CountAsync(x => x.MailboxId == mailbox.Id && x.Owner != null && x.ExpiresUnixMilliseconds > nowMilliseconds, ct), "Mailbox processing holds an active lease.", "Wait for processing to finish and refresh the assessment.", href, true);
            Add("mailbox_retained_activity", await db.Set<InboundMessageReceipt>().CountAsync(x => x.MailboxId == mailbox.Id, ct) + await db.InboundEmailProcessingLogs.CountAsync(x => x.MailboxId == mailbox.Id, ct), "Mailbox receipts and audit history retain replay and mapping evidence.", "Keep this archived source and organization disabled.", href);
            Add("mailbox_rules", await db.InboundEmailRules.CountAsync(x => x.MailboxId == mailbox.Id, ct), "Inbound rules retain a mailbox reference.", "Review mailbox rules through Email Rules or retain the archived source.", "/admin/email-rules");
            var mailboxId = mailbox.Id.ToString("D");
            var dispatchGroup = $"mailbox:{mailbox.Id:N}";
            var effects = db.Set<MailboxOutboxEffect>().AsNoTracking().Where(x => x.Kind == MailboxEffectKind.Email && (x.DispatchGroup == dispatchGroup || x.Payload.Contains(mailboxId)));
            Add("mailbox_retained_delivery", await effects.CountAsync(ct), "Delivery records preserve attempted effects and recipient outcomes.", "Keep delivery history and this archived source retained.", "/admin/pending-emails");
            Add("mailbox_unresolved_delivery", await effects.CountAsync(x => x.State == MailboxEffectState.NeedsReview || x.State == MailboxEffectState.Submitting || x.State == MailboxEffectState.InFlight || x.State == MailboxEffectState.Pending, ct), "Delivery work is pending, in flight or has an uncertain outcome.", "Review each delivery through Pending Emails; reconcile uncertain outcomes before retirement. Do not replay them.", "/admin/pending-emails", true);
            var ingestion = await db.Set<MailboxIngestionState>().AsNoTracking().SingleOrDefaultAsync(x => x.MailboxId == mailbox.Id, ct);
            if (ingestion is not null && HasRetainedIngestion(ingestion))
                Add("mailbox_retained_ingestion_state", 1, "The source cursor and synchronization history preserve the no-replay boundary.", "Keep the source archived and retain this organization disabled.", href);
        }
        return new(dependencies.Count == 0, dependencies.All(x => !x.RequiresResolution), !organization.IsEnabled, dependencies);
    }

    internal static bool HasRetainedIngestion(MailboxIngestionState state) =>
        state.Initialized || state.Cursor is not null || state.CurrentStage != "Never" ||
        state.LastAttemptUnixMilliseconds is not null || state.LastSyncUnixMilliseconds is not null ||
        state.BaselineCompletedUnixMilliseconds is not null || state.SyncRequestedVersion != 0 ||
        state.SyncCompletedVersion != 0 || state.NextRetryUnixMilliseconds is not null ||
        state.LastSyncCommandUnixMilliseconds is not null || state.ErrorCode is not null || state.LastSyncCommandErrorCode is not null;
}
