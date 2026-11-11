using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Helpdesk.API.Endpoints.Customers;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed class CustomerDeletionEndpointTests
{
    [Fact]
    public async Task InMemory_customer_routes_keep_the_guard_without_a_relational_transaction()
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        db.Customers.AddRange(
            new Customer { Id = "identity", IdentityUserId = "retired-user" },
            new Customer { Id = "contact" });
        await db.SaveChangesAsync();
        var customers = new EfRepository<Customer>(db);

        var retained = await CustomerDeletionEndpoint.DeleteAsync("identity", customers, db, default);
        var deleted = await CustomerDeletionEndpoint.DeleteAsync("contact", customers, db, default);
        var repeated = await CustomerDeletionEndpoint.DeleteAsync("contact", customers, db, default);

        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)retained).StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)deleted).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)repeated).StatusCode);
        Assert.Equal("retired-user", (await db.Customers.SingleAsync()).IdentityUserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_bound_customer_without_an_auth_link_rejects_repeated_deletion_and_retains_history(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.EditAsync(async db =>
        {
            db.Users.Add(new User { Id = "retired-user", Name = "Retired user", IsDeleted = true });
            db.Customers.Add(new Customer { Id = "identity-customer", Name = "Identity customer", Email = "identity@example.invalid", IdentityUserId = "retired-user" });
            AddHistory(db, "identity-customer");
            await db.SaveChangesAsync();
        });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await h.Client.DeleteAsync("/api/v1/customers/identity-customer");
            await AssertIdentityConflictAsync(response);
        }

        await h.EditAsync(async db =>
        {
            Assert.Equal("retired-user", (await db.Customers.SingleAsync()).IdentityUserId);
            Assert.True((await db.Users.IgnoreQueryFilters().SingleAsync()).IsDeleted);
            Assert.Empty(await db.CustomerAuthLinks.ToListAsync());
            await AssertHistoryAsync(db, "identity-customer");
        });
    }

    [Theory]
    [InlineData(false, "Authentik")]
    [InlineData(true, "Authentik")]
    [InlineData(false, "Local")]
    [InlineData(true, "Local")]
    [InlineData(false, "Pending")]
    [InlineData(true, "Pending")]
    public async Task Any_auth_link_blocks_repeated_deletion_and_preserves_retired_subject_fences(bool postgres, string identity)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var customerId = Guid.NewGuid().ToString("D");
        await h.EditAsync(async db =>
        {
            db.Users.Add(new User { Id = "retired-user", Name = "Retired user", IsDeleted = true });
            db.Customers.Add(new Customer { Id = customerId, Name = "Retained customer", Email = "retained@example.invalid" });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                Id = "retained-link", CustomerId = customerId,
                AuthProviderType = identity == "Local" ? "Local" : "Authentik",
                OidcIssuer = identity == "Authentik" ? "https://identity.example.invalid/" : null,
                OidcSubject = identity == "Authentik" ? "reserved-subject" : null,
                LocalAccountId = identity == "Local" ? "retired-account" : null,
                DomainUserId = identity == "Pending" ? null : "retired-user",
                InviteStatus = identity == "Pending" ? CustomerInviteStatus.Pending : CustomerInviteStatus.Active,
                DisabledAtUtc = identity == "Pending" ? null : DateTimeOffset.UtcNow
            });
            AddHistory(db, customerId);
            await db.SaveChangesAsync();
        });

        // The repository accepts both GUID formats; neither may bypass the link guard.
        foreach (var id in new[] { Guid.Parse(customerId).ToString("N"), customerId })
        {
            using var response = await h.Client.DeleteAsync($"/api/v1/customers/{id}");
            await AssertIdentityConflictAsync(response);
        }

        await h.EditAsync(async db =>
        {
            Assert.Null((await db.Customers.SingleAsync()).IdentityUserId);
            var link = await db.CustomerAuthLinks.SingleAsync();
            Assert.Equal("retained-link", link.Id);
            Assert.Equal(customerId, link.CustomerId);
            Assert.Equal(identity == "Authentik" ? "https://identity.example.invalid/" : null, link.OidcIssuer);
            Assert.Equal(identity == "Authentik" ? "reserved-subject" : null, link.OidcSubject);
            Assert.Equal(identity == "Local" ? "retired-account" : null, link.LocalAccountId);
            Assert.Equal(identity == "Pending" ? null : "retired-user", link.DomainUserId);
            Assert.Equal(identity == "Pending" ? CustomerInviteStatus.Pending : CustomerInviteStatus.Active, link.InviteStatus);
            Assert.Equal(identity != "Pending", link.DisabledAtUtc.HasValue);
            Assert.True((await db.Users.IgnoreQueryFilters().SingleAsync()).IsDeleted);
            await AssertHistoryAsync(db, customerId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlinked_contact_deletes_once_and_missing_or_repeated_deletion_stays_not_found(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.EditAsync(async db =>
        {
            db.Customers.Add(new Customer { Id = "unlinked", Name = "Disposable contact", Email = "unlinked@example.invalid" });
            db.ActivityLogs.Add(new ActivityLog { RelatedEntityId = "unlinked", Message = "Retained contact audit." });
            await db.SaveChangesAsync();
        });

        using var deleted = await h.Client.DeleteAsync("/api/v1/customers/unlinked");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        foreach (var id in new[] { "unlinked", "missing" })
        {
            using var missing = await h.Client.DeleteAsync($"/api/v1/customers/{id}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var problem = await missing.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
            Assert.Equal("Resource not found", problem!.Detail);
        }
        await h.EditAsync(async db =>
        {
            Assert.Empty(await db.Customers.ToListAsync());
            Assert.Empty(await db.CustomerAuthLinks.ToListAsync());
            Assert.Equal("Retained contact audit.", (await db.ActivityLogs.SingleAsync()).Message);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_invitation_cannot_commit_between_the_identity_check_and_customer_delete(bool postgres)
    {
        var gate = new DeleteGate();
        await using var h = await Harness.CreateAsync(postgres, gate);
        await h.EditAsync(async db =>
        {
            db.Customers.Add(new Customer { Id = "racing-contact", Name = "Disposable contact", Email = "race@example.invalid" });
            await db.SaveChangesAsync();
        });

        var deletion = h.Client.DeleteAsync("/api/v1/customers/racing-contact");
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await h.EditAsync(async db =>
            {
                if (postgres)
                {
                    await db.Database.OpenConnectionAsync();
                    await db.Database.ExecuteSqlRawAsync("SET lock_timeout = '250ms'");
                }
                else db.Database.SetCommandTimeout(1);
                db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "racing-contact", InviteStatus = CustomerInviteStatus.Pending });

                var error = await Record.ExceptionAsync(() => db.SaveChangesAsync());
                var databaseError = error is DbUpdateException update ? update.InnerException : error;
                if (postgres) Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(databaseError).SqlState);
                else Assert.Equal(5, Assert.IsType<SqliteException>(databaseError).SqliteErrorCode);
            });
        }
        finally { gate.Release.TrySetResult(); }

        using var deleted = await deletion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await h.EditAsync(async db =>
        {
            Assert.Empty(await db.Customers.ToListAsync());
            Assert.Empty(await db.CustomerAuthLinks.ToListAsync());
        });
    }

    private static async Task AssertIdentityConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<DeletionError>();
        Assert.Equal("customer_identity_retained", error!.Code);
        Assert.Contains("Team", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reserved-subject", error.Message, StringComparison.Ordinal);
    }

    private static void AddHistory(HelpdeskDbContext db, string customerId)
    {
        db.Requests.Add(new Request { Id = "historical-request", CustomerId = customerId,
            Title = "Historical request", TrackingId = "REQ-SYN-RETAINED" });
        db.ActivityLogs.Add(new ActivityLog { RelatedEntityId = customerId, Message = "Retained identity audit." });
    }

    private static async Task AssertHistoryAsync(HelpdeskDbContext db, string customerId)
    {
        Assert.Equal(customerId, (await db.Requests.IgnoreQueryFilters().SingleAsync()).CustomerId);
        Assert.Equal(customerId, (await db.ActivityLogs.SingleAsync()).RelatedEntityId);
    }

    private sealed record DeletionError(string Code, string Message);

    private sealed class Harness(WebApplication app, HttpClient client, string path, PostgreSqlContainer? postgres) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<Harness> CreateAsync(bool postgres, DbCommandInterceptor? interceptor = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"customer-delete-{Guid.NewGuid():N}.db");
            var container = postgres ? new PostgreSqlBuilder("postgres:16").Build() : null;
            if (container is not null) await container.StartAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            var tenant = Substitute.For<ITenantContext>();
            tenant.IsHelpdeskAdmin.Returns(true);
            builder.Services.AddSingleton(tenant);
            builder.Services.AddDbContext<HelpdeskDbContext>(options =>
            {
                if (container is not null) options.UseNpgsql(container.GetConnectionString());
                else options.UseSqlite($"Data Source={path}");
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            builder.Services.AddScoped<IRepository<Customer>, EfRepository<Customer>>();
            var app = builder.Build();
            app.MapDelete("/api/v1/customers/{id}", (string id, IRepository<Customer> customers,
                HelpdeskDbContext db, CancellationToken ct) => CustomerDeletionEndpoint.DeleteAsync(id, customers, db, ct));
            await app.StartAsync();
            var h = new Harness(app, app.GetTestClient(), path, container);
            await h.EditAsync(async db => await db.Database.EnsureCreatedAsync());
            return h;
        }

        public async Task EditAsync(Func<HelpdeskDbContext, Task> action)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            if (postgres is not null) await postgres.DisposeAsync();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class DeleteGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM \"Customers\"", StringComparison.Ordinal))
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
