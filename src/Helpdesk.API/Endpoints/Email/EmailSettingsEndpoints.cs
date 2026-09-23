using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Email;

public static class EmailSettingsEndpoints
{
    public static void MapEmailSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        MapGroup(app.MapGroup("/api/v1/email-settings"));
        MapGroup(app.MapGroup("/api/email-settings").ExcludeFromDescription());
    }

    private static void MapGroup(RouteGroupBuilder group)
    {
        group.RequireAuthorization("HelpdeskAdmin").WithTags("Email Settings");
        group.MapGet("/worker", async (MailboxWorkerPolicy policy, CancellationToken ct) =>
            Results.Ok(await policy.GetStatusAsync(ct)));
        group.MapPost("/worker", async ([FromBody] SetMailboxWorkerRequest request,
            MailboxWorkerPolicy policy, CancellationToken ct) =>
        {
            if (!request.Confirmed)
                return Results.BadRequest(new { message = "Confirm the instance mailbox worker change." });
            var status = await policy.SetRunningAsync(request.Running, ct);
            return request.Running && !status.DeploymentPermitsIngestion
                ? Results.Conflict(new { message = "EmailIngestion:Enabled=false blocks ingestion at deployment level.", status })
                : Results.Ok(status);
        });
        group.MapGet("/", async (HelpdeskDbContext db, CancellationToken ct) => Results.Ok(
            (await db.EmailInboxSettings.AsNoTracking().ToListAsync(ct)).Select(MailboxSettingsService.ToDto)));
        group.MapGet("/{id:guid}", async (Guid id, HelpdeskDbContext db, CancellationToken ct) =>
            await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) is { } mailbox
                ? Results.Ok(MailboxSettingsService.ToDto(mailbox)) : Results.NotFound());
        group.MapGet("/{id:guid}/outgoing", async (Guid id, MailboxOutgoingSettingsService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } outgoing ? Results.Ok(outgoing) : Results.NotFound());
        group.MapPut("/{id:guid}/outgoing", async (Guid id, [FromBody] MailboxOutgoingSettingsRequest request,
            MailboxOutgoingSettingsService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.SaveAsync(id, request, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "Outgoing configuration changed; reload before saving." }); }
        });
        group.MapPost("/{id:guid}/outgoing/test", async (Guid id, HelpdeskDbContext db,
            SmtpMailboxSender smtp, CancellationToken ct) =>
        {
            var outgoing = await db.Set<MailboxOutgoingSettings>().SingleOrDefaultAsync(x => x.MailboxId == id, ct);
            if (outgoing is null) return Results.Conflict(new { message = "Save outgoing settings before testing." });
            if (outgoing.Transport == MailboxOutgoingTransport.Graph)
                return Results.Ok(new MailboxConnectionTest(false,
                    "Graph send authorization cannot be verified by a connection-only check. Use Send test email after setup."));
            var result = await smtp.TestAsync(outgoing, ct);
            outgoing.LastTestUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            outgoing.TestedVersion = outgoing.Version;
            outgoing.LastTestCode = result.ErrorCode ?? result.Status;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailboxConnectionTest(result.Status == "Authenticated",
                result.Status == "Authenticated" ? "SMTP TLS and authentication succeeded; no message was sent."
                    : $"SMTP connection or authentication failed ({result.ErrorCode})."));
        });
        group.MapPost("/", async ([FromBody] MailboxSettingsRequest request, HelpdeskDbContext db, MailboxSettingsService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request.Id != Guid.Empty) return Results.BadRequest(new { message = "Create requires an empty ID; use PUT to edit." });
            try
            {
                var mailbox = await service.PrepareAsync(request, null, ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await InboundTenantRouter.LockOrganizationAsync(db, mailbox.OrganizationId, ct);
                if (await AssignmentExistsAsync(db, mailbox, ct)) return Results.Conflict(new { message = "An assignment already exists, including disabled assignments." });
                db.EmailInboxSettings.Add(mailbox);
                db.Set<MailboxLease>().Add(new() { MailboxId = mailbox.Id });
                db.Set<MailboxIngestionState>().Add(new() { MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey });
                AddConfigurationAudit(db, user, mailbox, "created");
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Created($"/api/v1/email-settings/{mailbox.Id}", MailboxSettingsService.ToDto(mailbox));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { message = "Mailbox assignment or connection already exists." }); }
        });
        group.MapPut("/{id:guid}", async (Guid id, [FromBody] MailboxSettingsRequest request, HelpdeskDbContext db, MailboxSettingsService service, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var existing = await db.EmailInboxSettings.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (existing is null) return Results.NotFound();
            try
            {
                var mailbox = await service.PrepareAsync(request, existing, ct);
                mailbox.Version++;
                db.Entry(existing).CurrentValues.SetValues(mailbox);
                AddConfigurationAudit(db, user, mailbox, "updated");
                await db.SaveChangesAsync(ct);
                return Results.Ok(MailboxSettingsService.ToDto(existing));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
            catch (DbUpdateException) { return Results.Conflict(new { message = "Configuration changed; reload before saving." }); }
        });
        group.MapPost("/test", TestDraftAsync);
        group.MapPost("/{id:guid}/test", async (Guid id, HelpdeskDbContext db, IEnumerable<IInboundMailboxAdapter> adapters, CancellationToken ct) =>
        {
            var mailbox = await db.EmailInboxSettings.SingleOrDefaultAsync(x => x.Id == id, ct);
            return mailbox is null ? Results.NotFound() : await TestAsync(mailbox, db, adapters, true, ct);
        });
        group.MapPost("/{id:guid}/enable", (Guid id, HelpdeskDbContext db, ClaimsPrincipal user, CancellationToken ct) => SetEnabledAsync(id, true, db, user, ct));
        group.MapPost("/{id:guid}/disable", (Guid id, HelpdeskDbContext db, ClaimsPrincipal user, CancellationToken ct) => SetEnabledAsync(id, false, db, user, ct));
        group.MapPost("/{id:guid}/archive", async (Guid id, [FromBody] ArchiveMailboxRequest request, HelpdeskDbContext db, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (!request.Confirmed) return Results.BadRequest(new { message = "Confirm archive/revert to global." });
            var mailbox = await db.EmailInboxSettings.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (mailbox is null) return Results.NotFound();
            if (mailbox.Version != request.Version) return Results.Conflict();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                mailbox.Archived = true;
                mailbox.Enabled = mailbox.BackgroundSyncEnabled = false;
                mailbox.Version++;
                AddConfigurationAudit(db, user, mailbox, "archived");
                // Match receipt lock order: existing mailbox configuration, then organization.
                await db.SaveChangesAsync(ct);
                await InboundTenantRouter.LockOrganizationAsync(db, mailbox.OrganizationId, ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(MailboxSettingsService.ToDto(mailbox));
            }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "Configuration changed; reload before archiving." }); }
        });
        group.MapGet("/effective", async (HelpdeskDbContext db, CancellationToken ct) =>
        {
            var mailboxes = await db.EmailInboxSettings.AsNoTracking().Where(x => !x.Archived).ToListAsync(ct);
            var global = mailboxes.SingleOrDefault(x => x.Scope == MailboxScope.Global);
            var organizations = await db.Organizations.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            return Results.Ok(organizations.Select(org =>
            {
                var dedicated = mailboxes.SingleOrDefault(x => x.OrganizationId == org.Id);
                var effective = dedicated ?? global;
                return new { org.Id, org.Name, MailboxId = effective?.Id,
                    Status = dedicated is not null ? dedicated.Enabled && dedicated.BackgroundSyncEnabled ? "Dedicated" : "Paused dedicated"
                        : global is not null ? "Inherited global" : "No ingress configured" };
            }));
        });
        group.MapGet("/{id:guid}/diagnostics", async (Guid id, HelpdeskDbContext db, MailboxWorkerPolicy policy, CancellationToken ct) =>
        {
            var state = await db.Set<MailboxIngestionState>().AsNoTracking().Where(x => x.MailboxId == id)
                .Select(x => new MailboxIngestionDiagnosticsDto(x.MailboxId, x.Initialized,
                    x.LastTestUnixMilliseconds, x.TestedVersion, x.LastSyncUnixMilliseconds,
                    x.NextRetryUnixMilliseconds, x.ErrorCode, x.Cursor != null)).SingleOrDefaultAsync(ct);
            var receipts = await db.Set<InboundMessageReceipt>().AsNoTracking().Where(x => x.MailboxId == id)
                .OrderByDescending(x => x.CreatedUnixMilliseconds).Take(100)
                .Select(x => new MailboxReceiptDiagnosticsDto(x.Id, x.Outcome, x.Reason,
                    x.OrganizationId, x.TicketId, x.CreatedUnixMilliseconds, x.Attempts, x.Acknowledged)).ToListAsync(ct);
            return Results.Ok(new MailboxDiagnosticsResponse(state, receipts, await policy.GetMailboxStatusAsync(id, ct)));
        });
        group.MapPost("/{id:guid}/receipts/{receiptId:guid}/retry", RetryReceiptAsync);
    }

    internal static async Task<IResult> RetryReceiptAsync(Guid id, Guid receiptId, HelpdeskDbContext db, CancellationToken ct)
    {
        var receipt = await db.Set<InboundMessageReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == receiptId && x.MailboxId == id, ct);
        if (receipt is null) return Results.NotFound();
        if (receipt.Outcome is not (InboundReceiptOutcome.NeedsReview or InboundReceiptOutcome.RetryableFailure) || receipt.ProtectedEnvelope.Length == 0)
            return Results.Conflict(new { message = "This receipt cannot be retried." });
        var changed = await db.Set<InboundMessageReceipt>().Where(x => x.Id == receiptId && x.MailboxId == id &&
            x.Outcome == receipt.Outcome && x.Attempts == receipt.Attempts && x.UpdatedUnixMilliseconds == receipt.UpdatedUnixMilliseconds &&
            x.ProtectedEnvelope != string.Empty)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Outcome, InboundReceiptOutcome.Pending)
                .SetProperty(x => x.Attempts, 0).SetProperty(x => x.Reason, (string?)null)
                .SetProperty(x => x.UpdatedUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ct);
        return changed == 1 ? Results.Accepted() : Results.Conflict(new { message = "Receipt changed; reload before retrying." });
    }

    private static Task<bool> AssignmentExistsAsync(HelpdeskDbContext db, EmailInboxSettings mailbox, CancellationToken ct) =>
        db.EmailInboxSettings.AnyAsync(x => !x.Archived && x.Scope == mailbox.Scope && x.OrganizationId == mailbox.OrganizationId, ct);

    private static async Task<IResult> SetEnabledAsync(Guid id, bool enabled, HelpdeskDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var mailbox = await db.EmailInboxSettings.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (mailbox is null) return Results.NotFound();
        if (mailbox.Archived || (enabled && (mailbox.Authentication == MailboxAuthentication.Password ? mailbox.Password : mailbox.ClientSecret).Length == 0))
            return Results.BadRequest(new { message = "Assigned mailbox and credentials are required." });
        mailbox.Enabled = enabled;
        mailbox.Version++;
        AddConfigurationAudit(db, user, mailbox, enabled ? "enabled" : "disabled");
        try
        {
            await db.SaveChangesAsync(ct);
            return Results.Ok(MailboxSettingsService.ToDto(mailbox));
        }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "Configuration changed; reload before saving." }); }
        catch (DbUpdateException) { return Results.Conflict(new { message = "Mailbox assignment or connection already exists." }); }
    }

    private static void AddConfigurationAudit(HelpdeskDbContext db, ClaimsPrincipal user, EmailInboxSettings mailbox, string operation) =>
        db.ActivityLogs.Add(new ActivityLog
        {
            UserId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? user.Identity?.Name ?? "unknown",
            RelatedEntityId = mailbox.Id.ToString("D"),
            // Configuration strings may contain credentials or connection details. Audit
            // only controlled enums, flags and version; never serialize requests/entities.
            Message = $"Mailbox configuration {operation}. Provider={mailbox.Provider}; Scope={mailbox.Scope}; Version={mailbox.Version}; Enabled={mailbox.Enabled}; BackgroundSync={mailbox.BackgroundSyncEnabled}."
        });

    private static async Task<IResult> TestDraftAsync([FromBody] MailboxSettingsRequest request, HelpdeskDbContext db,
        MailboxSettingsService service, IEnumerable<IInboundMailboxAdapter> adapters, CancellationToken ct)
    {
        var existing = request.Id == Guid.Empty ? null : await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.Id, ct);
        if (request.Id != Guid.Empty && existing is null) return Results.NotFound();
        try { return await TestAsync(await service.PrepareAsync(request, existing, ct), db, adapters, false, ct); }
        catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException) { return Results.Conflict(new { message = "Reload the saved configuration before testing." }); }
    }

    private static async Task<IResult> TestAsync(EmailInboxSettings mailbox, HelpdeskDbContext db,
        IEnumerable<IInboundMailboxAdapter> adapters, bool saved, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var result = await adapters.Single(x => x.Provider == mailbox.Provider).TestAsync(mailbox, timeout.Token);
            if (saved && result.Success)
            {
                var state = await db.Set<MailboxIngestionState>().SingleAsync(x => x.MailboxId == mailbox.Id, ct);
                state.LastTestUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                state.TestedVersion = mailbox.Version;
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(result);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Results.Ok(new MailboxConnectionTest(false, "Connection test failed. Check credentials, TLS, folder access and operator egress policy."));
        }
    }
}
public sealed record ArchiveMailboxRequest(long Version, bool Confirmed);
public sealed record SetMailboxWorkerRequest(bool Running, bool Confirmed);
