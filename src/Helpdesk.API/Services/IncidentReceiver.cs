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
using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using AutomationTrace;
using AutomationTrace.Contracts;
using Helpdesk.Infrastructure.EmailTemplates;

namespace Helpdesk.API.Services;

public sealed class IncidentReceiver(HelpdeskDbContext db, IIncidentReceiverAuthorization authorization,
    IRequestSender sender, ISupportAccessService support, ITicketNotificationService notifications,
    IHtmlSanitizerService sanitizer, IHtmlToPlainTextConverter converter, IIngressEffectContext ingress,
    MailboxOutboxStore outbox, TimeProvider time,
    InternalIncidentTemplateService? presentations = null, IncidentOriginValidator? origins = null,
    Helpdesk.Infrastructure.ServiceIdentity.IServicePublicSettingsResolver? publicSettings = null,
    ICurrentUserAccessService? queueAccess = null)
{
    public async Task<IResult> CreateAsync(ClaimsPrincipal principal, Guid sourceId, string key, CreateIncidentDto dto,
        HttpContext http, CancellationToken ct, AutomationIncidentCreate? trace = null, string? groupId = null)
    {
        if (groupId != null && (trace == null || !IncidentQueueContract.ValidGroupId(groupId)))
            return IncidentReceiverContract.Problem(400, "invalid-queue-request");
        var operation = groupId == null ? IncidentReceiverOperation.Create : IncidentReceiverOperation.CreateQueued;
        var approved = await authorization.ResolveAsync(principal, sourceId, operation, ct);
        if (approved is null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
        if (dto.OrganizationId != approved.Organization.Id || dto.CustomerId != approved.Customer.Id)
            return IncidentReceiverContract.Problem(403, "target-not-authorized");
        if (!IncidentReceiverContract.ValidRequest(dto)) return IncidentReceiverContract.Problem(400, "invalid-incident-request");
        if (trace is not null && !AutomationTraceValidation.Create(trace))
            return IncidentReceiverContract.Problem(400, "invalid-incident-context");
        var fingerprint = trace is null ? IncidentReceiverContract.Fingerprint(dto, sanitizer, converter) :
            groupId == null ? AutomationTraceValidation.Fingerprint(trace) : IncidentQueueContract.Fingerprint(trace, groupId);
        var prior = await db.IncidentCreateReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SourceNamespaceId == approved.Source.SourceNamespaceId && x.Key == key, ct);
        if (prior is not null)
        {
            if (prior.AssignedSupportGroupId != null && prior.Fingerprint == fingerprint &&
                !await CanUseQueueAsync(approved, prior.AssignedSupportGroupId, ct))
                return IncidentReceiverContract.Problem(403, "queue-not-authorized");
            return Replay(prior, approved, fingerprint, http);
        }
        var targetError = await ValidateSelectionAsync(groupId == null ? dto.AssignedToId : null,
            IncidentReceiverContract.Categories(dto.CategoryIds), approved, ct);
        if (targetError is not null) return targetError;
        var receiverId = await ReceiverIdAsync(ct);
        if (receiverId is null) return IncidentReceiverContract.Problem(503, "receiver-not-initialized");

        await using var transaction = groupId == null ? await db.Database.BeginTransactionAsync(ct) :
            await db.Database.BeginTransactionAsync(global::System.Data.IsolationLevel.Serializable, ct);
        if (groupId != null)
        {
            var renewed = await authorization.ResolveAsync(principal, sourceId, operation, ct);
            if (renewed == null || renewed.Source.SourceNamespaceId != approved.Source.SourceNamespaceId ||
                !await CanUseQueueAsync(approved, groupId, ct))
                return IncidentReceiverContract.Problem(403, "queue-not-authorized");
            if (!string.IsNullOrWhiteSpace(dto.AssignedToId) && (queueAccess is null ||
                !await AssigneeQueries.CanAssignToQueueAsync(db, queueAccess, TicketType.Incident, approved.Organization.Id, groupId, dto.AssignedToId, ct)))
                return IncidentReceiverContract.Problem(422, "invalid-incident-target", "assignedToId");
        }
        IncidentTemplatePreview? report = null;
        Helpdesk.Shared.Models.Request? originRequest = null;
        if (trace is not null)
        {
            if (trace.Presentation.Mode == "template" && await authorization.ResolveAsync(principal, sourceId,
                    IncidentReceiverOperation.ReadPresentation, ct) is null)
                return IncidentReceiverContract.Problem(403, "incident-template-access-denied");
            var originCheck = await (origins ?? throw new InvalidOperationException("Origin validation is unavailable.")).ValidateAsync(principal, approved, trace.Context, ct);
            if (originCheck.Error is not null) return originCheck.Error;
            originRequest = originCheck.Request;
            try { report = await (presentations ?? throw new InvalidOperationException("Presentation rendering is unavailable."))
                .RenderAsync(approved.Organization.Id, new(trace.Presentation, trace.Context), ct); }
            catch (Exception error) when (error is ArgumentException or Helpdesk.Application.Services.EmailTemplates.TemplateRenderException)
            { return IncidentReceiverContract.Problem(422, "incident-presentation-rejected"); }
        }
        var receipt = new IncidentCreateReceipt
        {
            Id = Guid.NewGuid(), SourceNamespaceId = approved.Source.SourceNamespaceId, Key = key,
            Fingerprint = fingerprint, OrganizationId = approved.Organization.Id, CustomerId = approved.Customer.Id,
            AssignedSupportGroupId = groupId
        };
        db.IncidentCreateReceipts.Add(receipt);
        // Reserve the unique namespace/key before invoking any business handler. An uncommitted reservation is invisible.
        await db.SaveChangesAsync(ct);
        using var effects = ingress.Begin(receipt.Id);
        ingress.OrganizationId = approved.Organization.Id;
        var categories = IncidentReceiverContract.Categories(dto.CategoryIds);
        var cc = IncidentReceiverContract.Cc(dto.CcRecipients);
        cc.Remove(approved.Customer.Email.Trim().ToLowerInvariant());
        // A queued incident can be visible to an operator who has no access to the source request.
        // Keep presentation fields derived from that request in the separately authorized report.
        var subject = groupId == null ? report?.Subject ?? dto.Title : $"Automation {trace!.Context.SourceOutcome}";
        var created = await sender.Send(new CreateIncidentCommand(subject,
            report?.Summary ?? converter.Convert(sanitizer.Sanitize(dto.Description)), dto.Priority, approved.Customer.Id, approved.Organization.Id,
            dto.LinkedAssetIds, dto.Attachments, IncidentReceiverContract.DueDateUtc(dto.DueDate), dto.Impact,
            approved.Customer.Email, cc, dto.AssignedToId) { SuppressUnassignedSupportNotification = groupId != null }, ct);
        if (groupId != null)
            TicketQueueAssignment.Stage(db, created, groupId, created.AssignedToId,
                $"source:{approved.Source.SourceNamespaceId:D}", null, time.GetUtcNow().UtcDateTime);
        db.IncidentCategoryLinks.AddRange(categories.Select(id => new IncidentCategoryLink
            { IncidentId = created.Id, TicketCategoryId = id }));
        await db.SaveChangesAsync(ct);
        // Confirmation is captured by the existing ingress email decorator in the same receipt transaction.
        if (!await notifications.SendNewTicketConfirmationAsync(created, approved.Customer.Email, approved.Customer.Name, cc, ct))
            throw new InvalidOperationException("Incident confirmation could not be durably captured.");
        receipt.IncidentId = created.Id;
        receipt.Location = $"{http.Request.PathBase}/api/v1/incidents/{created.Id}";
        receipt.CommittedAtUtc = time.GetUtcNow();
        TicketRelation? originRelation = null;
        if (trace is not null && report is not null)
        {
            db.Add(new AutomationIncidentReportRecord
            {
                Id = Guid.NewGuid(), IncidentId = created.Id, SourceRequestId = originRequest?.Id,
                Html = report.Html, PlainText = report.PlainText, ContextJson = JsonSerializer.Serialize(trace.Context, AutomationTraceValidation.Json),
                PresentationJson = JsonSerializer.Serialize(trace.Presentation, AutomationTraceValidation.Json),
                FlowRunId = trace.Context.FlowRunId, ActionId = trace.Context.ActionId, CreatedAtUtc = receipt.CommittedAtUtc
            });
            if (originRequest is not null)
            {
                originRelation = TicketRelationService.Map(created, originRequest, Helpdesk.Shared.Enums.TicketRelationType.AutomationFailureOf,
                    "automation", "Automation");
                db.TicketRelations.Add(originRelation);
            }
        }
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
        if (trace is null) receipt.AcceptedJson = JsonSerializer.Serialize(accepted, IncidentReceiverContract.Json);
        else
        {
            var settings = await (publicSettings ?? throw new InvalidOperationException("Public settings are unavailable.")).ResolveAsync(ct);
            var web = settings.Identity.WebBaseUrl.TrimEnd('/');
            var publicLink = Uri.TryCreate(web, UriKind.Absolute, out var publicOrigin) && publicOrigin.Scheme == "https" &&
                publicOrigin.UserInfo.Length == 0 && publicOrigin.Query.Length == 0 && publicOrigin.Fragment.Length == 0
                ? web + "/incidents/" + Uri.EscapeDataString(created.Id) : null;
            var integrationReceipt = new AutomationIncidentReceipt(groupId == null ? AutomationTraceContract.IncidentVersion : IncidentQueueContract.Version,
                receiverId.Value.ToString("D"), approved.Source.SourceNamespaceId.ToString("D"), sourceId.ToString("D"), key,
                fingerprint, "committed", created.Id, created.TrackingId, created.OrganizationId!, created.CustomerId!,
                receipt.CommittedAtUtc, receipt.Location, publicLink, trace.Context.FlowRunId, trace.Context.ActionId,
                new(originRelation is null ? "not-applicable" : "linked", originRelation?.Id.ToString("D"), originRequest?.Id,
                    originRequest is null ? null : trace.Context.Origin!.AttemptId), trace.Presentation);
            if (groupId == null)
                receipt.AcceptedJson = JsonSerializer.Serialize(new { id = created.Id, trackingId = created.TrackingId,
                    organizationId = created.OrganizationId, customerId = created.CustomerId, subject = created.Title,
                    summary = created.Description, integrationReceipt }, AutomationTraceValidation.Json);
            else
            {
                var queuedReceipt = JsonSerializer.SerializeToNode(integrationReceipt, AutomationTraceValidation.Json)!.AsObject();
                queuedReceipt["assignedSupportGroupId"] = groupId;
                receipt.AcceptedJson = JsonSerializer.Serialize(new { id = created.Id, trackingId = created.TrackingId,
                    organizationId = created.OrganizationId, customerId = created.CustomerId, subject = created.Title,
                    summary = created.Description, integrationReceipt = queuedReceipt }, AutomationTraceValidation.Json);
            }
        }
        await outbox.FlushAsync(ct);
        await transaction.CommitAsync(ct);
        http.Response.Headers.Location = receipt.Location;
        return Results.Content(receipt.AcceptedJson, "application/json", statusCode: 201);
    }

    public async Task<IResult> LookupAsync(ClaimsPrincipal principal, Guid sourceId, string key, HttpContext http, CancellationToken ct, bool queued = false)
    {
        var approved = await authorization.ResolveAsync(principal, sourceId,
            queued ? IncidentReceiverOperation.ReadQueuedReceipt : IncidentReceiverOperation.ReadReceipt, ct);
        if (approved is null) return IncidentReceiverContract.Problem(403, "source-not-authorized");
        var receipt = await db.IncidentCreateReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SourceNamespaceId == approved.Source.SourceNamespaceId && x.Key == key, ct);
        if (receipt == null || queued != (receipt.AssignedSupportGroupId != null)) return Results.NotFound();
        if (receipt.AssignedSupportGroupId is { } groupId && !await CanUseQueueAsync(approved, groupId, ct))
            return IncidentReceiverContract.Problem(403, "queue-not-authorized");
        return Replay(receipt, approved, null, http);
    }

    private async Task<bool> CanUseQueueAsync(AuthorizedIncidentSource source, string groupId, CancellationToken ct) =>
        await db.IncidentReceiverQueueRoutes.AnyAsync(route =>
            route.SourceNamespaceId == source.Source.SourceNamespaceId && route.SupportGroupId == groupId, ct) &&
        await TicketQueueAssignment.Destinations(db, source.Organization.Id).AnyAsync(group => group.Id == groupId, ct);

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
        Guid sourceId, string key, CreateIncidentDto dto, HttpContext http, CancellationToken ct, AutomationIncidentCreate? trace = null,
        string? groupId = null)
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
                        .CreateAsync(principal, sourceId, key, dto, http, ct, trace, groupId);
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
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } => true,
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.SerializationFailure } } => true,
        _ => false
    };
}
