using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(false, "peer-operation-failed")]
    [InlineData(true, "peer-unavailable")]
    public async Task Postgres_worker_persists_peer_error_after_serialization_reload_and_recovers_on_schedule_after_restart(
        bool loseResponse, string errorCode)
    {
        var race = new StagedProfileSerializationRace(1);
        var exchangeRequests = new ConcurrentQueue<ServiceLinkExchangeRequest>();
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync((app, _) => app.Use(async (http, next) =>
        {
            if (http.Request.Path.Value?.EndsWith("/exchange", StringComparison.Ordinal) == true)
            {
                http.Request.EnableBuffering();
                var request = await http.Request.ReadFromJsonAsync<ServiceLinkExchangeRequest>(ServiceLinkCanonicalJson.Json);
                exchangeRequests.Enqueue(request ?? throw new InvalidOperationException("Missing exchange request."));
                http.Request.Body.Position = 0;
                if (exchangeRequests.Count == 2)
                {
                    // The first exchange succeeds, then the real PostgreSQL stage aborts.
                    // Its replay reaches the same peer over HTTP and fails before staging.
                    if (loseResponse) http.Abort();
                    else http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
            }
            await next();
        }));
        await using var local = await LocalAsync(postgres: true, saveInterceptor: race);
        await using (var emptyProfile = local.Services.CreateAsyncScope())
            await emptyProfile.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                .UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto { ExpectedRevision = 0, Enabled = false });
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using (var approval = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
                   new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.Equal(HttpStatusCode.OK, approval.StatusCode);

        ServiceLinkAttempt originalAttempt;
        string originalPrincipal, originalSecret;
        await using (var approved = local.Services.CreateAsyncScope())
        {
            var db = approved.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            originalAttempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
            originalPrincipal = JsonSerializer.Serialize(Assert.Single(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync()));
            originalSecret = JsonSerializer.Serialize(Assert.Single(await db.Set<ServicePrincipalSecret>().AsNoTracking().ToListAsync()));
        }
        race.Arm();
        var interval = local.Services.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue.WorkerIntervalSeconds;
        var expectedNextWorkAt = local.Clock.GetUtcNow().ToUnixTimeSeconds() + interval;
        await using (var work = local.Services.CreateAsyncScope())
        {
            var db = work.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            await work.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal([PostgresErrorCodes.SerializationFailure], race.AbortedSaveSqlStates);
            Assert.Single(race.StagedTransactionIds);
            Assert.Equal(2, exchangeRequests.Count);
            // A separate scope reads persisted scheduling, independently of EF's tracker.
            await using var fresh = local.Services.CreateAsyncScope();
            var durable = await fresh.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
                .Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
            Assert.Equal(errorCode, durable.LastErrorCode);
            Assert.Equal(expectedNextWorkAt, durable.NextWorkAtUnixSeconds);
            var tracked = Assert.Single(db.ChangeTracker.Entries<ServiceLinkAttempt>());
            Assert.Equal(EntityState.Unchanged, tracked.State);
            Assert.Equal(durable.Revision, tracked.Entity.Revision);
            Assert.Equal(durable.LastErrorCode, tracked.Entity.LastErrorCode);
            Assert.Equal(durable.NextWorkAtUnixSeconds, tracked.Entity.NextWorkAtUnixSeconds);
        }
        var dispatched = await AssertRecoveryAuthorityAsync("approved");
        Assert.True(dispatched.ExchangeDispatched);
        Assert.Null(dispatched.ProtectedOutboundCredential);
        await using (var rolledBack = local.Services.CreateAsyncScope())
        {
            var profile = await rolledBack.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
                .M2MConnectivitySettings.AsNoTracking().SingleAsync();
            Assert.Equal(2, profile.Revision); // Only the competing writer committed.
            Assert.Null(profile.LinkId);
            Assert.True(string.IsNullOrEmpty(profile.ProtectedClientSecret));
            Assert.False(profile.Enabled);
        }

        await local.RestartAsync();
        await using (var notDue = local.Services.CreateAsyncScope())
            await notDue.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
        Assert.Equal(2, exchangeRequests.Count);
        local.Clock.Advance(TimeSpan.FromSeconds(interval));
        await using (var recovery = local.Services.CreateAsyncScope())
        {
            await recovery.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
            Assert.Null(recovery.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.CurrentTransaction);
        }
        var prepared = await AssertRecoveryAuthorityAsync("prepared");
        Assert.NotNull(prepared.ProtectedOutboundCredential);
        Assert.Null(prepared.LastErrorCode);
        Assert.Equal(expectedNextWorkAt + interval, prepared.NextWorkAtUnixSeconds);
        Assert.Equal(3, exchangeRequests.Count);
        Assert.Single(exchangeRequests.Select(request => ServiceLinkCanonicalJson.HashObject(request)).Distinct(StringComparer.Ordinal));
        Assert.All(exchangeRequests, request =>
        {
            Assert.Equal(originalAttempt.AttemptId, request.AttemptId);
            Assert.Equal(originalAttempt.ConsentId, request.InitiatorConsentId);
        });
        Assert.Equal(2, peer.ExchangeRequestFingerprints.Length);
        Assert.Single(peer.ExchangeRequestFingerprints.Distinct(StringComparer.Ordinal));
        Assert.Equal(2, race.StagedTransactionIds.Length);
        Assert.Equal(2, race.StagedTransactionIds.Distinct().Count());
        Assert.Equal([PostgresErrorCodes.SerializationFailure], race.AbortedSaveSqlStates);
        await using var final = local.Services.CreateAsyncScope();
        var staged = await final.ServiceProvider.GetRequiredService<HelpdeskDbContext>().M2MConnectivitySettings.AsNoTracking().SingleAsync();
        Assert.Equal(peer.LinkId, staged.LinkId);
        Assert.Equal(3, staged.Revision);
        Assert.Equal(staged.Revision, prepared.OutboundProfileRevision);
        Assert.False(staged.Enabled);
        Assert.False(staged.ManagedSenderEnabled);
        var resolved = await final.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>().GetResolvedOrchestratorSettingsAsync();
        Assert.Equal(peer.InboundCredential.ClientId, resolved.ClientId);
        Assert.Equal(peer.InboundCredential.ClientSecret, resolved.ClientSecret);

        async Task<ServiceLinkAttempt> AssertRecoveryAuthorityAsync(string state)
        {
            await using var scope = local.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
            Assert.Equal(state, attempt.LifecycleState);
            Assert.Equal(originalAttempt.AttemptId, attempt.AttemptId);
            Assert.Equal(originalAttempt.LinkId, attempt.LinkId);
            Assert.Equal(originalAttempt.LinkRevision, attempt.LinkRevision);
            Assert.Equal(originalAttempt.ConsentId, attempt.ConsentId);
            Assert.Equal(originalAttempt.DescriptorHash, attempt.DescriptorHash);
            Assert.Equal(originalAttempt.GrantHash, attempt.GrantHash);
            Assert.Equal(originalAttempt.ExpiresAtUnixSeconds, attempt.ExpiresAtUnixSeconds);
            Assert.Equal(originalAttempt.InboundPrincipalId, attempt.InboundPrincipalId);
            Assert.Equal("undecided", attempt.Decision);
            Assert.False(attempt.LocalPreparedAcknowledged);
            Assert.False(attempt.LocalInboundActive);
            Assert.False(attempt.LocalBusinessSenderEnabled);
            Assert.Equal(originalPrincipal, JsonSerializer.Serialize(Assert.Single(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync())));
            Assert.Equal(originalSecret, JsonSerializer.Serialize(Assert.Single(await db.Set<ServicePrincipalSecret>().AsNoTracking().ToListAsync())));
            Assert.False(Assert.Single(await db.IncidentReceiverPrincipalBindings.AsNoTracking().ToListAsync()).IsEnabled);
            Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
            Assert.Equal(0, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
            Assert.Equal(0, await db.Set<ServiceLinkRotation>().CountAsync());
            return attempt;
        }
    }

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

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Postgres_verified_rotation_retries_fresh_atomic_operations_and_preserves_rollback_and_exact_replay(int abortCount)
    {
        var race = new RecipientActivationSerializationRace(abortCount);
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres: true, saveInterceptor: race);
        var start = await ActivateInitiatorAsync(local, peer);
        using (var requested = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/links/" + peer.LinkId + "/rotate",
                   new ServiceLinkAdminAction(DirectionId: ServiceLinkContract.ResponderToInitiator)))
            Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        using (var offered = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
        var candidate = peer.RotationCandidate;
        // Reuse the existing rotation fixture restart boundary before candidate
        // controls, retaining the durable link and unchanged production rate limit.
        await local.RestartAsync();
        var verifyToken = await TokenAsync(candidate, ServiceIdentityScopes.Verify);
        ServiceLinkRotation originalRotation;
        ServicePrincipalRegistration originalPrincipal;
        string originalAttempt, originalSecrets;
        int receiptsBefore, journalsBefore;
        await using (var before = local.Services.CreateAsyncScope())
        {
            var db = before.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            originalRotation = Assert.Single(await db.Set<ServiceLinkRotation>().AsNoTracking().ToListAsync());
            originalPrincipal = Assert.Single(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync());
            Assert.True(originalRotation.IsIssuer);
            Assert.Null(originalRotation.ActivateDecisionId);
            Assert.Equal(1, originalPrincipal.CurrentCredentialRevision);
            originalAttempt = JsonSerializer.Serialize(Assert.Single(await db.Set<ServiceLinkAttempt>().AsNoTracking().ToListAsync()));
            originalSecrets = JsonSerializer.Serialize(await db.Set<ServicePrincipalSecret>().AsNoTracking()
                .OrderBy(secret => secret.CredentialRevision).ToListAsync());
            receiptsBefore = await db.Set<ServiceLinkVerificationReceipt>().CountAsync();
        }
        var probe = new ServiceLinkLifecycleRequest
        {
            OperationId = ServiceLinkValidation.NewId(), AttemptId = start.AttemptId,
            LinkId = peer.LinkId, LinkRevision = 1, GrantHash = peer.GrantHash,
            DirectionId = originalRotation.DirectionId, CredentialRevision = candidate.CredentialRevision,
            RotationId = originalRotation.RotationId
        };
        string receiptId;
        using (var verified = await local.ServiceAsync(HttpMethod.Post,
                   ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/verify", verifyToken, probe))
        {
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            receiptId = (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verification_receipt_id").GetString()!;
        }
        await using (var beforeActivation = local.Services.CreateAsyncScope())
        {
            var db = beforeActivation.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(receiptsBefore + 1, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
            journalsBefore = await db.Set<ServiceLinkOperation>().CountAsync();
            // Capture after the genuine probe, before any activation attempt.
            originalAttempt = JsonSerializer.Serialize(Assert.Single(await db.Set<ServiceLinkAttempt>().AsNoTracking().ToListAsync()));
            originalRotation = Assert.Single(await db.Set<ServiceLinkRotation>().AsNoTracking().ToListAsync());
        }
        var request = new ServiceLinkLifecycleRequest
        {
            OperationId = ServiceLinkValidation.NewId(), LinkId = peer.LinkId,
            LinkRevision = 1, GrantHash = peer.GrantHash,
            RotationId = originalRotation.RotationId, RotationPhase = "verified",
            DirectionId = originalRotation.DirectionId,
            ExpectedCurrentCredentialRevision = originalRotation.ExpectedCurrentCredentialRevision,
            SuccessorCredentialRevision = candidate.CredentialRevision,
            SuccessorVerificationReceiptId = receiptId
        };
        var control = await TokenAsync(candidate, ServiceIdentityScopes.Control);
        var route = ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/rotate";
        race.Arm(originalPrincipal.Id, candidate.CredentialRevision);
        string completedResponse;
        using (var response = await local.ServiceAsync(HttpMethod.Post, route, control, request))
        {
            Assert.Equal(abortCount == 1 ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
            if (abortCount == 4)
            {
                Assert.Equal("service-link-conflict", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
                await using var rolledBack = local.Services.CreateAsyncScope();
                var db = rolledBack.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                Assert.Equal(originalAttempt, JsonSerializer.Serialize(Assert.Single(await db.Set<ServiceLinkAttempt>().AsNoTracking().ToListAsync())));
                var rotation = Assert.Single(await db.Set<ServiceLinkRotation>().AsNoTracking().ToListAsync());
                Assert.Equal(JsonSerializer.Serialize(originalRotation), JsonSerializer.Serialize(rotation));
                Assert.Null(rotation.ActivateDecisionId);
                var principal = Assert.Single(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync());
                Assert.Equal(1, principal.CurrentCredentialRevision);
                Assert.Equal(originalPrincipal.Version + 4, principal.Version); // Only the independent writer committed.
                // Normalize only the independent counter in this detached read snapshot.
                principal.Version = originalPrincipal.Version;
                Assert.Equal(JsonSerializer.Serialize(originalPrincipal), JsonSerializer.Serialize(principal));
                Assert.Equal(originalSecrets, JsonSerializer.Serialize(await db.Set<ServicePrincipalSecret>().AsNoTracking()
                    .OrderBy(secret => secret.CredentialRevision).ToListAsync()));
                Assert.Equal(receiptsBefore + 1, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
                Assert.Equal(journalsBefore, await db.Set<ServiceLinkOperation>().CountAsync());
                Assert.False(await db.Set<ServiceLinkOperation>().AnyAsync(operation => operation.OperationId == request.OperationId));
            }
            completedResponse = abortCount == 1 ? await response.Content.ReadAsStringAsync() : "";
        }
        Assert.Equal(abortCount, race.AbortedSaveSqlStates.Length);
        Assert.All(race.AbortedSaveSqlStates, state => Assert.Equal(PostgresErrorCodes.SerializationFailure, state));
        Assert.Equal(abortCount == 1 ? 2 : 4, race.ContextIds.Length);
        Assert.Equal(race.ContextIds.Length, race.ContextIds.Distinct().Count());
        Assert.Equal(race.TransactionIds.Length, race.TransactionIds.Distinct().Count());
        Assert.All(race.FailedSaves, error => Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(error)));
        if (abortCount == 4)
        {
            // A new HTTP scope carries the SAME canonical request and operation.
            // It can commit only after the bounded original request failed honestly.
            using var recovery = await local.ServiceAsync(HttpMethod.Post, route, control, request);
            Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
            completedResponse = await recovery.Content.ReadAsStringAsync();
            Assert.Equal(5, race.ContextIds.Length);
            Assert.Equal(5, race.ContextIds.Distinct().Count());
        }
        await using (var committed = local.Services.CreateAsyncScope())
        {
            var db = committed.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var rotation = Assert.Single(await db.Set<ServiceLinkRotation>().AsNoTracking().ToListAsync());
            Assert.Equal("activated", rotation.RotationState);
            Assert.NotNull(rotation.ActivateDecisionId);
            Assert.Equal(receiptId, rotation.SuccessorVerificationReceiptId);
            Assert.Null(rotation.CallerSwitchRevision);
            var principal = Assert.Single(await db.Set<ServicePrincipalRegistration>().AsNoTracking().ToListAsync());
            Assert.Equal(candidate.CredentialRevision, principal.CurrentCredentialRevision);
            Assert.Equal(originalPrincipal.Version + abortCount + 1, principal.Version);
            var successor = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleAsync(secret =>
                secret.ServicePrincipalId == originalPrincipal.Id && secret.CredentialRevision == candidate.CredentialRevision);
            Assert.Equal("active", successor.Status);
            Assert.Equal(receiptsBefore + 1, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
            Assert.Equal(journalsBefore + 1, await db.Set<ServiceLinkOperation>().CountAsync());
            var operation = await db.Set<ServiceLinkOperation>().AsNoTracking().SingleAsync(item => item.OperationId == request.OperationId);
            Assert.False(operation.Outbound);
            Assert.Equal("rotate", operation.Kind);
            Assert.True(operation.Completed);
            Assert.Equal(ServiceLinkLifecycleProjection.Hash("rotate", request), operation.RequestFingerprint);
            Assert.Equal(ServiceLinkCanonicalJson.Canonicalize(completedResponse), ServiceLinkCanonicalJson.Canonicalize(operation.ResponseJson));
            Assert.Null(db.Database.CurrentTransaction);
        }
        var saves = race.ContextIds.Length;
        using (var replay = await local.ServiceAsync(HttpMethod.Post, route, control, request))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(ServiceLinkCanonicalJson.Canonicalize(completedResponse),
                ServiceLinkCanonicalJson.Canonicalize(await replay.Content.ReadAsStringAsync()));
        }
        using (var changed = await local.ServiceAsync(HttpMethod.Post, route, control,
                   request with { SuccessorVerificationReceiptId = ServiceLinkValidation.NewId() }))
        {
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal("operation-payload-conflict", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(saves, race.ContextIds.Length); // Neither replay nor a business conflict enters a save/retry.
        await using var final = local.Services.CreateAsyncScope();
        Assert.Equal(journalsBefore + 1, await final.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
            .Set<ServiceLinkOperation>().CountAsync());
    }

    private sealed class RecipientActivationSerializationRace(int abortLimit) : SaveChangesInterceptor
    {
        private Guid principalId;
        private long successorRevision;
        private int attempts;
        private readonly ConcurrentQueue<Guid> contextIds = new();
        private readonly ConcurrentQueue<Guid> transactionIds = new();
        private readonly ConcurrentQueue<string> sqlStates = new();
        private readonly ConcurrentQueue<Exception> failedSaves = new();
        public Guid[] ContextIds => contextIds.ToArray();
        public Guid[] TransactionIds => transactionIds.ToArray();
        public string[] AbortedSaveSqlStates => sqlStates.ToArray();
        public Exception[] FailedSaves => failedSaves.ToArray();
        public void Arm(Guid id, long revision) { principalId = id; successorRevision = revision; }
        private bool Eligible(DbContext db) => principalId != Guid.Empty && db.ChangeTracker.Entries<ServicePrincipalSecret>()
            .Any(entry => entry.State == EntityState.Modified && entry.Entity.ServicePrincipalId == principalId &&
                entry.Entity.CredentialRevision == successorRevision && entry.Entity.Status == "active");

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not { } db || !Eligible(db)) return result;
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            contextIds.Enqueue(db.ContextId.InstanceId);
            var transaction = Assert.IsType<NpgsqlTransaction>(db.Database.CurrentTransaction!.GetDbTransaction());
            Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);
            transactionIds.Enqueue(db.Database.CurrentTransaction.TransactionId);
            if (Interlocked.Increment(ref attempts) > abortLimit) return result;
            // Establish a real Serializable snapshot, then let a separate
            // committed writer change ONLY this principal's concurrency counter.
            await using (var snapshot = new NpgsqlCommand("SELECT \"Version\" FROM \"ServicePrincipalRegistrations\" WHERE \"Id\" = @id",
                (NpgsqlConnection)db.Database.GetDbConnection(), transaction) { CommandTimeout = 5 })
            {
                snapshot.Parameters.AddWithValue("id", principalId);
                Assert.NotNull(await snapshot.ExecuteScalarAsync(cancellationToken));
            }
            await using var writer = new NpgsqlConnection(db.Database.GetConnectionString());
            await writer.OpenAsync(cancellationToken);
            await using var update = new NpgsqlCommand(
                "UPDATE \"ServicePrincipalRegistrations\" SET \"Version\" = \"Version\" + 1 WHERE \"Id\" = @id", writer)
                { CommandTimeout = 5 };
            update.Parameters.AddWithValue("id", principalId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
            return result; // PostgreSQL itself raises 40001; no exception is fabricated or suppressed.
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } db && Eligible(db))
            {
                failedSaves.Enqueue(eventData.Exception);
                for (Exception? error = eventData.Exception; error is not null; error = error.InnerException)
                    if (error is PostgresException postgres) { sqlStates.Enqueue(postgres.SqlState); break; }
            }
            return Task.CompletedTask;
        }
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
