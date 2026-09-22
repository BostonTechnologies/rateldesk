using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Email;

public static class InboundEmailRuleEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapInboundEmailRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inbound-email-rules")
            .WithTags("Inbound Email Rules")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async (
            [FromServices] HelpdeskDbContext db,
            [FromQuery] string? tenantId,
            [FromQuery] Guid? mailboxId,
            CancellationToken ct) =>
        {
            var query = db.InboundEmailRules.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                query = query.Where(x => x.TenantId == tenantId);
            }
            if (mailboxId.HasValue)
            {
                query = query.Where(x => x.MailboxId == mailboxId);
            }

            var rules = await query
                .OrderByDescending(x => x.ScopeType == InboundEmailRuleScopeType.Tenant)
                .ThenBy(x => x.Priority)
                .ThenBy(x => x.Name)
                .ToListAsync(ct);
            return Results.Ok(rules.Select(ToDto));
        });

        group.MapGet("/{id}", async ([FromServices] HelpdeskDbContext db, [FromRoute] string id, CancellationToken ct) =>
        {
            var rule = await db.InboundEmailRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return rule is null ? Results.NotFound() : Results.Ok(ToDto(rule));
        });

        group.MapPost("/", async (
            [FromServices] HelpdeskDbContext db,
            [FromBody] CreateInboundEmailRuleRequest request,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var validation = Validate(request.ScopeType, request.TenantId, request.Name, request.Conditions, request.Actions)
                ?? await ValidateBindingAsync(db, request.ScopeType, request.TenantId, request.MailboxId, request.Conditions, request.Enabled, ct);
            if (validation is not null) return validation;

            var now = DateTimeOffset.UtcNow;
            var rule = new InboundEmailRule
            {
                ScopeType = request.ScopeType,
                TenantId = NormalizeTenant(request.ScopeType, request.TenantId),
                MailboxId = request.MailboxId,
                Name = request.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                Enabled = request.Enabled,
                Priority = request.Priority,
                ConditionsJson = Serialize(request.Conditions ?? []),
                ActionsJson = Serialize(request.Actions ?? []),
                StopProcessing = request.StopProcessing,
                CreatedBy = Actor(user),
                UpdatedBy = Actor(user),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.InboundEmailRules.Add(rule);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/inbound-email-rules/{rule.Id}", ToDto(rule));
        });

        group.MapPut("/{id}", async (
            [FromServices] HelpdeskDbContext db,
            [FromRoute] string id,
            [FromBody] UpdateInboundEmailRuleRequest request,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var rule = await db.InboundEmailRules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (rule is null) return Results.NotFound();
            var validation = Validate(request.ScopeType, request.TenantId, request.Name, request.Conditions, request.Actions)
                ?? await ValidateBindingAsync(db, request.ScopeType, request.TenantId, request.MailboxId, request.Conditions, request.Enabled, ct);
            if (validation is not null) return validation;

            rule.ScopeType = request.ScopeType;
            rule.TenantId = NormalizeTenant(request.ScopeType, request.TenantId);
            rule.MailboxId = request.MailboxId;
            rule.Name = request.Name.Trim();
            rule.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            rule.Enabled = request.Enabled;
            rule.Priority = request.Priority;
            rule.ConditionsJson = Serialize(request.Conditions ?? []);
            rule.ActionsJson = Serialize(request.Actions ?? []);
            rule.StopProcessing = request.StopProcessing;
            rule.UpdatedBy = Actor(user);
            rule.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(rule));
        });

        group.MapPost("/{id}/enable", async ([FromServices] HelpdeskDbContext db, [FromRoute] string id, ClaimsPrincipal user, CancellationToken ct) =>
            await SetEnabledAsync(db, id, true, Actor(user), ct));

        group.MapPost("/{id}/disable", async ([FromServices] HelpdeskDbContext db, [FromRoute] string id, ClaimsPrincipal user, CancellationToken ct) =>
            await SetEnabledAsync(db, id, false, Actor(user), ct));

        group.MapPost("/reorder", async (
            [FromServices] HelpdeskDbContext db,
            [FromBody] ReorderInboundEmailRulesRequest request,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var ids = request.Rules.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rules = await db.InboundEmailRules.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
            if (rules.Count != ids.Count)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["rules"] = ["One or more inbound email rules were not found."]
                });
            }

            if (request.Rules.Count != ids.Count || rules.Select(x => (x.ScopeType, x.TenantId, x.MailboxId)).Distinct().Count() != 1)
                return Results.BadRequest(new { message = "Reorder requires distinct rules from one scope, tenant and mailbox bucket." });
            var actor = Actor(user);
            foreach (var rule in rules)
            {
                rule.Priority = request.Rules.First(x => x.Id == rule.Id).Priority;
                rule.UpdatedBy = actor;
                rule.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(rules.OrderBy(x => x.Priority).Select(ToDto));
        });

        group.MapGet("/audit", async (
            [FromServices] HelpdeskDbContext db,
            [FromQuery] string? messageId,
            [FromQuery] string? ticketId,
            [FromQuery] string? tenantId,
            [FromQuery] Guid? mailboxId,
            CancellationToken ct) =>
        {
            var query = db.InboundEmailProcessingLogs.AsNoTracking();
            if (mailboxId.HasValue) query = query.Where(x => x.MailboxId == mailboxId.Value);
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                query = query.Where(x => x.MessageId == messageId);
            }
            if (!string.IsNullOrWhiteSpace(ticketId))
            {
                query = query.Where(x => x.TicketId == ticketId);
            }
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                query = query.Where(x => x.TenantId == tenantId);
            }

            var rows = await query
                .OrderByUtc(db, x => x.CreatedAtUtc, descending: true)
                .Take(200)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(ToAuditDto));
        });
    }

    private static async Task<IResult> SetEnabledAsync(HelpdeskDbContext db, string id, bool enabled, string? actor, CancellationToken ct)
    {
        var rule = await db.InboundEmailRules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (rule is null) return Results.NotFound();
        if (enabled)
        {
            var conditions = Deserialize<List<InboundEmailRuleConditionConfig>>(rule.ConditionsJson);
            var actions = Deserialize<List<InboundEmailRuleActionConfig>>(rule.ActionsJson);
            var validation = Validate(rule.ScopeType, rule.TenantId, rule.Name, conditions, actions)
                ?? await ValidateBindingAsync(db, rule.ScopeType, rule.TenantId, rule.MailboxId, conditions, true, ct);
            if (validation is not null) return validation;
        }
        rule.Enabled = enabled;
        rule.UpdatedBy = actor;
        rule.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(rule));
    }

    private static IResult? Validate(
        InboundEmailRuleScopeType scopeType,
        string? tenantId,
        string name,
        List<InboundEmailRuleConditionConfig>? conditions,
        List<InboundEmailRuleActionConfig>? actions)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(scopeType)) errors["scopeType"] = ["Unsupported scope."];
        if (conditions?.Any(x => !Enum.IsDefined(x.Type)) == true) errors["conditions"] = ["Unsupported condition."];
        if (actions?.Any(x => !Enum.IsDefined(x.Type)) == true) errors["actions"] = ["Unsupported action."];
        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Name is required."];
        }
        if (scopeType == InboundEmailRuleScopeType.Tenant && string.IsNullOrWhiteSpace(tenantId))
        {
            errors["tenantId"] = ["TenantId is required for tenant-scoped inbound email rules."];
        }
        if (scopeType == InboundEmailRuleScopeType.Global && !string.IsNullOrWhiteSpace(tenantId))
        {
            errors["tenantId"] = ["TenantId must be empty for global inbound email rules."];
        }
        if (actions is null || actions.Count == 0)
        {
            errors["actions"] = ["At least one action is required."];
        }
        if (conditions is null || conditions.Count == 0)
        {
            errors["conditions"] = ["At least one condition is required."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static async Task<IResult?> ValidateBindingAsync(HelpdeskDbContext db, InboundEmailRuleScopeType scope,
        string? tenantId, Guid? mailboxId, List<InboundEmailRuleConditionConfig>? conditions, bool enabled, CancellationToken ct)
    {
        if (scope == InboundEmailRuleScopeType.Tenant && !await db.Organizations.AnyAsync(x => x.Id == tenantId, ct))
            return Results.BadRequest(new { message = "Rule organization does not exist." });
        var ids = new HashSet<Guid>();
        if (mailboxId.HasValue) ids.Add(mailboxId.Value);
        foreach (var condition in conditions ?? [])
        {
            if (condition.Type != InboundEmailRuleConditionType.MailboxEquals) continue;
            if (!Guid.TryParse(condition.MailboxId, out var id)) return Results.BadRequest(new { message = "MailboxEquals requires a mailbox ID." });
            ids.Add(id);
        }
        if (ids.Count > 1) return Results.BadRequest(new { message = "Mailbox conditions conflict with the source binding." });
        foreach (var id in ids)
        {
            var mailbox = await db.EmailInboxSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (mailbox is null || mailbox.Archived || enabled && !mailbox.Enabled ||
                scope == InboundEmailRuleScopeType.Tenant && mailbox.OrganizationId is not null && mailbox.OrganizationId != tenantId)
                return Results.BadRequest(new { message = "Mailbox binding is unavailable or belongs to another organization." });
        }
        return null;
    }

    private static InboundEmailRuleDto ToDto(InboundEmailRule rule) => new(
        rule.Id,
        rule.ScopeType,
        rule.TenantId,
        rule.MailboxId,
        rule.Name,
        rule.Description,
        rule.Enabled,
        rule.Priority,
        Deserialize<List<InboundEmailRuleConditionConfig>>(rule.ConditionsJson) ?? [],
        Deserialize<List<InboundEmailRuleActionConfig>>(rule.ActionsJson) ?? [],
        rule.StopProcessing,
        rule.CreatedBy,
        rule.UpdatedBy,
        rule.CreatedAtUtc,
        rule.UpdatedAtUtc);

    private static InboundEmailRuleAuditDto ToAuditDto(InboundEmailProcessingLog log) => new(
        log.Id,
        log.MessageId,
        log.MailboxId,
        log.TenantId,
        log.RuleId,
        log.ActionKey,
        log.Matched,
        log.Status,
        log.TicketId,
        log.Error,
        log.CreatedAtUtc);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(json) ? "[]" : json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? NormalizeTenant(InboundEmailRuleScopeType scopeType, string? tenantId) =>
        scopeType == InboundEmailRuleScopeType.Tenant ? tenantId?.Trim() : null;

    private static string? Actor(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Email) ??
        user.FindFirstValue("email") ??
        user.FindFirstValue("preferred_username") ??
        user.Identity?.Name;
}
