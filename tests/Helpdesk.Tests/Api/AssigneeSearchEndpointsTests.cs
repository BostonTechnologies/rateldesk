using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Background;
using Helpdesk.API.Endpoints.Changes;
using Helpdesk.API.Endpoints.Incidents;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.Services.Changes;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Events;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Changes;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;
using TicketRequest = Helpdesk.Shared.Models.Request;

namespace Helpdesk.Tests.Api;

public sealed class AssigneeSearchEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_is_bounded_and_searches_the_entire_eligible_population_on_both_providers(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        Assert.Equal("external", Assert.Single((await h.Search("incident", "Support")).Items).Id);
        Assert.Equal("fallback", Assert.Single((await h.Search("change", "Fallback")).Items).Id);
        var first = await h.Search("request");
        Assert.Equal(20, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.DoesNotContain(first.Items, x => x.Id == "late");
        foreach (var term in new[] { " zULu ", "SuRNaMe", "late@", "Zulu Surname" })
        {
            var result = await h.Search("request", term);
            Assert.Equal("late", Assert.Single(result.Items).Id);
        }
        var duplicate = await h.Search("request", "Duplicate");
        Assert.Equal(new[] { "duplicate-1", "duplicate-2" }, duplicate.Items.Select(x => x.Id));
        Assert.Single((await h.Search("request", "100%_\\")).Items);
        Assert.Empty((await h.Search("request", "missing")).Items);
        Assert.Equal(50, (await h.Search("request", limit: 100)).Items.Count);
        // The existing task-label consumer retains the complete legacy list.
        var legacy = await h.Client.GetFromJsonAsync<List<UserDto>>("/api/v1/ticketing/assignees?module=request&organizationId=org");
        Assert.True(legacy!.Count > 50);
    }

    [Fact]
    public async Task Normal_request_list_includes_human_and_AI_provenance_and_legacy_filter_remains_explicit()
    {
        await using var h = await Harness.Create();
        await h.WithDb(async db => {
            db.Requests.AddRange(
                new TicketRequest { Id = "human-origin", Title = "Same synthetic request", OrganizationId = "org", TrackingId = "REQ-HUMAN" },
                new TicketRequest { Id = "ai-origin", Title = "Same synthetic request", OrganizationId = "org", TrackingId = "REQ-AI", SourceAutomationBindingId = "synthetic-binding" });
            await db.SaveChangesAsync();
        });
        var normal = await h.Client.GetFromJsonAsync<Helpdesk.Shared.DTOs.PagedResponse<Helpdesk.Shared.DTOs.Request.RequestDto>>("/api/v1/requests?q=Same%20synthetic&summaryOnly=true");
        Assert.Equal(new[] { "ai-origin", "human-origin" }, normal!.Items.Select(x => x.Id).Order());
        var legacy = await h.Client.GetFromJsonAsync<Helpdesk.Shared.DTOs.PagedResponse<Helpdesk.Shared.DTOs.Request.RequestDto>>("/api/v1/requests?q=Same%20synthetic&summaryOnly=true&aiOnly=true");
        Assert.Equal("ai-origin", Assert.Single(legacy!.Items).Id);
    }

    [Fact]
    public async Task Incident_candidates_and_saves_require_enabled_coverage_even_for_external_technicians()
    {
        await using var h = await Harness.Create();
        Assert.Equal("external", Assert.Single((await h.Search("incident")).Items).Id);
        var save = await h.AssignBulk("incidents", "incident", "external");
        save.EnsureSuccessStatusCode();
        Assert.Equal("external", await h.Assigned("incident"));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.AssignBulk("incidents", "incident", "late")).StatusCode);
        await h.WithDb(async db => { (await db.SupportGroupMembers.SingleAsync(x => x.UserId == "external")).IsEnabled = false; await db.SaveChangesAsync(); });
        Assert.Empty((await h.Search("incident")).Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.AssignBulk("incidents", "incident", "external")).StatusCode);
        Assert.Equal("external", await h.Assigned("incident"));
        var current = await h.Client.GetFromJsonAsync<UserDto>("/api/v1/ticketing/assignees/current?module=incident&organizationId=org&ticketId=incident");
        Assert.Equal("external", current!.Id);
        (await h.AssignBulk("incidents", "incident", null)).EnsureSuccessStatusCode();
        Assert.Null(await h.Assigned("incident"));
    }

    [Fact]
    public async Task Incident_grid_update_rechecks_coverage_and_distinguishes_null_from_explicit_clear()
    {
        await using var h = await Harness.Create();
        (await h.Client.PutAsJsonAsync("/api/v1/incidents/incident", new UpdateIncidentDto { AssignedToId = "external" })).EnsureSuccessStatusCode();
        Assert.Equal("external", await h.Assigned("incident"));
        (await h.Client.PutAsJsonAsync("/api/v1/incidents/incident", new UpdateIncidentDto())).EnsureSuccessStatusCode();
        Assert.Equal("external", await h.Assigned("incident"));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PutAsJsonAsync("/api/v1/incidents/incident", new UpdateIncidentDto { AssignedToId = "late" })).StatusCode);
        (await h.Client.PutAsJsonAsync("/api/v1/incidents/incident", new UpdateIncidentDto { AssignedToId = "" })).EnsureSuccessStatusCode();
        Assert.Null(await h.Assigned("incident"));
    }

    [Fact]
    public async Task Requests_reject_other_organizations_and_allow_explicit_unassignment()
    {
        await using var h = await Harness.Create();
        Assert.Empty((await h.Search("request", "External")).Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.AssignBulk("requests", "request", "external")).StatusCode);
        (await h.AssignBulk("requests", "request", "late")).EnsureSuccessStatusCode();
        Assert.Equal("late", await h.Assigned("request"));
        (await h.AssignBulk("requests", "request", null)).EnsureSuccessStatusCode();
        Assert.Null(await h.Assigned("request"));
    }

    [Fact]
    public async Task Change_search_includes_configured_population_and_candidate_role_fallback_and_saves_both_ids()
    {
        await using var h = await Harness.Create();
        Assert.Contains((await h.Search("change")).Items, x => x.Id == "external");
        Assert.Equal("fallback", Assert.Single((await h.Search("change", "Fallback")).Items).Id);
        Assert.Empty((await h.Search("change", "Surname")).Items);
        var response = await h.Client.PostAsJsonAsync("/api/v1/changes/change/assignee", new { ImplementorUserId = "fallback" });
        response.EnsureSuccessStatusCode();
        Assert.Equal("fallback", (await response.Content.ReadFromJsonAsync<UserDto>())!.Id);
        await h.WithDb(async db => { var change = await db.Changes.SingleAsync(); Assert.Equal("fallback", change.ImplementorUserId); Assert.Equal(change.ImplementorUserId, change.AssignedToId); });
        (await h.AssignBulk("changes", "change", "external")).EnsureSuccessStatusCode();
        Assert.Equal("external", await h.Assigned("change"));
        Assert.Equal(HttpStatusCode.BadRequest, (await h.AssignBulk("changes", "change", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.AssignBulk("changes", "change", "late")).StatusCode);
        await h.WithDb(async db => { (await db.Changes.SingleAsync()).LifecycleState = ChangeLifecycleState.Submitted; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.BadRequest, (await h.AssignBulk("changes", "change", "fallback")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync("/api/v1/changes/change/assignee", new { ImplementorUserId = "fallback" })).StatusCode);
        Assert.Equal("external", await h.Assigned("change"));
    }

    [Fact]
    public async Task Lookup_validates_module_and_scope_and_bulk_change_keeps_admin_authorization()
    {
        await using var h = await Harness.Create();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/v1/ticketing/assignees/search?module=users&organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/v1/ticketing/assignees/search?module=request&organizationId=org&limit=0")).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/assignees/search?module=request&organizationId=other")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.AssignBulk("changes", "change", "external")).StatusCode);
        (await h.Client.PostAsJsonAsync("/api/v1/changes/change/assignee", new { ImplementorUserId = "external" })).EnsureSuccessStatusCode();
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/assignees/search?module=request&organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.PostAsJsonAsync("/api/v1/changes/change/assignee", new { ImplementorUserId = "fallback" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.AssignBulk("requests", "request", "late")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/ticketing/assignees/current?module=incident&organizationId=other&ticketId=incident")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Category_assignments_preserve_existing_inactive_ids_and_reject_new_foreign_ids(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var retained = Guid.NewGuid(); var added = Guid.NewGuid(); var foreign = Guid.NewGuid();
        await h.WithDb(async db =>
        {
            db.TicketCategories.AddRange(
                new TicketCategory { Id = retained, Name = "Historical selection", Type = Helpdesk.Shared.Enums.TicketCategoryType.Service, IsActive = false },
                new TicketCategory { Id = added, Name = "Shared selection", Type = Helpdesk.Shared.Enums.TicketCategoryType.Service },
                new TicketCategory { Id = foreign, Name = "Foreign selection", Type = Helpdesk.Shared.Enums.TicketCategoryType.Service, TenantId = Guid.NewGuid() });
            db.IncidentCategoryLinks.Add(new IncidentCategoryLink { IncidentId = "incident", TicketCategoryId = retained });
            db.RequestCategoryLinks.Add(new RequestCategoryLink { RequestId = "request", TicketCategoryId = retained });
            db.ChangeCategoryLinks.Add(new ChangeCategoryLink { ChangeId = "change", TicketCategoryId = retained });
            await db.SaveChangesAsync();
        });
        foreach (var module in new[] { "incident", "request", "change" })
        {
            var path = $"/api/v1/{module}s/{module}";
            (await h.Client.PutAsJsonAsync(path, new { CategoryIds = new[] { retained, added } })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PutAsJsonAsync(path, new { CategoryIds = new[] { retained, added, foreign } })).StatusCode);
            using var reloaded = System.Text.Json.JsonDocument.Parse(await h.Client.GetStringAsync(path));
            Assert.Equal(new[] { retained, added }.Order(), reloaded.RootElement.GetProperty("categoryIds").EnumerateArray().Select(x => x.GetGuid()).Order());
            (await h.Client.PutAsJsonAsync(path, new { CategoryIds = new[] { added } })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PutAsJsonAsync(path, new { CategoryIds = new[] { retained, added } })).StatusCode);
        }
        await h.WithDb(async db => Assert.Equal(3, await db.TicketCategories.CountAsync()));
    }

    internal sealed class Harness(WebApplication app, HttpClient client, SqliteConnection? sqlite, PostgreSqlContainer? postgres) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public static async Task<Harness> Create(bool usePostgres = false)
        {
            SqliteConnection? sqlite = null;
            PostgreSqlContainer? postgres = null;
            if (usePostgres) { postgres = new PostgreSqlBuilder("postgres:16").Build(); await postgres.StartAsync(); }
            else { sqlite = new SqliteConnection("Data Source=:memory:"); await sqlite.OpenAsync(); }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => { if (postgres is not null) options.UseNpgsql(postgres.GetConnectionString()); else options.UseSqlite(sqlite!); });
            var tenant = Substitute.For<ITenantContext>();
            tenant.IsHelpdeskAdmin.Returns(true);
            builder.Services.AddSingleton(tenant);
            builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            builder.Services.AddScoped<ICurrentUserAccessService, ClaimsAccess>();
            builder.Services.AddScoped<ISupportAccessService, SupportAccessService>();
            builder.Services.AddSingleton<IChangeTemplateService, ChangeTemplateService>();
            builder.Services.AddSingleton(Substitute.For<ITimelineEventBus>());
            builder.Services.AddSingleton(Substitute.For<ISupportNotificationService>());
            builder.Services.AddSingleton(Substitute.For<IImageLinkSigner>());
            builder.Services.AddSingleton(Substitute.For<IBackgroundJobQueue>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaCompletionService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaService>());
            var slaRepository = Substitute.For<ITicketSlaRepository>();
            slaRepository.GetByTicketIdForUpdateAsync(Arg.Any<string>()).Returns(Task.FromResult<TicketSlaState?>(null));
            builder.Services.AddSingleton(slaRepository);
            builder.Services.AddSingleton(Substitute.For<ISlaClockService>());
            builder.Services.AddSingleton(Substitute.For<ISlaEscalationEvaluator>());
            builder.Services.AddSingleton(Substitute.For<ITicketNotificationService>());
            builder.Services.AddSingleton(Substitute.For<IDomainEventPublisher>());
            builder.Services.AddSingleton(Substitute.For<ICorrelationContext>());
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            builder.Services.AddAuthorization(options => {
                foreach (var policy in new[] { "IncidentAccess", "IncidentManager", "RequestAccess", "RequestManager", "ChangeAccess", "ChangeManager" })
                    options.AddPolicy(policy, p => p.RequireAuthenticatedUser());
                options.AddPolicy("HelpdeskAdmin", p => p.RequireRole("HelpdeskAdmin"));
            });
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            app.MapTicketingLookupEndpoints(); app.MapIncidentEndpoints(); app.MapRequestEndpoints(); app.MapChangeEndpoints();
            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "admin");
            var h = new Harness(app, client, sqlite, postgres);
            await h.WithDb(async db => {
                await db.Database.EnsureCreatedAsync();
                db.Organizations.AddRange(new Organization { Id = "org", Name = "Customer", ItSupportOrganizationId = "provider" }, new Organization { Id = "provider", Name = "Provider" }, new Organization { Id = "other", Name = "Other" });
                for (var i = 0; i < 55; i++) db.Users.Add(new User { Id = $"user-{i:D2}", Name = $"Alpha {i:D2}", Email = $"alpha{i}@example.test", OrganizationId = "org" });
                db.Users.AddRange(new User { Id = "late", Name = "Zulu Surname", Email = "late@example.test", OrganizationId = "org" },
                    new User { Id = "duplicate-1", Name = "Duplicate", Email = "one@example.test", OrganizationId = "org" },
                    new User { Id = "duplicate-2", Name = "Duplicate", Email = "two@example.test", OrganizationId = "org" },
                    new User { Id = "literal", Name = "100%_\\", Email = "literal@example.test", OrganizationId = "org" },
                    new User { Id = "external", Name = "External Support", Email = "external@example.test", OrganizationId = "provider" },
                    new User { Id = "fallback", Name = "Fallback Technician", Email = "fallback@example.test", OrganizationId = "other", Role = "Technician" });
                db.SupportGroups.Add(new SupportGroup { Id = "support", OwningOrganizationId = "provider", Name = "Support" });
                db.SupportGroupMembers.Add(new SupportGroupMember { Id = "member", SupportGroupId = "support", UserId = "external" });
                db.OrganizationSupportCoverages.Add(new OrganizationSupportCoverage { Id = "coverage", SupportGroupId = "support", CustomerOrganizationId = "org", ProviderOrganizationId = "provider" });
                db.Incidents.Add(new Incident { Id = "incident", Title = "Incident", TrackingId = "INC-146", OrganizationId = "org" });
                db.Requests.Add(new TicketRequest { Id = "request", Title = "Request", TrackingId = "REQ-146", OrganizationId = "org" });
                db.Changes.Add(new Change { Id = "change", Title = "Change", TrackingId = "CHG-146", OrganizationId = "org", RequestedForUserId = "late", ImplementorUserId = "external", AssignedToId = "external" });
                await db.SaveChangesAsync();
            });
            return h;
        }
        public async Task WithDb(Func<HelpdeskDbContext, Task> action) { await using var scope = app.Services.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()); }
        public Task<AssigneeSearchResult> Search(string module, string q = "", int limit = 20) => Client.GetFromJsonAsync<AssigneeSearchResult>($"/api/v1/ticketing/assignees/search?module={module}&organizationId=org&q={Uri.EscapeDataString(q)}&limit={limit}")!;
        public Task<HttpResponseMessage> AssignBulk(string module, string id, string? user) => Client.PostAsJsonAsync($"/api/v1/{module}/bulk/assign", new { Ids = new[] { id }, AssignedToId = user });
        public async Task<string?> Assigned(string id) { string? value = null; await WithDb(async db => value = await db.Tickets.Where(x => x.Id == id).Select(x => x.AssignedToId).SingleAsync()); return value; }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); if (sqlite is not null) await sqlite.DisposeAsync(); if (postgres is not null) await postgres.DisposeAsync(); }
    }
    private sealed class ClaimsAccess : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct = default) => Task.FromResult(CurrentUserAccessProfile.FromClaims(principal));
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var kind = Request.Headers.Authorization.ToString().Split(' ').Last();
            var claims = new List<Claim> { new(ClaimTypes.Name, "Operator"), new("allowed_organization_id", "org") };
            foreach (var module in kind == "request-only" ? new[] { "Request" } : new[] { "Incident", "Request", "Change" })
                claims.Add(new Claim(ClaimTypes.Role, module + (kind is "reader" or "request-only" ? ".Read" : kind == "own" ? ".User" : ".Write")));
            if (kind == "own") claims.Add(new Claim("customer_id", "contact"));
            if (kind == "admin") claims.Add(new Claim(ClaimTypes.Role, "HelpdeskAdmin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
