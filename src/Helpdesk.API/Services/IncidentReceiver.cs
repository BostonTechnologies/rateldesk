using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.API.Services;

public sealed class IncidentReceiver(HelpdeskDbContext db, IIncidentReceiverAuthorization authorization,
    IRequestSender sender, ISupportAccessService support, ITicketNotificationService notifications,
    IHtmlSanitizerService sanitizer, IHtmlToPlainTextConverter converter, IIngressEffectContext ingress,
    MailboxOutboxStore outbox, TimeProvider time)
{
    public async Task<IResult> CreateAsync(ClaimsPrincipal principal, Guid sourceId, string key, CreateIncidentDto dto,
        HttpContext http, CancellationToken ct)
    {
        var approved = await authorization.ResolveAsync(principal, sourceId, IncidentReceiverOperation.Create, ct);
        if (approved is null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
        if (dto.OrganizationId != approved.Organization.Id || dto.CustomerId != approved.Customer.Id)
            return IncidentReceiverContract.Problem(403, "target-not-authorized");
        if (!IncidentReceiverContract.ValidRequest(dto)) return IncidentReceiverContract.Problem(400, "invalid-incident-request");
        var fingerprint = IncidentReceiverContract.Fingerprint(dto, sanitizer, converter);
        var prior = await db.IncidentCreateReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SourceNamespaceId == approved.Source.SourceNamespaceId && x.Key == key, ct);
        if (prior is not null) return Replay(prior, approved, fingerprint, http);
        var targetError = await ValidateSelectionAsync(dto.AssignedToId, IncidentReceiverContract.Categories(dto.CategoryIds), approved, ct);
        if (targetError is not null) return targetError;
        var receiverId = await ReceiverIdAsync(ct);
        if (receiverId is null) return IncidentReceiverContract.Problem(503, "receiver-not-initialized");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receipt = new IncidentCreateReceipt
        {
            Id = Guid.NewGuid(), SourceNamespaceId = approved.Source.SourceNamespaceId, Key = key,
            Fingerprint = fingerprint, OrganizationId = approved.Organization.Id, CustomerId = approved.Customer.Id
        };
        db.IncidentCreateReceipts.Add(receipt);
        // Reserve the unique namespace/key before invoking any business handler. An uncommitted reservation is invisible.
        await db.SaveChangesAsync(ct);
        using var effects = ingress.Begin(receipt.Id);
        ingress.OrganizationId = approved.Organization.Id;
        var categories = IncidentReceiverContract.Categories(dto.CategoryIds);
        var cc = IncidentReceiverContract.Cc(dto.CcRecipients);
        cc.Remove(approved.Customer.Email.Trim().ToLowerInvariant());
        var created = await sender.Send(new CreateIncidentCommand(dto.Title,
            converter.Convert(sanitizer.Sanitize(dto.Description)), dto.Priority, approved.Customer.Id, approved.Organization.Id,
            dto.LinkedAssetIds, dto.Attachments, IncidentReceiverContract.DueDateUtc(dto.DueDate), dto.Impact,
            approved.Customer.Email, cc, dto.AssignedToId), ct);
        db.IncidentCategoryLinks.AddRange(categories.Select(id => new IncidentCategoryLink
            { IncidentId = created.Id, TicketCategoryId = id }));
        await db.SaveChangesAsync(ct);
        // Confirmation is captured by the existing ingress email decorator in the same receipt transaction.
        if (!await notifications.SendNewTicketConfirmationAsync(created, approved.Customer.Email, approved.Customer.Name, cc, ct))
            throw new InvalidOperationException("Incident confirmation could not be durably captured.");
        receipt.IncidentId = created.Id;
        receipt.Location = $"{http.Request.PathBase}/api/v1/incidents/{created.Id}";
        receipt.CommittedAtUtc = time.GetUtcNow();
        var accepted = new IntegrationIncidentDto
        {
            OrganizationId = created.OrganizationId, Id = created.Id, TrackingId = created.TrackingId,
            Subject = created.Title, State = created.State, Priority = created.Priority,
            UpdatedAt = created.UpdatedAt ?? created.CreatedAt, CustomerOrgName = approved.Organization.Name,
            CustomerId = approved.Customer.Id, CustomerName = approved.Customer.Name, CustomerEmail = approved.Customer.Email,
            LastReplierName = created.LastReplierName, RequesterEmail = created.RequesterEmail,
            CcRecipients = created.CcRecipients, AssignedToId = created.AssignedToId, CategoryIds = categories,
            IntegrationReceipt = new(IncidentReceiverContract.Version, receiverId.Value.ToString("D"),
                approved.Source.SourceNamespaceId.ToString("D"), sourceId.ToString("D"), key, fingerprint, "committed",
                created.Id, created.TrackingId, created.OrganizationId, created.CustomerId, receipt.CommittedAtUtc, receipt.Location)
        };
        receipt.AcceptedJson = JsonSerializer.Serialize(accepted, IncidentReceiverContract.Json);
        await outbox.FlushAsync(ct);
        await transaction.CommitAsync(ct);
        http.Response.Headers.Location = receipt.Location;
        return Results.Content(receipt.AcceptedJson, "application/json", statusCode: 201);
    }

    public async Task<IResult> LookupAsync(ClaimsPrincipal principal, Guid sourceId, string key, HttpContext http, CancellationToken ct)
    {
        var approved = await authorization.ResolveAsync(principal, sourceId, IncidentReceiverOperation.ReadReceipt, ct);
        if (approved is null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
        var receipt = await db.IncidentCreateReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SourceNamespaceId == approved.Source.SourceNamespaceId && x.Key == key, ct);
        return receipt is null ? Results.NotFound() : Replay(receipt, approved, null, http);
    }

    private static IResult Replay(IncidentCreateReceipt receipt, AuthorizedIncidentSource approved, string? fingerprint, HttpContext http)
    {
        if (receipt.OrganizationId != approved.Organization.Id || receipt.CustomerId != approved.Customer.Id)
            return IncidentReceiverContract.Problem(403, "target-not-authorized");
        if (fingerprint is not null && receipt.Fingerprint != fingerprint)
            return IncidentReceiverContract.Problem(409, "idempotency-payload-conflict");
        http.Response.Headers.Location = receipt.Location;
        http.Response.Headers["Idempotency-Replayed"] = "true";
        return Results.Content(receipt.AcceptedJson, "application/json", statusCode: 200);
    }

    public Task<Guid?> ReceiverIdAsync(CancellationToken ct) => db.InstanceInitializations.AsNoTracking()
        .Where(x => x.Id == InstanceInitialization.SingletonId).Select(x => (Guid?)x.InstanceId).SingleOrDefaultAsync(ct);

    public async Task<IResult?> ValidateSelectionAsync(string? assignee, List<Guid> categories, AuthorizedIncidentSource approved, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(assignee) && !await support.CanUserSupportOrganizationAsync(assignee, approved.Organization.Id, ct))
            return IncidentReceiverContract.Problem(422, "invalid-incident-target", "assignedToId");
        var found = await db.TicketCategories.AsNoTracking().Where(x => categories.Contains(x.Id) && x.IsActive &&
            (x.Type == Helpdesk.Shared.Enums.TicketCategoryType.Incident || x.Type == Helpdesk.Shared.Enums.TicketCategoryType.Service) &&
            (x.TenantId == null || x.TenantId.ToString() == approved.Organization.Id)).Select(x => x.Id).ToListAsync(ct);
        return found.Count == categories.Count ? null : IncidentReceiverContract.Problem(422, "invalid-incident-target", "categoryIds");
    }

    /// <summary>Execution strategy and losing-writer retries always use fresh scoped contexts and captured-effect buffers.</summary>
    public static async Task<IResult> ExecuteCreateAsync(IServiceProvider services, ClaimsPrincipal principal,
        Guid sourceId, string key, CreateIncidentDto dto, HttpContext http, CancellationToken ct)
    {
        var strategy = services.GetRequiredService<HelpdeskDbContext>().Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                await using var scope = services.CreateAsyncScope();
                try
                {
                    return await scope.ServiceProvider.GetRequiredService<IncidentReceiver>()
                        .CreateAsync(principal, sourceId, key, dto, http, ct);
                }
                catch (Exception error) when (attempt < 3 && IsCompetingWriter(error))
                {
                    // The failed context/transaction is disposed before the next winner read.
                    await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), ct);
                }
            }
        });
    }

    private static bool IsCompetingWriter(Exception error) => error switch
    {
        DbUpdateException { InnerException: PostgresException { SqlState: "23505", ConstraintName: "IX_IncidentCreateReceipts_SourceNamespaceId_Key" } } => true,
        DbUpdateException { InnerException: SqliteException sqlite } => sqlite.SqliteErrorCode == 5 ||
            sqlite.SqliteExtendedErrorCode == 2067 && sqlite.Message.Contains("IncidentCreateReceipts", StringComparison.Ordinal),
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6,
        _ => false
    };
}
