using Helpdesk.Shared.Enums;

namespace Helpdesk.Shared.DTOs.EmailRules;

public sealed record InboundEmailRuleDto(
    string Id,
    InboundEmailRuleScopeType ScopeType,
    string? TenantId,
    Guid? MailboxId,
    string Name,
    string? Description,
    bool Enabled,
    int Priority,
    List<InboundEmailRuleConditionConfig> Conditions,
    List<InboundEmailRuleActionConfig> Actions,
    bool StopProcessing,
    string? CreatedBy,
    string? UpdatedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateInboundEmailRuleRequest(
    InboundEmailRuleScopeType ScopeType,
    string? TenantId,
    Guid? MailboxId,
    string Name,
    string? Description,
    bool Enabled,
    int Priority,
    List<InboundEmailRuleConditionConfig>? Conditions,
    List<InboundEmailRuleActionConfig>? Actions,
    bool StopProcessing);

public sealed record UpdateInboundEmailRuleRequest(
    InboundEmailRuleScopeType ScopeType,
    string? TenantId,
    Guid? MailboxId,
    string Name,
    string? Description,
    bool Enabled,
    int Priority,
    List<InboundEmailRuleConditionConfig>? Conditions,
    List<InboundEmailRuleActionConfig>? Actions,
    bool StopProcessing);

public sealed record ReorderInboundEmailRulesRequest(List<InboundEmailRulePriorityDto> Rules);

public sealed record InboundEmailRulePriorityDto(string Id, int Priority);

public sealed record InboundEmailRuleAuditDto(
    string Id,
    string MessageId,
    Guid? MailboxId,
    string? TenantId,
    string? RuleId,
    string ActionKey,
    bool Matched,
    InboundEmailProcessingStatus Status,
    string? TicketId,
    string? Error,
    DateTimeOffset CreatedAtUtc);

public sealed record InboundEmailRuleConditionConfig(
    InboundEmailRuleConditionType Type,
    string? MailboxId = null,
    List<string>? Permissions = null);

public sealed record InboundEmailAuditQuery(
    string? Search = null, string? MessageId = null, string? TicketId = null,
    string? TenantId = null, Guid? MailboxId = null, string? RuleId = null,
    InboundEmailProcessingStatus? Status = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, string Sort = "created", bool Descending = true,
    int Page = 0, int PageSize = 25);

public sealed record InboundEmailAuditRow(InboundEmailRuleAuditDto Event,
    string RuleName, string OrganizationName, string MailboxName,
    string? TicketTrackingId, string? TicketRoute);

public sealed record InboundEmailAuditPage(IReadOnlyList<InboundEmailAuditRow> Items,
    int Total, int Page, int PageSize);

public sealed record InboundEmailRuleActionConfig(
    InboundEmailRuleActionType Type,
    string? ActionKey = null);
