extern alias NewWeb;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services;
using Helpdesk.Application.Events;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Events;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Category;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NSubstitute;
using Xunit.Abstractions;
using RequestDetail = NewWeb::HelpDesk.NewWeb.Components.Pages.Requests.RequestDetail;
using OwnDetail = NewWeb::HelpDesk.NewWeb.Components.Pages.SelfService.MyRequestDetail;
using Activity = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketActivityTabs;
using TimelineStream = NewWeb::HelpDesk.NewWeb.Services.TimelineStreamService;
using WebGatewayConfiguration = NewWeb::HelpDesk.NewWeb.Services.WebGatewayConfiguration;
using WebGatewayGuard = NewWeb::HelpDesk.NewWeb.Services.WebGatewayRequestGuard;

namespace Helpdesk.Tests.Api;

public sealed class RequestLiveProgressTests(ITestOutputHelper output)
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_automation_transitions_cross_gateway_SSE_and_update_rendered_tasks_header_and_workflow(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var factory = await harness.ClientsAsync("operator");
        factory.DiagnoseReads = true;
        await using var context = Context(factory);
        // The Activity component is deliberately absent. Tasks must own its subscription.
        context.ComponentFactories.AddStub<Activity>();
        var page = context.Render<RequestDetail>(p => p.Add(x => x.Id, "live-request"));
        await factory.Frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        page.WaitForAssertion(() => Assert.Contains(page.FindAll("[role=tab]"), tab => tab.TextContent.Trim() == "Tasks"), SetupTimeout);
        await page.InvokeAsync(() => page.FindAll("[role=tab]").Single(x => x.TextContent.Trim() == "Tasks").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".request-task-state").Count), SetupTimeout);
        WaitForInitialSnapshot(page, factory);
        await page.InvokeAsync(() => page.Find("button[aria-label='Task details: Harmless automation 1']").ClickAsync(new()));
        Assert.Single(page.FindAll(".request-task-details"));
        Assert.DoesNotContain("Running</button>", page.Markup);
        Assert.DoesNotContain("provider-run-1", page.Find(".request-tasks-table tbody").TextContent.Split("Provider outcome")[0]);

        await harness.CompleteAllAsync();
        var elapsed = Stopwatch.StartNew();
        await factory.Frames.State.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            page.WaitForAssertion(() =>
            {
                Assert.All(page.FindAll(".request-task-state"), row => Assert.Contains("Completed", row.TextContent));
                Assert.Contains("Resolved", page.Find(".ticket-header-status-chip").TextContent);
                Assert.Contains("Completed", page.Find("[data-testid=request-workflow-summary]").TextContent);
                Assert.Contains("3 / 3", page.Find("[data-testid=request-workflow-summary]").TextContent);
                Assert.Single(page.FindAll(".request-task-details"));
            }, TimeSpan.FromSeconds(2));
        }
        catch
        {
            output.WriteLine($"Failure at {DateTimeOffset.UtcNow:O}; elapsed={elapsed.Elapsed}; live={page.Find("[data-testid=request-live-updates]").TextContent.Trim()}; busy={page.Find("[data-testid=request-live-updates]").GetAttribute("aria-busy")}; header={page.Find("[data-testid=request-committed-state]").TextContent.Trim()}; workflow={page.Find("[data-testid=request-workflow-summary]").TextContent.Trim()}; rows={string.Join(" | ", page.FindAll(".request-task-state").Select(row => row.TextContent.Trim()))}");
            output.WriteLine("Canonical HTTP reads:\n" + string.Join("\n", factory.Reads));
            output.WriteLine("Actual stream:\n" + factory.Frames.Text);
            await harness.WithDbAsync(async db =>
            {
                foreach (var task in await db.RequestTasks.AsNoTracking().ToListAsync()) output.WriteLine($"Durable task {task.Id}={task.Status}");
                var request = await db.Requests.AsNoTracking().SingleAsync();
                output.WriteLine($"Durable request={request.State}/{request.WorkflowStatus}");
            });
            throw;
        }
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Gateway/frame/render convergence took {elapsed.Elapsed}.");
        output.WriteLine($"Provider={(postgres ? "PostgreSQL" : "SQLite")}; Request=live-request; gateway=true; persisted terminal → SSE → rendered convergence={elapsed.Elapsed.TotalMilliseconds:F0} ms");
        output.WriteLine($"State frame bytes observed at {factory.Frames.StateObservedAt:O}; actual stream:\n{factory.Frames.Text}");
        output.WriteLine("Canonical HTTP reads:\n" + string.Join("\n", factory.Reads));
        Assert.Contains("event: request-state", factory.Frames.Text);
        Assert.Contains("live-request", factory.Frames.Text);
        Assert.True(factory.Frames.GatewayHeaderSeen);
        await harness.WithDbAsync(async db =>
        {
            Assert.All(await db.RequestTasks.AsNoTracking().ToListAsync(), task => Assert.Equal(RequestTaskStatus.Completed, task.Status));
            Assert.Equal(TicketState.Resolved, (await db.Requests.AsNoTracking().SingleAsync()).State);
            Assert.True(await db.Notifications.AnyAsync(x => x.Source == "RequestTask"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Own_detail_resynchronizes_over_strict_gateway_stream_without_operator_data_or_actions(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var factory = await harness.ClientsAsync("requester");
        await using var context = Context(factory, "SelfService.User");
        var page = context.Render<OwnDetail>(p => p.Add(x => x.Id, "live-request"));
        await factory.Frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("td[data-label=Status]").Count), SetupTimeout);
        WaitForInitialSnapshot(page, factory);
        await harness.CompleteAllAsync();
        await factory.Frames.State.Task.WaitAsync(TimeSpan.FromSeconds(2));
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Resolved", page.Find("[data-testid=own-request-state]").TextContent);
            Assert.Contains("Completed", page.Find("[data-testid=own-request-workflow]").TextContent);
            Assert.All(page.FindAll("td[data-label=Status]"), x => Assert.Equal("Completed", x.TextContent));
        }, TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("Open recovery", page.Markup);
        Assert.DoesNotContain("provider-run", page.Markup);
        Assert.DoesNotContain("event: timeline", factory.Frames.Text);
        var foreign = await harness.ClientsAsync("other-requester");
        using var denied = await foreign.CreateClient("HelpdeskApi").GetAsync("api/v1/self-service/requests/live-request/timeline/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task Existing_local_session_is_revoked_before_a_new_private_frame_is_delivered()
    {
        await using var harness = await Harness.CreateAsync(false);
        var factory = await harness.ClientsAsync("operator");
        using var client = factory.CreateClient("HelpdeskApiStreaming");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync("api/v1/requests/live-request/timeline/stream", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        Assert.Equal("event: ready", await reader.ReadLineAsync(cancellation.Token));
        Assert.Equal("data: {}", await reader.ReadLineAsync(cancellation.Token));
        Assert.Equal("", await reader.ReadLineAsync(cancellation.Token));
        await using (var identity = harness.Database.Identity())
        {
            var account = await identity.Users.SingleAsync(x => x.Id == "operator");
            account.AuthorizationRevision++;
            await identity.SaveChangesAsync();
        }
        await harness.CompleteAllAsync();
        Assert.Null(await reader.ReadLineAsync(cancellation.Token));
        using var reopened = await client.GetAsync("api/v1/requests/live-request/timeline/stream", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.Forbidden, reopened.StatusCode);
        Assert.Empty(await reopened.Content.ReadAsStringAsync(cancellation.Token));
        Assert.DoesNotContain("event: request-state", factory.Frames.Text);
    }

    [Fact]
    public async Task Reconnect_resynchronizes_a_terminal_result_committed_while_the_stream_was_absent()
    {
        await using var harness = await Harness.CreateAsync(false);
        var factory = await harness.ClientsAsync("operator");
        await using var context = Context(factory);
        context.ComponentFactories.AddStub<Activity>();
        var page = context.Render<RequestDetail>(p => p.Add(x => x.Id, "live-request"));
        await factory.Frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        page.WaitForAssertion(() => Assert.Contains("In progress", page.Find(".ticket-header-status-chip").TextContent), SetupTimeout);
        WaitForInitialSnapshot(page, factory);
        factory.StreamingUnavailable = true;
        factory.CurrentStream!.Disconnect();
        page.WaitForAssertion(() => Assert.Contains("Reconnecting", page.Find("[data-testid=request-live-updates]").TextContent), SetupTimeout);
        await harness.CompleteAllAsync();
        factory.StreamingUnavailable = false;
        await factory.Frames.Reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        page.WaitForAssertion(() => Assert.Contains("Resolved", page.Find(".ticket-header-status-chip").TextContent), TimeSpan.FromSeconds(10));
        Assert.True(factory.Frames.Text.Split("event: ready").Length >= 3);
        Assert.DoesNotContain("event: request-state", factory.Frames.Text);
        Assert.Contains("Completed", page.Find("[data-testid=request-workflow-summary]").TextContent);
    }

    [Fact]
    public async Task Rejected_save_resynchronizes_a_committed_transition_and_preserves_the_editable_draft()
    {
        await using var harness = await Harness.CreateAsync(false);
        var factory = await harness.ClientsAsync("operator");
        await using var context = Context(factory);
        context.ComponentFactories.AddStub<Activity>();
        var page = context.Render<RequestDetail>(p => p.Add(x => x.Id, "live-request"));
        await factory.Frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        page.WaitForAssertion(() => Assert.Contains(page.FindAll("[role=tab]"), tab => tab.TextContent.Trim() == "Tasks"), SetupTimeout);
        await page.InvokeAsync(() => page.FindAll("[role=tab]").Single(x => x.TextContent.Trim() == "Tasks").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".request-task-state").Count), SetupTimeout);
        WaitForInitialSnapshot(page, factory);
        await page.InvokeAsync(() => page.FindComponent<MudSelect<TicketPriority>>().Instance.ValueChanged.InvokeAsync(TicketPriority.High));

        factory.PauseNextSnapshot = true;
        factory.RejectNextSave = true;
        await harness.CompleteAllAsync();
        await factory.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var saving = page.InvokeAsync(() => page.FindAll("button").Single(x => x.TextContent.Trim() == "Save Changes").ClickAsync(new()));
        await factory.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        page.WaitForAssertion(() => Assert.Equal("true", page.Find("[data-testid=request-live-updates]").GetAttribute("aria-busy")), SetupTimeout);
        factory.ReleaseSnapshot.TrySetResult(true);
        page.WaitForAssertion(() => Assert.Equal("false", page.Find("[data-testid=request-live-updates]").GetAttribute("aria-busy")), SetupTimeout);
        factory.ReleaseSave.TrySetResult(true);
        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        page.WaitForAssertion(() =>
        {
            Assert.All(page.FindAll(".request-task-state"), row => Assert.Contains("Completed", row.TextContent));
            Assert.Contains("Resolved", page.Find("[data-testid=request-committed-state]").TextContent);
            Assert.Equal(TicketPriority.High, page.FindComponent<MudSelect<TicketPriority>>().Instance.GetState(select => select.Value));
            Assert.False(page.FindAll("button").Single(x => x.TextContent.Trim() == "Save Changes").HasAttribute("disabled"));
        }, TimeSpan.FromSeconds(5));
        Assert.Contains("event: request-state", factory.Frames.Text);
    }

    private static void WaitForInitialSnapshot<TComponent>(IRenderedComponent<TComponent> page, ClientFactory factory) where TComponent : IComponent
    {
        page.WaitForAssertion(() => Assert.True(factory.RequestSnapshots >= 2), SetupTimeout);
        page.WaitForAssertion(() => Assert.Equal("false", page.Find("[data-testid=request-live-updates]").GetAttribute("aria-busy")), SetupTimeout);
    }

    [Fact]
    public async Task Navigating_to_another_request_discards_the_old_snapshot_and_stream_scope()
    {
        await using var harness = await Harness.CreateAsync(false);
        await harness.WithDbAsync(async db =>
        {
            db.Requests.Add(new Request { Id = "second-request", Title = "Second scoped request", TrackingId = "REQ-SECOND", OrganizationId = "live-org", State = TicketState.New, WorkflowStatus = "InProgress" });
            db.RequestTasks.Add(new RequestTask { Id = "second-task", RequestId = "second-request", OrganizationId = "live-org", Name = "Second manual task", Type = RequestTaskType.Manual, Status = RequestTaskStatus.Pending, State = TicketState.New });
            await db.SaveChangesAsync();
        });
        var factory = await harness.ClientsAsync("operator");
        await using var context = Context(factory);
        context.ComponentFactories.AddStub<Activity>();
        var page = context.Render<RequestDetail>(p => p.Add(x => x.Id, "live-request"));
        await factory.Frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        WaitForInitialSnapshot(page, factory);
        factory.PauseNextSnapshot = true;
        await harness.CompleteAllAsync();
        await factory.SnapshotStarted.Task.WaitAsync(SetupTimeout);

        page.Render(p => p.Add(x => x.Id, "second-request"));
        factory.ReleaseSnapshot.TrySetResult(true);
        await factory.Frames.Reconnected.Task.WaitAsync(SetupTimeout);
        page.WaitForAssertion(() => Assert.Contains(page.FindAll("[role=tab]"), tab => tab.TextContent.Trim() == "Tasks"), SetupTimeout);
        await page.InvokeAsync(() => page.FindAll("[role=tab]").Single(x => x.TextContent.Trim() == "Tasks").ClickAsync(new()));

        using var client = factory.CreateClient("HelpdeskApi");
        using var oldChange = await client.PostAsJsonAsync("api/v1/requests/live-request/state", new { NewState = TicketState.OnHold });
        oldChange.EnsureSuccessStatusCode();
        using var currentChange = await client.PostAsJsonAsync("api/v1/requests/second-request/state", new { NewState = TicketState.InProgress });
        currentChange.EnsureSuccessStatusCode();
        await factory.Frames.SecondRequestState.Task.WaitAsync(SetupTimeout);
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Second scoped request", page.Find("h1").TextContent);
            Assert.Contains("In progress", page.Find("[data-testid=request-committed-state]").TextContent);
            Assert.Equal("second-task", Assert.Single(page.FindAll(".request-task-state")).GetAttribute("data-task-id"));
            Assert.DoesNotContain("Harmless automation", page.Markup);
        }, SetupTimeout);
    }

    private static BunitContext Context(IHttpClientFactory factory, string role = "HelpdeskAdmin")
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized("Synthetic operator"); auth.SetRoles(role);
        context.Services.AddSingleton(factory);
        context.Services.AddSingleton<TimelineStream>();
        return context;
    }

    internal sealed class Harness(ServiceIdentityMigrationTests.DatabaseFixture database, WebApplication api, WebApplication gateway,
        WebGatewayConfiguration configuration) : IAsyncDisposable
    {
        public ServiceIdentityMigrationTests.DatabaseFixture Database { get; } = database;
        public static async Task<Harness> CreateAsync(bool postgres)
        {
            var database = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
            await using (var db = database.Open()) await db.Database.MigrateAsync();
            await using (var identity = database.Identity()) await identity.Database.MigrateAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped(_ => database.Open());
            builder.Services.AddScoped(_ => database.Identity());
            builder.Services.AddScoped<ITenantContext>(_ => new Tenant());
            builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            builder.Services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
            builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
            builder.Services.AddScoped<INotificationService, NotificationService>();
            builder.Services.AddScoped<IDomainEventPublisher, NotificationDomainEventPublisher>();
            builder.Services.AddScoped<IRequestTaskLifecycleService, RequestTaskLifecycleService>();
            builder.Services.AddScoped<IRequestTaskStateService, RequestTaskStateService>();
            builder.Services.AddSingleton<ITimelineEventBus, TimelineEventBus>();
            builder.Services.AddScoped<ISelfServiceAudienceService, SelfServiceAudienceService>();
            var correlation = Substitute.For<ICorrelationContext>(); correlation.GetCorrelationId().Returns("live-acceptance");
            builder.Services.AddSingleton(correlation);
            builder.Services.AddSingleton(Substitute.For<ITicketNotificationService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaCompletionService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaRepository>());
            builder.Services.AddSingleton(Substitute.For<ISlaClockService>());
            builder.Services.AddSingleton(Substitute.For<ISlaEscalationEvaluator>());
            builder.Services.AddSingleton(Substitute.For<IOrchestrationConnectivityService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingService>());
            builder.Services.AddSingleton(Substitute.For<IRequestTaskPayloadBuilder>());
            builder.Services.AddSingleton(Substitute.For<IOrchestrationInternalClient>());
            builder.Services.AddSingleton(Substitute.For<IFailurePolicyEngine>());
            builder.Services.AddAuthentication(LocalAuthenticationOptions.Scheme).AddCookie(LocalAuthenticationOptions.Scheme, options =>
            {
                options.Cookie.Name = "live-acceptance";
                options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            });
            builder.Services.AddAuthorization(options =>
            {
                foreach (var name in new[] { "RequestAccess", "RequestManager", "SelfService.User" }) options.AddPolicy(name, policy => policy.RequireAuthenticatedUser());
            });
            var api = builder.Build();
            api.UseAuthentication(); api.UseAuthorization();
            api.MapRequestEndpoints(); api.MapSelfServiceMyRequestsEndpoints(); api.MapRequestWorkflowTimelineEndpoints();
            api.MapGet("/api/v1/categories", () => Array.Empty<TicketCategoryDto>());
            api.MapGet("/api/v1/ticketing/assignees", () => Array.Empty<UserDto>());
            api.MapGet("/test/sign-in/{id}", async (string id, HttpContext context, RatelDeskIdentityDbContext identity) =>
            {
                var account = await identity.Users.AsNoTracking().SingleAsync(x => x.Id == id);
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, id), new("auth_mode", "local"),
                    new("security_stamp", account.SecurityStamp!), new("authorization_revision", account.AuthorizationRevision.ToString())
                };
                if (account.IsInstanceAdministrator) claims.Add(new(ClaimTypes.Role, "HelpdeskAdmin"));
                else claims.Add(new(ClaimTypes.Role, "SelfService.User"));
                await context.SignInAsync(LocalAuthenticationOptions.Scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, "Local")),
                    new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) });
                return Results.Ok();
            });
            await api.StartAsync();
            var configuration = new WebGatewayConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ApiBaseUrl"] = api.Urls.Single() + "/" }).Build());
            var web = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            web.Logging.ClearProviders(); web.WebHost.UseTestServer();
            web.Services.AddAuthorization(); web.Services.AddReverseProxy().LoadFromConfig(configuration.EffectiveConfiguration.GetSection("ReverseProxy"));
            var gateway = web.Build(); gateway.UseRouting();
            gateway.Use(async (context, next) => { context.Response.Headers["X-Request-Live-Gateway"] = "true"; await next(context); });
            WebGatewayGuard.UseWebGatewayRequestGuard(gateway, configuration.Options);
            gateway.MapReverseProxy().AllowAnonymous(); await gateway.StartAsync();
            var result = new Harness(database, api, gateway, configuration);
            await result.SeedAsync();
            return result;
        }

        private async Task SeedAsync()
        {
            await using (var identity = Database.Identity())
            {
                identity.Users.AddRange(new ApplicationUser { Id = "operator", UserName = "operator", IsInstanceAdministrator = true, SecurityStamp = "stamp-operator" },
                    new ApplicationUser { Id = "requester", UserName = "requester", SecurityStamp = "stamp-requester" },
                    new ApplicationUser { Id = "other-requester", UserName = "other-requester", SecurityStamp = "stamp-other" });
                await identity.SaveChangesAsync();
            }
            await WithDbAsync(async db =>
            {
                db.Organizations.Add(new Organization { Id = "live-org", Name = "Synthetic live organization" });
                db.Users.AddRange(new User { Id = "requester", Name = "Synthetic requester", OrganizationId = "live-org" },
                    new User { Id = "other-requester", Name = "Other requester", OrganizationId = "live-org" });
                db.Customers.AddRange(new Customer { Id = "live-customer", Name = "Synthetic requester", Email = "requester@example.test", OrganizationId = "live-org" },
                    new Customer { Id = "other-customer", Name = "Other requester", Email = "other@example.test", OrganizationId = "live-org" });
                db.CustomerAuthLinks.AddRange(new CustomerAuthLink { CustomerId = "live-customer", DomainUserId = "requester", LocalAccountId = "requester", AuthProviderType = "Local", InviteStatus = CustomerInviteStatus.Active },
                    new CustomerAuthLink { CustomerId = "other-customer", DomainUserId = "other-requester", LocalAccountId = "other-requester", AuthProviderType = "Local", InviteStatus = CustomerInviteStatus.Active });
                db.Roles.Add(new Role { Name = "Self service", Key = "live-self-service", Scope = RoleScopeKind.OwnResource,
                    Permissions = [new RolePermission { Permission = HelpdeskPermissions.SelfServiceUser }, new RolePermission { Permission = HelpdeskPermissions.RequestUser }] });
                foreach (var user in new[] { "requester", "other-requester" }) db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = user, RoleKey = "live-self-service", OrganizationId = "live-org" });
                db.Requests.Add(new Request { Id = "live-request", TrackingId = "REQ-LIVE", Title = "Synthetic committed progress", OrganizationId = "live-org", CustomerId = "live-customer", State = TicketState.InProgress, WorkflowStatus = "InProgress" });
                for (var number = 1; number <= 3; number++) db.RequestTasks.Add(new RequestTask
                {
                    Id = $"live-task-{number}", RequestId = "live-request", Name = $"Harmless automation {number}", OrganizationId = "live-org",
                    TrackingId = $"TASK-LIVE-{number}", Type = RequestTaskType.Automation, Status = RequestTaskStatus.InProgress,
                    State = TicketState.InProgress, StartedAt = DateTimeOffset.UtcNow, LastAutomationStatus = "running", Order = number,
                    OrchestrationExternalRunId = $"provider-run-{number}"
                });
                await db.SaveChangesAsync();
            });
        }
        public async Task CompleteAllAsync()
        {
            await using var scope = api.Services.CreateAsyncScope();
            await using var transaction = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.BeginTransactionAsync();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRequestTaskLifecycleService>();
            for (var number = 1; number <= 3; number++) await lifecycle.CompleteAsync($"live-task-{number}", null, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IRequestTaskStateService>().EvaluateParentRequestState("live-request");
            await transaction.CommitAsync();
        }
        public async Task WithDbAsync(Func<HelpdeskDbContext, Task> action)
        {
            await using var db = Database.Open(); await action(db);
        }
        public async Task<ClientFactory> ClientsAsync(string account)
        {
            using var login = new HttpClient { BaseAddress = new Uri(api.Urls.Single()) };
            using var response = await login.GetAsync("test/sign-in/" + account);
            response.EnsureSuccessStatusCode();
            var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            return new ClientFactory(gateway, cookie);
        }
        public async ValueTask DisposeAsync()
        {
            await gateway.DisposeAsync(); await api.DisposeAsync(); configuration.Dispose(); await Database.DisposeAsync();
        }
        private sealed class Tenant : ITenantContext { public string? TenantId => null; public string? UserId => "operator"; public bool IsHelpdeskAdmin => true; }
    }

    internal sealed class ClientFactory(WebApplication gateway, string cookie) : IHttpClientFactory
    {
        private int requestSnapshots;
        public bool DiagnoseReads { get; set; }
        public ConcurrentQueue<string> Reads { get; } = new();
        public FrameRecorder Frames { get; } = new();
        public int RequestSnapshots => Volatile.Read(ref requestSnapshots);
        public void RecordRequestSnapshot() => Interlocked.Increment(ref requestSnapshots);
        public bool StreamingUnavailable { get; set; }
        public RecordingStream? CurrentStream { get; set; }
        public bool PauseNextSnapshot { get; set; }
        public bool RejectNextSave { get; set; }
        public TaskCompletionSource<bool> SnapshotStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name)
        {
            var handler = name == "HelpdeskApiStreaming"
                ? (HttpMessageHandler)new RecordingHandler(this) { InnerHandler = gateway.GetTestServer().CreateHandler() }
                : new SnapshotBarrierHandler(this) { InnerHandler = gateway.GetTestServer().CreateHandler() };
            var client = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test"), Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Add("Cookie", cookie); return client;
        }
    }

    private sealed class SnapshotBarrierHandler(ClientFactory factory) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            if (request.Method == HttpMethod.Put && factory.RejectNextSave)
            {
                factory.RejectNextSave = false;
                factory.SaveStarted.TrySetResult(true);
                await factory.ReleaseSave.Task.WaitAsync(ct).ConfigureAwait(false);
                // This UI regression deliberately rejects the command. State changes and
                // invalidation frames still come from the real committed API lifecycle.
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("Another operation changed this request.") };
            }
            // Network instrumentation must not queue its completion behind the
            // renderer and add an artificial delay to canonical HTTP reads.
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (factory.DiagnoseReads && request.Method == HttpMethod.Get)
            {
                var prefix = $"GET {request.RequestUri?.PathAndQuery}; status={(int)response.StatusCode}";
                factory.Reads.Enqueue($"{DateTimeOffset.UtcNow:O} {prefix}; headersMs={started.Elapsed.TotalMilliseconds:F0}");
                if (response.IsSuccessStatusCode && request.RequestUri?.AbsolutePath.StartsWith("/api/v1/requests/", StringComparison.Ordinal) == true)
                {
                    var original = response.Content;
                    var trace = new ReadTraceStream(await original.ReadAsStreamAsync(ct).ConfigureAwait(false), body =>
                        factory.Reads.Enqueue($"{DateTimeOffset.UtcNow:O} {prefix}; readMs={started.Elapsed.TotalMilliseconds:F0}; body={body}"));
                    var content = new StreamContent(trace);
                    foreach (var header in original.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    response.Content = content;
                }
            }
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath is "/api/v1/requests/live-request" or "/api/v1/self-service/requests/live-request")
            {
                factory.RecordRequestSnapshot();
                if (factory.PauseNextSnapshot)
                {
                    factory.PauseNextSnapshot = false;
                    factory.SnapshotStarted.TrySetResult(true);
                    await factory.ReleaseSnapshot.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            return response;
        }
    }

    private sealed class ReadTraceStream(Stream inner, Action<string> record) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); record(Encoding.UTF8.GetString(buffer, offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false); record(Encoding.UTF8.GetString(buffer.Span[..read])); return read; }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
    internal sealed class FrameRecorder
    {
        private readonly ConcurrentQueue<string> text = new();
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> State { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? StateObservedAt { get; private set; }
        public TaskCompletionSource<bool> SecondRequestState { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GatewayHeaderSeen { get; set; }
        public string Text => string.Concat(text);
        public void Record(ReadOnlySpan<byte> bytes)
        {
            text.Enqueue(Encoding.UTF8.GetString(bytes));
            var all = Text;
            if (all.Contains("event: ready", StringComparison.Ordinal)) Ready.TrySetResult(true);
            if (all.Split("event: ready", StringSplitOptions.None).Length >= 3) Reconnected.TrySetResult(true);
            if (all.Contains("event: request-state", StringComparison.Ordinal))
            {
                StateObservedAt ??= DateTimeOffset.UtcNow;
                State.TrySetResult(true);
            }
            if (all.Contains("\"ticketId\":\"second-request\"", StringComparison.Ordinal)) SecondRequestState.TrySetResult(true);
        }
    }
    private sealed class RecordingHandler(ClientFactory factory) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (factory.StreamingUnavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var response = await base.SendAsync(request, ct);
            factory.Frames.GatewayHeaderSeen = response.Headers.Contains("X-Request-Live-Gateway");
            var original = response.Content;
            factory.CurrentStream = new RecordingStream(await original.ReadAsStreamAsync(ct), factory.Frames);
            var content = new StreamContent(factory.CurrentStream);
            foreach (var header in original.Headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = content; return response;
        }
    }
    internal sealed class RecordingStream(Stream inner, FrameRecorder recorder) : Stream
    {
        private readonly CancellationTokenSource interrupted = new();
        public void Disconnect() => interrupted.Cancel();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); recorder.Record(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, interrupted.Token);
            try { var read = await inner.ReadAsync(buffer, linked.Token); recorder.Record(buffer.Span[..read]); return read; }
            catch (OperationCanceledException) when (interrupted.IsCancellationRequested) { return 0; }
        }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); interrupted.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); interrupted.Dispose(); GC.SuppressFinalize(this); }
    }
}
