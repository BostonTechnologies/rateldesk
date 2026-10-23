using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRequest = Helpdesk.Shared.Models.Request;

namespace Helpdesk.Tests.Api;

public sealed partial class TicketCustomerAssignmentEndpointsTests
{
    [Theory]
    [InlineData("incidents", "incident-1")]
    [InlineData("requests", "request-1")]
    [InlineData("changes", "change-1")]
    public async Task CustomerAssignment_Saves_ValidScopedCustomer(string ticketType, string ticketId)
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync();
        await harness.SeedTicketAsync(ticketType, ticketId, "org-1");
        await harness.SeedCustomerAsync("customer-1", "org-1", "Example User", "user@example.com");

        var response = await harness.Client.PostAsJsonAsync(
            $"/api/v1/{ticketType}/{ticketId}/customer",
            new { CustomerId = "customer-1" });

        response.EnsureSuccessStatusCode();
        var ticket = await harness.GetTicketAsync(ticketType, ticketId);
        Assert.Equal("customer-1", ticket!.CustomerId);
        Assert.Equal("user@example.com", ticket.RequesterEmail);
        Assert.NotNull(ticket.UpdatedAt);
    }

    [Fact]
    public async Task CustomerAssignment_AllowsTechnician()
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync(role: "Technician");
        await harness.SeedTicketAsync("incidents", "incident-1", "org-1");
        await harness.SeedCustomerAsync("customer-1", "org-1", "Technician User", "tech-user@example.com");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/incident-1/customer",
            new { CustomerId = "customer-1" });

        response.EnsureSuccessStatusCode();
        var ticket = await harness.GetTicketAsync("incidents", "incident-1");
        Assert.Equal("customer-1", ticket!.CustomerId);
    }

    [Fact]
    public async Task CustomerAssignment_RejectsTechnicianOutsideTheirTenantScope()
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync(role: "Technician");
        await harness.SeedTicketAsync("incidents", "incident-1", "org-2");
        await harness.SeedCustomerAsync("customer-1", "org-2", "Other Tenant User", "other-tenant@example.com");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/incident-1/customer",
            new { CustomerId = "customer-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var ticket = await harness.GetTicketAsync("incidents", "incident-1");
        Assert.Null(ticket!.CustomerId);
    }

    [Fact]
    public async Task CustomerAssignment_RejectsCustomerFromDifferentOrganization()
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync();
        await harness.SeedTicketAsync("incidents", "incident-1", "org-1");
        await harness.SeedCustomerAsync("customer-2", "org-2", "Other Customer", "other@example.com");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/incidents/incident-1/customer",
            new { CustomerId = "customer-2" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var ticket = await harness.GetTicketAsync("incidents", "incident-1");
        Assert.Null(ticket!.CustomerId);
    }

    [Fact]
    public async Task CustomerAssignment_RejectsDisabledCustomer()
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync();
        await harness.SeedTicketAsync("requests", "request-1", "org-1");
        await harness.SeedCustomerAsync("customer-1", "org-1", "Blocked Customer", "blocked@example.com", enabled: false);

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/requests/request-1/customer",
            new { CustomerId = "customer-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var ticket = await harness.GetTicketAsync("requests", "request-1");
        Assert.Null(ticket!.CustomerId);
    }

    [Fact]
    public async Task CustomerAssignment_RejectsTicketThatAlreadyHasCustomer()
    {
        await using var harness = await TicketCustomerAssignmentHarness.CreateAsync();
        await harness.SeedTicketAsync("changes", "change-1", "org-1", customerId: "existing-customer");
        await harness.SeedCustomerAsync("customer-1", "org-1", "New Customer", "new@example.com");

        var response = await harness.Client.PostAsJsonAsync(
            "/api/v1/changes/change-1/customer",
            new { CustomerId = "customer-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var ticket = await harness.GetTicketAsync("changes", "change-1");
        Assert.Equal("existing-customer", ticket!.CustomerId);
    }

    private sealed class TicketCustomerAssignmentHarness : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly ServiceIdentityMigrationTests.DatabaseFixture database;

        private TicketCustomerAssignmentHarness(
            WebApplication app,
            HttpClient client,
            ServiceIdentityMigrationTests.DatabaseFixture database,
            RequestReadBarrier barrier)
        {
            this.app = app;
            this.database = database;
            Barrier = barrier;
            Client = client;
        }

        public HttpClient Client { get; }
        public RequestReadBarrier Barrier { get; }

        public static async Task<TicketCustomerAssignmentHarness> CreateAsync(string role = "HelpdeskAdmin", bool postgres = false)
        {
            var database = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
            await using (var db = database.Open())
            {
                await db.Database.EnsureCreatedAsync();
                db.Organizations.AddRange(new Organization { Id = "org-1", Name = "Primary fixture" },
                    new Organization { Id = "org-2", Name = "Other fixture" });
                await db.SaveChangesAsync();
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var barrier = new RequestReadBarrier();
            builder.Services.AddScoped<HelpdeskDbContext>(_ => database.Open(barrier));
            builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            builder.Services.AddSingleton(barrier);
            builder.Services.AddSingleton<ICurrentUserAccessService, TestUserAccessService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization();

            var app = builder.Build();

            app.UseAuthentication();
            app.UseAuthorization();
            var ticketGroup = app.MapGroup("/api/v1/{ticketType}/{ticketId}")
                .RequireAuthorization();
            TicketEndpoints.MapTicketCustomerEndpoint(ticketGroup);

            await app.StartAsync();
            var harness = new TicketCustomerAssignmentHarness(app, app.GetTestClient(), database, barrier);
            harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", role);
            return harness;
        }

        public async Task SeedTicketAsync(string ticketType, string ticketId, string organizationId, string? customerId = null)
        {
            await using var db = database.Open();
            switch (ticketType)
            {
                case "incidents":
                    db.Incidents.Add(new Incident
                    {
                        Id = ticketId,
                        Title = "Incident",
                        TrackingId = "INC-TEST",
                        OrganizationId = organizationId,
                        CustomerId = customerId
                    });
                    break;
                case "requests":
                    db.Requests.Add(new TicketRequest
                    {
                        Id = ticketId,
                        Title = "Request",
                        TrackingId = "REQ-TEST",
                        OrganizationId = organizationId,
                        CustomerId = customerId
                    });
                    break;
                case "changes":
                    db.Changes.Add(new Change
                    {
                        Id = ticketId,
                        Title = "Change",
                        TrackingId = "CHG-TEST",
                        OrganizationId = organizationId,
                        CustomerId = customerId
                    });
                    break;
            }
            await db.SaveChangesAsync();
        }

        public async Task SeedCustomerAsync(string id, string organizationId, string name, string email, bool enabled = true)
        {
            await using var db = database.Open();
            db.Customers.Add(new Customer
            {
                Id = id,
                OrganizationId = organizationId,
                Name = name,
                Email = email,
                IsEnabled = enabled
            });
            await db.SaveChangesAsync();
        }

        public async Task<Ticket?> GetTicketAsync(string ticketType, string ticketId)
        {
            await using var db = database.Open();
            return ticketType switch
            {
                "incidents" => await db.Incidents.AsNoTracking().SingleOrDefaultAsync(ticket => ticket.Id == ticketId),
                "requests" => await db.Requests.AsNoTracking().SingleOrDefaultAsync(ticket => ticket.Id == ticketId),
                "changes" => await db.Changes.AsNoTracking().SingleOrDefaultAsync(ticket => ticket.Id == ticketId),
                _ => null
            };
        }

        public async Task WithDbAsync(Func<HelpdeskDbContext, Task> action)
        {
            await using var db = database.Open();
            await action(db);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private sealed class RequestReadBarrier : DbCommandInterceptor
    {
        public bool HoldNext { get; set; }
        public bool HoldWrite { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int held;

        public async Task AfterTicketReadAsync(CancellationToken ct)
        {
            if (HoldNext) await HoldAsync(ct);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (HoldWrite && command.CommandText.Contains("UPDATE \"Tickets\"", StringComparison.Ordinal))
                await HoldAsync(cancellationToken);
            return result;
        }

        private async Task HoldAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref held, 1) != 0) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.Authorization.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "Test"),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TestUserAccessService(RequestReadBarrier barrier) : ICurrentUserAccessService
    {
        public async Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            // The real endpoint calls this after its real repository read has
            // completed and closed the reader, before checking/updating the requester.
            await barrier.AfterTicketReadAsync(ct);
            if (user.IsInRole(HelpdeskPermissions.HelpdeskAdmin))
            {
                return new CurrentUserAccessProfile(
                    true,
                    "Test",
                    null,
                    null,
                    null,
                    null,
                    true,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            var permissions = user.IsInRole("Technician")
                ? HelpdeskPermissions.TechnicalBundle.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new CurrentUserAccessProfile(
                true,
                "Test",
                null,
                "org-1",
                null,
                null,
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { HelpdeskRoleBundles.Technical },
                permissions,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "org-1" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }
}
