using Helpdesk.API.Endpoints.Organization;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed class OrganizationRetentionTests
{
    [Theory]
    [InlineData("23503")]
    [InlineData("40001")]
    [InlineData("40P01")]
    public void Expected_PostgreSql_mutation_conflicts_require_reassessment(string sqlState)
    {
        var failure = new Npgsql.PostgresException("Synthetic conflict", "ERROR", "ERROR", sqlState);
        Assert.True(OrganizationRetentionEndpoint.IsDependencyRace(new DbUpdateException("Synthetic update", failure)));
        if (sqlState != "23503") Assert.True(OrganizationRetentionEndpoint.IsDependencyRace(failure));
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(19, 787)]
    public void Expected_SQLite_mutation_conflicts_require_reassessment(int code, int extendedCode)
    {
        var failure = new SqliteException("Synthetic conflict", code, extendedCode);
        Assert.True(OrganizationRetentionEndpoint.IsDependencyRace(new DbUpdateException("Synthetic update", failure)));
        if (code != 19) Assert.True(OrganizationRetentionEndpoint.IsDependencyRace(failure));
    }

    [Fact]
    public void Unexpected_database_faults_and_timeouts_are_not_dependency_denials()
    {
        Assert.True(OrganizationRetentionEndpoint.IsDependencyRace(new DbUpdateConcurrencyException("Synthetic stale write")));
        Assert.False(OrganizationRetentionEndpoint.IsDependencyRace(new TimeoutException("Synthetic timeout")));
        Assert.False(OrganizationRetentionEndpoint.IsDependencyRace(new OperationCanceledException("Synthetic cancellation")));
        Assert.False(OrganizationRetentionEndpoint.IsDependencyRace(new DbUpdateException("Synthetic database fault")));
        Assert.False(OrganizationRetentionEndpoint.IsDependencyRace(new DbUpdateException("Synthetic uniqueness fault",
            new Npgsql.PostgresException("Synthetic constraint", "ERROR", "ERROR", "23505"))));
    }

    [Fact]
    public void Every_persisted_ingestion_boundary_requires_retention()
    {
        var boundaries = new Action<MailboxIngestionState>[]
        {
            state => state.Initialized = true,
            state => state.Cursor = "synthetic-cursor",
            state => state.CurrentStage = "Completed",
            state => state.LastAttemptUnixMilliseconds = 1,
            state => state.LastSyncUnixMilliseconds = 1,
            state => state.BaselineCompletedUnixMilliseconds = 1,
            state => state.SyncRequestedVersion = 1,
            state => state.SyncCompletedVersion = 1,
            state => state.NextRetryUnixMilliseconds = 1,
            state => state.LastSyncCommandUnixMilliseconds = 1,
            state => state.ErrorCode = "SyntheticFailure",
            state => state.LastSyncCommandErrorCode = "SyntheticFailure"
        };
        Assert.False(OrganizationRetentionEndpoint.HasRetainedIngestion(new()));
        foreach (var setBoundary in boundaries)
        {
            var state = new MailboxIngestionState();
            setBoundary(state);
            Assert.True(OrganizationRetentionEndpoint.HasRetainedIngestion(state));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_dependency_change_cannot_orphan_or_discard_a_committed_identity(bool postgres)
    {
        await using var server = postgres ? new PostgreSqlBuilder("postgres:16-alpine").Build() : null;
        if (server is not null) await server.StartAsync();
        var sqlitePath = Path.Combine(Path.GetTempPath(), "rateldesk-retention-race-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>();
        if (server is not null) options.UseNpgsql(server.GetConnectionString()); else options.UseSqlite("Data Source=" + sqlitePath + ";Default Timeout=1");
        try
        {
            await using var db = new HelpdeskDbContext(options.Options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
            await db.Database.EnsureCreatedAsync();
            if (!postgres) await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            var organization = new Helpdesk.Shared.Models.Organization { Name = "Synthetic concurrent dependency" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var underlying = new EfRepository<Helpdesk.Shared.Models.Organization>(db);
            var repository = Substitute.For<IRepository<Helpdesk.Shared.Models.Organization>>();
            var writerBlocked = false;
            repository.GetAsync(organization.Id).Returns(_ => underlying.GetAsync(organization.Id));
            repository.DeleteAsync(organization.Id).Returns(async _ =>
            {
                // Independent writer commits after the endpoint's serializable preflight.
                await using var writer = new HelpdeskDbContext(options.Options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
                writer.Set<ServicePrincipalRegistration>().Add(new ServicePrincipalRegistration
                {
                    OrganizationId = organization.Id, Name = "Synthetic retained identity", ClientId = "concurrent", NormalizedClientId = "CONCURRENT", Status = "revoked"
                });
                try { await writer.SaveChangesAsync(); }
                catch (DbUpdateException failure) when (!postgres && failure.InnerException is SqliteException { SqliteErrorCode: 5 })
                {
                    // SQLite's immediate serializable transaction prevents this writer
                    // from committing. PostgreSQL permits it and denies the stale delete.
                    writerBlocked = true;
                }
                return await underlying.DeleteAsync(organization.Id);
            });
            var result = await OrganizationDeletionEndpoint.DeleteAsync(organization.Id, repository, db, default);
            Assert.Equal(postgres ? 409 : 204, ((IStatusCodeHttpResult)result).StatusCode);
            await using var verification = new HelpdeskDbContext(options.Options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
            Assert.Equal(!postgres, writerBlocked);
            Assert.Equal(postgres, await verification.Organizations.AnyAsync(x => x.Id == organization.Id));
            Assert.Equal(postgres, await verification.Set<ServicePrincipalRegistration>().AnyAsync(x => x.OrganizationId == organization.Id && x.Status == "revoked"));
        }
        finally
        {
            if (!postgres)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(sqlitePath);
                File.Delete(sqlitePath + "-wal");
                File.Delete(sqlitePath + "-shm");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_principals_and_deliveries_have_atomic_assessment_and_retirement_on_both_providers(bool postgres)
    {
        await using var server = postgres ? new PostgreSqlBuilder("postgres:16-alpine").Build() : null;
        if (server is not null) await server.StartAsync();
        await using var sqlite = postgres ? null : new SqliteConnection("Data Source=:memory:");
        if (sqlite is not null) await sqlite.OpenAsync();
        var errors = new List<string>();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>().LogTo(errors.Add, LogLevel.Error);
        if (server is not null) options.UseNpgsql(server.GetConnectionString()); else options.UseSqlite(sqlite!);
        await using var db = new HelpdeskDbContext(options.Options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();
        foreach (var state in new[] { "revoked", "active", "needs-review", "in-flight", "completed", "lease" })
        {
            var organization = new Helpdesk.Shared.Models.Organization { Id = "retention-" + state, Name = "Synthetic retained organization" };
            db.Organizations.Add(organization);
            var principal = new ServicePrincipalRegistration { OrganizationId = organization.Id, Name = "Synthetic principal", ClientId = state, NormalizedClientId = state.ToUpperInvariant(), Status = state == "active" ? "active" : "revoked" };
            db.Set<ServicePrincipalRegistration>().Add(principal);
            db.Set<ServicePrincipalSecret>().Add(new ServicePrincipalSecret { ServicePrincipalId = principal.Id, CredentialRevision = 1, Status = "revoked", SecretHash = "synthetic-verifier", Salt = "synthetic-salt" });
            var mailbox = new EmailInboxSettings { OrganizationId = organization.Id, Scope = MailboxScope.Organization, Archived = true, Enabled = false, BackgroundSyncEnabled = false, MailboxAddress = state + "@example.test" };
            db.EmailInboxSettings.Add(mailbox);
            if (state is "needs-review" or "in-flight" or "completed")
                db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect { EffectKey = state, DispatchGroup = $"mailbox:{mailbox.Id:N}", Kind = MailboxEffectKind.Email, State = state == "needs-review" ? MailboxEffectState.NeedsReview : state == "in-flight" ? MailboxEffectState.InFlight : MailboxEffectState.Completed, Payload = "{}" });
            if (state == "lease") db.Set<MailboxLease>().Add(new MailboxLease { MailboxId = mailbox.Id, Owner = "synthetic-worker", ExpiresUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds() });
            await db.SaveChangesAsync();

            var assessment = await OrganizationRetentionEndpoint.AssessAsync(organization, db, default);
            Assert.False(assessment.CanDelete);
            Assert.Contains(assessment.Dependencies, x => x.Code == "service_principal_retention");
            var safe = state is "revoked" or "completed";
            Assert.Equal(safe, assessment.CanRetire);
            var deletion = await OrganizationDeletionEndpoint.DeleteAsync(organization.Id, new EfRepository<Helpdesk.Shared.Models.Organization>(db), db, default);
            Assert.Equal(409, ((IStatusCodeHttpResult)deletion).StatusCode);
            var retirement = await OrganizationRetentionEndpoint.RetireAsync(organization.Id, db, default);
            Assert.Equal(safe ? 204 : 409, ((IStatusCodeHttpResult)retirement).StatusCode);
            db.ChangeTracker.Clear();
            Assert.Equal(!safe, (await db.Organizations.SingleAsync(x => x.Id == organization.Id)).IsEnabled);
            Assert.Equal(principal.Status, (await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == principal.Id)).Status);
            Assert.True(await db.Set<ServicePrincipalSecret>().AnyAsync(x => x.ServicePrincipalId == principal.Id));
            Assert.True(await db.EmailInboxSettings.AnyAsync(x => x.Id == mailbox.Id && x.Archived && !x.Enabled));
            if (state == "needs-review") Assert.Equal(MailboxEffectState.NeedsReview, (await db.Set<MailboxOutboxEffect>().SingleAsync(x => x.EffectKey == state)).State);
        }
        Assert.Empty(errors);
    }
}
