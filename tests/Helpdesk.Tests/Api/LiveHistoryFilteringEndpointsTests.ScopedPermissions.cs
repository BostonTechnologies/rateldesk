using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

public sealed partial class LiveHistoryFilteringEndpointsTests
{
    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task NamedQueueScopeAppliesBeforeTrackingSearchTotalsAndPaging(string module)
    {
        var permission = $"{module}.Read";
        var profile = ScopedTicketProfile() with
        {
            Permissions = new HashSet<string> { permission }, UsesScopedPermissions = true,
            RestrictedTicketOrganizationIds = new HashSet<string> { "org-1", "org-2" },
            TicketQueuePermissionGrants = new HashSet<TicketQueuePermissionGrant> { new(permission, "org-2", "app", "org-2") }
        };
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(profile);
        harness.UseRole(permission);
        await harness.SeedAsync(db =>
        {
            SeedScopedTickets(db);
            db.SupportGroups.AddRange(new SupportGroup { Id = "app", Name = "Application", OwningOrganizationId = "org-2" },
                new SupportGroup { Id = "desktop", Name = "Desktop", OwningOrganizationId = "org-2" });
            db.Tickets.Local.Single(ticket => ticket.Id == $"{module}-b-peer").AssignedSupportGroupId = "app";
            db.Tickets.Local.Single(ticket => ticket.Id == $"{module}-b-own").AssignedSupportGroupId = "desktop";
        });
        var route = $"/api/v1/{module.ToLowerInvariant()}s";
        var result = await harness.Client.GetFromJsonAsync<JsonElement>($"{route}?page=1&pageSize=1&supportGroupId=app&supportGroupId=desktop&mainQueue=true&includeTotal=true");
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        Assert.Equal($"{module}-b-peer", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        var hidden = await harness.Client.GetFromJsonAsync<JsonElement>($"{route}?q={module}-b-own&includeTotal=true");
        Assert.Equal(0, hidden.GetProperty("totalCount").GetInt32());
        using var denied = await harness.Client.GetAsync($"{route}/{module}-b-own");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var allowed = await harness.Client.GetAsync($"{route}/{module}-b-peer");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task ScopedTicketLists_UnionTenantReadAndOwnTicketsWithoutUnrelatedMembership(string module)
    {
        var profile = ScopedTicketProfile(
            new(module + ".Read", "org-1"),
            new(module + ".User", "org-2"),
            new(HelpdeskPermissions.SelfServiceUser, "org-3"));
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(profile);
        harness.UseRole(module + ".Read");
        await harness.SeedAsync(SeedScopedTickets);
        var route = "/api/v1/" + module.ToLowerInvariant() + "s";

        var list = await harness.Client.GetFromJsonAsync<JsonElement>(route + "?page=1&pageSize=20");
        var ids = list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { module + "-a-peer", module + "-b-own" }, ids.Order(StringComparer.Ordinal));
        foreach (var id in ids)
        {
            var detail = await harness.Client.GetAsync(route + "/" + id);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        }
        var unrelatedOwn = await harness.Client.GetAsync(route + "/" + module + "-c-own");
        Assert.Equal(HttpStatusCode.Forbidden, unrelatedOwn.StatusCode);
        var otherCustomer = await harness.Client.GetAsync(route + "/" + module + "-b-peer");
        Assert.Equal(HttpStatusCode.Forbidden, otherCustomer.StatusCode);
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task TenantReader_SeesColleagueTicketButCannotUpdateOrDelete(string module)
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(
            ScopedTicketProfile(new ScopedPermissionGrant(module + ".Read", "org-1")));
        harness.UseRole(module + ".Read");
        await harness.SeedAsync(SeedScopedTickets);
        var route = "/api/v1/" + module.ToLowerInvariant() + "s/" + module + "-a-peer";

        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.PutAsJsonAsync(route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.DeleteAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task ScopedDeletion_RequiresDeleteOnTheTargetTenantWithoutRequiringWrite(string module)
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new(module + ".Read", "org-1"),
            new(module + ".Delete", "org-1"),
            new(module + ".Read", "org-2")));
        harness.UseRole(module + ".Delete");
        await harness.SeedAsync(SeedScopedTickets);
        var route = "/api/v1/" + module.ToLowerInvariant() + "s/";

        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.DeleteAsync(route + module + "-b-peer")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await harness.Client.DeleteAsync(route + module + "-a-peer")).StatusCode);
        await harness.WithDbAsync(async db => Assert.Null(await db.Tickets.FindAsync(module + "-a-peer")));
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    [InlineData("Change")]
    public async Task ScopedWriter_DoesNotImplicitlyGrantDelete(string module)
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new(module + ".Read", "org-1"), new(module + ".Write", "org-1")));
        harness.UseRole(module + ".Write");
        await harness.SeedAsync(SeedScopedTickets);

        var response = await harness.Client.DeleteAsync("/api/v1/" + module.ToLowerInvariant() + "s/" + module + "-a-peer");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await harness.WithDbAsync(async db => Assert.NotNull(await db.Tickets.FindAsync(module + "-a-peer")));
    }

    [Theory]
    [InlineData("Incident")]
    [InlineData("Request")]
    public async Task ScopedWriter_CanUpdateItsTenantButCannotUpdateAnotherTenant(string module)
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new(module + ".Read", "org-1"), new(module + ".Write", "org-1"),
            new(module + ".Read", "org-2")));
        harness.UseRole(module + ".Write");
        await harness.SeedAsync(SeedScopedTickets);
        var route = "/api/v1/" + module.ToLowerInvariant() + "s/";
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.PostAsJsonAsync(
            route + module + "-b-peer/state", new { NewState = TicketState.InProgress })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsJsonAsync(
            route + module + "-a-peer/state", new { NewState = TicketState.InProgress })).StatusCode);
    }

    [Theory]
    [InlineData("Request", true, 0)]
    [InlineData("Request", false, 2)]
    [InlineData("Change", true, 0)]
    [InlineData("Change", false, 2)]
    public async Task OwnTicketContributors_CannotCreateInternalNotesOrBillableWork(string module, bool internalNote, double hours)
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new ScopedPermissionGrant(module + ".User", "org-2")));
        harness.UseRole(module + ".User");
        await harness.SeedAsync(SeedScopedTickets);
        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/" + module.ToLowerInvariant() + "s/" + module + "-b-own/worklogs",
            new CreateWorkLogDto { Notes = "Own contribution", IsInternalNote = internalNote, Hours = hours });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RequestTimeline_OnSqlite_OrdersDatesAndAllowsStaffReadersToSeeInternalNotes()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new ScopedPermissionGrant(HelpdeskPermissions.RequestRead, "org-1")));
        harness.UseRole(HelpdeskPermissions.RequestRead);
        await harness.SeedAsync(db =>
        {
            SeedScopedTickets(db);
            db.TicketTimelineEvents.AddRange(
                new TicketTimelineEvent { TicketId = "Request-a-peer", MessageText = "Later", EventType = TimelineEventType.InternalNote, CreatedUtc = DateTimeOffset.Parse("2026-01-02T00:00:00Z") },
                new TicketTimelineEvent { TicketId = "Request-a-peer", MessageText = "Earlier", EventType = TimelineEventType.SystemNotification, CreatedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z") },
                new TicketTimelineEvent { TicketId = "Request-b-peer", MessageText = "Other tenant", EventType = TimelineEventType.InternalNote });
        });

        var timeline = await harness.Client.GetFromJsonAsync<List<TicketTimelineEventDto>>("/api/v1/requests/Request-a-peer/timeline?order=asc");
        Assert.Equal(new[] { "Earlier", "Later" }, timeline!.Select(x => x.MessageText));
        var descending = await harness.Client.GetFromJsonAsync<List<TicketTimelineEventDto>>("/api/v1/requests/Request-a-peer/timeline");
        Assert.Equal(new[] { "Later", "Earlier" }, descending!.Select(x => x.MessageText));
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.PostAsJsonAsync(
            "/api/v1/requests/Request-a-peer/worklogs", new CreateWorkLogDto { Notes = "Reader cannot post" })).StatusCode);
    }

    [Fact]
    public async Task ChangeWriter_CannotApproveThroughLifecycleOrGeneralUpdate()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new(HelpdeskPermissions.ChangeRead, "org-1"), new(HelpdeskPermissions.ChangeWrite, "org-1")));
        harness.UseRole(HelpdeskPermissions.ChangeWrite);
        await harness.SeedAsync(db =>
        {
            SeedScopedTickets(db);
            db.Changes.Local.Single(x => x.Id == "Change-a-peer").LifecycleState = ChangeLifecycleState.PendingApproval;
        });

        var lifecycle = await harness.Client.PostAsJsonAsync("/api/v1/changes/Change-a-peer/lifecycle",
            new { LifecycleState = ChangeLifecycleState.ApprovedForImplementation });
        Assert.Equal(HttpStatusCode.Forbidden, lifecycle.StatusCode);
        var update = await harness.Client.PutAsJsonAsync("/api/v1/changes/Change-a-peer",
            new UpdateChangeDto { LifecycleState = ChangeLifecycleState.ApprovedForImplementation });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        await harness.WithDbAsync(async db => Assert.Equal(ChangeLifecycleState.PendingApproval,
            (await db.Changes.FindAsync("Change-a-peer"))!.LifecycleState));
    }

    [Fact]
    public async Task ChangeApprover_CanApproveItsTenantButCannotImplementOrApproveAnotherTenant()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new(HelpdeskPermissions.ChangeRead, "org-1"), new(HelpdeskPermissions.ChangeApprove, "org-1"),
            new(HelpdeskPermissions.ChangeRead, "org-2")));
        harness.UseRole(HelpdeskPermissions.ChangeApprove);
        await harness.SeedAsync(db =>
        {
            SeedScopedTickets(db);
            foreach (var change in db.Changes.Local) change.LifecycleState = ChangeLifecycleState.PendingApproval;
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/Change-b-peer/lifecycle", new { LifecycleState = ChangeLifecycleState.ApprovedForImplementation })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/Change-a-peer/lifecycle", new { LifecycleState = ChangeLifecycleState.ApprovedForImplementation })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/Change-a-peer/lifecycle", new { LifecycleState = ChangeLifecycleState.ImplementationInProgress })).StatusCode);
        await harness.WithDbAsync(async db => Assert.Equal(ChangeLifecycleState.ApprovedForImplementation,
            (await db.Changes.FindAsync("Change-a-peer"))!.LifecycleState));
    }

    [Fact]
    public async Task ChangeWriter_CreatingWithoutNamedApproversDoesNotAutomaticallyApprove()
    {
        await using var harness = await LiveHistoryFilteringHarness.CreateAsync(ScopedTicketProfile(
            new(HelpdeskPermissions.ChangeRead, "org-1"), new(HelpdeskPermissions.ChangeWrite, "org-1")));
        harness.UseRole(HelpdeskPermissions.ChangeWrite);
        await harness.SeedAsync(db =>
        {
            db.Organizations.Add(new Organization { Id = "org-1", Name = "Organization" });
            db.Users.Add(new User { Id = "operator", Name = "Operator", Email = "operator@example.test", OrganizationId = "org-1", Role = "Technician" });
        });

        var response = await harness.Client.PostAsJsonAsync("/api/v1/changes", new CreateChangeDto
        {
            Title = "Requires approval", Description = "A writer does not grant approval by omitting approvers.",
            OrganizationId = "org-1", RequestedForUserId = "operator", ImplementorUserId = "operator",
            ChangeType = "Standard", ChangeTemplate = new ChangeTemplateDto(),
            ImplementationStartAt = DateTime.UtcNow.AddDays(1), ImplementationEndAt = DateTime.UtcNow.AddDays(1).AddHours(1)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(ChangeLifecycleState.PendingApproval, (await response.Content.ReadFromJsonAsync<ChangeDto>())!.LifecycleState);
    }

    private static CurrentUserAccessProfile ScopedTicketProfile(params ScopedPermissionGrant[] grants) => new(
        true, "Scoped operator", "operator@example.test", "org-2", "Own tenant", "customer-own", false,
        new HashSet<string>(), grants.Select(x => x.Permission).ToHashSet(StringComparer.OrdinalIgnoreCase),
        grants.Select(x => x.OrganizationId).ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<string>())
    {
        ScopedPermissionGrants = grants.ToHashSet()
    };

    private static void SeedScopedTickets(HelpdeskDbContext db)
    {
        db.Organizations.AddRange(
            new Organization { Id = "org-1", Name = "Read tenant" },
            new Organization { Id = "org-2", Name = "Own tenant" },
            new Organization { Id = "org-3", Name = "Unrelated module tenant" });
        db.Customers.AddRange(
            new Customer { Id = "customer-own", Name = "Own", Email = "own@example.test", OrganizationId = "org-2" },
            new Customer { Id = "customer-a-peer", Name = "A peer", Email = "a@example.test", OrganizationId = "org-1" },
            new Customer { Id = "customer-b-peer", Name = "B peer", Email = "b@example.test", OrganizationId = "org-2" });
        foreach (var module in new[] { "Incident", "Request", "Change" })
        {
            foreach (var (suffix, organization, customer) in new[]
            {
                ("a-peer", "org-1", "customer-a-peer"), ("b-own", "org-2", "customer-own"),
                ("b-peer", "org-2", "customer-b-peer"), ("c-own", "org-3", "customer-own")
            })
            {
                Ticket ticket = module switch
                {
                    "Incident" => new Incident(),
                    "Request" => new Request(),
                    _ => new Change { LifecycleState = ChangeLifecycleState.Draft }
                };
                ticket.Id = module + "-" + suffix;
                ticket.TrackingId = module + "-" + suffix;
                ticket.Title = module + " " + suffix;
                ticket.OrganizationId = organization;
                ticket.CustomerId = customer;
                ticket.State = TicketState.New;
                db.Add(ticket);
            }
        }
    }

    private sealed class FixedTicketAccessService(CurrentUserAccessProfile profile) : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default) => Task.FromResult(profile);
    }
}
