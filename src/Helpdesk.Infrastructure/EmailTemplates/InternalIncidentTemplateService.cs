using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AutomationTrace;
using AutomationTrace.Contracts;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.EmailTemplates;

/// <summary>RatelDesk owns both preview and acceptance rendering. No email or ticket operations occur here.</summary>
public sealed partial class InternalIncidentTemplateService(HelpdeskDbContext db, ITemplateEngine engine,
    IHtmlSanitizerService sanitizer, IHtmlToPlainTextConverter plain, TimeProvider time)
{
    public const string Purpose = "netratel-internal-incident";
    public static readonly string[] Sources = ["job", "request", "script", "alert", "monitoring", "generic"];
    public const string DefaultSubject = "{{RequestReference}} {{DefinitionName}} — {{SourceOutcome}}";
    public const string DefaultHtml = """
        <h3>Automation execution report</h3><p>{{Summary}}</p>
        <dl><dt>Source outcome</dt><dd>{{SourceOutcome}}</dd>
        {{#if RequestReference}}<dt>Source request</dt><dd>{{RequestReference}} — {{RequestTitle}}</dd>{{/if}}
        {{#if ServiceName}}<dt>Service</dt><dd>{{ServiceName}}</dd>{{/if}}
        {{#if SubmittedBy}}<dt>Submitted by</dt><dd>{{SubmittedBy}}</dd>{{/if}}
        {{#if RequestedFor}}<dt>Requested for</dt><dd>{{RequestedFor}}</dd>{{/if}}
        {{#if DefinitionName}}<dt>Job / definition</dt><dd>{{DefinitionName}}</dd>{{/if}}
        <dt>Started</dt><dd>{{StartedAtUtc}}</dd><dt>Completed</dt><dd>{{CompletedAtUtc}}</dd><dt>Duration</dt><dd>{{Duration}}</dd>
        <dt>Targets</dt><dd>{{TargetSummary}}</dd><dt>Evidence</dt><dd>{{EvidenceState}}</dd></dl>
        {{#if Failures}}<table><thead><tr><th>Affected targets</th><th>Activity / step</th><th>Outcome</th><th>Child exit code</th><th>Reason / permitted diagnostic</th></tr></thead>
        <tbody>{{#each Failures}}<tr><td>{{Targets}}</td><td>{{Ordinal}} {{Name}} ({{Type}})</td><td>{{Outcome}}</td><td>{{ExitCode}}</td><td>{{Reason}}<pre>{{ErrorExcerpt}}</pre></td></tr>{{/each}}</tbody></table>{{/if}}
        {{#if Omitted}}<p>{{Omitted}}</p>{{/if}}
        {{#if SourceDetails}}<details><summary>Source details and technical evidence</summary><pre>{{SourceDetails}}</pre></details>{{/if}}
        {{#if AiInvestigation}}<h4>AI investigation</h4><pre>{{AiInvestigation}}</pre>{{/if}}
        {{#if Recovery}}<h4>Recovery</h4><pre>{{Recovery}}</pre>{{/if}}
        <ul>{{#each Links}}<li><a href="{{Url}}">{{Label}}</a></li>{{/each}}</ul>
        <details><summary>Technical details</summary><p>Native request {{NativeRequestId}} · execution {{ExecutionId}} · flow {{FlowRunId}} · action {{ActionId}}</p>
        {{#if OriginAttempt}}<p>Source attempt {{OriginAttempt}}</p>{{/if}}
        <table><thead><tr><th>Target</th><th>Child run</th><th>Outcome</th></tr></thead><tbody>{{#each Nodes}}<tr><td>{{Name}}</td><td>{{ChildRunId}}</td><td>{{Outcome}}</td></tr>{{/each}}</tbody></table></details>
        """;
    private static readonly TemplateEngineOptions Options = new() { MaxDepth = 8, MaxOutputChars = 65_536, UnknownTokenBehavior = UnknownTokenBehavior.Throw };
    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex SubjectToken();
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static readonly string[] SafeSubjectTokens = ["RequestReference", "RequestTitle", "ServiceName", "DefinitionName", "SourceKind", "SourceOutcome", "TargetSummary"];
    private static IncidentPresentationReference Reference(string id, int revision, string subject, string html, string layout, string[] sources, string mode = "template") =>
        new(mode, id, revision.ToString(CultureInfo.InvariantCulture), AutomationTraceContract.ContextVersion,
            Hash(JsonSerializer.Serialize(new { subject, html, sources }, AutomationTraceValidation.Json)), Hash(layout));
    public static IncidentPresentationReference Standard => Reference("system-standard", 1, DefaultSubject, DefaultHtml, "", Sources, "standard");
    private static IEnumerable<(string Id, string Name, string[] Sources)> Defaults() =>
        new[] { ("system-standard", "Standard human summary", Sources), ("system-job-failure", "Job execution failure", new[] { "job" }),
            ("system-request-failure", "Request automation failure", new[] { "request" }), ("system-script-failure", "Script / task failure", new[] { "script" }),
            ("system-alert", "Monitoring alert", new[] { "alert", "monitoring" }), ("system-execution-failure", "Generic execution failure", Sources) };
    public static InternalIncidentTemplateDraft SystemDraft(string id)
    {
        var source = Defaults().FirstOrDefault(x => x.Id == id);
        if (source.Id is null) throw new ArgumentException("template-default-unavailable");
        return new("", 0, source.Name, null, DefaultSubject, DefaultHtml, null, source.Sources);
    }
    public async Task<IncidentTemplatePreview> PreviewDraftAsync(InternalIncidentTemplateDraft draft, AutomationIncidentContext? context, CancellationToken ct)
    {
        var sample = context ?? SyntheticContext(draft.SourceKinds.FirstOrDefault() ?? "job");
        if (!AutomationTraceValidation.Context(sample)) throw new ArgumentException("invalid-presentation-context");
        var (html, layout) = await ExpandLayoutAsync(draft.HtmlTemplate, draft.LayoutId, ct);
        return Render(draft.SubjectTemplate, html, sample,
            Reference("draft-preview", 1, draft.SubjectTemplate, html, layout, draft.SourceKinds.ToArray()));
    }
    public async Task<IReadOnlyList<IncidentTemplateCatalogueItem>> CatalogueAsync(string org, CancellationToken ct)
    {
        var result = Defaults().Select(x => new IncidentTemplateCatalogueItem(x.Id, x.Name, null, Purpose, x.Sources,
            Reference(x.Id, 1, DefaultSubject, DefaultHtml, "", x.Sources, x.Id == "system-standard" ? "standard" : "template"), false, false)).ToList();
        var revisions = await (from revision in db.Set<InternalIncidentTemplateRevision>().AsNoTracking()
                               join template in db.Set<InternalIncidentTemplateRecord>().AsNoTracking() on revision.TemplateId equals template.Id
                               where template.OrganizationId == org && template.Purpose == Purpose && !template.Retired &&
                                     revision.Revision == template.PublishedRevision && !revision.Revoked
                               orderby revision.Name, revision.TemplateId
                               select revision).Take(250).ToArrayAsync(ct);
        result.AddRange(revisions.Select(x => new IncidentTemplateCatalogueItem(x.TemplateId.ToString("D"), x.Name, org, Purpose,
            ReadSources(x.SourceKindsJson), new("template", x.TemplateId.ToString("D"), x.Revision.ToString(CultureInfo.InvariantCulture),
                AutomationTraceContract.ContextVersion, x.ContentDigest, x.LayoutDigest), false, false)));
        return result;
    }

    public async Task<IncidentTemplatePreview> RenderAsync(string org, IncidentTemplatePreviewRequest input, CancellationToken ct)
    {
        if (!AutomationTraceValidation.Context(input.Context) || !AutomationTraceValidation.Presentation(input.Presentation))
            throw new ArgumentException("invalid-presentation-context");
        var builtIn = Defaults().FirstOrDefault(x => x.Id == input.Presentation.TemplateId);
        string subject, html;
        if (builtIn.Id is not null)
        {
            var expected = Reference(builtIn.Id, 1, DefaultSubject, DefaultHtml, "", builtIn.Sources, builtIn.Id == "system-standard" ? "standard" : "template");
            if (input.Presentation != expected || !builtIn.Sources.Contains(input.Context.SourceKind))
                throw new ArgumentException("presentation-revision-incompatible");
            subject = DefaultSubject; html = DefaultHtml;
        }
        else
        {
            if (!Guid.TryParseExact(input.Presentation.TemplateId, "D", out var id) ||
                !int.TryParse(input.Presentation.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
                throw new ArgumentException("presentation-revision-unavailable");
            var saved = await db.Set<InternalIncidentTemplateRevision>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.TemplateId == id && x.Revision == revision && x.OrganizationId == org, ct)
                ?? throw new ArgumentException("presentation-revision-unavailable");
            if (saved.Revoked) throw new ArgumentException("presentation-revision-revoked");
            if (input.Presentation.Mode != "template" || saved.ContentDigest != input.Presentation.ContentDigest ||
                saved.LayoutDigest != input.Presentation.LayoutDigest || !ReadSources(saved.SourceKindsJson).Contains(input.Context.SourceKind))
                throw new ArgumentException("presentation-revision-incompatible");
            subject = saved.SubjectTemplate; html = saved.ExpandedHtml;
        }
        return Render(subject, html, input.Context, input.Presentation);
    }

    public IncidentTemplatePreview Render(string subject, string html, AutomationIncidentContext context, IncidentPresentationReference presentation)
    {
        ValidateTemplates(subject, html);
        var safe = SafeModel(context);
        var publicSubject = plain.Convert(sanitizer.Sanitize(engine.Render(subject, safe, Options))).ReplaceLineEndings(" ").Trim();
        if (publicSubject.Length == 0) publicSubject = "Automation " + context.SourceOutcome;
        publicSubject = publicSubject[..Math.Min(200, publicSubject.Length)];
        var model = new Dictionary<string, object?>(safe);
        model["Summary"] = safe["TargetSummary"];
        model["SubmittedBy"] = context.Origin is null ? null : context.Origin.SubmittedBy?.DisplayName ?? "Unavailable in retained evidence";
        model["RequestedFor"] = context.Origin is null ? null : context.Origin.RequestedFor?.DisplayName ?? "Unavailable in retained evidence";
        model["EvidenceState"] = context.EvidenceState;
        model["NativeRequestId"] = context.NativeRequestId;
        model["ExecutionId"] = context.ExecutionId;
        model["FlowRunId"] = context.FlowRunId; model["ActionId"] = context.ActionId;
        model["OriginAttempt"] = context.Origin?.AttemptId;
        model["SourceDetails"] = context.Supplement?.SourceDetails;
        model["AiInvestigation"] = context.Supplement?.AiInvestigation;
        model["Recovery"] = context.Supplement?.Recovery;
        model["StartedAtUtc"] = context.StartedAtUtc?.ToString("u", CultureInfo.InvariantCulture);
        model["CompletedAtUtc"] = context.CompletedAtUtc?.ToString("u", CultureInfo.InvariantCulture);
        model["Duration"] = context.StartedAtUtc is { } started && context.CompletedAtUtc is { } completed
            ? (completed - started).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds" : "Unavailable";
        model["Nodes"] = context.Targets.Select(target => new Dictionary<string, object?>
        {
            ["Name"] = target.Hostname ?? target.Name ?? target.Id,
            ["ChildRunId"] = target.ChildRunId ?? "Unavailable", ["Outcome"] = target.Outcome
        }).ToArray();
        var groups = context.Targets.SelectMany(target => target.FailedSteps.Select(step => (target, step)))
            .GroupBy(x => new { x.step.Ordinal, x.step.Name, x.step.Type, x.step.Outcome, x.step.ExitCode, x.step.Reason, x.step.ErrorExcerpt }).ToArray();
        model["Failures"] = groups.Take(12).Select(g => new Dictionary<string, object?>
        {
            ["Targets"] = string.Join(", ", g.Select(x => x.target.Hostname ?? x.target.Name ?? x.target.Id).Distinct()) + $" ({g.Select(x => x.target.Id).Distinct().Count()} affected)",
            ["Ordinal"] = g.Key.Ordinal, ["Name"] = g.Key.Name, ["Type"] = g.Key.Type, ["Outcome"] = g.Key.Outcome,
            ["ExitCode"] = g.Key.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable", ["Reason"] = g.Key.Reason, ["ErrorExcerpt"] = g.Key.ErrorExcerpt
        }).ToArray();
        var links = (context.Origin?.Links ?? []).Concat(context.Links).Where(AutomationTraceValidation.SafeLink).ToArray();
        model["Links"] = links.Select(x => new Dictionary<string, object?> { ["Label"] = x.Label, ["Url"] = x.Url }).ToArray();
        var omitted = context.OmittedTargets + context.Targets.Sum(x => x.OmittedSteps) + Math.Max(0, groups.Length - 12);
        model["Omitted"] = omitted == 0 ? null : $"{omitted} additional target, step or error groups omitted by report limits. Open the authorized execution for full retained evidence.";
        var sanitized = sanitizer.Sanitize(engine.Render(html, model, Options));
        var document = new HtmlParser().ParseDocument(sanitized);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (!links.Any(x => x.Url == anchor.GetAttribute("href"))) anchor.RemoveAttribute("href");
            else { anchor.SetAttribute("rel", "noopener noreferrer"); anchor.SetAttribute("target", "_blank"); }
        }
        foreach (var element in document.QuerySelectorAll("img,style,link,iframe")) element.Remove();
        foreach (var element in document.QuerySelectorAll("[style]")) element.RemoveAttribute("style");
        var result = document.Body?.InnerHtml ?? "";
        if (Encoding.UTF8.GetByteCount(result) > 65_536) throw new ArgumentException("presentation-output-limit");
        var textDocument = new HtmlParser().ParseDocument(result);
        foreach (var cell in textDocument.QuerySelectorAll("th,td")) cell.AppendChild(textDocument.CreateTextNode(" | "));
        foreach (var block in textDocument.QuerySelectorAll("br,p,div,h1,h2,h3,h4,h5,h6,li,tr,dd,dt,pre,summary"))
            block.AppendChild(textDocument.CreateTextNode("\n"));
        return new(publicSubject, safe["TargetSummary"]?.ToString() ?? "Automation execution", result,
            plain.Convert(textDocument.Body?.InnerHtml ?? result), presentation);
    }
    private static Dictionary<string, object?> SafeModel(AutomationIncidentContext context) => new()
    {
        ["RequestReference"] = context.Origin?.Request.Reference, ["RequestTitle"] = context.Origin?.Request.Title,
        ["ServiceName"] = context.Origin?.Service?.Name, ["DefinitionName"] = context.Definition?.Name,
        ["SourceKind"] = context.SourceKind, ["SourceOutcome"] = context.SourceOutcome,
        ["TargetSummary"] = context.Totals is { } counts ? $"{counts.Failed} of {counts.Total} targets failed; {counts.Succeeded} succeeded, {counts.Skipped} skipped, {counts.TimedOut} timed out, {counts.Cancelled} cancelled, {counts.Unknown} unknown." : "Source outcome: " + context.SourceOutcome
    };
    private static void ValidateTemplates(string subject, string html)
    {
        if (!AutomationTraceValidation.Text(subject, 1000) || !AutomationTraceValidation.Text(html, 32_768, multiline: true) ||
            subject.Contains("{{{", StringComparison.Ordinal) || html.Contains("{{{", StringComparison.Ordinal) ||
            SubjectToken().Matches(subject).Any(x => !SafeSubjectTokens.Contains(x.Groups[1].Value)) ||
            SubjectToken().Replace(subject, "").Contains("{{", StringComparison.Ordinal))
            throw new ArgumentException("invalid-template-or-unsafe-subject-token");
    }
    public async Task<InternalIncidentTemplateDraft> SaveAsync(InternalIncidentTemplateDraft draft, CancellationToken ct)
    {
        if (draft.Purpose != Purpose || !AutomationTraceValidation.Text(draft.Name, 256) ||
            !AutomationTraceValidation.Text(draft.OrganizationId, 64) || draft.SourceKinds is not { Count: > 0 and <= 6 } ||
            draft.SourceKinds.Any(x => !Sources.Contains(x))) throw new ArgumentException("invalid-internal-template");
        ValidateTemplates(draft.SubjectTemplate, draft.HtmlTemplate);
        var id = string.IsNullOrWhiteSpace(draft.Id) ? Guid.NewGuid() : Guid.Parse(draft.Id);
        var row = await db.Set<InternalIncidentTemplateRecord>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
        {
            if (draft.Version != 0) throw new DbUpdateConcurrencyException();
            row = new() { Id = id, OrganizationId = draft.OrganizationId! }; db.Add(row);
        }
        else if (row.Version != draft.Version || row.OrganizationId != draft.OrganizationId) throw new DbUpdateConcurrencyException();
        else row.Version++;
        row.Name = draft.Name; row.SubjectTemplate = draft.SubjectTemplate; row.HtmlTemplate = draft.HtmlTemplate;
        row.LayoutId = draft.LayoutId; row.SourceKindsJson = JsonSerializer.Serialize(draft.SourceKinds.Distinct().Order().ToArray());
        await db.SaveChangesAsync(ct); return Dto(row);
    }
    public async Task<IncidentPresentationReference> PublishAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var row = await db.Set<InternalIncidentTemplateRecord>().SingleAsync(x => x.Id == id, ct);
        if (row.Version != expectedVersion || row.Retired) throw new DbUpdateConcurrencyException();
        var (html, layout) = await ExpandLayoutAsync(row.HtmlTemplate, row.LayoutId, ct);
        var revision = row.PublishedRevision + 1; var sources = ReadSources(row.SourceKindsJson);
        var reference = Reference(id.ToString("D"), revision, row.SubjectTemplate, html, layout, sources);
        _ = Render(row.SubjectTemplate, html, SyntheticContext(sources[0]), reference);
        db.Add(new InternalIncidentTemplateRevision { TemplateId = id, Revision = revision, OrganizationId = row.OrganizationId,
            Name = row.Name, SubjectTemplate = row.SubjectTemplate, ExpandedHtml = html, SourceKindsJson = row.SourceKindsJson,
            ContentDigest = reference.ContentDigest, LayoutDigest = reference.LayoutDigest, PublishedAtUtc = time.GetUtcNow() });
        row.PublishedRevision = revision; row.Version++; await db.SaveChangesAsync(ct); return reference;
    }
    public static InternalIncidentTemplateDraft Dto(InternalIncidentTemplateRecord x) => new(x.Id.ToString("D"), x.Version, x.Name,
        x.OrganizationId, x.SubjectTemplate, x.HtmlTemplate, x.LayoutId, ReadSources(x.SourceKindsJson), x.Retired, x.PublishedRevision);
    private static string[] ReadSources(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
    private async Task<(string Html, string Layout)> ExpandLayoutAsync(string body, int? layoutId, CancellationToken ct)
    {
        var layout = layoutId is null ? "" : (await db.EmailLayouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == layoutId, ct))?.HtmlContent
            ?? throw new ArgumentException("layout-unavailable");
        // Expand once. No mutable layout reference survives into a published revision.
        var html = layout.Length == 0 ? body : layout.Replace("{{{Body}}}", body, StringComparison.Ordinal).Replace("{{{BODY}}}", body, StringComparison.Ordinal);
        if (layout.Length > 0 && html == layout) throw new ArgumentException("layout-body-token-required");
        return (html, layout);
    }
    public static AutomationIncidentContext SyntheticContext(string source = "job") => new(AutomationTraceContract.ContextVersion,
        source, new(AutomationTraceContract.OriginVersion, "11111111-1111-4111-8111-111111111111", new("demo-org", "Demo organization"),
            new("demo-request", "REQ-DEMO-001", "Multi-node directory test", DateTimeOffset.Parse("2026-11-16T08:00:00Z", CultureInfo.InvariantCulture)),
            new("demo-service", "Workplace automation"), new("demo-form", "Directory test"), new("demo-task", "Check directory"),
            "automation-attempt-demo-001", "automation-attempt-demo-001", new("demo-submitter", "Sam Demo"), new("demo-person", "Alex Example"),
            [new("source-request", "REQ-DEMO-001", "https://desk.example.com/requests/demo-request")]),
        "130", new("67", "Multi-node directory test"), "639300000000000001", "33333333-3333-4333-8333-333333333333",
        "44444444-4444-4444-8444-444444444444", "Failed", null, null, new(5, 0, 5, 0, 0, 0, 0),
        Enumerable.Range(1, 5).Select(i => new IncidentTargetEvidence("demo-node-" + i, "Demo node " + i, $"demo-{i}.example.test",
            (639300000000000001L + i).ToString(CultureInfo.InvariantCulture), "Failed",
            [new("activity-" + i, 6, "List requested directory", "command", "Failed", 2, "Directory not found",
                "ls: cannot access '/demo/missing': No such file or directory", "available")], 0)).ToArray(), 0, "available",
        [new("native-request", "Request 130", "https://net.example.com/requests?search=130"),
         new("job-run", "Job execution", "https://net.example.com/jobs/67/runs/639300000000000001"),
         new("flow-run", "Flow run", "https://net.example.com/flows?tenantId=1&run=33333333-3333-4333-8333-333333333333")]);
}
