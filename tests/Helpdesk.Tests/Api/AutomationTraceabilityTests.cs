using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutomationTrace;
using AutomationTrace.Contracts;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Tests.Infrastructure.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class AutomationTraceabilityTests
{
    [Fact]
    public void Queue_fingerprint_matches_the_documented_vector_with_the_frozen_v2_payload()
    {
        var path = Path.Combine(TestEnvironment.RepositoryRoot, "docs", "contracts", "rateldesk-incident-create.v2.fixtures.json");
        using var vector = JsonDocument.Parse(File.ReadAllText(path));
        var incident = vector.RootElement.GetProperty("create").Deserialize<AutomationIncidentCreate>(AutomationTraceValidation.Json)!;
        Assert.Equal(vector.RootElement.GetProperty("fingerprint").GetString(), AutomationTraceValidation.Fingerprint(incident));
        Assert.Equal("bcfab6e3eb8b9e5a3a5f20d258bb2098e8af3e471c02cdd299ae5715ca4300b9",
            IncidentQueueContract.Fingerprint(incident, "app-vendor"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Negotiated_queue_create_is_source_bound_and_replays_the_same_committed_assignment(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h);
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        using var route = await h.AdminAsync(HttpMethod.Put,
            $"/api/v1/integrations/netratel/sources/{client.MappingId:D}/queues",
            new { expectedRevision = 1, supportGroupIds = new[] { "support-group" } });
        Assert.Equal(HttpStatusCode.OK, route.StatusCode);
        var body = Payload(h);
        using var capabilities = await SendQueuedAsync(HttpMethod.Get, "queue-capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        using var discovery = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.Equal(IncidentQueueContract.Version, discovery.RootElement.GetProperty("contractVersion").GetString());
        Assert.Equal(client.MappingId, discovery.RootElement.GetProperty("sourceNamespaceId").GetGuid());
        using var choices = await SendQueuedAsync(HttpMethod.Get, "queues");
        Assert.Equal(HttpStatusCode.OK, choices.StatusCode);
        Assert.Contains("support-group", await choices.Content.ReadAsStringAsync());

        await SeedOriginAsync(h, client, body.Context);
        using var unauthorized = await SendQueuedAsync(HttpMethod.Post, "queued-incidents", "unapproved-route", "not-allowlisted");
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);

        var attempts = await Task.WhenAll(
            SendQueuedAsync(HttpMethod.Post, "queued-incidents", "queued-incident", "support-group"),
            SendQueuedAsync(HttpMethod.Post, "queued-incidents", "queued-incident", "support-group"));
        try
        {
            var created = Assert.Single(attempts, attempt => attempt.StatusCode == HttpStatusCode.Created);
            var retry = Assert.Single(attempts, attempt => attempt.StatusCode == HttpStatusCode.OK);
            var accepted = await created.Content.ReadAsStringAsync();
            using var envelope = JsonDocument.Parse(accepted);
            var receipt = envelope.RootElement.GetProperty("integrationReceipt");
            Assert.Equal("support-group", receipt.GetProperty("assignedSupportGroupId").GetString());
            Assert.Equal(IncidentQueueContract.Fingerprint(body, "support-group"), receipt.GetProperty("fingerprint").GetString());
            Assert.Equal(accepted, await retry.Content.ReadAsStringAsync());

            await h.RestartAsync();
            using var lookup = await SendQueuedAsync(HttpMethod.Get, "queue-receipt", "queued-incident");
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            Assert.Equal(accepted, await lookup.Content.ReadAsStringAsync());
            using var conflict = await SendQueuedAsync(HttpMethod.Post, "queued-incidents", "queued-incident", "not-allowlisted");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var legacyConflict = await SendAsync(h, token, "queued-incident", body);
            Assert.Equal(HttpStatusCode.Conflict, legacyConflict.StatusCode);
        }
        finally
        {
            foreach (var attempt in attempts) attempt.Dispose();
        }

        await h.EditAsync(async db =>
        {
            Assert.Equal("support-group", (await db.Incidents.SingleAsync()).AssignedSupportGroupId);
            Assert.Single(await db.TicketQueueTransitions.ToListAsync());
            Assert.Single(await db.Set<MailboxOutboxEffect>().Where(effect => effect.Kind == MailboxEffectKind.SupportGroupAssignment).ToListAsync());
            (await db.SupportGroups.SingleAsync()).IsEnabled = false;
        });
        using var revoked = await SendQueuedAsync(HttpMethod.Get, "queue-receipt", "queued-incident");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal(1, (await h.CountsAsync()).Incidents);

        async Task<HttpResponseMessage> SendQueuedAsync(HttpMethod method, string path, string? key = null, string? groupId = null)
        {
            using var request = new HttpRequestMessage(method, $"/api/v1/integrations/netratel/{path}");
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.Add("X-NetRatel-Source-Instance", h.SourceId.ToString("D"));
            if (key is not null) request.Headers.Add("Idempotency-Key", key);
            if (groupId is not null) request.Content = JsonContent.Create(new IncidentQueueContract.Create(IncidentQueueContract.Version, groupId, body));
            return await h.Client.SendAsync(request);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_queue_alert_sends_one_notification_and_the_signed_in_vendor_claims_the_linked_incident(bool postgres)
    {
        const string groupId = "app-vendor";
        const string vendorId = "vendor-operator";
        const string vendorEmail = "vendor@example.test";
        const string password = "synthetic vendor support password";
        var mailpitPort = Environment.GetEnvironmentVariable("RATELDESK_QUEUE_TEST_MAILPIT_SMTP_PORT");
        await using var smtp = mailpitPort == null ? new SmtpMailboxSenderTests.SmtpFixture() : null;
        var port = smtp?.Port ?? int.Parse(mailpitPort!, global::System.Globalization.CultureInfo.InvariantCulture);
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var providerId = Guid.NewGuid().ToString("D");
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = await identity.CreateAsync(new ApplicationUser
            {
                Id = vendorId, UserName = vendorEmail, Email = vendorEmail, IsEnabled = true
            }, password);
            Assert.True(account.Succeeded, string.Join(", ", account.Errors.Select(error => error.Code)));
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.Add(new Organization { Id = providerId, Name = "Synthetic application vendor", IsEnabled = true });
            (await db.Organizations.SingleAsync(item => item.Id == h.OrganizationId)).ItSupportOrganizationId = providerId;
            db.Users.Add(new User { Id = vendorId, OrganizationId = providerId, Name = "Application operator", Email = vendorEmail });
            db.SupportGroups.Add(new SupportGroup { Id = groupId, Name = "App Vendor", OwningOrganizationId = providerId });
            db.SupportGroupMembers.Add(new SupportGroupMember { UserId = vendorId, SupportGroupId = groupId });
            db.OrganizationSupportCoverages.Add(new OrganizationSupportCoverage
            {
                CustomerOrganizationId = h.OrganizationId, ProviderOrganizationId = providerId, SupportGroupId = groupId
            });
            var role = new Role
            {
                Key = "custom.vendor-incidents", Name = "Application incident operator", OwnerOrganizationId = providerId,
                TicketAccessScope = TicketAccessScope.MySupportGroups,
                Permissions = [new RolePermission { Permission = HelpdeskPermissions.IncidentRead },
                    new RolePermission { Permission = HelpdeskPermissions.IncidentWrite }]
            };
            db.Roles.Add(role);
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = vendorId, OrganizationId = providerId, RoleKey = role.Key });
            await TicketWorkforceRestrictions.EnsureAsync(db, providerId, [vendorId], "owner", default);
            db.Incidents.Add(new Incident { Id = "hidden-main", OrganizationId = h.OrganizationId, Title = "Private Main Queue incident" });
            IncidentReceiverTests.ConfigureQueueEmail(scope.ServiceProvider, h.OrganizationId, groupId, port,
                smtp == null ? MailboxTlsMode.StartTls : MailboxTlsMode.TlsOnConnect);
            await db.SaveChangesAsync();
        }

        var peer = await PairingBusinessFixture.CreateAsync(h);
        var token = await PairingBusinessFixture.TokenAsync(h, peer);
        using var approval = await h.AdminAsync(HttpMethod.Put,
            $"/api/v1/integrations/netratel/sources/{peer.MappingId:D}/queues", new { expectedRevision = 1, supportGroupIds = new[] { groupId } });
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var payload = Payload(h);
        await SeedOriginAsync(h, peer, payload.Context);
        using var first = await SubmitAsync();
        using var retry = await SubmitAsync();
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var accepted = await first.Content.ReadAsStringAsync();
        Assert.Equal(accepted, await retry.Content.ReadAsStringAsync());
        using var receipt = JsonDocument.Parse(accepted);
        Assert.Equal("Automation Failed", receipt.RootElement.GetProperty("subject").GetString());
        var incidentId = receipt.RootElement.GetProperty("integrationReceipt").GetProperty("incidentId").GetString();
        var link = $"https://receiver.example.test/incidents/{incidentId}";

        // Keep the independently authorized customer receipt queued while this SMTP fixture observes the group event.
        await h.EditAsync(async db =>
        {
            var confirmation = Assert.Single(await db.Set<MailboxOutboxEffect>().Where(effect => effect.Kind == MailboxEffectKind.Email).ToListAsync());
            confirmation.AvailableUnixMilliseconds = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        });
        using var dispatcher = new MailboxOutboxDispatcher(h.App.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MailboxOutboxDispatcher>.Instance);
        for (var cycle = 0; cycle < 4; cycle++) await dispatcher.DispatchBatchAsync();
        await h.EditAsync(async db =>
        {
            var delivery = Assert.Single(await db.SupportNotificationDeliveries.ToListAsync());
            Assert.Equal((incidentId, vendorEmail, SupportNotificationDeliveryStatus.Sent),
                (delivery.TicketId, delivery.RecipientEmail, delivery.Status));
        });
        if (smtp != null)
        {
            await smtp.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(smtp.Commands, command => command == "DATA");
            Assert.Contains(smtp.Commands, command => command.Equals($"RCPT TO:<{vendorEmail}>", StringComparison.OrdinalIgnoreCase));
            using var raw = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(smtp.Message));
            Assert.Contains(link, (await MimeKit.MimeMessage.LoadAsync(raw)).HtmlBody);
        }

        await h.PrepareProcessBootstrapAsync();
        var (process, vendor) = await h.StartApiProcessAsync();
        try
        {
            using var anonymous = await vendor.GetAsync($"/api/v1/incidents/{incidentId}");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            vendor.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            using var login = await vendor.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(vendorEmail, password));
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
            var incidentPath = "/api/v1" + new Uri(link).AbsolutePath;
            var opened = await vendor.GetFromJsonAsync<IncidentDto>(incidentPath);
            Assert.Equal(groupId, opened!.AssignedSupportGroupId);
            Assert.Equal("Automation Failed", opened.Subject);
            using var state = await vendor.PostAsJsonAsync(incidentPath + "/state", new { newState = TicketState.InProgress });
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            using var hidden = await vendor.GetAsync("/api/v1/incidents/hidden-main");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            foreach (var unsupported in new[]
            {
                $"/api/v1/tickets/{incidentId}/automation-reports",
                $"/api/v1/reports/sla/breached?tenantId={h.OrganizationId}&ticketType=Incident&page=1&pageSize=10",
                "/api/v1/incidents/hidden-main/ai-assistant/chat",
                "/api/v1/incidents/hidden-main/ai-assistant/eligible-configurations"
            })
            {
                using var denied = await vendor.GetAsync(unsupported);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.Empty(await denied.Content.ReadAsStringAsync());
            }
            using var claim = await vendor.PostAsync($"/api/v1/ticket-queues/incident/{incidentId}/claim?expectedVersion={opened.QueueAssignmentVersion}", null);
            Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
            Assert.Equal(vendorId, (await vendor.GetFromJsonAsync<IncidentDto>(incidentPath))!.AssignedToId);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var hiddenStream = await vendor.GetAsync("/api/v1/incidents/hidden-main/timeline/stream",
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, hiddenStream.StatusCode);
            using var stream = await vendor.GetAsync(incidentPath + "/timeline/stream",
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(deadline.Token));
            Assert.Equal(": connected", await reader.ReadLineAsync(deadline.Token));
            Assert.Equal(string.Empty, await reader.ReadLineAsync(deadline.Token));
            await h.EditAsync(async db => (await db.SupportGroupMembers.SingleAsync(member => member.UserId == vendorId)).IsEnabled = false);
            Assert.Null(await reader.ReadLineAsync(deadline.Token));
            using var revoked = await vendor.GetAsync(incidentPath);
            Assert.Contains(revoked.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

            await h.EditAsync(async db =>
            {
                (await db.SupportGroupMembers.SingleAsync(member => member.UserId == vendorId)).IsEnabled = true;
                var role = await db.Roles.Include(role => role.Permissions).SingleAsync(role => role.Key == "custom.vendor-incidents");
                role.Permissions.Add(new RolePermission { Permission = HelpdeskPermissions.ChangeRead });
                db.Changes.AddRange(
                    new Change { Id = "visible-change", Title = "Synthetic vendor change", OrganizationId = h.OrganizationId, AssignedSupportGroupId = groupId },
                    new Change { Id = "hidden-change", Title = "Synthetic hidden change", OrganizationId = h.OrganizationId });
            });
            using var changeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var hiddenChange = await vendor.GetAsync("/api/v1/changes/hidden-change/timeline/stream",
                HttpCompletionOption.ResponseHeadersRead, changeDeadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, hiddenChange.StatusCode);
            using var changeStream = await vendor.GetAsync("/api/v1/changes/visible-change/timeline/stream",
                HttpCompletionOption.ResponseHeadersRead, changeDeadline.Token);
            Assert.Equal(HttpStatusCode.OK, changeStream.StatusCode);
            using var changeReader = new StreamReader(await changeStream.Content.ReadAsStreamAsync(changeDeadline.Token));
            Assert.Equal(": connected", await changeReader.ReadLineAsync(changeDeadline.Token));
            Assert.Equal(string.Empty, await changeReader.ReadLineAsync(changeDeadline.Token));
            await h.EditAsync(async db => (await db.SupportGroupMembers.SingleAsync(member => member.UserId == vendorId)).IsEnabled = false);
            Assert.Null(await changeReader.ReadLineAsync(changeDeadline.Token));
            using var deniedReconnect = await vendor.GetAsync("/api/v1/changes/visible-change/timeline/stream",
                HttpCompletionOption.ResponseHeadersRead, changeDeadline.Token);
            Assert.Contains(deniedReconnect.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }
        finally { await IncidentReceiverTests.Harness.StopProcessAsync(process, vendor); }

        async Task<HttpResponseMessage> SubmitAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/netratel/queued-incidents")
            {
                Content = JsonContent.Create(new IncidentQueueContract.Create(IncidentQueueContract.Version, groupId, payload))
            };
            h.Authorize(request, "synthetic-vendor-alert", credential: token);
            return await h.Client.SendAsync(request);
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Human_report_origin_relation_and_effects_commit_once_and_replay_after_restart(bool postgres)
    {
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true);
        var client = await PairingBusinessFixture.CreateAsync(h);
        var token = await PairingBusinessFixture.TokenAsync(h, client);
        var body = Payload(h);
        await SeedOriginAsync(h, client, body.Context);
        using var emptyReports = await h.AdminAsync(HttpMethod.Get, "/api/v1/tickets/source-request/automation-reports");
        Assert.Equal(HttpStatusCode.OK, emptyReports.StatusCode);
        Assert.Empty((await emptyReports.Content.ReadFromJsonAsync<List<InternalIncidentReportDto>>())!);
        using var first = await SendAsync(h, token, "trace-demo", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var accepted = await first.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(accepted);
        var receipt = document.RootElement.GetProperty("integrationReceipt").Deserialize<AutomationIncidentReceipt>(AutomationTraceValidation.Json)!;
        Assert.Equal("linked", receipt.OriginRelation.Status);
        Assert.Equal(body.Context.Origin!.Request.Id, receipt.OriginRelation.SourceRequestId);
        Assert.Equal(body.Context.Origin.AttemptId, receipt.OriginRelation.SourceAttemptId);
        Assert.Equal(AutomationTraceValidation.Fingerprint(body), receipt.Fingerprint);
        foreach (var ticketId in new[] { receipt.IncidentId, "source-request" })
        {
            using var reportResponse = await h.AdminAsync(HttpMethod.Get, $"/api/v1/tickets/{ticketId}/automation-reports");
            Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
            var report = Assert.Single((await reportResponse.Content.ReadFromJsonAsync<List<InternalIncidentReportDto>>())!);
            Assert.Equal(receipt.IncidentId, report.IncidentId);
            Assert.Contains("5 of 5 targets failed", report.PlainText);
        }
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
