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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_automation_transitions_cross_gateway_SSE_and_update_rendered_tasks_header_and_workflow(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        var factory = await harness.ClientsAsync("operator");
        await using var context = Context(factory);
        // The Activity component is deliberately absent. Tasks must own its subscription.
        context.ComponentFactories.AddStub<Activity>();
        var page = context.Render<RequestDetail>(p => p.Add(x => x.Id, "live-request"));
        await factory.Frames.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await page.FindAll("button[role=tab]").Single(x => x.TextContent.Trim() == "Tasks").ClickAsync(new());
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".request-task-state").Count));
        await page.Find("button[aria-label='Task details: Harmless automation 1']").ClickAsync(new());
        Assert.Single(page.FindAll(".request-task-details"));
        Assert.DoesNotContain("Running</button>", page.Markup);
        Assert.DoesNotContain("provider-run-1", page.Find(".request-tasks-table tbody").TextContent.Split("Provider outcome")[0]);

        await harness.CompleteAllAsync();
        var elapsed = Stopwatch.StartNew();
        await factory.Frames.State.Task.WaitAsync(TimeSpan.FromSeconds(2));
        page.WaitForAssertion(() =>
        {
            Assert.All(page.FindAll(".request-task-state"), row => Assert.Contains("Completed", row.TextContent));
            Assert.Contains("Resolved", page.Find(".ticket-header-status-chip").TextContent);
            Assert.Contains("Completed", page.Find("[data-testid=request-workflow-summary]").TextContent);
            Assert.Contains("3 / 3", page.Find("[data-testid=request-workflow-summary]").TextContent);
            Assert.Single(page.FindAll(".request-task-details"));
        }, TimeSpan.FromSeconds(2));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Gateway/frame/render convergence took {elapsed.Elapsed}.");
        output.WriteLine($"Provider={(postgres ? "PostgreSQL" : "SQLite")}; Request=live-request; gateway=true; persisted terminal → SSE → rendered convergence={elapsed.Elapsed.TotalMilliseconds:F0} ms");
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
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("td[data-label=Status]").Count));
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
        page.WaitForAssertion(() => Assert.Contains("In progress", page.Find(".ticket-header-status-chip").TextContent));
        factory.StreamingUnavailable = true;
        factory.CurrentStream!.Disconnect();
        page.WaitForAssertion(() => Assert.Contains("Reconnecting", page.Find("[data-testid=request-live-updates]").TextContent));
        await harness.CompleteAllAsync();
        factory.StreamingUnavailable = false;
        page.WaitForAssertion(() => Assert.Contains("Resolved", page.Find(".ticket-header-status-chip").TextContent), TimeSpan.FromSeconds(10));
        Assert.True(factory.Frames.Text.Split("event: ready").Length >= 3);
        Assert.DoesNotContain("event: request-state", factory.Frames.Text);
        Assert.Contains("Completed", page.Find("[data-testid=request-workflow-summary]").TextContent);
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
            var lifecycle = scope.ServiceProvider.GetRequiredService<IRequestTaskLifecycleService>();
            for (var number = 1; number <= 3; number++) await lifecycle.CompleteAsync($"live-task-{number}", null, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IRequestTaskStateService>().EvaluateParentRequestState("live-request");
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
        public FrameRecorder Frames { get; } = new();
        public bool StreamingUnavailable { get; set; }
        public RecordingStream? CurrentStream { get; set; }
        public HttpClient CreateClient(string name)
        {
            var handler = name == "HelpdeskApiStreaming" ? new RecordingHandler(this) { InnerHandler = gateway.GetTestServer().CreateHandler() } : gateway.GetTestServer().CreateHandler();
            var client = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test"), Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Add("Cookie", cookie); return client;
        }
    }
    internal sealed class FrameRecorder
    {
        private readonly ConcurrentQueue<string> text = new();
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> State { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GatewayHeaderSeen { get; set; }
        public string Text => string.Concat(text);
        public void Record(ReadOnlySpan<byte> bytes)
        {
            text.Enqueue(Encoding.UTF8.GetString(bytes));
            var all = Text;
            if (all.Contains("event: ready", StringComparison.Ordinal)) Ready.TrySetResult(true);
            if (all.Contains("event: request-state", StringComparison.Ordinal)) State.TrySetResult(true);
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
