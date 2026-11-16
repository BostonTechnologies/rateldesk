using System.Text.Json.Serialization;

namespace AutomationTrace.Contracts;

// Mirrored byte-for-byte in RatelDesk. IDs on this boundary are strings, including native Int64/UInt64 IDs.
public static class AutomationTraceContract
{
    public const string OriginVersion = "rateldesk.automation-origin.v1";
    public const string ContextVersion = "netratel.incident-context.v1";
    public const string IncidentVersion = "rateldesk.incident-create.v2";
    public const string PresentationVersion = "rateldesk.incident-presentation.v1";
    public const int MaximumContextBytes = 65_536;
    public const int MaximumTargets = 25;
    public const int MaximumStepsPerTarget = 8;
    public const int MaximumExcerptCharacters = 512;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationEntity(string Id, string? Name);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationPerson(string? Id, string? DisplayName);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationLink(string Kind, string Label, string Url);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationSourceRequest(string Id, string Reference, string Title, DateTimeOffset SubmittedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationOriginContext(string SchemaVersion, string SourceInstanceId,
    AutomationEntity Organization, AutomationSourceRequest Request, AutomationEntity? Service,
    AutomationEntity? RequestForm, AutomationEntity Task, string AttemptId, string CorrelationId,
    AutomationPerson? SubmittedBy, AutomationPerson? RequestedFor, IReadOnlyList<AutomationLink> Links);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncidentTargetTotals(int Total, int Succeeded, int Failed, int Skipped,
    int Cancelled, int TimedOut, int Unknown);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncidentFailedStep(string? Id, int? Ordinal, string? Name, string? Type,
    string Outcome, int? ExitCode, string? Reason, string? ErrorExcerpt, string DiagnosticState);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncidentTargetEvidence(string Id, string? Name, string? Hostname,
    string? ChildRunId, string Outcome, IReadOnlyList<IncidentFailedStep> FailedSteps, int OmittedSteps);

// Contains classified, authorized evidence only. It never carries raw business inputs or output-protection identifiers.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationIncidentContext(string SchemaVersion, string SourceKind,
    AutomationOriginContext? Origin, string? NativeRequestId, AutomationEntity? Definition,
    string? ExecutionId, string FlowRunId, string ActionId, string SourceOutcome,
    DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, IncidentTargetTotals? Totals,
    IReadOnlyList<IncidentTargetEvidence> Targets, int OmittedTargets, string EvidenceState,
    IReadOnlyList<AutomationLink> Links)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IncidentPresentationSupplement? Supplement { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutomationJobExecution? JobExecution { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationJobExecution(string DefinitionId, string RunId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncidentPresentationSupplement(string? SourceDetails, string? AiInvestigation, string? Recovery);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IncidentPresentationReference(string Mode, string TemplateId, string Revision,
    string SchemaVersion, string ContentDigest, string LayoutDigest);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationIncidentTarget(string OrganizationId, string CustomerId,
    string? AssignedToId, IReadOnlyList<string> CategoryIds, int Priority);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationIncidentCreate(string ContractVersion, AutomationIncidentTarget Target,
    IncidentPresentationReference Presentation, AutomationIncidentContext Context);

public sealed record IncidentTemplateCatalogueItem(string Id, string Name, string? OrganizationId,
    string Purpose, IReadOnlyList<string> SourceKinds, IncidentPresentationReference Presentation,
    bool Retired, bool Revoked);
public sealed record IncidentTemplatePreviewRequest(IncidentPresentationReference Presentation,
    AutomationIncidentContext Context);
public sealed record IncidentTemplatePreview(string Subject, string Summary, string Html, string PlainText,
    IncidentPresentationReference Presentation);
public sealed record IncidentOriginRelationEvidence(string Status, string? RelationId,
    string? SourceRequestId, string? SourceAttemptId);
public sealed record AutomationIncidentReceipt(string ContractVersion, string ReceiverInstanceId,
    string SourceNamespaceId, string SourceInstanceId, string Key, string Fingerprint, string Outcome,
    string IncidentId, string TrackingId, string OrganizationId, string CustomerId,
    DateTimeOffset CommittedAtUtc, string Location, string? PublicLink,
    string FlowRunId, string ActionId, IncidentOriginRelationEvidence OriginRelation,
    IncidentPresentationReference Presentation);
