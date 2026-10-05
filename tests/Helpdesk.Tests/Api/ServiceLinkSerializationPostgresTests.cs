using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    public async Task Postgres_staged_exchange_abort_recovers_same_handoff_with_bounded_reload_and_no_partial_profile(int abortCount, bool backgroundWork)
    {
        var race = new StagedProfileSerializationRace(abortCount);
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres: true, saveInterceptor: race);
        // An unused, disabled profile is created through the production configuration service.
        await using (var emptyProfile = local.Services.CreateAsyncScope())
        {
            var providers = emptyProfile.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>();
            var initial = await providers.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
            { ExpectedRevision = 0, Enabled = false });
            Assert.Equal(1, initial.Revision);
        }
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using (var approval = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
                   new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        ServicePrincipalRegistration originalPrincipal;
        ServicePrincipalSecret originalSecret;
        await using (var approved = local.Services.CreateAsyncScope())
        {
            var db = approved.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            originalPrincipal = Assert.Single(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync());
            originalSecret = Assert.Single(await db.Set<ServicePrincipalSecret>().AsNoTracking().ToListAsync());
        }

        race.Arm();
        var expectedNextWorkAt = local.Clock.GetUtcNow().ToUnixTimeSeconds() +
            local.Services.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue.WorkerIntervalSeconds;
        if (backgroundWork)
        {
            await using var work = local.Services.CreateAsyncScope();
            var workerDb = work.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            await work.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
            var tracked = Assert.Single(workerDb.ChangeTracker.Entries<ServiceLinkAttempt>());
            Assert.Equal(EntityState.Unchanged, tracked.State);
            Assert.Equal(expectedNextWorkAt, tracked.Entity.NextWorkAtUnixSeconds);
            Assert.Null(workerDb.Database.CurrentTransaction);
        }
        else
        {
            using var resumed = await ResumeAsync(local, peer.LinkId);
            Assert.Equal(abortCount == 1 ? HttpStatusCode.OK : HttpStatusCode.Conflict, resumed.StatusCode);
            if (abortCount == 4)
            {
                var problem = await resumed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                Assert.Equal("service-link-conflict", problem.GetProperty("code").GetString());
            }
        }
        Assert.Equal(abortCount, race.AbortedSaveSqlStates.Length);
        Assert.All(race.AbortedSaveSqlStates, state => Assert.Equal(PostgresErrorCodes.SerializationFailure, state));
        Assert.Equal(abortCount == 1 ? 2 : 4, race.StagedTransactionIds.Length);
        Assert.Equal(race.StagedTransactionIds.Length, race.StagedTransactionIds.Distinct().Count());
        Assert.Equal(abortCount == 1 ? 2 : 4, peer.ExchangeRequestFingerprints.Length);
        Assert.Single(peer.ExchangeRequestFingerprints.Distinct(StringComparer.Ordinal));

        if (abortCount == 4)
        {
            // Exhaustion reports the known abort instead of saving tracked, rolled-back state.
            await using (var aborted = local.Services.CreateAsyncScope())
            {
                var db = aborted.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
                Assert.True(attempt.ExchangeDispatched);
                Assert.Null(attempt.ProtectedOutboundCredential);
                Assert.False(attempt.LocalInboundActive);
                Assert.False(attempt.LocalBusinessSenderEnabled);
                var profile = await db.M2MConnectivitySettings.AsNoTracking().SingleAsync();
                Assert.Null(profile.LinkId);
                Assert.Equal(5, profile.Revision);
                Assert.True(string.IsNullOrEmpty(profile.ProtectedClientSecret));
                Assert.False(profile.Enabled);
                Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
            }
            await local.RestartAsync();
            using var recovered = await ResumeAsync(local, peer.LinkId);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(5, peer.ExchangeRequestFingerprints.Length);
            Assert.Single(peer.ExchangeRequestFingerprints.Distinct(StringComparer.Ordinal));
        }

        await using var final = local.Services.CreateAsyncScope();
        var finalDb = final.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var prepared = await finalDb.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
        Assert.Equal("prepared", prepared.LifecycleState);
        Assert.NotNull(prepared.ProtectedOutboundCredential);
        Assert.False(prepared.LocalPreparedAcknowledged);
        Assert.False(prepared.LocalInboundActive);
        Assert.False(prepared.LocalBusinessSenderEnabled);
        if (backgroundWork) Assert.Equal(expectedNextWorkAt, prepared.NextWorkAtUnixSeconds);
        var currentPrincipal = Assert.Single(await finalDb.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync());
        Assert.Equal(originalPrincipal.Id, currentPrincipal.Id);
        Assert.Equal(originalPrincipal.CurrentCredentialRevision, currentPrincipal.CurrentCredentialRevision);
        Assert.Equal(originalPrincipal.Revision, currentPrincipal.Revision);
        Assert.Equal(originalPrincipal.Version, currentPrincipal.Version);
        Assert.Equal(originalPrincipal.Status, currentPrincipal.Status);
        var currentSecret = Assert.Single(await finalDb.Set<ServicePrincipalSecret>().AsNoTracking().ToListAsync());
        Assert.Equal(originalSecret.ServicePrincipalId, currentSecret.ServicePrincipalId);
        Assert.Equal(originalSecret.SecretHash, currentSecret.SecretHash);
        Assert.Equal(originalSecret.Salt, currentSecret.Salt);
        Assert.Equal(originalSecret.CredentialRevision, currentSecret.CredentialRevision);
        Assert.Equal(originalSecret.Status, currentSecret.Status);
        var binding = Assert.Single(await finalDb.IncidentReceiverPrincipalBindings.AsNoTracking().ToListAsync());
        Assert.False(binding.IsEnabled);
        var staged = await finalDb.M2MConnectivitySettings.AsNoTracking().SingleAsync();
        Assert.Equal(peer.LinkId, staged.LinkId);
        Assert.Equal(2 + abortCount, staged.Revision);
        Assert.Equal(staged.Revision, prepared.OutboundProfileRevision);
        Assert.False(staged.Enabled);
        Assert.False(staged.ManagedSenderEnabled);
        var resolved = await final.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>().GetResolvedOrchestratorSettingsAsync();
        Assert.Equal(peer.InboundCredential.ClientId, resolved.ClientId);
        Assert.Equal(peer.InboundCredential.ClientSecret, resolved.ClientSecret);
        Assert.False(resolved.Enabled);
        Assert.Equal(0, await finalDb.Set<ServiceLinkOperation>().CountAsync());
    }

    private sealed class StagedProfileSerializationRace(int abortLimit) : SaveChangesInterceptor
    {
        private readonly ConcurrentQueue<string> abortedSaveSqlStates = new();
        private readonly ConcurrentQueue<Guid> stagedTransactionIds = new();
        private int armed;
        private int stagedSaves;
        public string[] AbortedSaveSqlStates => abortedSaveSqlStates.ToArray();
        public Guid[] StagedTransactionIds => stagedTransactionIds.ToArray();
        public void Arm() => Volatile.Write(ref armed, 1);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            if (Volatile.Read(ref armed) == 0 || context?.Database.CurrentTransaction is null) return result;
            var entry = context.ChangeTracker.Entries<M2MConnectivitySettings>().SingleOrDefault(candidate =>
                candidate.State == EntityState.Modified && candidate.Entity.ProviderKey == "Orchestrator" && candidate.Entity.LinkId is not null);
            if (entry is null) return result;
            stagedTransactionIds.Enqueue(context.Database.CurrentTransaction.TransactionId);
            if (Interlocked.Increment(ref stagedSaves) > abortLimit) return result;

            // The coordinator has already read this row in its real Serializable snapshot.
            // A separate committed writer changes it before the attempted staged UPDATE.
            // PostgreSQL, not the interceptor, raises 40001 and aborts that transaction.
            await using var writer = new NpgsqlConnection(context.Database.GetConnectionString());
            await writer.OpenAsync(cancellationToken);
            await using var update = writer.CreateCommand();
            update.CommandText = "UPDATE \"M2MConnectivitySettings\" SET \"Revision\" = \"Revision\" + 1 WHERE \"Id\" = @id";
            update.Parameters.AddWithValue("id", entry.Entity.Id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            for (Exception? error = eventData.Exception; error is not null; error = error.InnerException)
                if (error is PostgresException postgres)
                {
                    abortedSaveSqlStates.Enqueue(postgres.SqlState);
                    break;
                }
            return Task.CompletedTask;
        }
    }
}
