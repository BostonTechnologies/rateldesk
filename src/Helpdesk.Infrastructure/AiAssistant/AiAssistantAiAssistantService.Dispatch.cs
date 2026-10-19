using System.Data;
using Helpdesk.Application.AiAssistant;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Infrastructure.AiAssistant;

public sealed partial class AiAssistantAiAssistantService
{
    public async Task<AiInvestigationInvocationDto> DispatchAsync(string ticketId, string ticketType, DispatchAiInvestigationDto request, string actor, CancellationToken ct)
    {
        var instruction = request.OperatorAssistanceRequest?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("Operator assistance request is required.");
        if (instruction.Length > 16000) throw new ArgumentException("Operator assistance request may contain at most 16000 characters.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 || request.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("An idempotency key of 1–128 characters without control characters is required.");
        if (request.ConfigurationId == Guid.Empty) throw new ArgumentException("An eligible webhook configuration is required.");
        if (string.IsNullOrWhiteSpace(actor)) throw new UnauthorizedAccessException("An authenticated operator is required.");

        var queued = await QueueDispatchWithRetryAsync(ticketId, ticketType, request, instruction, actor, ct);
        if (queued.Entry is null) return ToDto(queued.Invocation);
        // Neither queue retries nor repeated idempotency keys enter the transport
        // path. Queue ownership commits before any event or provider request escapes.
        await Publish(queued.Ticket.Id, queued.Entry);
        await DispatchAiAssistantAsync(queued.Invocation, queued.Configuration!, queued.Ticket, ct);
        return ToDto(queued.Invocation);
    }

    private async Task<QueuedDispatch> QueueDispatchWithRetryAsync(string ticketId, string ticketType,
        DispatchAiInvestigationDto request, string instruction, string actor, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await QueueDispatchAsync(ticketId, ticketType, request, instruction, actor, ct);
            }
            catch (Exception error) when (IsDispatchQueueConflict(error))
            {
                // Only database queue transactions are retried. The losing transaction
                // rolls back before a fresh read; no remote effect has been attempted.
                db.ChangeTracker.Clear();
                if (attempt == 2)
                    throw new AiInvestigationDispatchConflictException("AI dispatch changed concurrently. Read the ticket investigation worklog and retry the exact same idempotency key only for the same request.");
            }
        }
        throw new InvalidOperationException("AI dispatch queue retry limit was exceeded.");
    }

    private async Task<QueuedDispatch> QueueDispatchAsync(string ticketId, string ticketType,
        DispatchAiInvestigationDto request, string instruction, string actor, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var ticket = await TicketAsync(ticketId, ticketType, ct);
        var org = ticket.OrganizationId ?? throw new ArgumentException("Ticket organization is required.");
        var area = Area(ticketType);
        if (db.Database.IsRelational() && await db.Organizations
            .Where(organization => organization.Id == org)
            .ExecuteUpdateAsync(update => update.SetProperty(organization => organization.Name, organization => organization.Name), ct) != 1)
            throw new ArgumentException("Ticket organization is unavailable.");

        // Idempotency is organization-wide. Fence that organization rather than just
        // one ticket, so a competing key cannot be claimed for another ticket/config.
        var existing = await db.AiInvestigationInvocations.AsNoTracking()
            .SingleOrDefaultAsync(invocation => invocation.OrganizationId == org && invocation.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.TicketId != ticket.Id || existing.TicketArea != area || existing.ConfigurationId != request.ConfigurationId ||
                existing.OperatorAssistanceRequest != instruction || existing.RequestedByUserId != actor)
                throw new AiInvestigationDispatchConflictException("This idempotency key is already bound to a different AI dispatch. It cannot change ticket, configuration, operator or assistance request.");
            return new(ticket, null, existing, null);
        }
        var configuration = await db.AiAssistantWebhookConfigurations.SingleOrDefaultAsync(candidate => candidate.Id == request.ConfigurationId &&
            candidate.OrganizationId == org && candidate.IsEnabled && !candidate.IsArchived, ct)
            ?? throw new ArgumentException("Configuration is not eligible.");
        if (!configuration.TicketAreas.Contains(area)) throw new ArgumentException("Configuration is not eligible for this ticket area.");
        var invocation = new AiInvestigationInvocation
        {
            OrganizationId = org, TicketId = ticket.Id, TicketArea = area, ConfigurationId = configuration.Id,
            RequestedByUserId = actor, OperatorAssistanceRequest = instruction,
            CorrelationId = Guid.NewGuid().ToString("N"), IdempotencyKey = request.IdempotencyKey
        };
        var entry = new AiInvestigationWorklogEntry
        {
            InvocationId = invocation.Id, OrganizationId = org, TicketId = ticket.Id, TicketArea = area,
            Source = AiInvestigationSource.Operator, Status = AiInvestigationStatus.Queued,
            Message = "AI assistance requested by operator.", CorrelationId = invocation.CorrelationId
        };
        db.AiInvestigationInvocations.Add(invocation);
        db.AiInvestigationWorklogEntries.Add(entry);
        db.AiAssistantWebhookAuditRecords.Add(new()
        {
            OrganizationId = org, ConfigurationId = configuration.Id, InvocationId = invocation.Id,
            Action = "dispatch-queued", Actor = actor
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(ticket, configuration, invocation, entry);
    }

    private static bool IsDispatchQueueConflict(Exception error) => error switch
    {
        DbUpdateConcurrencyException => true,
        PostgresException postgres => postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected ||
            postgres is { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_AiInvestigationInvocations_OrganizationId_IdempotencyKey" },
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6 || sqlite.SqliteExtendedErrorCode == 2067,
        DbUpdateException { InnerException: { } inner } => IsDispatchQueueConflict(inner),
        // Npgsql's execution strategy can wrap an actual serialization failure.
        // The wrapper alone is never retryable; its provider cause must match above.
        InvalidOperationException { InnerException: { } inner } => IsDispatchQueueConflict(inner),
        _ => false
    };

    private sealed record QueuedDispatch(Ticket Ticket, AiAssistantWebhookConfiguration? Configuration,
        AiInvestigationInvocation Invocation, AiInvestigationWorklogEntry? Entry);
}
