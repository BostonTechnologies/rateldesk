using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using Helpdesk.Application.AiAssistant;
using Helpdesk.Application.Services.AI;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.AiAssistant;

public sealed partial class AiAssistantAiAssistantService(HelpdeskDbContext db, ITenantContext tenantContext, IAiInvestigationEventBus eventBus, IHttpClientFactory httpClientFactory, ISecretProtector secretProtector) : IAiAssistantAiAssistantService
{
    public async Task<IReadOnlyList<AiAssistantWebhookConfigurationDto>> ListConfigurationsAsync(string organizationId, CancellationToken ct)
    {
        var org = await ResolveOrganizationIdAsync(organizationId, ct);
        return (await db.AiAssistantWebhookConfigurations.Where(x => x.OrganizationId == org).OrderBy(x => x.Name).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<AiAssistantWebhookConfigurationDto> UpsertConfigurationAsync(Guid? id, UpsertAiAssistantWebhookConfigurationDto r, string actor, CancellationToken ct)
    {
        if (!IsAllowedEndpoint(r.Endpoint, out var endpointError)) throw new ArgumentException(endpointError);
        if (r.TicketAreas.Count == 0) throw new ArgumentException("At least one ticket area is required.");
        var org = await ResolveOrganizationIdAsync(r.OrganizationId, ct);
        var entity = id.HasValue ? await db.AiAssistantWebhookConfigurations.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct) : null;
        if (id.HasValue && entity is null) throw new KeyNotFoundException("Webhook configuration not found.");
        entity ??= new AiAssistantWebhookConfiguration { OrganizationId = org, CreatedByUserId = actor };
        entity.Name = r.Name.Trim(); entity.TicketAreas = r.TicketAreas.Distinct().ToList(); entity.Endpoint = r.Endpoint.Trim(); entity.RouteName = r.RouteName?.Trim(); entity.PromptTemplate = r.PromptTemplate?.Trim(); entity.PermittedInputsSchemaJson = r.PermittedInputsSchemaJson; entity.TimeoutSeconds = Math.Clamp(r.TimeoutSeconds, 1, 120); entity.MaxBodyBytes = Math.Clamp(r.MaxBodyBytes, 1024, 262144); entity.IsEnabled = r.IsEnabled; entity.UpdatedUtc = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(r.SigningSecret)) entity.SigningSecretProtected = secretProtector.Protect(r.SigningSecret.Trim());
        if (!id.HasValue) db.AiAssistantWebhookConfigurations.Add(entity);
        db.AiAssistantWebhookAuditRecords.Add(new() { OrganizationId = org, ConfigurationId = entity.Id, Action = id.HasValue ? "updated" : "created", Actor = actor });
        await db.SaveChangesAsync(ct); return ToDto(entity);
    }
    public async Task SetConfigurationStateAsync(Guid id, string organizationId, bool enabled, bool archived, string actor, CancellationToken ct)
    {
        var org = await ResolveOrganizationIdAsync(organizationId, ct);
        var entity = await db.AiAssistantWebhookConfigurations.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, ct) ?? throw new KeyNotFoundException();
        entity.IsEnabled = enabled; entity.IsArchived = archived; entity.UpdatedUtc = DateTimeOffset.UtcNow;
        db.AiAssistantWebhookAuditRecords.Add(new() { OrganizationId = entity.OrganizationId, ConfigurationId = id, Action = archived ? "archived" : enabled ? "enabled" : "disabled", Actor = actor }); await db.SaveChangesAsync(ct);
    }
    public async Task<IReadOnlyList<AiAssistantWebhookConfigurationDto>> GetEligibleAsync(string ticketId, string ticketType, CancellationToken ct)
    {
        var ticket = await TicketAsync(ticketId, ticketType, ct); var area = Area(ticketType); var org = ticket.OrganizationId;
        return (await db.AiAssistantWebhookConfigurations.Where(x => x.OrganizationId == org && x.IsEnabled && !x.IsArchived).ToListAsync(ct)).Where(x => x.TicketAreas.Contains(area)).Select(ToDto).ToList();
    }
    public async Task<IReadOnlyList<AiInvestigationWorklogEntryDto>> GetWorklogAsync(string ticketId, string ticketType, CancellationToken ct)
    {
        var ticket = await TicketAsync(ticketId, ticketType, ct); return (await db.AiInvestigationWorklogEntries.Where(x => x.OrganizationId == ticket.OrganizationId && x.TicketId == ticketId && x.TicketArea == Area(ticketType)).OrderByUtc(db, x => x.OccurredUtc, descending: true).ToListAsync(ct)).Select(ToDto).ToList();
    }
    public async Task<bool> AppendMcpWorklogAsync(Guid invocationId, AppendAiInvestigationWorklogDto update, CancellationToken ct)
    {
        var area = update.TicketType?.ToLowerInvariant() switch
        {
            "incidents" => AiAssistantTicketArea.Incidents,
            "requests" => AiAssistantTicketArea.Requests,
            "changes" => AiAssistantTicketArea.Changes,
            _ => (AiAssistantTicketArea?)null
        };
        if (area is null || !Enum.IsDefined(update.Status) || string.IsNullOrWhiteSpace(update.EventId) ||
            string.IsNullOrWhiteSpace(update.TicketId) || string.IsNullOrWhiteSpace(update.CorrelationId) ||
            string.IsNullOrWhiteSpace(update.Message)) return false;

        var invocation = await db.AiInvestigationInvocations.SingleOrDefaultAsync(x => x.Id == invocationId && x.CorrelationId == update.CorrelationId, ct);
        // The MCP endpoint is authenticated with the existing AI-agent policy. Its tenant
        // scope is bound to this persisted invocation (and its ticket/correlation), not to
        // a tenant claim supplied by the MCP identity or request payload.
        if (invocation is null || update.TicketId != invocation.TicketId || area != invocation.TicketArea) return false;
        var previous = await FindCallbackAsync(update.EventId, ct);
        if (previous is not null) return MatchesCallback(previous, invocation, update);
        if (IsTerminal(invocation.Status)) return false;

        var entry = new AiInvestigationWorklogEntry
        {
            InvocationId = invocation.Id, OrganizationId = invocation.OrganizationId, TicketId = invocation.TicketId,
            TicketArea = invocation.TicketArea, Source = AiInvestigationSource.AiAssistant, Status = update.Status,
            Severity = update.Severity, Message = Redact(update.Message), MetadataJson = update.MetadataJson,
            ArtifactReferencesJson = update.ArtifactReferencesJson, CallbackEventId = update.EventId,
            CorrelationId = update.CorrelationId, OccurredUtc = update.OccurredUtc
        };
        var relational = db.Database.IsRelational();
        await using var transaction = relational && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        if (relational)
        {
            // Predicate and event insert share a transaction. A concurrent terminal
            // callback cannot be overwritten after the earlier tracked read.
            var updated = await db.AiInvestigationInvocations.Where(candidate =>
                    candidate.Id == invocation.Id && candidate.OrganizationId == invocation.OrganizationId &&
                    candidate.TicketId == update.TicketId && candidate.TicketArea == area &&
                    candidate.CorrelationId == update.CorrelationId &&
                    candidate.Status != AiInvestigationStatus.Completed && candidate.Status != AiInvestigationStatus.Failed &&
                    candidate.Status != AiInvestigationStatus.Cancelled)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.Status, update.Status)
                    .SetProperty(candidate => candidate.AiAssistantRunReference, candidate => update.RunReference ?? candidate.AiAssistantRunReference), ct);
            if (updated == 0)
            {
                if (transaction is not null) await transaction.RollbackAsync(ct);
                var concurrent = await FindCallbackAsync(update.EventId, ct);
                return concurrent is not null && MatchesCallback(concurrent, invocation, update);
            }
        }
        else
        {
            invocation.Status = update.Status;
            invocation.AiAssistantRunReference = update.RunReference ?? invocation.AiAssistantRunReference;
        }
        db.AiInvestigationWorklogEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (transaction is not null && IsCallbackUniqueViolation(exception))
        {
            await transaction.RollbackAsync(ct);
            db.Entry(entry).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            await db.Entry(invocation).ReloadAsync(ct);
            var concurrent = await FindCallbackAsync(update.EventId, ct);
            // The database's existing global event-ID index remains authoritative.
            // A collision is successful only when this exact bound event exists.
            if (concurrent is null) throw;
            return MatchesCallback(concurrent, invocation, update);
        }
        if (relational) await db.Entry(invocation).ReloadAsync(ct);
        await Publish(invocation.TicketId, entry);
        return true;
    }

    private Task<AiInvestigationWorklogEntry?> FindCallbackAsync(string eventId, CancellationToken ct) =>
        db.AiInvestigationWorklogEntries.AsNoTracking().SingleOrDefaultAsync(entry => entry.CallbackEventId == eventId, ct);

    // The adapter supplies the current time when occurredUtc is omitted. A retry
    // retains the event's timestamp. RunReference is invocation metadata, not an
    // event snapshot; matching retries cannot rewrite its current value.
    private static bool MatchesCallback(AiInvestigationWorklogEntry entry, AiInvestigationInvocation invocation, AppendAiInvestigationWorklogDto update) =>
        entry.InvocationId == invocation.Id && entry.OrganizationId == invocation.OrganizationId &&
        entry.TicketId == invocation.TicketId && entry.TicketArea == invocation.TicketArea &&
        entry.CorrelationId == invocation.CorrelationId && entry.Source == AiInvestigationSource.AiAssistant &&
        entry.Status == update.Status && entry.Severity == update.Severity && entry.Message == Redact(update.Message) &&
        entry.MetadataJson == update.MetadataJson && entry.ArtifactReferencesJson == update.ArtifactReferencesJson;

    private static bool IsTerminal(AiInvestigationStatus status) =>
        status is AiInvestigationStatus.Completed or AiInvestigationStatus.Failed or AiInvestigationStatus.Cancelled;

    private static bool IsCallbackUniqueViolation(DbUpdateException exception) => exception.InnerException is
        Npgsql.PostgresException { SqlState: "23505", ConstraintName: "IX_AiInvestigationWorklogEntries_CallbackEventId" } or
        Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 };
    private async Task<Ticket> TicketAsync(string id, string type, CancellationToken ct)
    {
        var ticket = await db.Set<Ticket>().IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (ticket is null || Area(type) switch { AiAssistantTicketArea.Incidents => ticket is not Incident, AiAssistantTicketArea.Requests => ticket is not Request, _ => ticket is not Change }) throw new KeyNotFoundException("Ticket not found.");
        return ticket;
    }
    private string RequiredTenant() => tenantContext.TenantId ?? throw new UnauthorizedAccessException("Tenant claim is required.");
    private async Task<string> ResolveOrganizationIdAsync(string requestedOrganizationId, CancellationToken ct)
    {
        var organizationId = ValidateOrganizationId(requestedOrganizationId);
        if (!tenantContext.IsHelpdeskAdmin && !string.Equals(organizationId, RequiredTenant(), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Organization scope is not permitted.");
        var exists = await db.Organizations.AnyAsync(x => x.Id == organizationId && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct);
        if (!exists) throw new ArgumentException("Select an enabled tenant organization.");
        return organizationId;
    }
    private static string ValidateOrganizationId(string? organizationId) => !string.IsNullOrWhiteSpace(organizationId) ? organizationId.Trim() : throw new ArgumentException("Tenant organization is required.");
    private static AiAssistantTicketArea Area(string type) => type.ToLowerInvariant() switch { "incidents" => AiAssistantTicketArea.Incidents, "requests" => AiAssistantTicketArea.Requests, "changes" => AiAssistantTicketArea.Changes, _ => throw new ArgumentException("Unsupported ticket type.") };
    public static bool IsAllowedEndpoint(string? value, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)) { error = "Endpoint must be an absolute URL."; return false; }
        if (endpoint.Scheme == Uri.UriSchemeHttps) return true;
        if (endpoint.Scheme == Uri.UriSchemeHttp && IsPrivateOrLoopbackAddress(endpoint.Host)) return true;
        error = "Endpoint must use HTTPS, except for a private or loopback IP address used by a trusted internal AiAssistant deployment.";
        return false;
    }
    private static bool IsPrivateOrLoopbackAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168));
    }
    private static string Redact(string message) => message.Length > 4000 ? message[..4000] : message;
    private async Task DispatchAiAssistantAsync(AiInvestigationInvocation invocation, AiAssistantWebhookConfiguration config, Ticket ticket, CancellationToken ct)
    {
        var secret = ResolveSecret(config);
        if (string.IsNullOrWhiteSpace(secret)) { invocation.FailureMessage = "Webhook signing secret is unavailable."; await PersistDispatchFailureAsync(invocation, ct); return; }
        var ticketType = invocation.TicketArea.ToString().ToLowerInvariant();
        var body = JsonSerializer.Serialize(new
        {
            schemaVersion = "v1",
            eventType = "helpdesk.ai-assistance.requested",
            invocationId = invocation.Id,
            correlationId = invocation.CorrelationId,
            tenantId = invocation.OrganizationId,
            ticket = new { id = ticket.Id, type = ticketType, ticket.TrackingId, ticket.Title, ticket.Description, state = ticket.State.ToString(), priority = ticket.Priority.ToString() },
            operatorAssistanceRequest = invocation.OperatorAssistanceRequest,
            mcpContext = new
            {
                tool = "helpdesk_ai_assistant",
                operation = "report",
                invocationId = invocation.Id,
                ticketId = ticket.Id,
                ticketType,
                correlationId = invocation.CorrelationId,
                tenantId = invocation.OrganizationId,
                requiredFields = new[] { "eventId", "status", "message" },
                allowedStatuses = Enum.GetNames<AiInvestigationStatus>()
            },
            prompt = config.PromptTemplate,
            mcpInstructions = "The ticket object is ticket context. operatorAssistanceRequest is the authoritative operator instruction. Use mcpContext verbatim when calling helpdesk_ai_assistant report. Generate one unique eventId per update and supply status and message. Use other MCP tools only within their existing authorization and confirmation policies."
        });
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            invocation.DispatchAttempts = attempt;
            using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Webhook-Signature", signature); request.Headers.Add("X-Webhook-Event", "helpdesk.ai-assistance.requested"); request.Headers.Add("X-Webhook-Delivery", invocation.IdempotencyKey); request.Headers.Add("X-Correlation-ID", invocation.CorrelationId);
            try { var client = httpClientFactory.CreateClient("AiAssistantWebhook"); client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds); using var response = await client.SendAsync(request, ct); if (response.IsSuccessStatusCode) { invocation.DispatchedUtc = DateTimeOffset.UtcNow; config.LastDispatchUtc = invocation.DispatchedUtc; await db.SaveChangesAsync(ct); return; } invocation.FailureMessage = $"AiAssistant returned {(int)response.StatusCode}."; }
            catch (Exception ex) when (attempt < 3) { invocation.FailureMessage = ex.Message; await Task.Delay(TimeSpan.FromSeconds(attempt), ct); }
            catch (Exception ex) { invocation.FailureMessage = ex.Message; }
        }
        config.LastHealthMessage = invocation.FailureMessage;
        await PersistDispatchFailureAsync(invocation, ct);
    }

    private async Task PersistDispatchFailureAsync(AiInvestigationInvocation invocation, CancellationToken ct)
    {
        // Delivery attempts and health remain useful even when a callback already
        // completed the run while the outbound HTTP response was pending.
        await db.SaveChangesAsync(ct);
        if (db.Database.IsRelational())
        {
            await db.AiInvestigationInvocations.Where(candidate => candidate.Id == invocation.Id &&
                    candidate.Status != AiInvestigationStatus.Completed && candidate.Status != AiInvestigationStatus.Failed &&
                    candidate.Status != AiInvestigationStatus.Cancelled)
                .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, AiInvestigationStatus.Failed), ct);
            await db.Entry(invocation).ReloadAsync(ct);
            return;
        }
        await db.Entry(invocation).ReloadAsync(ct);
        if (IsTerminal(invocation.Status)) return;
        invocation.Status = AiInvestigationStatus.Failed;
        await db.SaveChangesAsync(ct);
    }
    private string? ResolveSecret(AiAssistantWebhookConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.SigningSecretProtected)) return secretProtector.Unprotect(configuration.SigningSecretProtected);
        return configuration.SecretReference?.StartsWith("env:", StringComparison.OrdinalIgnoreCase) == true ? Environment.GetEnvironmentVariable(configuration.SecretReference[4..]) : null;
    }
    private async ValueTask Publish(string ticketId, AiInvestigationWorklogEntry entry) { var dto = ToDto(entry); if (eventBus is AiInvestigationEventBus bus) await bus.PublishAsync(ticketId, dto); else await eventBus.PublishAsync(dto); }
    private static AiAssistantWebhookConfigurationDto ToDto(AiAssistantWebhookConfiguration x) => new(x.Id, x.Name, x.IsEnabled, x.IsArchived, x.TicketAreas, x.Endpoint, x.RouteName, x.PromptTemplate, x.PermittedInputsSchemaJson, x.TimeoutSeconds, x.MaxBodyBytes, !string.IsNullOrWhiteSpace(x.SigningSecretProtected) || !string.IsNullOrWhiteSpace(x.SecretReference), x.LastDispatchUtc, x.LastHealthMessage);
    private static AiInvestigationInvocationDto ToDto(AiInvestigationInvocation x) => new(x.Id, x.Status, x.CorrelationId, x.AiAssistantRunReference, x.FailureMessage, x.CreatedUtc);
    private static AiInvestigationWorklogEntryDto ToDto(AiInvestigationWorklogEntry x) => new(x.Id, x.InvocationId, x.Source, x.Status, x.Severity, x.Message, x.MetadataJson, x.ArtifactReferencesJson, x.CorrelationId, x.OccurredUtc);
}
