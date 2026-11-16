using System.Security.Cryptography;
using System.Text.Json;
using AutomationTrace.Contracts;

namespace AutomationTrace;

/// <summary>Closed, bounded wire validation. Provenance and resource authorization are checked by the owning services.</summary>
public static class AutomationTraceValidation
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool Text(string? value, int maximum, bool optional = false, bool multiline = false) =>
        value is null ? optional : value.Length <= maximum && (optional || !string.IsNullOrWhiteSpace(value)) &&
        !value.Any(c => char.IsControl(c) && !(multiline && c is '\r' or '\n' or '\t'));
    public static bool GuidId(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
    public static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Entity(AutomationEntity? value, bool optional = false) => value is null ? optional :
        Text(value.Id, 256) && Text(value.Name, 256, optional: true);
    private static bool Person(AutomationPerson? value) => value is null || Text(value.Id, 256, optional: true) && Text(value.DisplayName, 256, optional: true);
    private static bool Utc(DateTimeOffset? value) => value is null || value != default(DateTimeOffset) && value.Value.Offset == TimeSpan.Zero;
    private static bool DecimalId(string? value) => value is null || value.Length is > 0 and <= 32 && value[0] != '0' && value.All(char.IsAsciiDigit);
    public static bool SafeLink(AutomationLink? link) => link is not null &&
        link.Kind is "source-request" or "source-portal" or "native-request" or "job-run" or "flow-run" &&
        Text(link.Label, 128) && link.Url is { Length: <= 2048 } && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    public static bool Origin(AutomationOriginContext? origin) => origin is not null &&
        origin.SchemaVersion == AutomationTraceContract.OriginVersion && GuidId(origin.SourceInstanceId) &&
        Entity(origin.Organization) && origin.Request is { } request && Text(request.Id, 256) &&
        Text(request.Reference, 128) && Text(request.Title, 512, optional: true) && Utc(request.SubmittedAtUtc) &&
        Entity(origin.Service, optional: true) && Entity(origin.RequestForm, optional: true) && Entity(origin.Task) &&
        Text(origin.AttemptId, 256) && Text(origin.CorrelationId, 256) && Person(origin.SubmittedBy) && Person(origin.RequestedFor) &&
        origin.Links is { Count: <= 2 } && origin.Links.All(SafeLink);
    public static bool Presentation(IncidentPresentationReference? value) => value is not null &&
        value.Mode is "standard" or "template" && Text(value.TemplateId, 256) && Text(value.Revision, 32) &&
        value.SchemaVersion == AutomationTraceContract.ContextVersion && Digest(value.ContentDigest) && Digest(value.LayoutDigest);
    private static bool Outcome(string? value) => value is "Succeeded" or "Failed" or "Skipped" or "Cancelled" or "TimedOut" or "Unknown" or "Unsupported" or "Denied";
    private static bool EvidenceState(string? value) => value is "available" or "truncated" or "redacted" or "unavailable";
    private static bool Count(int value) => value is >= 0 and <= 1_000_000;
    private static bool Totals(IncidentTargetTotals? value) => value is null ||
        Count(value.Total) && Count(value.Succeeded) && Count(value.Failed) && Count(value.Skipped) && Count(value.Cancelled) &&
        Count(value.TimedOut) && Count(value.Unknown) && value.Total == (long)value.Succeeded + value.Failed + value.Skipped + value.Cancelled + value.TimedOut + value.Unknown;
    public static bool Context(AutomationIncidentContext? value) => value is not null &&
        value.SchemaVersion == AutomationTraceContract.ContextVersion && value.SourceKind is "job" or "request" or "script" or "alert" or "monitoring" or "generic" &&
        (value.Origin is null || Origin(value.Origin)) && DecimalId(value.NativeRequestId) && Entity(value.Definition, optional: true) &&
        Text(value.ExecutionId, 256, optional: true) && GuidId(value.FlowRunId) && GuidId(value.ActionId) && Outcome(value.SourceOutcome) &&
        Utc(value.StartedAtUtc) && Utc(value.CompletedAtUtc) && !(value.StartedAtUtc > value.CompletedAtUtc) && Totals(value.Totals) &&
        (value.JobExecution is null || DecimalId(value.JobExecution.DefinitionId) && value.JobExecution.DefinitionId is not null && DecimalId(value.JobExecution.RunId) && value.JobExecution.RunId is not null) &&
        (value.Supplement is null || Text(value.Supplement.SourceDetails, 12_000, optional: true, multiline: true) &&
            Text(value.Supplement.AiInvestigation, 2_000, optional: true, multiline: true) && Text(value.Supplement.Recovery, 2_500, optional: true, multiline: true)) &&
        Count(value.OmittedTargets) && EvidenceState(value.EvidenceState) && value.Links is { Count: <= 3 } && value.Links.All(SafeLink) &&
        value.Targets is { Count: <= AutomationTraceContract.MaximumTargets } && value.Targets.All(Target) &&
        JsonSerializer.SerializeToUtf8Bytes(value, Json).Length <= AutomationTraceContract.MaximumContextBytes;
    private static bool Target(IncidentTargetEvidence? value) => value is not null && Text(value.Id, 256) &&
        Text(value.Name, 256, optional: true) && Text(value.Hostname, 256, optional: true) && DecimalId(value.ChildRunId) &&
        Outcome(value.Outcome) && Count(value.OmittedSteps) && value.FailedSteps is { Count: <= AutomationTraceContract.MaximumStepsPerTarget } &&
        value.FailedSteps.All(step => step is not null && Text(step.Id, 256, optional: true) && step.Ordinal is not (< 0 or > 1_000_000) &&
            Text(step.Name, 256, optional: true) && Text(step.Type, 128, optional: true) && Outcome(step.Outcome) &&
            Text(step.Reason, 512, optional: true, multiline: true) && Text(step.ErrorExcerpt, 512, optional: true, multiline: true) && EvidenceState(step.DiagnosticState));
    public static bool Create(AutomationIncidentCreate? value) => value is not null &&
        value.ContractVersion == AutomationTraceContract.IncidentVersion && Presentation(value.Presentation) && Context(value.Context) &&
        value.Target is { } target && Text(target.OrganizationId, 64) && Text(target.CustomerId, 64) &&
        Text(target.AssignedToId, 64, optional: true) && target.Priority is >= 0 and <= 3 &&
        target.CategoryIds is { Count: <= 16 } && target.CategoryIds.All(GuidId);
    public static AutomationIncidentCreate Normalize(AutomationIncidentCreate value) => value with
    {
        Target = value.Target with { AssignedToId = string.IsNullOrWhiteSpace(value.Target.AssignedToId) ? null : value.Target.AssignedToId,
            CategoryIds = value.Target.CategoryIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() }
    };
    public static string Fingerprint(AutomationIncidentCreate value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Normalize(value), Json)));
    public static bool NoDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in value.EnumerateObject())
                if (!names.Add(member.Name) || !NoDuplicateMembers(member.Value)) return false;
        }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!NoDuplicateMembers(item)) return false;
        return true;
    }
}
