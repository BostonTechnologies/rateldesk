using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Helpdesk.API.Endpoints.Organization;

/// <summary>Disposes unused archived mailbox configuration in the same transaction as its organization.</summary>
public static class OrganizationDeletionEndpoint
{
    public static async Task<IResult> DeleteAsync(string id, IRepository<Helpdesk.Shared.Models.Organization> organizations,
        HelpdeskDbContext db, CancellationToken ct)
    {
        var organization = await organizations.GetAsync(id);
        if (organization is null) return Results.NotFound();

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var assessment = await OrganizationRetentionEndpoint.AssessAsync(organization, db, ct);
            if (!assessment.CanDelete) return OrganizationRetentionEndpoint.Conflict(assessment);
            var mailboxes = await db.EmailInboxSettings.Where(x => x.OrganizationId == organization.Id).ToListAsync(ct);
            foreach (var mailbox in mailboxes)
            {
                if (!mailbox.Archived || mailbox.Enabled || mailbox.BackgroundSyncEnabled)
                    return Conflict("mailbox_active", mailbox.Id);
                if (await db.Set<InboundMessageReceipt>().AnyAsync(x => x.MailboxId == mailbox.Id, ct) ||
                    await db.InboundEmailProcessingLogs.AnyAsync(x => x.MailboxId == mailbox.Id, ct) ||
                    await db.InboundEmailRules.AnyAsync(x => x.MailboxId == mailbox.Id, ct))
                    return Conflict("mailbox_retained_activity", mailbox.Id);
                var mailboxId = mailbox.Id.ToString("D");
                var dispatchGroup = $"mailbox:{mailbox.Id:N}";
                if (await db.Set<MailboxOutboxEffect>().AnyAsync(x =>
                    x.Kind == MailboxEffectKind.Email &&
                    (x.DispatchGroup == dispatchGroup || x.Payload.Contains(mailboxId)), ct))
                    return Conflict("mailbox_retained_delivery", mailbox.Id);

                var lease = await db.Set<MailboxLease>().SingleOrDefaultAsync(x => x.MailboxId == mailbox.Id, ct);
                if (lease is not null)
                {
                    if (lease.Owner is not null && lease.ExpiresUnixMilliseconds > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        return Conflict("mailbox_active_lease", mailbox.Id);
                    db.Remove(lease);
                }

                var ingestion = await db.Set<MailboxIngestionState>().SingleOrDefaultAsync(x => x.MailboxId == mailbox.Id, ct);
                if (ingestion is not null)
                {
                    if (OrganizationRetentionEndpoint.HasRetainedIngestion(ingestion))
                        return Conflict("mailbox_retained_ingestion_state", mailbox.Id);
                    db.Remove(ingestion);
                }

                var outgoing = await db.Set<MailboxOutgoingSettings>().SingleOrDefaultAsync(x => x.MailboxId == mailbox.Id, ct);
                if (outgoing is not null)
                    db.Remove(outgoing);
                db.EmailInboxSettings.Remove(mailbox);
            }

            await db.SaveChangesAsync(ct);
            if (!await organizations.DeleteAsync(organization.Id)) return Results.NotFound();
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (Exception failure) when (OrganizationRetentionEndpoint.IsDependencyRace(failure))
        {
            return OrganizationRetentionEndpoint.ChangedDependencies();
        }
    }

    private static IResult Conflict(string code, Guid mailboxId) => Results.Conflict(new
    {
        code,
        mailboxId,
        message = "Archived mailbox has retained activity or processing state; resolve it before deleting its organization."
    });
}
