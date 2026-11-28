using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Tests.Fixtures;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.RequestTasks;
using Helpdesk.API.Middleware;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.Workflow;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Shared.Auth;
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

namespace Helpdesk.Tests.Api;

public sealed class AuthenticatedRequestApprovalEndpointTests
{
    [Fact]
    public async Task Replaying_a_committed_approval_recovers_a_lost_workflow_continuation_without_duplicate_audit()
    {
        await using var fixture = await Fixture.StartAsync(failFirstContinuation: true);
        using var client = fixture.Application.GetTestClient();

        var interrupted = await client.PostAsync("/api/v1/request-tasks/task-1/approval/approve", null);
        Assert.Equal(HttpStatusCode.InternalServerError, interrupted.StatusCode);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Approved, RequestTaskStatus.Completed, 1);

        var replayed = await client.PostAsync("/api/v1/request-tasks/task-1/approval/approve", null);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(2, fixture.ContinuationCalls);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Approved, RequestTaskStatus.Completed, 1);
        var opposite = await client.PostAsJsonAsync("/api/v1/request-tasks/task-1/approval/reject", new { reason = "Opposite replay" });
        Assert.Equal(HttpStatusCode.Conflict, opposite.StatusCode);
        Assert.Equal(2, fixture.ContinuationCalls);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Approved, RequestTaskStatus.Completed, 1);
    }

    [Fact]
    public async Task An_owner_cannot_review_another_designated_owners_approval_or_supply_their_identity_in_a_body()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.Application.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", "different-owner");
        var details = await client.GetAsync("/api/v1/request-tasks/task-1/approval/");
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        var impersonated = await client.PostAsJsonAsync("/api/v1/request-tasks/task-1/approval/approve", new { actorId = "owner-1", email = "owner@example.test" });
        Assert.Equal(HttpStatusCode.NotFound, impersonated.StatusCode);
        Assert.Equal(0, fixture.ContinuationCalls);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Pending, RequestTaskStatus.PendingApproval, 0);
    }

    [Fact]
    public async Task Scoped_request_review_requires_write_access_to_the_current_parent_queue()
    {
        await using var fixture = await Fixture.StartAsync(scoped: true);
        using var client = fixture.Application.GetTestClient();
        const string route = "/api/v1/request-tasks/task-1/approval";

        await fixture.SetAssignedGroupAsync(null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        await fixture.SetAssignedGroupAsync("wintel");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(route + "/approve", null)).StatusCode);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Pending, RequestTaskStatus.PendingApproval, 0);

        await fixture.SetAssignedGroupAsync("vendor");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(route + "/approve", null)).StatusCode);
        await fixture.AssertStoredAsync(RequestTaskApprovalStatus.Approved, RequestTaskStatus.Completed, 1);

        await fixture.SetAssignedGroupAsync("wintel");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(route + "/approve", null)).StatusCode);
        Assert.Equal(1, fixture.ContinuationCalls);
    }

    private sealed class Fixture(WebApplication application, SqliteConnection connection) : IAsyncDisposable
    {
        public WebApplication Application { get; } = application;
        public int ContinuationCalls { get; private set; }

        public static async Task<Fixture> StartAsync(bool failFirstContinuation = false, bool scoped = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            var tenant = Substitute.For<ITenantContext>();
            tenant.IsHelpdeskAdmin.Returns(true);
            tenant.TenantId.Returns("org-1");
            builder.Services.AddSingleton(tenant);
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
            var access = new CurrentUserAccessProfile(true, "Designated owner", "owner@example.test", "org-1", "Synthetic organization", null, true,
                new HashSet<string>(), new HashSet<string> { HelpdeskPermissions.HelpdeskAdmin }, new HashSet<string> { "org-1" }, new HashSet<string> { "org-1" });
            if (scoped)
                access = access with
                {
                    IsHelpdeskAdmin = false, UsesScopedPermissions = true, RestrictedTicketOrganizationIds = new HashSet<string> { "org-1" },
                    Permissions = new HashSet<string> { HelpdeskPermissions.RequestWrite },
                    TicketQueuePermissionGrants = new HashSet<TicketQueuePermissionGrant>
                    {
                        new(HelpdeskPermissions.RequestWrite, "org-1", "vendor", "org-1")
                    }
                };
            var accessService = Substitute.For<ICurrentUserAccessService>();
            accessService.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(access);
            builder.Services.AddSingleton(accessService);
            builder.Services.AddSingleton<IRequestFormSchemaParser, RequestFormSchemaParser>();
            builder.Services.AddSingleton(Substitute.For<ITicketNotificationService>());
            PublicLinkFixture.AddServices(builder);
            builder.Services.AddSingleton(Substitute.For<ITimelineEventBus>());
            builder.Services.AddScoped<RequestTaskApprovalService>();
            builder.Services.AddScoped<IAuthenticatedRequestTaskApprovalService>(provider => provider.GetRequiredService<RequestTaskApprovalService>());
            var workflows = Substitute.For<IWorkflowEngine>();
            builder.Services.AddSingleton(workflows);
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
            var app = builder.Build();
            var fixture = new Fixture(app, connection);
            workflows.RunAsync("request-1", WorkflowRunReason.ApprovalReceived, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                fixture.ContinuationCalls++;
                return fixture.ContinuationCalls == 1 && failFirstContinuation
                    ? Task.FromException<WorkflowRunResult>(new InvalidOperationException("Synthetic lost workflow continuation."))
                    : Task.FromResult(new WorkflowRunResult { RequestId = "request-1" });
            });
            app.UseExceptionHandler(handler => handler.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return context.Response.WriteAsync("Synthetic continuation failure.");
            }));
            app.UseAuthentication();
            app.UseMiddleware<UserAccessClaimsMiddleware>();
            app.UseAuthorization();
            app.MapAuthenticatedRequestApprovalEndpoints();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Organizations.Add(new Organization { Id = "org-1", Name = "Synthetic organization" });
                if (scoped)
                    db.SupportGroups.AddRange(
                        new SupportGroup { Id = "vendor", Name = "App Vendor", OwningOrganizationId = "org-1" },
                        new SupportGroup { Id = "wintel", Name = "Wintel", OwningOrganizationId = "org-1" });
                db.Requests.Add(new Request { Id = "request-1", Title = "Approval fixture", TrackingId = "REQ-APPROVAL", OrganizationId = "org-1" });
                db.RequestTasks.Add(new RequestTask { Id = "task-1", Title = "Designated decision", RequestId = "request-1", OrganizationId = "org-1", Type = RequestTaskType.Approval, Status = RequestTaskStatus.PendingApproval, DueAt = DateTimeOffset.UtcNow.AddHours(1) });
                db.RequestTaskApprovals.Add(new RequestTaskApproval
                {
                    Id = "approval-1", RequestId = "request-1", RequestTaskId = "task-1", ApproverSource = "User", ApproverId = "owner-1",
                    ApproverName = "Designated owner", ApproverEmail = "owner@example.test", Status = RequestTaskApprovalStatus.Pending
                });
                await db.SaveChangesAsync();
            }
            await app.StartAsync();
            return fixture;
        }

        public async Task SetAssignedGroupAsync(string? groupId)
        {
            await using var scope = Application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var request = await db.Requests.SingleAsync();
            request.AssignedSupportGroupId = groupId;
            await db.SaveChangesAsync();
        }

        public async Task AssertStoredAsync(RequestTaskApprovalStatus approval, RequestTaskStatus task, int auditCount)
        {
            await using var scope = Application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(approval, (await db.RequestTaskApprovals.IgnoreQueryFilters().SingleAsync()).Status);
            Assert.Equal(task, (await db.RequestTasks.IgnoreQueryFilters().SingleAsync()).Status);
            var audit = await db.ActivityLogs.Where(item => item.RelatedEntityId == "task-1").ToListAsync();
            Assert.Equal(auditCount, audit.Count);
            Assert.All(audit, item => Assert.Equal("owner-1", item.UserId));
            Assert.Equal(auditCount, await db.TicketTimelineEvents.IgnoreQueryFilters().CountAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers["X-Test-Actor"].FirstOrDefault() ?? "owner-1";
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor)], "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}
