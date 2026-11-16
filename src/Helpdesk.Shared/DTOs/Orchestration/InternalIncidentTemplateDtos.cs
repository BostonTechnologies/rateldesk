using AutomationTrace.Contracts;

namespace Helpdesk.Shared.DTOs.Orchestration;

public sealed record InternalIncidentTemplateDraft(string Id, long Version, string Name, string? OrganizationId,
    string SubjectTemplate, string HtmlTemplate, int? LayoutId, IReadOnlyList<string> SourceKinds,
    bool Retired = false, int PublishedRevision = 0, string Purpose = "netratel-internal-incident");
public sealed record InternalIncidentTemplatePreview(InternalIncidentTemplateDraft Draft, AutomationIncidentContext? Context = null);
public sealed record InternalIncidentTemplateState(long ExpectedVersion, bool Retired);
public sealed record InternalIncidentReportDto(string IncidentId, string IncidentReference, string? SourceRequestId,
    string Html, string PlainText, IncidentPresentationReference Presentation, DateTimeOffset CreatedAtUtc);
