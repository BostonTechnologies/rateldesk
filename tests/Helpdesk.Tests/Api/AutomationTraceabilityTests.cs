using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutomationTrace;
using AutomationTrace.Contracts;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class AutomationTraceabilityTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Human_report_origin_relation_and_effects_commit_once_and_replay_after_restart(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h);
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        var body = Payload(h);
        await SeedOriginAsync(h, client, body.Context);
        using var first = await SendAsync(h, token, "trace-demo", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var accepted = await first.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(accepted);
        var receipt = document.RootElement.GetProperty("integrationReceipt").Deserialize<AutomationIncidentReceipt>(AutomationTraceValidation.Json)!;
        Assert.Equal("linked", receipt.OriginRelation.Status);
        Assert.Equal(body.Context.Origin!.Request.Id, receipt.OriginRelation.SourceRequestId);
        Assert.Equal(body.Context.Origin.AttemptId, receipt.OriginRelation.SourceAttemptId);
        Assert.Equal(AutomationTraceValidation.Fingerprint(body), receipt.Fingerprint);
        Assert.DoesNotContain("No such file", document.RootElement.GetProperty("subject").GetString());
        Assert.DoesNotContain("No such file", document.RootElement.GetProperty("summary").GetString());
        var counts = await h.CountsAsync();
        await h.EditAsync(async db =>
        {
            var report = await db.Set<AutomationIncidentReportRecord>().SingleAsync();
            Assert.Contains("5 of 5 targets failed", report.PlainText);
            Assert.Contains("Sam Demo", report.PlainText); Assert.Contains("Alex Example", report.PlainText);
            Assert.Contains("List requested directory", report.PlainText); Assert.Contains("No such file", report.PlainText);
            Assert.Contains("639300000000000001", report.PlainText);
            Assert.Contains("(5 affected)", report.PlainText);
            var relation = await db.TicketRelations.SingleAsync();
            Assert.Equal(receipt.OriginRelation.RelationId, relation.Id.ToString("D"));
            Assert.Equal(Helpdesk.Shared.Enums.TicketRelationType.AutomationFailureOf, relation.RelationType);
            var incident = await db.Incidents.SingleAsync();
            Assert.Equal(h.CustomerId, incident.CustomerId); Assert.Empty(incident.CcRecipients);
            Assert.Equal(0, incident.Replies); Assert.Equal(0, incident.TimeSpentHours);
            Assert.Equal(RequestTaskStatus.Failed, (await db.RequestTasks.SingleAsync()).Status);
        });
        await h.RestartAsync();
        using var replay = await SendAsync(h, token, "trace-demo", body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(accepted, await replay.Content.ReadAsStringAsync());
        Assert.Equal(counts, await h.CountsAsync());
        using var conflicting = await SendAsync(h, token, "trace-demo", body with { Context = body.Context with { SourceOutcome = "Unknown" } });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        using var requestRelations = await h.AdminAsync(HttpMethod.Get, "/api/v1/tickets/source-request/relations/");
        var page = await requestRelations.Content.ReadFromJsonAsync<TicketRelationPage>();
        Assert.Equal(receipt.IncidentId, Assert.Single(page!.Items).SourceTicketId);
    }

    [Fact]
    public async Task Early_incident_defers_until_exact_admission_and_foreign_attempt_does_not_link()
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(false, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h); var token = await PairingBusinessFixture.TokenAsync(h, client);
        var body = Payload(h); await SeedOriginAsync(h, client, body.Context, admitted: false);
        using var pending = await SendAsync(h, token, "before-callback", body);
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode); Assert.Contains("origin-admission-pending", await pending.Content.ReadAsStringAsync());
        Assert.Equal(0, (await h.CountsAsync()).Incidents);
        await h.EditAsync(async db => { var task = await db.RequestTasks.SingleAsync(); task.OrchestrationExternalRequestId = "130"; task.OrchestrationExternalRunId = "639300000000000001"; });
        using var accepted = await SendAsync(h, token, "before-callback", body); Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var counts = await h.CountsAsync();
        using var foreign = await SendAsync(h, token, "foreign-attempt", body with
        { Context = body.Context with { Origin = body.Context.Origin! with { AttemptId = "another-attempt" } } });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode); Assert.Equal(counts, await h.CountsAsync());
    }

    [Fact]
    public async Task Published_template_and_expanded_layout_are_immutable_and_preview_matches_acceptance()
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(false, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h, templates: true); var token = await PairingBusinessFixture.TokenAsync(h, client);
        var body = Payload(h); await SeedOriginAsync(h, client, body.Context);
        IncidentPresentationReference reference;
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var layout = new EmailLayout { Name = "Internal fixture", HtmlContent = "<article>{{{Body}}}</article>" }; db.Add(layout); await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<InternalIncidentTemplateService>();
            var draft = await service.SaveAsync(InternalIncidentTemplateService.SystemDraft("system-job-failure") with
            { OrganizationId = h.OrganizationId, LayoutId = layout.Id }, default);
            reference = await service.PublishAsync(Guid.Parse(draft.Id), draft.Version, default);
            layout.HtmlContent = "<p>Mutable layout must never appear</p>"; await db.SaveChangesAsync();
        }
        body = body with { Presentation = reference };
        using var preview = await PairingBusinessFixture.SendAsync(h, token, HttpMethod.Post,
            "/api/v1/integrations/netratel/incident-templates/preview", new IncidentTemplatePreviewRequest(reference, body.Context));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var rendered = await preview.Content.ReadFromJsonAsync<IncidentTemplatePreview>();
        using var create = await SendAsync(h, token, "custom-template", body); Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        await h.EditAsync(async db =>
        {
            var report = await db.Set<AutomationIncidentReportRecord>().SingleAsync(); Assert.Equal(rendered!.Html, report.Html);
            Assert.DoesNotContain("Mutable layout", report.Html);
            var revision = await db.Set<InternalIncidentTemplateRevision>().SingleAsync(); revision.ExpandedHtml = "changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        });
        using var deniedCatalogue = await PairingBusinessFixture.SendAsync(h,
            await PairingBusinessFixture.TokenAsync(h, client, [Helpdesk.Infrastructure.ServiceIdentity.ServiceIdentityScopes.IncidentReceipts]),
            HttpMethod.Get, "/api/v1/integrations/netratel/incident-templates");
        Assert.Equal(HttpStatusCode.Forbidden, deniedCatalogue.StatusCode);
    }

    [Fact]
    public async Task Standard_report_encodes_diagnostics_once_and_restricts_customer_subject_tokens()
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(false);
        await using var scope = h.App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<InternalIncidentTemplateService>();
        var context = InternalIncidentTemplateService.SyntheticContext();
        var first = context.Targets[0]; var malicious = first.FailedSteps[0] with { ErrorExcerpt = "<script>alert(1)</script> & private diagnostic" };
        context = context with { Targets = [first with { FailedSteps = [malicious] }], OmittedTargets = 4 };
        var result = await service.RenderAsync("demo-org", new(InternalIncidentTemplateService.Standard, context), default);
        Assert.DoesNotContain("<script>", result.Html); Assert.Contains("&lt;script&gt;", result.Html); Assert.DoesNotContain("&amp;lt;script", result.Html);
        Assert.DoesNotContain("private diagnostic", result.Subject); Assert.DoesNotContain("private diagnostic", result.Summary);
        Assert.Throws<ArgumentException>(() => service.Render("{{ErrorExcerpt}}", "<p>{{Summary}}</p>", context, InternalIncidentTemplateService.Standard));
        Assert.Throws<ArgumentException>(() => service.Render("{{DefinitionName}}", "<p>{{{ErrorExcerpt}}}</p>", context, InternalIncidentTemplateService.Standard));
    }

    private static AutomationIncidentCreate Payload(IncidentReceiverTests.Harness h)
    {
        var context = InternalIncidentTemplateService.SyntheticContext();
        context = context with { Origin = context.Origin! with { SourceInstanceId = h.PublishedId.ToString("D"),
            Organization = new(h.OrganizationId, "Synthetic organization"), Request = context.Origin.Request with { Id = "source-request" },
            Task = new("source-task", "Directory check"), Links = [new("source-request", "REQ-DEMO-001", "https://receiver-web.example.test/requests/source-request")] },
            Links = [new("native-request", "Request 130", "https://peer.example.test/requests?search=130"),
                new("job-run", "Job execution", "https://peer.example.test/jobs/67/runs/639300000000000001"),
                new("flow-run", "Flow run", "https://peer.example.test/flows?tenantId=17&run=33333333-3333-4333-8333-333333333333")] };
        return new(AutomationTraceContract.IncidentVersion, new(h.OrganizationId, h.CustomerId, null, [], 2), InternalIncidentTemplateService.Standard, context);
    }
    private static Task SeedOriginAsync(IncidentReceiverTests.Harness h, PairingBusinessFixture.Client client,
        AutomationIncidentContext context, bool admitted = true) => h.EditAsync(db =>
    {
        db.Requests.Add(new Request { Id = "source-request", TrackingId = "REQ-DEMO-001", Title = "Multi-node directory test",
            OrganizationId = h.OrganizationId, CustomerId = h.CustomerId });
        db.RequestTasks.Add(new() { Id = "source-task", RequestId = "source-request", OrganizationId = h.OrganizationId,
            Name = "Directory check", Type = RequestTaskType.Automation, Status = RequestTaskStatus.Failed,
            AutomationAttemptCorrelationId = context.Origin!.AttemptId, AutomationOriginJson = JsonSerializer.Serialize(context.Origin, AutomationTraceValidation.Json),
            AutomationDispatchPayloadJson = "{}", OrchestrationLinkId = client.MappingId.ToString("D"), OrchestrationPeerInstanceId = client.PeerId,
            OrchestrationJobDefinitionId = "67", OrchestrationExternalRequestId = admitted ? "130" : null,
            OrchestrationExternalRunId = admitted ? "639300000000000001" : null });
        return Task.CompletedTask;
    });
    private static async Task<HttpResponseMessage> SendAsync(IncidentReceiverTests.Harness h, string token, string key, AutomationIncidentCreate body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/netratel/incidents") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new("Bearer", token); request.Headers.Add("X-NetRatel-Source-Instance", h.SourceId.ToString("D"));
        request.Headers.Add("Idempotency-Key", key); return await h.Client.SendAsync(request);
    }
}
