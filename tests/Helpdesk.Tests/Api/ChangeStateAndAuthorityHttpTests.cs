using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Changes;
using Helpdesk.API.Endpoints.Notifications;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.API.Endpoints.Organization;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class ChangeStateAndAuthorityHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_change_ticket_state_is_real_scoped_and_distinct_from_lifecycle(bool postgres)
    {
        await using var h = await CreateAsync(postgres);
        await SeedChangesAsync(h);
        await GrantChangeWriterAsync(h);
        using var update = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/draft/state", new { NewState = TicketState.InProgress });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var response = await update.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(TicketState.InProgress, response!.State);
        Assert.Equal(ChangeLifecycleState.Draft, response.LifecycleState);
        var committed = await SnapshotAsync(h);
        Assert.Contains("draft:3:0", committed.Changes);
        using var readback = await BoundedAsync(h, HttpMethod.Get, "/api/v1/changes/draft");
        var reread = await readback.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(response.State, reread!.State);
        Assert.Equal(response.LifecycleState, reread.LifecycleState);
        Assert.Equal(response.Priority, reread.Priority);
        using var repeated = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/draft/state", new { NewState = TicketState.InProgress });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(committed, await SnapshotAsync(h));

        foreach (var attempt in new[]
        {
            ("/api/v1/changes/draft/state", (object)new { NewState = 999 }),
            ("/api/v1/changes/draft/state", new { NewState = TicketState.Resolved }),
            ("/api/v1/changes/approved/state", new { NewState = TicketState.OnHold }),
            ("/api/v1/changes/implemented/state", new { NewState = TicketState.New }),
            ("/api/v1/changes/draft/lifecycle", new { LifecycleState = 999 }),
            ("/api/v1/changes/draft/lifecycle", new { LifecycleState = ChangeLifecycleState.ImplementedSuccess })
        })
        {
            using var rejected = await BoundedAsync(h, HttpMethod.Post, attempt.Item1, attempt.Item2);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(committed, await SnapshotAsync(h));
        }
        using var foreign = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/foreign/state", new { NewState = TicketState.OnHold });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(committed, await SnapshotAsync(h));
        using var bulkDenied = await BoundedAsync(h, HttpMethod.Post, "/api/v1/changes/bulk/state", new { Ids = new[] { "draft" }, NewState = TicketState.OnHold });
        Assert.Equal(HttpStatusCode.Forbidden, bulkDenied.StatusCode);
        Assert.Equal(committed, await SnapshotAsync(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Change_approval_template_editlock_and_atomic_bulk_rules_survive_state_support(bool postgres)
    {
        await using var h = await CreateAsync(postgres);
        await SeedChangesAsync(h);
        var before = await SnapshotAsync(h);
        foreach (var attempt in new[]
        {
            ("/api/v1/changes/bulk/state", (object)new { Ids = new[] { "draft", "approved" }, NewState = TicketState.OnHold }),
            ("/api/v1/changes/bulk/state", new { Ids = new[] { "draft" }, NewState = 999 }),
            ("/api/v1/changes/pending/lifecycle", new { LifecycleState = ChangeLifecycleState.ApprovedForImplementation }),
            ("/api/v1/changes/incomplete/lifecycle", new { LifecycleState = ChangeLifecycleState.ImplementedSuccess })
        })
        {
            using var rejected = await h.AdminAsync(HttpMethod.Post, attempt.Item1, attempt.Item2);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(before, await SnapshotAsync(h));
        }
        foreach (var body in new object[] { new { State = 999 }, new { Priority = 999 }, new { LifecycleState = 999 }, new { State = TicketState.Resolved } })
        {
            using var rejected = await h.AdminAsync(HttpMethod.Put, "/api/v1/changes/draft", body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(before, await SnapshotAsync(h));
        }
        using var partial = await h.AdminAsync(HttpMethod.Put, "/api/v1/changes/draft", new { Priority = TicketPriority.Critical });
        Assert.True(partial.StatusCode == HttpStatusCode.OK, await partial.Content.ReadAsStringAsync());
        var priorityResponse = await partial.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(TicketPriority.Critical, priorityResponse!.Priority);
        Assert.Equal(TicketState.New, priorityResponse.State);
        using var start = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/approved/lifecycle", new { LifecycleState = ChangeLifecycleState.ImplementationInProgress });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(TicketState.New, (await start.Content.ReadFromJsonAsync<ChangeDto>())!.State);
        using var finish = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/approved/lifecycle", new { LifecycleState = ChangeLifecycleState.ImplementedSuccess });
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        var completed = await finish.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(TicketState.Resolved, completed!.State);
        Assert.Equal(ChangeLifecycleState.ImplementedSuccess, completed.LifecycleState);
        await h.EditAsync(async db =>
        {
            var saved = await db.Changes.AsNoTracking().SingleAsync(x => x.Id == "approved");
            Assert.Equal(completed.State, saved.State);
            Assert.Equal(completed.LifecycleState, saved.LifecycleState);
            Assert.NotNull(saved.ClosedAt);
            Assert.Equal("support-user", saved.ImplementorUserId);
            Assert.True(await db.TicketTimelineEvents.AnyAsync(x => x.TicketId == saved.Id));
            var sla = await db.TicketSlaStates.AsNoTracking().SingleAsync(x => x.TicketId == saved.Id);
            Assert.Equal(SlaStatus.Completed, sla.Status);
            Assert.NotNull(sla.CompletedAt);
        });
        var after = await SnapshotAsync(h);
        using var noOp = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/approved/state", new { NewState = TicketState.Resolved });
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal(after, await SnapshotAsync(h));
        using var terminal = await h.AdminAsync(HttpMethod.Post, "/api/v1/changes/approved/lifecycle", new { LifecycleState = ChangeLifecycleState.Draft });
        Assert.Equal(HttpStatusCode.BadRequest, terminal.StatusCode);
        Assert.Equal(after, await SnapshotAsync(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seen_and_notification_read_have_independent_persisted_postconditions(bool postgres)
    {
        await using var h = await CreateAsync(postgres);
        var personalId = Guid.NewGuid();
        var broadcastId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var oldRead = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await h.EditAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "foreign-org", Name = "Foreign" });
            db.Incidents.Add(new Incident { Id = "seen-ticket", TrackingId = "INC-SEEN", Title = "Seen fixture", OrganizationId = h.OrganizationId, CustomerId = h.CustomerId });
            db.Incidents.Add(new Incident { Id = "foreign-ticket", TrackingId = "INC-FOREIGN", Title = "Foreign fixture", OrganizationId = "foreign-org" });
            db.WorkLogs.AddRange(new WorkLog { Id = "unread-worklog", TicketId = "seen-ticket", NotesText = "Unread customer message" },
                new WorkLog { Id = "read-worklog", TicketId = "seen-ticket", NotesText = "Previously read message", SeenByCustomerAt = oldRead });
            db.Notifications.AddRange(
                new NotificationEntity { Id = personalId, UserId = "owner", TenantId = h.OrganizationId, Reference = "seen-ticket", Title = "Personal", CreatedUtc = DateTime.UtcNow },
                new NotificationEntity { Id = broadcastId, IsGlobal = true, TenantId = h.OrganizationId, Reference = "seen-ticket", Title = "Broadcast", CreatedUtc = DateTime.UtcNow },
                new NotificationEntity { Id = foreignId, IsGlobal = true, TenantId = "foreign-org", Reference = "foreign-ticket", Title = "Foreign", CreatedUtc = DateTime.UtcNow });
            return Task.CompletedTask;
        });
        using var beforeSeen = await BoundedAsync(h, HttpMethod.Get, "/api/v1/incidents/seen-ticket");
        Assert.Equal(HttpStatusCode.OK, beforeSeen.StatusCode);
        using (var beforeJson = JsonDocument.Parse(await beforeSeen.Content.ReadAsStringAsync()))
            Assert.Equal(JsonValueKind.Null, beforeJson.RootElement.GetProperty("lastViewedByCustomerAt").ValueKind);
        using var seen = await BoundedAsync(h, HttpMethod.Post, "/api/v1/tickets/seen-ticket/mark-as-seen");
        Assert.Equal(HttpStatusCode.NoContent, seen.StatusCode);
        using var afterSeen = await BoundedAsync(h, HttpMethod.Get, "/api/v1/incidents/seen-ticket");
        Assert.Equal(HttpStatusCode.OK, afterSeen.StatusCode);
        using (var afterJson = JsonDocument.Parse(await afterSeen.Content.ReadAsStringAsync()))
            Assert.NotEqual(JsonValueKind.Null, afterJson.RootElement.GetProperty("lastViewedByCustomerAt").ValueKind);
        await h.EditAsync(async db =>
        {
            Assert.NotNull((await db.Incidents.AsNoTracking().SingleAsync(x => x.Id == "seen-ticket")).LastViewedByCustomerAt);
            Assert.NotNull((await db.WorkLogs.AsNoTracking().SingleAsync(x => x.Id == "unread-worklog")).SeenByCustomerAt);
            Assert.Equal(oldRead, (await db.WorkLogs.AsNoTracking().SingleAsync(x => x.Id == "read-worklog")).SeenByCustomerAt);
            Assert.All(await db.Notifications.AsNoTracking().ToListAsync(), n => Assert.Null(n.ReadUtc));
            Assert.Empty(await db.NotificationReads.ToListAsync());
        });
        using var denied = await BoundedAsync(h, HttpMethod.Post, "/api/v1/tickets/foreign-ticket/mark-as-seen");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var marked = await BoundedAsync(h, HttpMethod.Post, "/api/v1/notifications/mark-read", new { Ids = new[] { personalId, broadcastId, foreignId } });
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await marked.Content.ReadAsStringAsync()).RootElement.GetProperty("updated").GetInt32());
        await h.EditAsync(async db =>
        {
            Assert.NotNull((await db.Notifications.AsNoTracking().SingleAsync(x => x.Id == personalId)).ReadUtc);
            Assert.Null((await db.Notifications.AsNoTracking().SingleAsync(x => x.Id == broadcastId)).ReadUtc);
            var read = Assert.Single(await db.NotificationReads.AsNoTracking().ToListAsync());
            Assert.Equal(broadcastId, read.NotificationId);
            Assert.Equal("owner", read.UserId);
            Assert.Null((await db.Notifications.AsNoTracking().SingleAsync(x => x.Id == foreignId)).ReadUtc);
            Assert.Null((await db.Incidents.AsNoTracking().SingleAsync(x => x.Id == "foreign-ticket")).LastViewedByCustomerAt);
        });
        using var repeated = await BoundedAsync(h, HttpMethod.Post, "/api/v1/notifications/mark-read", new { Ids = new[] { personalId, broadcastId } });
        Assert.Equal(0, JsonDocument.Parse(await repeated.Content.ReadAsStringAsync()).RootElement.GetProperty("updated").GetInt32());
        using var unread = await BoundedAsync(h, HttpMethod.Get, "/api/v1/notifications/unread-errors");
        Assert.Equal(HttpStatusCode.OK, unread.StatusCode);
        Assert.Equal("[]", await unread.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Administrative_branding_uses_integer_tenant_and_bounded_credentials_remain_denied(bool postgres)
    {
        await using var h = await CreateAsync(postgres);
        using var deniedTest = await BoundedAsync(h, HttpMethod.Post, "/api/v1/admin/orchestration/test");
        Assert.Equal(HttpStatusCode.Forbidden, deniedTest.StatusCode);
        using var deniedBranding = await BoundedAsync(h, HttpMethod.Put, "/api/v1/tenants/17/branding", new { BrandName = "Denied" });
        Assert.Equal(HttpStatusCode.Forbidden, deniedBranding.StatusCode);
        using var saved = await h.AdminAsync(HttpMethod.Put, "/api/v1/tenants/17/branding", new { BrandName = "Synthetic brand", FooterHtml = "<p>Owned fixture</p>", PrimaryColor = "#123456", FromName = "Support", ReplyTo = "support@example.test" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var reread = await h.AdminAsync(HttpMethod.Get, "/api/v1/tenants/17/branding");
        var branding = await reread.Content.ReadFromJsonAsync<TenantBranding>();
        Assert.Equal(17, branding!.TenantId);
        Assert.Equal("Synthetic brand", branding.BrandName);
        Assert.Equal("#123456", branding.PrimaryColor);
        await h.EditAsync(async db => Assert.Equal(branding.BrandName, (await db.TenantBrandings.AsNoTracking().SingleAsync(x => x.TenantId == 17)).BrandName));
        using var updated = await h.AdminAsync(HttpMethod.Put, "/api/v1/tenants/17/branding", new { BrandName = "Updated synthetic brand", PrimaryColor = "#654321" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedRead = await h.AdminAsync(HttpMethod.Get, "/api/v1/tenants/17/branding");
        Assert.Equal("Updated synthetic brand", (await updatedRead.Content.ReadFromJsonAsync<TenantBranding>())!.BrandName);
        await h.EditAsync(async db => Assert.Equal(1, await db.TenantBrandings.CountAsync(x => x.TenantId == 17)));
        using var wrongId = await h.AdminAsync(HttpMethod.Get, "/api/v1/tenants/organization-label/branding");
        Assert.Equal(HttpStatusCode.NotFound, wrongId.StatusCode);
        using var noPeer = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/orchestration/test");
        Assert.Equal(HttpStatusCode.OK, noPeer.StatusCode);
        var diagnostic = await noPeer.Content.ReadFromJsonAsync<OrchestrationConnectivityTestResultDto>();
        Assert.False(diagnostic!.Success);
        Assert.Contains("connection", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        await using var scope = h.App.Services.CreateAsyncScope();
        // The authenticated denial above must not create instance authority or a stored gateway secret.
        var credential = await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>().IntegrationCredentials.SingleAsync();
        Assert.Equal("Incident.Read Incident.Write", credential.Permissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Orchestration_test_uses_its_saved_peer_and_protected_read_contract_without_ingest(bool postgres)
    {
        using var peer = new PairingTestPeer(Guid.NewGuid()) { AutomationBusinessEnabled = true };
        await using var h = await CreateAsync(postgres, peer);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var service = peer.Service(scope.ServiceProvider, new PairingTestClock());
            var code = await service.GenerateAsync("owner", default);
            var paired = await service.ExchangeAsync(peer.Exchange(code.Code), default);
            await service.SaveAsync(paired.PairId, new PairingMapping(Guid.NewGuid().ToString("D"), paired.PairId,
                "Synthetic orchestration test", "17", h.OrganizationId, null, false, true), "owner", default);
        }
        var before = await SnapshotAsync(h);
        using var tested = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/orchestration/test");
        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        var result = await tested.Content.ReadFromJsonAsync<OrchestrationConnectivityTestResultDto>();
        Assert.True(result!.Success, result.Message);
        Assert.Equal(["AcquireToken", "RemoteHealth", "ProtectedIdentity"], result.Probes.Select(p => p.ProbeName));
        Assert.Equal(1, peer.AutomationTokenCalls);
        Assert.Equal(1, peer.AutomationHealthCalls);
        Assert.Equal(1, peer.AutomationIdentityCalls);
        Assert.Equal(0, peer.AutomationIngestCalls);
        Assert.Equal(before, await SnapshotAsync(h));
        using var denied = await BoundedAsync(h, HttpMethod.Post, "/api/v1/admin/orchestration/test");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, peer.AutomationHealthCalls);
        Assert.Equal(before, await SnapshotAsync(h));
    }

    private static Task<IncidentReceiverTests.Harness> CreateAsync(bool postgres, IHttpClientFactory? peer = null) => IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true, pairingPeer: peer,
        configureServices: services => services.AddAuthorization(options =>
        {
            options.AddPolicy("ChangeAccess", p => p.RequireAuthenticatedUser().RequireRole("Change.Read", "Change.Write", "Change.Approve", "Change.Manager", "HelpdeskAdmin"));
            options.AddPolicy("ChangeManager", p => p.RequireAuthenticatedUser().RequireRole("Change.Write", "Change.Manager", "HelpdeskAdmin"));
            options.AddPolicy("HelpdeskAdmin", p => p.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
            options.AddPolicy("NotificationAccess", p => p.RequireAuthenticatedUser());
        }),
        configureApp: app =>
        {
            app.MapChangeEndpoints();
            app.MapTicketEndpoints();
            app.MapNotificationEndpoints();
            app.MapTenantBrandingEndpoints();
            app.MapExternalOrchestrationEndpoints();
        });

    private static async Task GrantChangeWriterAsync(IncidentReceiverTests.Harness h)
    {
        await using var scope = h.App.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
        var credential = await identity.IntegrationCredentials.SingleAsync(x => x.Id == h.CredentialId);
        credential.Permissions += " Change.Read Change.Write";
        await identity.SaveChangesAsync();
    }

    private static async Task SeedChangesAsync(IncidentReceiverTests.Harness h) => await h.EditAsync(db =>
    {
        db.Organizations.Add(new Organization { Id = "foreign-org", Name = "Foreign" });
        foreach (var name in new[] { "ChangeImplementationInProgress", "ChangeImplemented" })
            db.EmailTemplates.Add(new EmailTemplate { Name = name, Subject = "Synthetic change {{TicketRef}}", HtmlContent = "<p>Synthetic change lifecycle notification.</p>" });
        var template = JsonSerializer.Serialize(new ChangeTemplateDto { IsPreApproved = true, ExistingRunbookReference = "SOP-SYNTHETIC", ScopeOfChange = "Owned test", AffectedSystems = ["Test service"], ImplementationSteps = ["Apply"], ValidationSteps = ["Verify"], RollbackPlan = "Restore" });
        foreach (var (id, lifecycle, state) in new[] { ("draft", ChangeLifecycleState.Draft, TicketState.New), ("approved", ChangeLifecycleState.ApprovedForImplementation, TicketState.New), ("pending", ChangeLifecycleState.PendingApproval, TicketState.New), ("implemented", ChangeLifecycleState.ImplementedSuccess, TicketState.Resolved), ("incomplete", ChangeLifecycleState.ImplementationInProgress, TicketState.InProgress) })
            db.Changes.Add(new Change { Id = id, TrackingId = "CHG-" + id, Title = "Synthetic " + id, OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, RequestedForUserId = h.CustomerId, State = state, Priority = TicketPriority.High, LifecycleState = lifecycle, ChangeType = "Standard", ChangeTemplateJson = id == "incomplete" ? null : template, ImplementorUserId = "support-user", AssignedToId = "support-user", UpdatedAt = DateTime.UtcNow.AddDays(-1) });
        db.Changes.Add(new Change { Id = "foreign", Title = "Foreign", OrganizationId = "foreign-org", State = TicketState.New, LifecycleState = ChangeLifecycleState.Draft });
        db.ChangeApprovals.Add(new ChangeApproval { ChangeId = "pending", ApproverId = "support-user", ApproverName = "Synthetic support", ApproverEmail = "support@example.test", Status = ChangeApprovalStatus.Pending });
        db.TicketSlaStates.Add(new TicketSlaState { TicketId = "approved", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ResponseDueAt = DateTimeOffset.UtcNow.AddHours(1), ResolutionDueAt = DateTimeOffset.UtcNow.AddHours(4), Status = SlaStatus.InProgress });
        return Task.CompletedTask;
    });

    private static async Task<HttpResponseMessage> BoundedAsync(IncidentReceiverTests.Harness h, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        h.Authorize(request);
        return await h.Client.SendAsync(request);
    }

    private sealed record Snapshot(string Changes, string Approvals, string Sla, int Timeline, int Notifications, int Outbox, int SupportDeliveries, int Worklogs);
    private static async Task<Snapshot> SnapshotAsync(IncidentReceiverTests.Harness h)
    {
        await using var scope = h.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var changes = await db.Changes.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var approvals = await db.ChangeApprovals.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        var sla = await db.TicketSlaStates.AsNoTracking().OrderBy(x => x.TicketId).ToListAsync();
        return new(string.Join('|', changes.Select(x => $"{x.Id}:{(int)x.State}:{(int?)x.LifecycleState}:{x.UpdatedAt:O}:{x.ClosedAt:O}:{x.Priority}:{x.AssignedToId}")),
            string.Join('|', approvals.Select(x => $"{x.Id}:{x.Status}:{x.ReviewedAtUtc:O}:{x.ViewedAtUtc:O}")),
            JsonSerializer.Serialize(sla), await db.TicketTimelineEvents.CountAsync(), await db.Notifications.CountAsync(),
            await db.Set<MailboxOutboxEffect>().CountAsync(), await db.SupportNotificationDeliveries.CountAsync(), await db.WorkLogs.CountAsync());
    }
}
