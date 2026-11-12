using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Services;
using Helpdesk.Application.Events;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
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
using Testcontainers.PostgreSql;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("SelfServiceMyRequestsEndpoints")]
public sealed class SelfServiceMyRequestsEndpointsTests
{
    [Fact]
    public async Task GetMyRequests_ReturnsAccessible_Request_WithReleaseStatus()
    {
        await using var harness = await SelfServiceMyRequestsTestHarness.CreateAsync();

        var response = await harness.Client.GetAsync("/api/v1/self-service/requests");
        var payload = await response.Content.ReadFromJsonAsync<PagedResponse<MyRequestListItemDto>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal(1, payload!.TotalCount);
        Assert.Single(payload.Items);
        Assert.Equal(harness.RequestId, payload.Items[0].Id);
        Assert.Equal(RequestFormReleaseStatus.Production, payload.Items[0].ReleaseStatus);
    }

    [Fact]
    public async Task GetMyRequestDetail_ReturnsOwned_Request()
    {
        await using var harness = await SelfServiceMyRequestsTestHarness.CreateAsync();

        var response = await harness.Client.GetAsync($"/api/v1/self-service/requests/{harness.RequestId}");
        var payload = await response.Content.ReadFromJsonAsync<MyRequestDetailDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal(harness.RequestId, payload!.Id);
        Assert.Equal(RequestFormReleaseStatus.Production, payload.ReleaseStatus);
        Assert.Equal("Need laptop access", payload.Title);
    }

    [Fact]
    public async Task IntegrationCredential_ScopedToAnotherOrganization_DoesNotExposeCustomerRequests()
    {
        await using var harness = await SelfServiceMyRequestsTestHarness.CreateAsync("IntegrationB");

        var list = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>("/api/v1/self-service/requests");
        var detail = await harness.Client.GetAsync($"/api/v1/self-service/requests/{harness.RequestId}");
        var tasks = await harness.Client.GetAsync($"/api/v1/self-service/requests/{harness.RequestId}/tasks");

        Assert.NotNull(list);
        Assert.Equal(0, list!.TotalCount);
        Assert.Empty(list.Items);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, tasks.StatusCode);
    }

    [Fact]
    public async Task GetMyRequests_DoesNotUseMatchingEmailWithoutAnExplicitCustomerLink()
    {
        await using var harness = await SelfServiceMyRequestsTestHarness.CreateAsync("EmailOnly");

        var response = await harness.Client.GetAsync("/api/v1/self-service/requests");
        var payload = await response.Content.ReadFromJsonAsync<PagedResponse<MyRequestListItemDto>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(payload);
        Assert.Equal(0, payload!.TotalCount);
        Assert.Empty(payload.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_query_filters_sorts_counts_and_clamps_beyond_one_hundred_records(bool postgres)
    {
        await using var harness = await SelfServiceMyRequestsTestHarness.CreateAsync(postgres: postgres);
        await harness.SeedDirectoryAsync();
        var first = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>("/api/v1/self-service/requests?sortBy=title&descending=false");
        Assert.NotNull(first);
        Assert.Equal(138, first.TotalCount);
        Assert.Equal(25, first.PageSize);
        Assert.Equal(25, first.Items.Count);
        Assert.DoesNotContain(first.Items, x => x.Id == "request-2");

        var pages = new List<MyRequestListItemDto>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>($"/api/v1/self-service/requests?page={page}&pageSize=50&sortBy=created&descending=false");
            Assert.Equal(138, result!.TotalCount);
            pages.AddRange(result.Items);
        }
        Assert.Equal(138, pages.Count);
        Assert.Equal(138, pages.Select(x => x.Id).Distinct().Count());
        var tiedRows = pages.Where(x => x.Id.StartsWith("bulk-", StringComparison.Ordinal)).ToList();
        Assert.Equal(tiedRows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal), tiedRows.Select(x => x.Id));

        var exact = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>(
            "/api/v1/self-service/requests?q=REQUEST%20136&state=3&releaseStage=1&createdFromUtc=2026-11-01T00:00:00Z&createdBeforeUtc=2026-11-02T00:00:00Z&updatedFromUtc=2026-11-03T00:00:00Z&updatedBeforeUtc=2026-11-04T00:00:00Z&sortBy=updated");
        Assert.Equal("bulk-136", Assert.Single(exact!.Items).Id);
        Assert.Equal(1, exact.TotalCount);
        var dateOnly = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>(
            "/api/v1/self-service/requests?createdFromUtc=2026-11-01&createdBeforeUtc=2026-11-02&updatedFromUtc=2026-11-03&updatedBeforeUtc=2026-11-04");
        Assert.Equal(137, dateOnly!.TotalCount);

        var last = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>("/api/v1/self-service/requests?page=2147483647&pageSize=100&sortBy=trackingId&descending=false");
        Assert.Equal(2, last!.Page);
        Assert.Equal(38, last.Items.Count);
        var noMatch = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>("/api/v1/self-service/requests?page=19&q=absent");
        Assert.Equal(1, noMatch!.Page);
        Assert.Equal(0, noMatch.TotalCount);
        Assert.Empty(noMatch.Items);

        foreach (var sort in new[] { "created", "updated", "trackingId", "title", "state" })
        {
            var ascending = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>($"/api/v1/self-service/requests?sortBy={sort}&descending=false&pageSize=100");
            var descending = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>($"/api/v1/self-service/requests?sortBy={sort}&descending=true&pageSize=100");
            Assert.Equal(138, ascending!.TotalCount);
            Assert.Equal(138, descending!.TotalCount);
            Assert.Equal(100, ascending.Items.Count);
            Assert.Equal(100, descending.Items.Count);
            Assert.Equal(sort is "created" or "updated" ? "bulk-000" : "request-1", ascending.Items[0].Id);
            Assert.Equal(sort switch
            {
                "created" or "updated" => "request-1",
                "state" => "bulk-001",
                _ => "bulk-136"
            }, descending.Items[0].Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filters_preserve_owned_request_fences_and_authorized_instance_administrator_exception(bool postgres)
    {
        await using var harness = await SelfServiceMyRequestsTestHarness.CreateAsync(postgres: postgres);
        await harness.SeedDirectoryAsync();
        var hidden = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>("/api/v1/self-service/requests?q=foreign");
        Assert.Empty(hidden!.Items);
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "Admin");
        var admin = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>("/api/v1/self-service/requests?q=foreign&releaseStage=0");
        Assert.Equal("foreign-request", Assert.Single(admin!.Items).Id);
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "IntegrationB");
        var denied = await harness.Client.GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>("/api/v1/self-service/requests?q=Request&state=3&page=999");
        Assert.Empty(denied!.Items);
        Assert.Equal(0, denied.TotalCount);
        Assert.Equal(1, denied.Page);
    }

    [Theory]
    [InlineData("sortBy=unsupported")]
    [InlineData("state=999")]
    [InlineData("releaseStage=999")]
    [InlineData("createdFromUtc=2026-11-03&createdBeforeUtc=2026-11-02")]
    [InlineData("updatedFromUtc=2026-11-03&updatedBeforeUtc=2026-11-03")]
    public async Task Invalid_query_has_a_useful_bad_request(string query)
    {
        await using var harness = await SelfServiceMyRequestsTestHarness.CreateAsync();
        var response = await harness.Client.GetAsync("/api/v1/self-service/requests?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid request filters", await response.Content.ReadAsStringAsync());
    }

    private sealed class SelfServiceMyRequestsTestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly WebApplication _app;
        private readonly PostgreSqlContainer? _postgres;

        private SelfServiceMyRequestsTestHarness(
            SqliteConnection connection,
            WebApplication app,
            HttpClient client,
            string requestId,
            PostgreSqlContainer? postgres)
        {
            _connection = connection;
            _app = app;
            Client = client;
            RequestId = requestId;
            _postgres = postgres;
        }

        public HttpClient Client { get; }
        public string RequestId { get; }

        public static async Task<SelfServiceMyRequestsTestHarness> CreateAsync(string authHeader = "SelfService", bool postgres = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            PostgreSqlContainer? container = null;
            if (postgres)
            {
                container = new PostgreSqlBuilder("postgres:16").Build();
                await container.StartAsync();
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<HelpdeskDbContext>(options =>
            {
                if (container is null) options.UseSqlite(connection);
                else options.UseNpgsql(container.GetConnectionString());
            });
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("tenant-1", "user-1", isHelpdeskAdmin: false));
            builder.Services.AddScoped<ISelfServiceAudienceService, AllowingSelfServiceAudienceService>();
            builder.Services.AddScoped<ICurrentUserAccessService, ClaimsCurrentUserAccessService>();
            builder.Services.AddSingleton<IDomainEventPublisher, TestDomainEventPublisher>();
            builder.Services.AddScoped<ICorrelationContext>(_ => new TestCorrelationContext("corr-self-service-tests"));
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("SelfService.User", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("SelfService.User", "HelpdeskAdmin");
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapSelfServiceMyRequestsEndpoints();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync();

                var user = new User
                {
                    Id = "user-1",
                    Name = "Requester",
                    Email = "requester@example.com",
                    OrganizationId = "tenant-1",
                    Role = "Customer",
                    IsTestUser = false
                };

                var customer = new Customer
                {
                    Id = "customer-1",
                    Name = "Requester",
                    Email = "requester@example.com",
                    OrganizationId = "tenant-1"
                };

                var form = new RequestForm
                {
                    Id = "form-1",
                    Title = "Laptop Access",
                    Description = "Request access",
                    ServiceId = "service-1",
                    OrganizationId = "tenant-1",
                    ReleaseStatus = RequestFormReleaseStatus.Production
                };

                var request = new Request
                {
                    Id = "request-1",
                    TrackingId = "REQ-001",
                    Title = "Need laptop access",
                    Description = "Please grant laptop access",
                    CustomerId = "customer-1",
                    OrganizationId = "tenant-1",
                    RequestFormId = "form-1",
                    State = TicketState.New,
                    CreatedAt = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc)
                };

                var otherRequest = new Request
                {
                    Id = "request-2",
                    TrackingId = "REQ-002",
                    Title = "Other request",
                    Description = "Should stay hidden",
                    CustomerId = "customer-2",
                    OrganizationId = "tenant-1",
                    RequestFormId = "form-1",
                    State = TicketState.New,
                    CreatedAt = DateTime.UtcNow
                };

                db.Users.Add(user);
                db.Customers.Add(customer);
                db.RequestForms.Add(form);
                db.Requests.Add(request);
                db.Requests.Add(otherRequest);
                await db.SaveChangesAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", authHeader);
            return new SelfServiceMyRequestsTestHarness(connection, app, client, "request-1", container);
        }

        public async Task SeedDirectoryAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.RequestForms.Add(new RequestForm
            {
                Id = "foreign-form", Title = "Foreign form", ServiceId = "service-foreign",
                OrganizationId = "tenant-2", ReleaseStatus = RequestFormReleaseStatus.InTesting
            });
            db.Requests.Add(new Request
            {
                Id = "foreign-request", TrackingId = "REQ-FOREIGN", Title = "Foreign request",
                CustomerId = "customer-1", OrganizationId = "tenant-2", RequestFormId = "foreign-form"
            });
            for (var i = 0; i < 137; i++)
                db.Requests.Add(new Request
                {
                    Id = $"bulk-{i:D3}", TrackingId = $"REQ-BULK-{i:D3}", Title = $"Request {i:D3}",
                    CustomerId = "customer-1", OrganizationId = "tenant-1", RequestFormId = "form-1",
                    State = i % 2 == 0 ? TicketState.InProgress : TicketState.OnHold,
                    CreatedAt = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc)
                });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
            if (_postgres is not null) await _postgres.DisposeAsync();
        }
    }

    private sealed class TestTenantContext(string? tenantId, string? userId, bool isHelpdeskAdmin) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin { get; } = isHelpdeskAdmin;
    }

    private sealed class TestDomainEventPublisher : IDomainEventPublisher
    {
        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AllowingSelfServiceAudienceService : ISelfServiceAudienceService
    {
        public Task<bool> IsTestUserAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> CanAccessRequestFormAsync(RequestForm requestForm, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public IQueryable<RequestForm> ApplyAudienceFilter(
            IQueryable<RequestForm> query,
            bool isAdmin,
            bool isTestUser,
            string? tenantId) =>
            query.Where(f =>
                f.OrganizationId == tenantId &&
                f.ReleaseStatus == RequestFormReleaseStatus.Production);
    }

    private sealed class TestCorrelationContext(string correlationId) : ICorrelationContext
    {
        public string GetCorrelationId() => correlationId;
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
            var claims = Request.Headers.Authorization.ToString().Contains("Admin", StringComparison.OrdinalIgnoreCase)
                ? new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(ClaimTypes.Name, "Administrator"),
                    new Claim(ClaimTypes.Role, "HelpdeskAdmin")
                }
                : Request.Headers.Authorization.ToString().Contains("IntegrationB", StringComparison.OrdinalIgnoreCase)
                ? new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(ClaimTypes.Name, "Requester"),
                    new Claim("preferred_username", "requester@example.com"),
                    new Claim("customer_id", "customer-1"),
                    new Claim("allowed_organization_id", "tenant-2"),
                    new Claim(ClaimTypes.Role, "SelfService.User")
                }
                : Request.Headers.Authorization.ToString().Contains("EmailOnly", StringComparison.OrdinalIgnoreCase)
                ? new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-1"),
                    new Claim(ClaimTypes.Name, "Requester"),
                    new Claim("preferred_username", "requester@example.com"),
                    new Claim(ClaimTypes.Role, "SelfService.User")
                }
                : new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim(ClaimTypes.Name, "Requester"),
                new Claim("preferred_username", "requester@example.com"),
                new Claim("customer_id", "customer-1"),
                new Claim("organization_id", "tenant-1"),
                new Claim(ClaimTypes.Role, "SelfService.User")
            };

            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class ClaimsCurrentUserAccessService : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult(CurrentUserAccessProfile.FromClaims(user));
    }
}

[CollectionDefinition("SelfServiceMyRequestsEndpoints", DisableParallelization = true)]
public sealed class SelfServiceMyRequestsEndpointsCollection
{
}
