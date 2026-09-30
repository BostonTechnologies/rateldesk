using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Helpdesk.API.Background;
using Helpdesk.API.Endpoints.Changes;
using Helpdesk.API.Endpoints.Incidents;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.RequestTasks;
using Helpdesk.API.Endpoints.WorkLogs;
using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Changes;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace Helpdesk.Tests.Api;

public sealed partial class LiveHistoryFilteringEndpointsTests
{
    [Fact]
    public async Task Requests_DefaultsToLiveAndHistoricOnlyReturnsResolved()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Requests.AddRange(
                new Request { Id = "req-live", TrackingId = "REQ-LIVE", Title = "Live request", State = TicketState.InProgress, OrganizationId = "org-1" },
                new Request { Id = "req-resolved", TrackingId = "REQ-DONE", Title = "Done request", State = TicketState.Resolved, OrganizationId = "org-1" });
        });

        var live = await harness.Client.GetFromJsonAsync<PagedResponse<RequestDto>>("/api/v1/requests?page=1&pageSize=20");
        var historic = await harness.Client.GetFromJsonAsync<PagedResponse<RequestDto>>("/api/v1/requests?page=1&pageSize=20&historicOnly=true");

        Assert.Collection(live!.Items, item => Assert.Equal("REQ-LIVE", item.TrackingId));
        Assert.Collection(historic!.Items, item => Assert.Equal("REQ-DONE", item.TrackingId));
    }

    [Fact]
    public async Task Changes_DefaultsToLiveAndHistoricOnlyReturnsImplementedLifecycleStates()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Changes.AddRange(
                new Change { Id = "chg-live", TrackingId = "CHG-LIVE", Title = "Live change", State = TicketState.New, LifecycleState = ChangeLifecycleState.ImplementationInProgress, OrganizationId = "org-1" },
                new Change { Id = "chg-success", TrackingId = "CHG-SUCCESS", Title = "Successful change", State = TicketState.New, LifecycleState = ChangeLifecycleState.ImplementedSuccess, OrganizationId = "org-1" },
                new Change { Id = "chg-backed-out", TrackingId = "CHG-BACKED-OUT", Title = "Backed out change", State = TicketState.Replied, LifecycleState = ChangeLifecycleState.ImplementedBackedOut, OrganizationId = "org-1" });
        });

        var live = await harness.Client.GetFromJsonAsync<PagedResponse<ChangeDto>>("/api/v1/changes?page=1&pageSize=20");
        var active = await harness.Client.GetFromJsonAsync<PagedResponse<ChangeDto>>("/api/v1/changes?page=1&pageSize=20&activeOnly=true");
        var historic = await harness.Client.GetFromJsonAsync<PagedResponse<ChangeDto>>("/api/v1/changes?page=1&pageSize=20&historicOnly=true");

        Assert.Collection(live!.Items, item => Assert.Equal("CHG-LIVE", item.TrackingId));
        Assert.Collection(active!.Items, item => Assert.Equal("CHG-LIVE", item.TrackingId));
        Assert.Equal(
            ["CHG-BACKED-OUT", "CHG-SUCCESS"],
            historic!.Items.Select(x => x.TrackingId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Changes_List_TreatsNullLifecycleStateAsDraft()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Changes.Add(new Change
            {
                Id = "chg-null-lifecycle",
                TrackingId = "CHG-NULL",
                Title = "Legacy change",
                State = TicketState.New,
                OrganizationId = "org-1",
                LifecycleState = null
            });
        });

        var live = await harness.Client.GetFromJsonAsync<PagedResponse<ChangeDto>>("/api/v1/changes?page=1&pageSize=20");

        var change = Assert.Single(live!.Items);
        Assert.Equal("CHG-NULL", change.TrackingId);
        Assert.Equal(ChangeLifecycleState.Draft, change.LifecycleState);
    }

    [Fact]
    public async Task Changes_Create_AssignsTrackingIdRequestedForAndImplementorAssignee()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-1", Name = "Example Organization" });
            db.Users.AddRange(
                new User
                {
                    Id = "requested-user",
                    Name = "Requested Person",
                    Email = "requested@example.com",
                    OrganizationId = "org-1",
                    Role = "User"
                },
                new User
                {
                    Id = "implementor-user",
                    Name = "Implementor Person",
                    Email = "implementor@example.com",
                    OrganizationId = "org-1",
                    Role = "Technician"
                });
        });

        var create = new CreateChangeDto
        {
            Title = "Tracking regression",
            Description = "Verify change identity and participants.",
            OrganizationId = "org-1",
            RequestedForUserId = "requested-user",
            ImplementorUserId = "implementor-user",
            ChangeType = "Standard",
            ImplementationStartAt = DateTime.UtcNow.AddDays(1),
            ImplementationEndAt = DateTime.UtcNow.AddDays(1).AddHours(1),
            ChangeTemplate = new ChangeTemplateDto()
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/changes", create);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<ChangeDto>();

        Assert.NotNull(created);
        Assert.StartsWith("CHG-", created!.TrackingId);
        Assert.Equal("requested-user", created.RequestedForUserId);
        Assert.Equal("Requested Person", created.RequestedForUserName);
        Assert.Equal("implementor-user", created.ImplementorUserId);
        Assert.Equal("Implementor Person", created.ImplementorUserName);
        Assert.Equal("implementor-user", created.AssignedToId);

        var live = await harness.Client.GetFromJsonAsync<PagedResponse<ChangeDto>>("/api/v1/changes?page=1&pageSize=20");
        var listed = Assert.Single(live!.Items);
        Assert.Equal(created.TrackingId, listed.TrackingId);
        Assert.Equal("Requested Person", listed.RequestedForUserName);
        Assert.Equal("Implementor Person", listed.ImplementorUserName);
        Assert.Equal("implementor-user", listed.AssignedToId);

        var createdEvent = Assert.IsType<ChangeCreatedDomainEvent>(Assert.Single(harness.DomainEvents.Events));
        Assert.Equal(created.TrackingId, createdEvent.TrackingId);
        Assert.Equal(created.TrackingId, createdEvent.Reference);
    }

    [Fact]
    public async Task Changes_Timeline_ReturnsSystemAndEmailEvents()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Changes.Add(new Change
            {
                Id = "chg-timeline",
                TrackingId = "CHG-TIMELINE",
                Title = "Timeline change",
                State = TicketState.New,
                OrganizationId = "org-1"
            });
            db.TicketTimelineEvents.AddRange(
                new TicketTimelineEvent
                {
                    TicketId = "chg-timeline",
                    EventType = TimelineEventType.SystemNotification,
                    CreatedByUserId = "system",
                    CreatedByUserName = "System",
                    MessageText = "Approval requested from Jane."
                },
                new TicketTimelineEvent
                {
                    TicketId = "chg-timeline",
                    EventType = TimelineEventType.EmailDelivery,
                    EmailStatus = EmailDeliveryStatus.Delivered,
                    EmailRecipient = "jane@example.com",
                    CreatedByUserId = "system",
                    CreatedByUserName = "System",
                    MessageText = "Email successfully delivered to jane@example.com"
                });
        });

        var timeline = await harness.Client.GetFromJsonAsync<List<TicketTimelineEventDto>>("/api/v1/changes/chg-timeline/timeline?order=asc");

        Assert.Equal(2, timeline!.Count);
        Assert.Contains(timeline, x => x.EventType == TimelineEventType.SystemNotification);
        Assert.Contains(timeline, x => x.EventType == TimelineEventType.EmailDelivery && x.EmailStatus == EmailDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task PublicApprovalView_RecordsFirstOpenOnly()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Changes.Add(new Change
            {
                Id = "chg-approval-view",
                TrackingId = "CHG-VIEW",
                Title = "Approval view change",
                State = TicketState.New,
                OrganizationId = "org-1",
                LifecycleState = ChangeLifecycleState.PendingApproval,
                Approvals =
                [
                    new ChangeApproval
                    {
                        ChangeId = "chg-approval-view",
                        ApproverEmail = "jane@example.com",
                        ApproverName = "Jane"
                    }
                ]
            });
        });

        var url = "/api/v1/changes/public/approval?trackingId=CHG-VIEW&email=jane%40example.com&token=token:CHG-VIEW:jane%40example.com";
        var first = await harness.Client.GetAsync(url);
        var second = await harness.Client.GetAsync(url);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var approvalView = await first.Content.ReadFromJsonAsync<PublicChangeApprovalDto>();
        Assert.True(approvalView!.CanReview);
        await harness.WithDbAsync(async db =>
        {
            var approval = await db.ChangeApprovals.SingleAsync(x => x.ChangeId == "chg-approval-view");
            Assert.NotNull(approval.ViewedAtUtc);
            var openEvents = await db.TicketTimelineEvents
                .Where(x => x.TicketId == "chg-approval-view" && x.MessageText!.Contains("opened approval link"))
                .CountAsync();
            Assert.Equal(1, openEvents);
        });
    }

    [Fact]
    public async Task PublicApprovalView_AllowsReadOnlyRequesterAndReturnsTemplateDetails()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-public-view", Name = "Example Organization" });
            db.Users.AddRange(
                new User
                {
                    Id = "requested-viewer",
                    Name = "Requested Viewer",
                    Email = "requested-viewer@example.com",
                    OrganizationId = "org-public-view",
                    Role = "User"
                },
                new User
                {
                    Id = "implementor-viewer",
                    Name = "Implementor Viewer",
                    Email = "implementor-viewer@example.com",
                    OrganizationId = "org-public-view",
                    Role = "Technician"
                });
            db.Changes.Add(new Change
            {
                Id = "chg-readonly-view",
                TrackingId = "CHG-READONLY",
                Title = "Read-only change",
                Description = "Requester view",
                State = TicketState.New,
                OrganizationId = "org-public-view",
                RequestedForUserId = "requested-viewer",
                ImplementorUserId = "implementor-viewer",
                ChangeTemplateJson = """
                    {
                      "scopeOfChange": "Upgrade the access switch firmware.",
                      "affectedSystems": ["Access switch", "Meeting room Wi-Fi"],
                      "implementationSteps": ["Download firmware", "Apply update"],
                      "validationSteps": ["Confirm switch stack online"],
                      "rollbackPlan": "Reload previous firmware image.",
                      "rollbackReference": "KB-42"
                    }
                    """
            });
        });

        var response = await harness.Client.GetAsync("/api/v1/changes/public/approval?trackingId=CHG-READONLY&email=requested-viewer%40example.com&token=token:CHG-READONLY:requested-viewer%40example.com");

        response.EnsureSuccessStatusCode();
        var view = await response.Content.ReadFromJsonAsync<PublicChangeApprovalDto>();
        Assert.NotNull(view);
        Assert.False(view!.CanReview);
        Assert.Equal("Upgrade the access switch firmware.", view.ScopeOfChange);
        Assert.Contains("Access switch", view.AffectedSystems);
        Assert.Contains("Apply update", view.ImplementationSteps);
        Assert.Contains("Confirm switch stack online", view.ValidationSteps);
        Assert.Equal("Reload previous firmware image.", view.RollbackPlan);
        Assert.Equal("KB-42", view.RollbackReference);

        await harness.WithDbAsync(async db =>
        {
            var openEvents = await db.TicketTimelineEvents
                .Where(x => x.TicketId == "chg-readonly-view" && x.MessageText!.Contains("opened approval link"))
                .CountAsync();
            Assert.Equal(0, openEvents);
        });
    }

    [Fact]
    public async Task ChangeLifecycle_AllowsLifecycleOnlyProgressionOutsideDraft()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-lifecycle", Name = "Example Organization" });
            db.Users.AddRange(
                new User { Id = "requested-lifecycle", Name = "Requested", Email = "requested-lifecycle@example.com", OrganizationId = "org-lifecycle", Role = "User" },
                new User { Id = "implementor-lifecycle", Name = "Implementor", Email = "implementor-lifecycle@example.com", OrganizationId = "org-lifecycle", Role = "Technician" });
            db.Changes.Add(new Change
            {
                Id = "chg-lifecycle-progress",
                TrackingId = "CHG-LIFECYCLE",
                Title = "Lifecycle progress",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                OrganizationId = "org-lifecycle",
                RequestedForUserId = "requested-lifecycle",
                ImplementorUserId = "implementor-lifecycle",
                LifecycleState = ChangeLifecycleState.ApprovedForImplementation
            });
        });

        var response = await harness.Client.PutAsJsonAsync(
            "/api/v1/changes/chg-lifecycle-progress",
            new UpdateChangeDto
            {
                State = TicketState.New,
                Priority = TicketPriority.Low,
                LifecycleState = ChangeLifecycleState.ImplementationInProgress
            });

        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(ChangeLifecycleState.ImplementationInProgress, updated!.LifecycleState);
    }

    [Fact]
    public async Task ChangeLifecycle_ImplementedMirrorsSharedStateToResolved()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-implemented", Name = "Example Organization" });
            db.Users.AddRange(
                new User { Id = "requested-implemented", Name = "Requested", Email = "requested-implemented@example.com", OrganizationId = "org-implemented", Role = "User" },
                new User { Id = "implementor-implemented", Name = "Implementor", Email = "implementor-implemented@example.com", OrganizationId = "org-implemented", Role = "Technician" });
            db.Changes.Add(new Change
            {
                Id = "chg-implemented",
                TrackingId = "CHG-IMPLEMENTED",
                Title = "Implemented lifecycle",
                State = TicketState.InProgress,
                Priority = TicketPriority.Low,
                OrganizationId = "org-implemented",
                RequestedForUserId = "requested-implemented",
                ImplementorUserId = "implementor-implemented",
                ChangeType = "Standard",
                LifecycleState = ChangeLifecycleState.ImplementationInProgress,
                ChangeTemplateJson = JsonSerializer.Serialize(new ChangeTemplateDto
                {
                    IsPreApproved = true,
                    ExistingRunbookReference = "SOP-1",
                    ScopeOfChange = "Firmware update",
                    AffectedSystems = ["Firewall"],
                    ImplementationSteps = ["Fail over", "Install firmware"],
                    ValidationSteps = ["Check HA status"],
                    RollbackPlan = "Revert firmware"
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        });

        var response = await harness.Client.PutAsJsonAsync(
            "/api/v1/changes/chg-implemented",
            new UpdateChangeDto
            {
                State = TicketState.InProgress,
                Priority = TicketPriority.Low,
                LifecycleState = ChangeLifecycleState.ImplementedSuccess
            });

        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(ChangeLifecycleState.ImplementedSuccess, updated!.LifecycleState);
        Assert.Equal(TicketState.Resolved, updated.State);
    }

    [Fact]
    public async Task ChangeLifecycle_BlocksLockedDetailUpdatesOutsideDraft()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-locked", Name = "Example Organization" });
            db.Users.AddRange(
                new User { Id = "requested-locked", Name = "Requested", Email = "requested-locked@example.com", OrganizationId = "org-locked", Role = "User" },
                new User { Id = "implementor-locked", Name = "Implementor", Email = "implementor-locked@example.com", OrganizationId = "org-locked", Role = "Technician" });
            db.Changes.Add(new Change
            {
                Id = "chg-locked-update",
                TrackingId = "CHG-LOCKED",
                Title = "Locked update",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                OrganizationId = "org-locked",
                RequestedForUserId = "requested-locked",
                ImplementorUserId = "implementor-locked",
                LifecycleState = ChangeLifecycleState.ApprovedForImplementation
            });
        });

        var response = await harness.Client.PutAsJsonAsync(
            "/api/v1/changes/chg-locked-update",
            new UpdateChangeDto
            {
                State = TicketState.New,
                Priority = TicketPriority.Low,
                LifecycleState = ChangeLifecycleState.ApprovedForImplementation,
                ChangeType = "Normal"
            });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var message = await response.Content.ReadAsStringAsync();
        Assert.Contains("locked", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeLifecycle_BlocksManualApprovalWhileApprovalsPending()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-pending", Name = "Example Organization" });
            db.Users.AddRange(
                new User { Id = "requested-pending", Name = "Requested", Email = "requested-pending@example.com", OrganizationId = "org-pending", Role = "User" },
                new User { Id = "implementor-pending", Name = "Implementor", Email = "implementor-pending@example.com", OrganizationId = "org-pending", Role = "Technician" });
            db.Changes.Add(new Change
            {
                Id = "chg-pending-approval",
                TrackingId = "CHG-PENDING",
                Title = "Pending approval",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                OrganizationId = "org-pending",
                RequestedForUserId = "requested-pending",
                ImplementorUserId = "implementor-pending",
                LifecycleState = ChangeLifecycleState.PendingApproval,
                Approvals =
                [
                    new ChangeApproval
                    {
                        ChangeId = "chg-pending-approval",
                        ApproverEmail = "approver@example.com",
                        ApproverName = "Approver",
                        Status = ChangeApprovalStatus.Pending
                    }
                ]
            });
        });

        var response = await harness.Client.PutAsJsonAsync(
            "/api/v1/changes/chg-pending-approval",
            new UpdateChangeDto
            {
                State = TicketState.New,
                Priority = TicketPriority.Low,
                LifecycleState = ChangeLifecycleState.ApprovedForImplementation
            });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var message = await response.Content.ReadAsStringAsync();
        Assert.Contains("approvals are pending", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuickState_TechnicianCanUpdateIncidentAndRequestState()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-1", Name = "Technician organization" },
                new Organization { Id = "org-2", Name = "Foreign organization" });
            db.Users.Add(new User
            {
                Id = "admin-1",
                Name = "Technician One",
                Email = "technician@example.com",
                OrganizationId = "org-1",
                Role = "Technician"
            });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "admin-1",
                OrganizationId = "org-1",
                RoleKey = ScopedRoleCatalog.Technician
            });
            db.Incidents.AddRange(
                new Incident { Id = "inc-quick-state", TrackingId = "INC-QUICK", Title = "Quick incident", State = TicketState.New, OrganizationId = "org-1" },
                new Incident { Id = "inc-quick-state-foreign", TrackingId = "INC-QUICK-FOREIGN", Title = "Foreign quick incident", State = TicketState.New, OrganizationId = "org-2" });
            db.Requests.AddRange(
                new Request { Id = "req-quick-state", TrackingId = "REQ-QUICK", Title = "Quick request", State = TicketState.New, OrganizationId = "org-1" },
                new Request { Id = "req-quick-state-foreign", TrackingId = "REQ-QUICK-FOREIGN", Title = "Foreign quick request", State = TicketState.New, OrganizationId = "org-2" });
        });
        harness.UseRole("Technician");

        var incidentResponse = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-quick-state/state",
            new { NewState = TicketState.InProgress });
        var requestResponse = await harness.Client.PostAsJsonAsync(
            "/api/v1/requests/req-quick-state/state",
            new { NewState = TicketState.OnHold });
        var foreignIncidentResponse = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-quick-state-foreign/state",
            new { NewState = TicketState.InProgress });
        var foreignRequestResponse = await harness.Client.PostAsJsonAsync(
            "/api/v1/requests/req-quick-state-foreign/state",
            new { NewState = TicketState.OnHold });

        incidentResponse.EnsureSuccessStatusCode();
        requestResponse.EnsureSuccessStatusCode();
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreignIncidentResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreignRequestResponse.StatusCode);
        await harness.WithDbAsync(async db =>
        {
            Assert.Equal(TicketState.InProgress, (await db.Incidents.FindAsync("inc-quick-state"))!.State);
            Assert.Equal(TicketState.OnHold, (await db.Requests.FindAsync("req-quick-state"))!.State);
            Assert.Equal(TicketState.New, (await db.Incidents.FindAsync("inc-quick-state-foreign"))!.State);
            Assert.Equal(TicketState.New, (await db.Requests.FindAsync("req-quick-state-foreign"))!.State);
        });
    }

    [Fact]
    public async Task QuickChangeLifecycle_TechnicianCanMoveApprovedChangeForward()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-quick-approved", Name = "Example Organization" });
            AddTechnicianChangeManagerAccess(db, "org-quick-approved");
            db.Changes.Add(new Change
            {
                Id = "chg-quick-approved",
                TrackingId = "CHG-QUICK-APPROVED",
                Title = "Quick approved",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                OrganizationId = "org-quick-approved",
                LifecycleState = ChangeLifecycleState.ApprovedForImplementation
            });
        });
        harness.UseRole("Technician");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/chg-quick-approved/lifecycle",
            new { LifecycleState = ChangeLifecycleState.ImplementationInProgress });

        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(ChangeLifecycleState.ImplementationInProgress, updated!.LifecycleState);
    }

    [Fact]
    public async Task QuickChangeLifecycle_BlocksApprovedBackToDraft()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-quick-block", Name = "Example Organization" });
            AddTechnicianChangeManagerAccess(db, "org-quick-block");
            db.Changes.Add(new Change
            {
                Id = "chg-quick-block",
                TrackingId = "CHG-QUICK-BLOCK",
                Title = "Quick block",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                OrganizationId = "org-quick-block",
                LifecycleState = ChangeLifecycleState.ApprovedForImplementation
            });
        });
        harness.UseRole("Technician");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/chg-quick-block/lifecycle",
            new { LifecycleState = ChangeLifecycleState.Draft });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task QuickChangeLifecycle_PendingApprovalCanMoveBackToDraftAndResetApprovals()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-quick-draft", Name = "Example Organization" });
            AddTechnicianChangeManagerAccess(db, "org-quick-draft");
            db.Changes.Add(new Change
            {
                Id = "chg-quick-draft",
                TrackingId = "CHG-QUICK-DRAFT",
                Title = "Quick draft",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                OrganizationId = "org-quick-draft",
                LifecycleState = ChangeLifecycleState.PendingApproval,
                Approvals =
                [
                    new ChangeApproval
                    {
                        ChangeId = "chg-quick-draft",
                        ApproverEmail = "approver@example.com",
                        ApproverName = "Approver",
                        Status = ChangeApprovalStatus.Reviewed,
                        ReviewedAtUtc = DateTimeOffset.UtcNow,
                        ViewedAtUtc = DateTimeOffset.UtcNow
                    }
                ]
            });
        });
        harness.UseRole("Technician");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/chg-quick-draft/lifecycle",
            new { LifecycleState = ChangeLifecycleState.Draft });

        response.EnsureSuccessStatusCode();
        await harness.WithDbAsync(async db =>
        {
            var change = await db.Changes.FindAsync("chg-quick-draft");
            var approval = await db.ChangeApprovals.SingleAsync(x => x.ChangeId == "chg-quick-draft");
            Assert.Equal(ChangeLifecycleState.Draft, change!.LifecycleState);
            Assert.Equal(ChangeApprovalStatus.Pending, approval.Status);
            Assert.Null(approval.ReviewedAtUtc);
            Assert.Null(approval.ViewedAtUtc);
        });
    }

    [Fact]
    public async Task QuickChangeLifecycle_ImplementedMirrorsSharedStateToResolved()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-quick-implemented", Name = "Example Organization" });
            AddTechnicianChangeManagerAccess(db, "org-quick-implemented");
            db.Changes.Add(new Change
            {
                Id = "chg-quick-implemented",
                TrackingId = "CHG-QUICK-IMPLEMENTED",
                Title = "Quick implemented",
                State = TicketState.InProgress,
                Priority = TicketPriority.Low,
                OrganizationId = "org-quick-implemented",
                ChangeType = "Standard",
                LifecycleState = ChangeLifecycleState.ImplementationInProgress,
                ChangeTemplateJson = JsonSerializer.Serialize(new ChangeTemplateDto
                {
                    IsPreApproved = true,
                    ExistingRunbookReference = "SOP-1",
                    ScopeOfChange = "Firmware update",
                    AffectedSystems = ["Firewall"],
                    ImplementationSteps = ["Install firmware"],
                    ValidationSteps = ["Check HA status"],
                    RollbackPlan = "Revert firmware"
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        });
        harness.UseRole("Technician");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/chg-quick-implemented/lifecycle",
            new { LifecycleState = ChangeLifecycleState.ImplementedBackedOut });

        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<ChangeDto>();
        Assert.Equal(ChangeLifecycleState.ImplementedBackedOut, updated!.LifecycleState);
        Assert.Equal(TicketState.Resolved, updated.State);
    }

    [Fact]
    public async Task QuickChangeLifecycle_RequiresScopedManagerAccess()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-1", Name = "Organization One" });
            db.Users.Add(new User { Id = "self-service-1", Name = "Self-service user", Email = "self-service@example.com", OrganizationId = "org-1", Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "self-service-1", OrganizationId = "org-1", RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.Changes.Add(new Change
            {
                Id = "chg-self-service-lifecycle",
                TrackingId = "CHG-SELF-LIFECYCLE",
                Title = "Self-service change",
                OrganizationId = "org-1",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                LifecycleState = ChangeLifecycleState.ApprovedForImplementation
            });
        });
        harness.UseRole("SelfService");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/chg-self-service-lifecycle/lifecycle",
            new { LifecycleState = ChangeLifecycleState.ImplementationInProgress });

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        await harness.WithDbAsync(async db =>
        {
            var change = await db.Changes.FindAsync("chg-self-service-lifecycle");
            Assert.Equal(ChangeLifecycleState.ApprovedForImplementation, change!.LifecycleState);
        });
    }

    [Fact]
    public async Task RequestTasks_DefaultsToActionableStatusesAndHistoricOnlyReturnsCompletedAndSkipped()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Requests.Add(new Request { Id = "req-1", TrackingId = "REQ-1", Title = "Parent request", State = TicketState.InProgress, OrganizationId = "org-1" });
            db.RequestTasks.AddRange(
                NewTask("task-pending", "Pending task", RequestTaskStatus.Pending),
                NewTask("task-progress", "In progress task", RequestTaskStatus.InProgress),
                NewTask("task-failed", "Failed task", RequestTaskStatus.Failed),
                NewTask("task-completed", "Completed task", RequestTaskStatus.Completed),
                NewTask("task-skipped", "Skipped task", RequestTaskStatus.Skipped));
        });

        var live = await harness.Client.GetFromJsonAsync<PagedResponse<RequestTaskListItemDto>>("/api/v1/request-tasks?page=1&pageSize=20&assignedToMe=false");
        var historic = await harness.Client.GetFromJsonAsync<PagedResponse<RequestTaskListItemDto>>("/api/v1/request-tasks?page=1&pageSize=20&assignedToMe=false&historicOnly=true");

        Assert.Equal(
            new[] { RequestTaskStatus.Pending, RequestTaskStatus.InProgress, RequestTaskStatus.Failed },
            live!.Items.Select(x => x.Status).OrderBy(x => x));
        Assert.Equal(
            new[] { RequestTaskStatus.Completed, RequestTaskStatus.Skipped },
            historic!.Items.Select(x => x.Status).OrderBy(x => x));
    }

    [Fact]
    public async Task RequestTasks_RequestManagerSeesOnlyAllowedTenantTasks()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-1", Name = "Allowed tenant" },
                new Organization { Id = "org-2", Name = "Other tenant" });
            db.Customers.Add(new Customer
            {
                Id = "customer-request-manager",
                Name = "Request manager",
                Email = "operator@example.test",
                OrganizationId = "org-1"
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = "customer-request-manager",
                OidcIssuer = "https://id.example.test",
                OidcSubject = "operator",
                InviteStatus = CustomerInviteStatus.Active
            });
            db.Requests.AddRange(
                new Request { Id = "req-1", TrackingId = "REQ-1", Title = "Tenant request", State = TicketState.InProgress, OrganizationId = "org-1" },
                new Request { Id = "req-2", TrackingId = "REQ-2", Title = "Other tenant request", State = TicketState.InProgress, OrganizationId = "org-2" });
            db.RequestTasks.AddRange(
                NewTask("task-tenant", "Tenant task", RequestTaskStatus.Pending),
                new RequestTask
                {
                    Id = "task-other",
                    TrackingId = "task-other",
                    Title = "Other tenant task",
                    RequestId = "req-2",
                    Status = RequestTaskStatus.Pending,
                    Type = RequestTaskType.Manual,
                    OrganizationId = "org-2"
                });
        });
        harness.UseRole("Request.Manager");

        var response = await harness.Client.GetFromJsonAsync<PagedResponse<RequestTaskListItemDto>>(
            "/api/v1/request-tasks?page=1&pageSize=20&assignedToMe=false");

        var item = Assert.Single(response!.Items);
        Assert.Equal("task-tenant", item.Id);
    }

    [Fact]
    public async Task RequestTasks_CreateDoesNotLinkAnAccessibleTenantToAnotherTenantsRequest()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-1", Name = "Allowed tenant" },
                new Organization { Id = "org-2", Name = "Other tenant" });
            db.Customers.Add(new Customer
            {
                Id = "customer-request-manager",
                Name = "Request manager",
                Email = "operator@example.test",
                OrganizationId = "org-1"
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = "customer-request-manager",
                OidcIssuer = "https://id.example.test",
                OidcSubject = "operator",
                InviteStatus = CustomerInviteStatus.Active
            });
            db.Requests.Add(new Request
            {
                Id = "req-other",
                TrackingId = "REQ-OTHER",
                Title = "Other tenant request",
                State = TicketState.InProgress,
                OrganizationId = "org-2"
            });
        });
        harness.UseRole("Request.Manager");

        var response = await harness.Client.PostAsJsonAsync("/api/v1/request-tasks", new
        {
            Title = "Cross-tenant task",
            Description = "Must not be created",
            RequestId = "req-other",
            OrganizationId = "org-1"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await harness.WithDbAsync(db =>
        {
            Assert.Empty(db.RequestTasks);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RequestTasks_PutDoesNotChangeTheAuthorizedParentRequest()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-1", Name = "Allowed tenant" },
                new Organization { Id = "org-2", Name = "Other tenant" });
            db.Customers.Add(new Customer
            {
                Id = "customer-request-manager",
                Name = "Request manager",
                Email = "operator@example.test",
                OrganizationId = "org-1"
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = "customer-request-manager",
                OidcIssuer = "https://id.example.test",
                OidcSubject = "operator",
                InviteStatus = CustomerInviteStatus.Active
            });
            db.Requests.AddRange(
                new Request { Id = "req-allowed", TrackingId = "REQ-ALLOWED", Title = "Allowed request", State = TicketState.InProgress, OrganizationId = "org-1" },
                new Request { Id = "req-other", TrackingId = "REQ-OTHER", Title = "Other tenant request", State = TicketState.InProgress, OrganizationId = "org-2" });
            db.RequestTasks.Add(new RequestTask
            {
                Id = "task-allowed",
                TrackingId = "TASK-ALLOWED",
                Title = "Allowed task",
                RequestId = "req-allowed",
                OrganizationId = "org-1",
                Status = RequestTaskStatus.Pending,
                Type = RequestTaskType.Manual,
                State = TicketState.New
            });
        });
        harness.UseRole("Request.Manager");

        var response = await harness.Client.PutAsJsonAsync("/api/v1/request-tasks/task-allowed", new RequestTask
        {
            Id = "task-allowed",
            TrackingId = "TASK-ALLOWED",
            Title = "Allowed task",
            RequestId = "req-other",
            OrganizationId = "org-1",
            Status = RequestTaskStatus.Pending,
            Type = RequestTaskType.Manual,
            State = TicketState.New
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await harness.WithDbAsync(db =>
        {
            Assert.Equal("req-allowed", db.RequestTasks.Single(task => task.Id == "task-allowed").RequestId);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RequestTasks_PatchUpdatesOnlyTheBoundedMetadataFields()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Requests.Add(new Request { Id = "req-1", TrackingId = "REQ-1", Title = "Parent request", State = TicketState.InProgress, OrganizationId = "org-1" });
            db.RequestTasks.Add(new RequestTask
            {
                Id = "task-patch",
                TrackingId = "TASK-PATCH",
                Title = "Original task",
                Description = "Original description",
                RequestId = "req-1",
                OrganizationId = "org-1",
                Status = RequestTaskStatus.Pending,
                Type = RequestTaskType.Manual,
                State = TicketState.New,
                DueDate = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc)
            });
        });

        var response = await harness.Client.PatchAsJsonAsync(
            "/api/v1/request-tasks/task-patch",
            new { Title = "Updated task", Priority = TicketPriority.High, ClearDueDate = true, LinkedAssetIds = new[] { "asset-1" } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await harness.WithDbAsync(db =>
        {
            var task = db.RequestTasks.Single(x => x.Id == "task-patch");
            Assert.Equal("Updated task", task.Title);
            Assert.Equal("Original description", task.Description);
            Assert.Equal(TicketPriority.High, task.Priority);
            Assert.Null(task.DueDate);
            Assert.Equal(["asset-1"], task.LinkedAssetIds);
            Assert.Equal("req-1", task.RequestId);
            Assert.Equal(RequestTaskStatus.Pending, task.Status);
            Assert.Equal(TicketState.New, task.State);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Incidents_List_CanFilterByOrganizationId()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Incidents.AddRange(
                new Incident { Id = "inc-org-1-a", TrackingId = "INC-ORG-1-A", Title = "Org one A", State = TicketState.InProgress, OrganizationId = "org-1" },
                new Incident { Id = "inc-org-1-b", TrackingId = "INC-ORG-1-B", Title = "Org one B", State = TicketState.New, OrganizationId = "org-1" },
                new Incident { Id = "inc-org-2-a", TrackingId = "INC-ORG-2-A", Title = "Org two A", State = TicketState.InProgress, OrganizationId = "org-2" });
        });

        var response = await harness.Client.GetFromJsonAsync<PagedResponse<IncidentDto>>(
            "/api/v1/incidents?page=1&pageSize=10&organizationId=org-1&summaryOnly=true");

        Assert.Equal(["INC-ORG-1-A", "INC-ORG-1-B"], response!.Items.Select(x => x.TrackingId).OrderBy(x => x).ToArray());
        Assert.All(response.Items, x => Assert.Equal("org-1", x.OrganizationId));
    }

    [Fact]
    public async Task Incidents_List_CanExcludeCurrentIncident()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Incidents.AddRange(
                new Incident { Id = "inc-picker-source", TrackingId = "INC-PICKER-SOURCE", Title = "Source", State = TicketState.InProgress, OrganizationId = "org-1" },
                new Incident { Id = "inc-picker-target", TrackingId = "INC-PICKER-TARGET", Title = "Target", State = TicketState.InProgress, OrganizationId = "org-1" });
        });

        var response = await harness.Client.GetFromJsonAsync<PagedResponse<IncidentDto>>(
            "/api/v1/incidents?page=1&pageSize=10&organizationId=org-1&excludeId=inc-picker-source&summaryOnly=true");

        var item = Assert.Single(response!.Items);
        Assert.Equal("INC-PICKER-TARGET", item.TrackingId);
    }

    [Fact]
    public async Task Incidents_List_CanFilterByRequesterEmail()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-filter-1", Name = "Example Organization" },
                new Organization { Id = "org-filter-2", Name = "Other Org" });
            db.Customers.AddRange(
                new Customer { Id = "cust-filter-1", Name = "Example User", Email = "user@example.com", OrganizationId = "org-filter-1", IsEnabled = true },
                new Customer { Id = "cust-filter-2", Name = "Other", Email = "other@example.com", OrganizationId = "org-filter-2", IsEnabled = true });
            db.Incidents.AddRange(
                new Incident { Id = "inc-filter-1", TrackingId = "INC-FILTER-1", Title = "VPN", State = TicketState.InProgress, OrganizationId = "org-filter-1", CustomerId = "cust-filter-1", RequesterEmail = "requester@example.com" },
                new Incident { Id = "inc-filter-2", TrackingId = "INC-FILTER-2", Title = "Printer", State = TicketState.InProgress, OrganizationId = "org-filter-2", CustomerId = "cust-filter-2", RequesterEmail = "other@example.com" });
        });

        var response = await harness.Client.GetFromJsonAsync<PagedResponse<IncidentDto>>(
            "/api/v1/incidents?page=1&pageSize=10&requesterEmail=USER@EXAMPLE.COM&summaryOnly=true");

        var item = Assert.Single(response!.Items);
        Assert.Equal("INC-FILTER-1", item.TrackingId);
        Assert.Equal("requester@example.com", item.RequesterEmail);
        Assert.Equal("user@example.com", item.CustomerEmail);
        Assert.Equal("Example Organization", item.CustomerOrgName);
    }

    [Fact]
    public async Task Incidents_GetListAndUpdate_ReturnConsistentCustomerProjection()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        var createdAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-projection", Name = "Projection Org" });
            db.Customers.Add(new Customer { Id = "cust-projection", Name = "Projection Customer", Email = "projection@example.com", OrganizationId = "org-projection", IsEnabled = true });
            db.Incidents.Add(new Incident
            {
                Id = "inc-projection",
                TrackingId = "INC-PROJECTION",
                Title = "Projection",
                State = TicketState.New,
                Priority = TicketPriority.Low,
                OrganizationId = "org-projection",
                CustomerId = "cust-projection",
                RequesterEmail = "projection@example.com",
                CreatedAt = createdAt
            });
        });

        var getResponse = await harness.Client.GetAsync("/api/v1/incidents/inc-projection");
        var getBody = await getResponse.Content.ReadAsStringAsync();
        Assert.True(getResponse.IsSuccessStatusCode, getBody);
        var get = JsonSerializer.Deserialize<IncidentDto>(getBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var list = await harness.Client.GetFromJsonAsync<PagedResponse<IncidentDto>>(
            "/api/v1/incidents?page=1&pageSize=10&requesterEmail=projection@example.com&summaryOnly=true");
        var update = await harness.Client.PutAsJsonAsync(
            "/api/v1/incidents/inc-projection",
            new UpdateIncidentDto { State = TicketState.InProgress, Priority = TicketPriority.High });
        var updateBody = await update.Content.ReadAsStringAsync();
        Assert.True(update.IsSuccessStatusCode, updateBody);
        var updated = JsonSerializer.Deserialize<IncidentDto>(updateBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        AssertConsistentProjection(get!);
        AssertConsistentProjection(Assert.Single(list!.Items));
        AssertConsistentProjection(updated!);
        Assert.Equal(TicketState.InProgress, updated!.State);

        static void AssertConsistentProjection(IncidentDto dto)
        {
            Assert.Equal("Projection Org", dto.CustomerOrgName);
            Assert.Equal("cust-projection", dto.CustomerId);
            Assert.Equal("Projection Customer", dto.CustomerName);
            Assert.Equal("projection@example.com", dto.CustomerEmail);
            Assert.Equal("projection@example.com", dto.RequesterEmail);
            Assert.NotNull(dto.UpdatedAt);
        }
    }

    [Fact]
    public async Task IncidentRelations_RejectsSelfLink()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-source/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-SOURCE",
                RelationType = TicketRelationType.RelatedTo
            });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IncidentRelations_RejectsDuplicateRelation()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);
        var dto = new CreateTicketRelationDto
        {
            TargetTicketId = "INC-TARGET",
            RelationType = TicketRelationType.RelatedTo,
            CloseSourceTicket = false
        };

        var first = await harness.Client.PostAsJsonAsync("/api/v1/incidents/inc-source/relations", dto);
        var second = await harness.Client.PostAsJsonAsync("/api/v1/incidents/inc-source/relations", dto);

        first.EnsureSuccessStatusCode();
        Assert.Equal(System.Net.HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task IncidentRelations_RejectsOrganizationMismatch()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-source/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-OTHER-ORG",
                RelationType = TicketRelationType.RelatedTo
            });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IncidentRelations_DuplicateOfClosesSourceIncident()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/INC-SOURCE/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-TARGET",
                RelationType = TicketRelationType.DuplicateOf,
                CloseSourceTicket = false
            });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TicketRelationCreateResultDto>();
        Assert.Equal(TicketRelationType.DuplicateOf, result!.Relation.RelationType);
        Assert.Equal(TicketState.Resolved, result.SourceIncident.State);

        await harness.WithDbAsync(async db =>
        {
            var source = await db.Incidents.FindAsync("inc-source");
            Assert.Equal(TicketState.Resolved, source!.State);
            Assert.NotNull(source.ClosedAt);
            Assert.Contains(await db.TicketRelations.ToListAsync(), x =>
                x.SourceTicketId == "inc-source" &&
                x.TargetTicketId == "inc-target" &&
                x.RelationType == TicketRelationType.DuplicateOf);
        });
    }

    [Fact]
    public async Task IncidentRelations_RelatedToRespectsOptionalClose()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);

        var keepOpen = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-related-open/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-TARGET",
                RelationType = TicketRelationType.RelatedTo,
                CloseSourceTicket = false
            });
        var close = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-related-close/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-TARGET",
                RelationType = TicketRelationType.RelatedTo,
                CloseSourceTicket = true
            });

        keepOpen.EnsureSuccessStatusCode();
        close.EnsureSuccessStatusCode();
        await harness.WithDbAsync(async db =>
        {
            var openSource = await db.Incidents.FindAsync("inc-related-open");
            var closedSource = await db.Incidents.FindAsync("inc-related-close");
            Assert.Equal(TicketState.InProgress, openSource!.State);
            Assert.Null(openSource.ClosedAt);
            Assert.Equal(TicketState.Resolved, closedSource!.State);
            Assert.NotNull(closedSource.ClosedAt);
        });
    }

    [Fact]
    public async Task IncidentRelations_CopiesSourceRequesterAndListenersToParent()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-source/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-TARGET",
                RelationType = TicketRelationType.RelatedTo,
                CloseSourceTicket = false
            });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TicketRelationCreateResultDto>();
        Assert.Equal(["source-cc@example.com", "source-requester@example.com"], result!.AddedParentListeners.OrderBy(x => x));

        await harness.WithDbAsync(async db =>
        {
            var target = await db.Incidents.FindAsync("inc-target");
            Assert.Contains("existing@example.com", target!.CcRecipients);
            Assert.Contains("source-requester@example.com", target.CcRecipients);
            Assert.Contains("source-cc@example.com", target.CcRecipients);
            Assert.DoesNotContain("target-requester@example.com", target.CcRecipients);
            Assert.Equal(target.CcRecipients.Count, target.CcRecipients.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public async Task IncidentRelations_ListReturnsIncomingAndOutgoingLinks()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);
        var create = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-source/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-TARGET",
                RelationType = TicketRelationType.RelatedTo,
                CloseSourceTicket = false
            });
        create.EnsureSuccessStatusCode();

        var outgoing = await GetRelationsAsync(harness.Client, "/api/v1/incidents/INC-SOURCE/relations");
        var incoming = await GetRelationsAsync(harness.Client, "/api/v1/incidents/inc-target/relations");

        var outgoingRelation = Assert.Single(outgoing!);
        Assert.True(outgoingRelation.IsOutgoing);
        Assert.Equal("INC-TARGET", outgoingRelation.TargetTrackingId);

        var incomingRelation = Assert.Single(incoming!);
        Assert.False(incomingRelation.IsOutgoing);
        Assert.Equal("INC-SOURCE", incomingRelation.SourceTrackingId);
    }

    [Fact]
    public async Task IncidentRelations_HideRelatedIncidentsOutsideSelfServiceScope()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-1", Name = "Organization One" });
            db.Users.Add(new User
            {
                Id = "self-service-1",
                Name = "Self-service user",
                Email = "self-service@example.com",
                OrganizationId = "org-1",
                Role = "User"
            });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "self-service-1",
                OrganizationId = "org-1",
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            AddSelfServiceCustomerAccess(db);
        });
        await SeedRelationIncidentsAsync(harness, "self-service@example.com", SelfServiceCustomerId);
        await harness.SeedAsync(db => db.TicketRelations.Add(new TicketRelation
        {
            SourceTicketId = "inc-source",
            TargetTicketId = "inc-target",
            RelationType = TicketRelationType.RelatedTo,
            CreatedByUserId = "admin-1",
            CreatedByUserName = "Admin One"
        }));
        harness.UseRole("SelfService");

        var ownRelations = await GetRelationsAsync(harness.Client, "/api/v1/incidents/inc-source/relations");
        var foreignRelations = await harness.Client.GetAsync("/api/v1/incidents/inc-target/relations");

        Assert.Empty(ownRelations);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreignRelations.StatusCode);
    }

    [Fact]
    public async Task IncidentUpdate_RequiresScopedIncidentManagerAccess()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-1", Name = "Organization One" });
            db.Users.Add(new User
            {
                Id = "self-service-1",
                Name = "Self-service user",
                Email = "self-service@example.com",
                OrganizationId = "org-1",
                Role = "User"
            });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "self-service-1",
                OrganizationId = "org-1",
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            db.Incidents.Add(new Incident
            {
                Id = "inc-self-service-update",
                TrackingId = "INC-SELF-UPDATE",
                Title = "Self-service incident",
                OrganizationId = "org-1",
                RequesterEmail = "self-service@example.com",
                State = TicketState.New,
                Priority = TicketPriority.Low
            });
        });
        harness.UseRole("SelfService");

        var response = await harness.Client.PutAsJsonAsync(
            "/api/v1/incidents/inc-self-service-update",
            new UpdateIncidentDto { State = TicketState.Resolved });

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        await harness.WithDbAsync(async db =>
        {
            Assert.Equal(TicketState.New, (await db.Incidents.FindAsync("inc-self-service-update"))!.State);
        });
    }

    [Fact]
    public async Task IncidentDetail_ResolvesScopedAccessFromPersistedGrants()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-1", Name = "Organization One" },
                new Organization { Id = "org-2", Name = "Organization Two" });
            db.Users.Add(new User { Id = "self-service-1", Name = "Self-service user", Email = "self-service@example.com", OrganizationId = "org-1", Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "self-service-1", OrganizationId = "org-1", RoleKey = ScopedRoleCatalog.SelfServiceUser });
            AddSelfServiceCustomerAccess(db);
            db.Incidents.AddRange(
                new Incident { Id = "inc-self-service-detail", TrackingId = "INC-SELF-DETAIL", Title = "Self-service incident", OrganizationId = "org-1", CustomerId = SelfServiceCustomerId, RequesterEmail = "self-service@example.com" },
                new Incident { Id = "inc-foreign-detail", TrackingId = "INC-FOREIGN-DETAIL", Title = "Foreign incident", OrganizationId = "org-2", RequesterEmail = "foreign@example.com" });
        });
        harness.UseRole("SelfService");

        var own = await harness.Client.GetAsync("/api/v1/incidents/inc-self-service-detail");
        var foreign = await harness.Client.GetAsync("/api/v1/incidents/inc-foreign-detail");

        Assert.Equal(System.Net.HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task TicketLists_ResolveScopedAccessFromPersistedGrants()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-1", Name = "Organization One" },
                new Organization { Id = "org-2", Name = "Organization Two" });
            db.Users.Add(new User { Id = "self-service-1", Name = "Self-service user", Email = "self-service@example.com", OrganizationId = "org-1", Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "self-service-1", OrganizationId = "org-1", RoleKey = ScopedRoleCatalog.SelfServiceUser });
            AddSelfServiceCustomerAccess(db);
            db.Incidents.AddRange(
                new Incident { Id = "inc-list-own", TrackingId = "INC-LIST-OWN", Title = "Own incident", OrganizationId = "org-1", CustomerId = SelfServiceCustomerId, RequesterEmail = "self-service@example.com" },
                new Incident { Id = "inc-list-foreign", TrackingId = "INC-LIST-FOREIGN", Title = "Foreign incident", OrganizationId = "org-2", RequesterEmail = "foreign@example.com" });
            db.Requests.AddRange(
                new Request { Id = "req-list-own", TrackingId = "REQ-LIST-OWN", Title = "Own request", OrganizationId = "org-1", CustomerId = SelfServiceCustomerId, RequesterEmail = "self-service@example.com" },
                new Request { Id = "req-list-foreign", TrackingId = "REQ-LIST-FOREIGN", Title = "Foreign request", OrganizationId = "org-2", RequesterEmail = "foreign@example.com" });
            db.Changes.AddRange(
                new Change { Id = "chg-list-own", TrackingId = "CHG-LIST-OWN", Title = "Own change", OrganizationId = "org-1", CustomerId = SelfServiceCustomerId, RequesterEmail = "self-service@example.com" },
                new Change { Id = "chg-list-foreign", TrackingId = "CHG-LIST-FOREIGN", Title = "Foreign change", OrganizationId = "org-2", RequesterEmail = "foreign@example.com" });
        });
        harness.UseRole("SelfService");

        var incidents = await harness.Client.GetFromJsonAsync<PagedResponse<IncidentDto>>("/api/v1/incidents?page=1&pageSize=20");
        var requests = await harness.Client.GetFromJsonAsync<PagedResponse<RequestDto>>("/api/v1/requests?page=1&pageSize=20");
        var changes = await harness.Client.GetFromJsonAsync<PagedResponse<ChangeDto>>("/api/v1/changes?page=1&pageSize=20");

        Assert.Equal(["INC-LIST-OWN"], incidents!.Items.Select(item => item.TrackingId));
        Assert.Equal(["REQ-LIST-OWN"], requests!.Items.Select(item => item.TrackingId));
        // SelfServiceUser deliberately contains Incident.User and Request.User,
        // not Change.User. The old assertion depended on a list-query leak that
        // accepted unrelated organization membership as a change permission.
        Assert.Empty(changes!.Items);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await harness.Client.GetAsync("/api/v1/changes/chg-list-own")).StatusCode);

        await harness.SeedAsync(db =>
        {
            db.Roles.Add(new Role
            {
                Key = "custom.own-change-reader", Name = "Own change access", Scope = RoleScopeKind.Tenant,
                OwnerOrganizationId = "org-1", Permissions = [new RolePermission { Permission = HelpdeskPermissions.ChangeUser }]
            });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "self-service-1", OrganizationId = "org-1", RoleKey = "custom.own-change-reader"
            });
        });
        changes = await harness.Client.GetFromJsonAsync<PagedResponse<ChangeDto>>("/api/v1/changes?page=1&pageSize=20");
        Assert.Equal(["CHG-LIST-OWN"], changes!.Items.Select(item => item.TrackingId));
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await harness.Client.GetAsync("/api/v1/changes/chg-list-own")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await harness.Client.GetAsync("/api/v1/changes/chg-list-foreign")).StatusCode);
    }

    [Fact]
    public async Task RequestAndChangeMutations_RequireScopedManagerAccess()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-1", Name = "Organization One" });
            db.Users.Add(new User { Id = "self-service-1", Name = "Self-service user", Email = "self-service@example.com", OrganizationId = "org-1", Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "self-service-1", OrganizationId = "org-1", RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.Requests.Add(new Request { Id = "req-self-service-update", TrackingId = "REQ-SELF-UPDATE", Title = "Self-service request", OrganizationId = "org-1", RequesterEmail = "self-service@example.com", State = TicketState.New, Priority = TicketPriority.Low });
            db.Changes.Add(new Change { Id = "chg-self-service-update", TrackingId = "CHG-SELF-UPDATE", Title = "Self-service change", OrganizationId = "org-1", RequesterEmail = "self-service@example.com", State = TicketState.New, Priority = TicketPriority.Low });
        });
        harness.UseRole("SelfService");

        var requestUpdate = await harness.Client.PutAsJsonAsync("/api/v1/requests/req-self-service-update", new UpdateRequestDto { State = TicketState.Resolved });
        var changeUpdate = await harness.Client.PutAsJsonAsync("/api/v1/changes/chg-self-service-update", new UpdateChangeDto { State = TicketState.Resolved, Priority = TicketPriority.Low });
        var changeDelete = await harness.Client.DeleteAsync("/api/v1/changes/chg-self-service-update");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, requestUpdate.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, changeUpdate.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, changeDelete.StatusCode);
        await harness.WithDbAsync(async db =>
        {
            Assert.Equal(TicketState.New, (await db.Requests.FindAsync("req-self-service-update"))!.State);
            Assert.Equal(TicketState.New, (await db.Changes.FindAsync("chg-self-service-update"))!.State);
        });
    }

    [Fact]
    public async Task RequestTaskEndpoints_RespectRequestScope()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await harness.SeedAsync(db =>
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-1", Name = "Organization One" },
                new Organization { Id = "org-2", Name = "Organization Two" });
            db.Users.Add(new User { Id = "self-service-1", Name = "Self-service user", Email = "self-service@example.com", OrganizationId = "org-1", Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "self-service-1", OrganizationId = "org-1", RoleKey = ScopedRoleCatalog.SelfServiceUser });
            AddSelfServiceCustomerAccess(db);
            db.Requests.AddRange(
                new Request { Id = "req-self-service-tasks", TrackingId = "REQ-SELF-TASKS", Title = "Self-service request", OrganizationId = "org-1", CustomerId = SelfServiceCustomerId, RequesterEmail = "self-service@example.com" },
                new Request { Id = "req-foreign-tasks", TrackingId = "REQ-FOREIGN-TASKS", Title = "Foreign request", OrganizationId = "org-2", RequesterEmail = "foreign@example.com" });
        });
        harness.UseRole("SelfService");

        var ownTasks = await harness.Client.GetAsync("/api/v1/requests/req-self-service-tasks/tasks");
        var foreignTasks = await harness.Client.GetAsync("/api/v1/requests/req-foreign-tasks/tasks");
        var ownRequest = await harness.Client.GetAsync("/api/v1/requests/req-self-service-tasks");
        var foreignRequest = await harness.Client.GetAsync("/api/v1/requests/req-foreign-tasks");

        Assert.Equal(System.Net.HttpStatusCode.OK, ownTasks.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreignTasks.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, ownRequest.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, foreignRequest.StatusCode);
    }

    [Fact]
    public async Task IncidentRelations_DeleteRemovesExistingLink()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync();
        await SeedRelationIncidentsAsync(harness);
        var create = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/inc-source/relations",
            new CreateTicketRelationDto
            {
                TargetTicketId = "INC-TARGET",
                RelationType = TicketRelationType.RelatedTo,
                CloseSourceTicket = false
            });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<TicketRelationCreateResultDto>();

        var delete = await harness.Client.DeleteAsync($"/api/v1/incidents/inc-source/relations/{created!.Relation.Id}");

        Assert.Equal(System.Net.HttpStatusCode.NoContent, delete.StatusCode);
        await harness.WithDbAsync(async db =>
        {
            Assert.Empty(await db.TicketRelations.ToListAsync());
        });
    }

    private const string SelfServiceCustomerId = "customer-self-service-1";

    private static void AddSelfServiceCustomerAccess(HelpdeskDbContext db)
    {
        db.Customers.Add(new Customer
        {
            Id = SelfServiceCustomerId,
            Name = "Self-service customer",
            Email = "self-service@example.com",
            OrganizationId = "org-1"
        });
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = SelfServiceCustomerId,
            AuthProviderType = "Local",
            LocalAccountId = "self-service-1",
            InviteStatus = CustomerInviteStatus.Active,
            InviteAcceptedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static async Task SeedRelationIncidentsAsync(
        LiveHistoryFilteringHarness harness,
        string sourceRequesterEmail = "source-requester@example.com",
        string? sourceCustomerId = null)
    {
        await harness.SeedAsync(db =>
        {
            db.Incidents.AddRange(
                new Incident
                {
                    Id = "inc-source",
                    TrackingId = "INC-SOURCE",
                    Title = "Source incident",
                    State = TicketState.InProgress,
                    Priority = TicketPriority.Medium,
                    OrganizationId = "org-1",
                    CustomerId = sourceCustomerId,
                    RequesterEmail = sourceRequesterEmail,
                    CcRecipients = ["source-cc@example.com", "existing@example.com", "TARGET-REQUESTER@example.com"]
                },
                new Incident
                {
                    Id = "inc-target",
                    TrackingId = "INC-TARGET",
                    Title = "Target incident",
                    State = TicketState.InProgress,
                    Priority = TicketPriority.High,
                    OrganizationId = "org-1",
                    RequesterEmail = "target-requester@example.com",
                    CcRecipients = ["existing@example.com"]
                },
                new Incident
                {
                    Id = "inc-other-org",
                    TrackingId = "INC-OTHER-ORG",
                    Title = "Other org target",
                    State = TicketState.InProgress,
                    Priority = TicketPriority.Low,
                    OrganizationId = "org-2"
                },
                new Incident
                {
                    Id = "inc-related-open",
                    TrackingId = "INC-RELATED-OPEN",
                    Title = "Related open source",
                    State = TicketState.InProgress,
                    Priority = TicketPriority.Low,
                    OrganizationId = "org-1"
                },
                new Incident
                {
                    Id = "inc-related-close",
                    TrackingId = "INC-RELATED-CLOSE",
                    Title = "Related close source",
                    State = TicketState.InProgress,
                    Priority = TicketPriority.Low,
                    OrganizationId = "org-1"
                });
        });
    }

    private static async Task<List<TicketRelationDto>> GetRelationsAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<List<TicketRelationDto>>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
    }

    private static RequestTask NewTask(string id, string title, RequestTaskStatus status) => new()
    {
        Id = id,
        TrackingId = id,
        Title = title,
        RequestId = "req-1",
        Status = status,
        Type = RequestTaskType.Manual,
        OrganizationId = "org-1"
    };

    private static void AddTechnicianChangeManagerAccess(HelpdeskDbContext db, string organizationId)
    {
        db.Users.Add(new User
        {
            Id = "admin-1",
            Name = "Technician One",
            Email = "technician@example.com",
            OrganizationId = organizationId,
            Role = "Technician"
        });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
        {
            UserId = "admin-1",
            OrganizationId = organizationId,
            RoleKey = ScopedRoleCatalog.Technician
        });
    }

    private sealed class LiveHistoryFilteringHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly WebApplication app;

        private LiveHistoryFilteringHarness(SqliteConnection connection, WebApplication app, HttpClient client)
        {
            this.connection = connection;
            this.app = app;
            Client = client;
            DomainEvents = app.Services.GetRequiredService<CapturingDomainEventPublisher>();
        }

        public HttpClient Client { get; }
        public CapturingDomainEventPublisher DomainEvents { get; }

        public static async Task<LiveHistoryFilteringHarness> CreateAsync(CurrentUserAccessProfile? accessProfile = null,
            Helpdesk.Application.RequestTasks.IRequestTaskLifecycleService? lifecycleService = null,
            IImageLinkSigner? imageLinkSigner = null, StorageOptions? storageOptions = null,
            ITimelineEventBus? timelineEventBus = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });

            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddScoped<IRepository<Incident>, EfRepository<Incident>>();
            builder.Services.AddScoped<IRepository<Request>, EfRepository<Request>>();
            builder.Services.AddScoped<IRepository<RequestTask>, EfRepository<RequestTask>>();
            builder.Services.AddScoped<IRepository<User>, EfRepository<User>>();
            builder.Services.AddScoped<IRepository<Change>, EfRepository<Change>>();
            builder.Services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
            if (accessProfile is not null)
            {
                builder.Services.AddSingleton<ICurrentUserAccessService>(new FixedTicketAccessService(accessProfile));
            }
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("org-1", "admin-1", isHelpdeskAdmin: true));
            builder.Services.AddSingleton<CapturingDomainEventPublisher>();
            builder.Services.AddSingleton<IDomainEventPublisher>(sp => sp.GetRequiredService<CapturingDomainEventPublisher>());
            builder.Services.AddSingleton<ICorrelationContext, TestCorrelationContext>();
            builder.Services.AddSingleton<IPublicTicketLinkSigner, TestPublicTicketLinkSigner>();
            builder.Services.AddSingleton<IImageLinkSigner>(imageLinkSigner ?? new TestImageLinkSigner());
            builder.Services.AddSingleton<ITimelineEventBus>(timelineEventBus ?? new TestTimelineEventBus());
            if (storageOptions is not null)
                builder.Services.AddSingleton<IOptions<StorageOptions>>(Options.Create(storageOptions));
            builder.Services.AddSingleton<IRequestSender, FailFastRequestSender>();
            if (lifecycleService is not null)
            {
                builder.Services.AddSingleton(lifecycleService);
                builder.Services.AddSingleton(NSubstitute.Substitute.For<Helpdesk.Application.Workflow.IWorkflowEngine>());
            }
            builder.Services.AddSingleton<Helpdesk.Application.Services.Tickets.ITicketRefGeneratorService, Helpdesk.Application.Services.Tickets.TicketRefGeneratorService>();
            builder.Services.AddSingleton<ITicketNotificationService, NoopTicketNotificationService>();
            builder.Services.AddSingleton<ITicketSlaInitializer, NoopTicketSlaInitializer>();
            builder.Services.AddSingleton<ITicketSlaCompletionService, NoopTicketSlaCompletionService>();
            builder.Services.AddSingleton<ITicketSlaService, NoopTicketSlaService>();
            builder.Services.AddSingleton<ITicketSlaRepository, NoopTicketSlaRepository>();
            builder.Services.AddSingleton<ISlaClockService, NoopSlaClockService>();
            builder.Services.AddSingleton<ISlaEscalationEvaluator, NoopSlaEscalationEvaluator>();
            builder.Services.AddSingleton<IChangeTemplateService, TestChangeTemplateService>();
            builder.Services.AddSingleton<IBackgroundJobQueue, NoopBackgroundJobQueue>();
            builder.Services.AddSingleton<ISupportNotificationService, NoopSupportNotificationService>();
            builder.Services.AddSingleton<ISupportAccessService, AllowAllSupportAccessService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                    .RequireAuthenticatedUser()
                    .Build();
                options.AddPolicy("HelpdeskAdmin", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("HelpdeskAdmin");
                });
                options.AddPolicy("HelpdeskStaff", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("HelpdeskAdmin", "Technician");
                });
                options.AddPolicy("IncidentAccess", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Incident.User", "Incident.Read", "Incident.Write", "Incident.Delete", "Incident.Manager", "HelpdeskAdmin");
                });
                options.AddPolicy("IncidentManager", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Incident.Write", "Incident.Manager", "HelpdeskAdmin");
                });
                options.AddPolicy("RequestAccess", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Request.User", "Request.Read", "Request.Write", "Request.Delete", "Request.Manager", "HelpdeskAdmin");
                });
                options.AddPolicy("RequestManager", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Request.Write", "Request.Manager", "HelpdeskAdmin");
                });
                options.AddPolicy("ChangeAccess", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Change.User", "Change.Read", "Change.Write", "Change.Delete", "Change.Approve", "Change.Manager", "HelpdeskAdmin");
                });
                options.AddPolicy("ChangeManager", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Change.Write", "Change.Manager", "HelpdeskAdmin");
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapIncidentEndpoints();
            app.MapRequestEndpoints();
            app.MapChangeEndpoints();
            app.MapRequestTaskEndpoints();
            app.MapWorkLogEndpoints();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

            return new LiveHistoryFilteringHarness(connection, app, client);
        }

        public void UseRole(string role)
        {
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", role);
        }

        public async Task SeedAsync(Action<HelpdeskDbContext> seed)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            seed(db);
            await db.SaveChangesAsync();
        }

        public async Task WithDbAsync(Func<HelpdeskDbContext, Task> action)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            await action(db);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestTenantContext(string? tenantId, string? userId, bool isHelpdeskAdmin) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin { get; } = isHelpdeskAdmin;
    }

    public sealed class CapturingDomainEventPublisher : IDomainEventPublisher
    {
        public List<DomainEvent> Events { get; } = new();

        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            Events.Add(domainEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopTicketSlaInitializer : ITicketSlaInitializer
    {
        public Task InitializeAsync(Ticket ticket) => Task.CompletedTask;
    }

    private sealed class NoopTicketSlaCompletionService : ITicketSlaCompletionService
    {
        public Task HandleTicketClosedAsync(string ticketId, string closedByUserId, DateTimeOffset nowUtc) =>
            Task.CompletedTask;
    }

    private sealed class NoopTicketSlaService : ITicketSlaService
    {
        public Task PauseAsync(string ticketId, string userId, string reason) => Task.CompletedTask;
        public Task PauseAsync(Ticket ticket, string userId, string reason) => Task.CompletedTask;
        public Task ResumeAsync(string ticketId, string userId) => Task.CompletedTask;
        public Task ResumeAsync(Ticket ticket, string userId) => Task.CompletedTask;
        public Task AutoResumeIfDueAsync(string ticketId) => Task.CompletedTask;
    }

    private sealed class NoopTicketSlaRepository : ITicketSlaRepository
    {
        public Task AddAsync(TicketSlaState state) => Task.CompletedTask;
        public Task<TicketSlaState?> GetByTicketIdAsync(string ticketId) => Task.FromResult<TicketSlaState?>(null);
        public Task<TicketSlaState?> GetByTicketIdForUpdateAsync(string ticketId) => Task.FromResult<TicketSlaState?>(null);
        public Task UpdateAsync(TicketSlaState state) => Task.CompletedTask;
    }

    private sealed class NoopSlaClockService : ISlaClockService
    {
        public SlaClockSnapshot Compute(TicketSlaState state, DateTimeOffset nowUtc, WorkingCalendar? calendar = null) =>
            new(
                state.Status,
                state.StartedAt,
                state.ResponseDueAt,
                state.ResolutionDueAt,
                state.AccumulatedPauseDuration,
                state.PausedAt,
                state.ResumeAt,
                state.PauseReason,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                state.ResponseBreached,
                state.ResolutionBreached);
    }

    private sealed class NoopSlaEscalationEvaluator : ISlaEscalationEvaluator
    {
        public Task EvaluateAndNotifyAsync(Ticket ticket, TicketSlaState slaState, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopBackgroundJobQueue : IBackgroundJobQueue
    {
        public void Queue(Func<IServiceProvider, CancellationToken, Task> work)
        {
        }

        public ValueTask<Func<IServiceProvider, CancellationToken, Task>> DequeueAsync(CancellationToken ct) =>
            new((_, _) => Task.CompletedTask);
    }

    private sealed class NoopSupportNotificationService : ISupportNotificationService
    {
        public Task NotifyTicketCreatedUnassignedAsync(Ticket ticket, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task NotifyTicketAssignedAsync(Ticket ticket, string? previousAssignedToId, string? newAssignedToId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class AllowAllSupportAccessService : ISupportAccessService
    {
        public Task<bool> CanUserSupportOrganizationAsync(string userId, string customerOrganizationId, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class TestChangeTemplateService : IChangeTemplateService
    {
        public string NormalizeChangeType(string? changeType) =>
            changeType?.Trim().ToLowerInvariant() switch
            {
                "standard" => "Standard",
                "normal" => "Normal",
                "emergency" => "Emergency",
                _ => string.Empty
            };

        public ChangeTemplateDto? DeserializeTemplate(string? templateJson) => new();

        public string SerializeTemplate(ChangeTemplateDto? template) => "{}";

        public ChangeTemplateValidationDto ValidateTemplate(string? changeType, ChangeTemplateDto? template) => new()
        {
            IsComplete = true,
            Errors = []
        };

    }

    private sealed class NoopTicketNotificationService : ITicketNotificationService
    {
        public Task<bool> SendNewTicketConfirmationAsync(Ticket ticket, string recipientEmail, string recipientName, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendSelfServiceRequestCreatedAsync(Request request, RequestForm requestForm, string recipientEmail, string recipientName, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendSelfServiceRequestCompletedAsync(Request request, string recipientEmail, string recipientName, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendSelfServiceRequestFailedAsync(Request request, Incident incident, string failureReason, string recipientEmail, string recipientName, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendRequestApprovalRequiredAsync(Request request, RequestTask task, RequestTaskApproval approval, string approvers, string payloadHtml, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendRequestApprovalDeclinedAsync(Request request, RequestTask task, RequestTaskApproval approval, string rejectionReason, string payloadHtml, string recipientEmail, string recipientName, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendTicketResolvedAsync(Ticket ticket, string recipientEmail, string recipientName, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendChangeSubmittedAsync(Change change, string recipientEmail, string recipientName, string organizationName, string requestedForName, string implementorName, string approvers, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendChangeApprovalRequiredAsync(Change change, ChangeApproval approval, string organizationName, string requestedForName, string implementorName, string approvers, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendChangeApprovedAsync(Change change, string recipientEmail, string recipientName, string organizationName, string requestedForName, string implementorName, string approvers, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendChangeImplementationInProgressAsync(Change change, string recipientEmail, string recipientName, string organizationName, string requestedForName, string implementorName, string approvers, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendChangeImplementedAsync(Change change, string recipientEmail, string recipientName, string organizationName, string requestedForName, string implementorName, string approvers, string completionState, IEnumerable<string>? cc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string GetCorrelationId() => "corr-live-history-tests";
    }

    private sealed class TestPublicTicketLinkSigner : IPublicTicketLinkSigner
    {
        public string GenerateToken(string trackingId, string email, DateTimeOffset expires) => $"token:{trackingId}:{email}";

        public bool ValidateToken(string token, string trackingId, string email) =>
            token == GenerateToken(trackingId, email, DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TestImageLinkSigner : IImageLinkSigner
    {
        public string GenerateToken(string scope, string id, string filename, DateTimeOffset expires) =>
            $"image-token:{scope}:{id}:{filename}";

        public bool ValidateToken(string token, string scope, string id, string filename) =>
            token == GenerateToken(scope, id, filename, DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TestTimelineEventBus : ITimelineEventBus
    {
        public ChannelReader<TicketTimelineEventDto> Subscribe(string ticketId) =>
            Channel.CreateUnbounded<TicketTimelineEventDto>().Reader;

        public void Unsubscribe(string ticketId, ChannelReader<TicketTimelineEventDto> reader)
        {
        }

        public ValueTask PublishAsync(TicketTimelineEventDto evt) => ValueTask.CompletedTask;
    }

    private sealed class FailFastRequestSender : IRequestSender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"Unexpected request dispatch in this test harness: {request.GetType().Name}");
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = "HelpdeskAdmin";
            if (Request.Headers.TryGetValue("Authorization", out var header))
            {
                role = header.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? role;
            }

            var isSelfService = string.Equals(role, "SelfService", StringComparison.OrdinalIgnoreCase);
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, isSelfService ? "self-service-1" : "admin-1"),
                new Claim(ClaimTypes.Name, isSelfService ? "Self-service user" : "Admin One"),
                new Claim("iss", "https://id.example.test"),
                new Claim("sub", "operator"),
                new Claim("organization_id", "org-1"),
                new Claim(ClaimTypes.Role, isSelfService ? HelpdeskPermissions.IncidentUser : role),
                new Claim("roles", isSelfService ? HelpdeskPermissions.IncidentUser : role)
            };

            if (isSelfService)
            {
                claims.Add(new Claim(ClaimTypes.Email, "self-service@example.com"));
                claims.Add(new Claim("auth_mode", "local"));
                foreach (var selfServiceRole in new[] { HelpdeskPermissions.RequestUser, HelpdeskPermissions.ChangeUser })
                {
                    claims.Add(new Claim(ClaimTypes.Role, selfServiceRole));
                    claims.Add(new Claim("roles", selfServiceRole));
                }
            }

            if (string.Equals(role, "Technician", StringComparison.OrdinalIgnoreCase))
            {
                claims.Add(new Claim("auth_mode", "local"));
                claims.Add(new Claim(ClaimTypes.Email, "technician@example.com"));
                foreach (var managerRole in new[] { "Incident.Manager", "Request.Manager", "Change.Manager" })
                {
                    claims.Add(new Claim(ClaimTypes.Role, managerRole));
                    claims.Add(new Claim("roles", managerRole));
                }
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
