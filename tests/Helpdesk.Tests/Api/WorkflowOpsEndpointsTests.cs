using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Ops;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.Orchestration;
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

namespace Helpdesk.Tests.Api;

public sealed class WorkflowOpsEndpointsTests
{
    [Fact]
    public async Task RequestManager_CanReadTenantScopedWorkflowOps_ButNotRetriesPending()
    {
        await using var harness = await WorkflowOpsHarness.CreateAsync("RequestManager");

        var overdue = await harness.Client.GetAsync("/api/v1/ops/tasks/overdue?page=1&pageSize=25");
        var escalated = await harness.Client.GetAsync("/api/v1/ops/tasks/escalated?page=1&pageSize=25");
        var critical = await harness.Client.GetAsync(
            $"/api/v1/ops/tasks/critical-failures?sinceUtc={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&page=1&pageSize=25");
        var retries = await harness.Client.GetAsync("/api/v1/ops/tasks/retries-pending?page=1&pageSize=25");

        Assert.Equal(HttpStatusCode.OK, overdue.StatusCode);
        Assert.Equal(HttpStatusCode.OK, escalated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, critical.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, retries.StatusCode);
    }

    [Fact]
    public async Task HelpdeskAdmin_CanReadRetriesPending()
    {
        await using var harness = await WorkflowOpsHarness.CreateAsync("Admin");

        var response = await harness.Client.GetAsync("/api/v1/ops/tasks/retries-pending?page=1&pageSize=25");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Workflow_ops_supports_sqlite_due_date_filters_and_sorting()
    {
        await using var harness = await WorkflowOpsHarness.CreateAsync("Admin", useSqlite: true);

        var overdue = await harness.Client.GetFromJsonAsync<PagedResponse<TaskOpsRowDto>>(
            "/api/v1/ops/tasks/overdue?page=1&pageSize=25");
        var retries = await harness.Client.GetFromJsonAsync<PagedResponse<TaskOpsRowDto>>(
            "/api/v1/ops/tasks/retries-pending?page=1&pageSize=25");

        Assert.Equal(["task-overdue", "task-other"], overdue!.Items.Select(item => item.TaskId));
        Assert.Equal("task-retry", Assert.Single(retries!.Items).TaskId);
    }

    [Theory]
    [InlineData("RequestManager", 2, "binding-later", "binding-earlier")]
    [InlineData("Admin", 3, "binding-foreign", "binding-later")]
    public async Task Binding_issues_on_migrated_sqlite_page_by_utc_instant_with_tenant_scope(
        string actor, int expectedCount, string firstId, string secondId)
    {
        await using var harness = await WorkflowOpsHarness.CreateAsync(actor, useSqlite: true);

        var firstResponse = await harness.Client.GetAsync(
            "/api/v1/ops/tasks/orchestration/binding-issues?page=1&pageSize=1");
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        Assert.True(firstResponse.IsSuccessStatusCode, firstJson);
        Assert.DoesNotContain("restricted-customer-id", firstJson, StringComparison.Ordinal);
        var first = System.Text.Json.JsonSerializer.Deserialize<PagedResponse<AutomationBindingIssueOpsDto>>(
            firstJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var second = await harness.Client.GetFromJsonAsync<PagedResponse<AutomationBindingIssueOpsDto>>(
            "/api/v1/ops/tasks/orchestration/binding-issues?page=2&pageSize=1");

        Assert.Equal(expectedCount, first!.TotalCount);
        Assert.Equal(firstId, Assert.Single(first.Items).BindingId);
        Assert.Equal(expectedCount, second!.TotalCount);
        Assert.Equal(secondId, Assert.Single(second.Items).BindingId);
    }

    private sealed class WorkflowOpsHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly SqliteConnection? _connection;

        private WorkflowOpsHarness(WebApplication app, HttpClient client, SqliteConnection? connection)
        {
            _app = app;
            Client = client;
            _connection = connection;
        }

        public HttpClient Client { get; }

        public static async Task<WorkflowOpsHarness> CreateAsync(string actor, bool useSqlite = false)
        {
            SqliteConnection? connection = null;
            if (useSqlite)
            {
                connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            var databaseName = $"workflow-ops-{Guid.NewGuid():N}";
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContext<HelpdeskDbContext>(options =>
            {
                if (connection is not null)
                    options.UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
                else
                    options.UseInMemoryDatabase(databaseName);
            });
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("org-alpha", "user-1", actor == "Admin"));
            builder.Services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("HelpdeskAdmin", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin);
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapWorkflowOpsEndpoints();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                if (connection is not null)
                {
                    await db.Database.MigrateAsync();
                    Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                }
                else
                    await db.Database.EnsureCreatedAsync();
                Seed(db);
                await db.SaveChangesAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", actor);
            return new WorkflowOpsHarness(app, client, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            if (_connection is not null)
                await _connection.DisposeAsync();
        }

        private static void Seed(HelpdeskDbContext db)
        {
            db.Organizations.AddRange(
                new Organization { Id = "org-alpha", Name = "Alpha" },
                new Organization { Id = "org-other", Name = "Other" });
            db.Customers.Add(new Customer
            {
                Id = "customer-manager",
                Name = "Workflow manager",
                Email = "manager@example.test",
                OrganizationId = "org-alpha"
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = "customer-manager",
                OidcIssuer = "https://issuer.example.test",
                OidcSubject = "manager-1"
            });
            db.Requests.AddRange(
                new Request { Id = "req-alpha", TrackingId = "REQ-ALPHA", Title = "Alpha request", OrganizationId = "org-alpha" },
                new Request { Id = "req-other", TrackingId = "REQ-OTH", Title = "Other request", OrganizationId = "org-other" });

            db.RequestTasks.AddRange(
                NewTask("task-overdue", "req-alpha", "org-alpha", RequestTaskStatus.InProgress, dueAt: DateTimeOffset.UtcNow.AddHours(-2)),
                NewTask("task-escalated", "req-alpha", "org-alpha", RequestTaskStatus.InProgress, escalated: true),
                NewTask("task-critical", "req-alpha", "org-alpha", RequestTaskStatus.Failed, isCritical: true),
                NewTask("task-retry", "req-alpha", "org-alpha", RequestTaskStatus.Failed, nextRetryAt: DateTimeOffset.UtcNow.AddMinutes(30)),
                NewTask("task-other", "req-other", "org-other", RequestTaskStatus.InProgress, dueAt: DateTimeOffset.UtcNow.AddHours(-3)));

            db.Services.Add(new Service
            {
                Id = "workflow-service",
                Name = "Workflow service",
                AllowedCustomerIds = ["restricted-customer-id"]
            });
            db.RequestForms.AddRange(
                new RequestForm { Id = "form-alpha", Title = "Alpha form", OrganizationId = "org-alpha", ServiceId = "workflow-service" },
                new RequestForm { Id = "form-other", Title = "Other form", OrganizationId = "org-other", ServiceId = "workflow-service" });
            db.AutomationBindings.AddRange(
                NewBinding("binding-earlier", "org-alpha", "form-alpha", "2026-09-13T12:00:00+02:00"),
                NewBinding("binding-later", "org-alpha", "form-alpha", "2026-09-13T06:01:00-04:00"),
                NewBinding("binding-foreign", "org-other", "form-other", "2026-09-13T11:00:00Z"));
        }

        private static AutomationBinding NewBinding(string id, string organizationId, string formId, string updatedAt) => new()
        {
            Id = id,
            OrganizationId = organizationId,
            RequestFormId = formId,
            TaskTemplateId = Guid.NewGuid(),
            Enabled = false,
            UpdatedAtUtc = DateTimeOffset.Parse(updatedAt)
        };

        private static RequestTask NewTask(
            string id,
            string requestId,
            string organizationId,
            RequestTaskStatus status,
            DateTimeOffset? dueAt = null,
            DateTimeOffset? nextRetryAt = null,
            bool escalated = false,
            bool isCritical = false) => new()
            {
                Id = id,
                TrackingId = id,
                Title = id,
                RequestId = requestId,
                OrganizationId = organizationId,
                Status = status,
                Type = RequestTaskType.Manual,
                DueAt = dueAt,
                NextRetryAt = nextRetryAt,
                Escalated = escalated,
                IsCritical = isCritical,
                UpdatedAt = isCritical ? DateTime.UtcNow : null
            };
    }

    private sealed class TestTenantContext(string? tenantId, string? userId, bool isHelpdeskAdmin) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin { get; } = isHelpdeskAdmin;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers.Authorization.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "RequestManager";
            var claims = actor == "Admin"
                ? new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.HelpdeskAdmin)
                }
                : new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "manager-1"),
                    new Claim("iss", "https://issuer.example.test"),
                    new Claim("sub", "manager-1"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.RequestManager),
                    new Claim("roles", HelpdeskPermissions.RequestManager)
                };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
