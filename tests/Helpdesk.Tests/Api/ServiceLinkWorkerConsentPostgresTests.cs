using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData("responder-consent")]
    [InlineData("initiator-callback")]
    [InlineData("initiator-consent")]
    public async Task Postgres_worker_does_not_write_a_live_human_consent_attempt_between_its_read_and_save(string step)
    {
        var interleaving = new HumanConsentWorkerInterleaving(step);
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres: true, saveInterceptor: interleaving);
        string attemptId;
        ServiceLinkRequestDescriptor? descriptor = null;
        ServiceLinkCallbackRequest? callback = null;
        if (step == "responder-consent")
        {
            descriptor = await PrepareResponderConsentAsync(local, peer);
            attemptId = descriptor.AttemptId;
        }
        else
        {
            var start = await StartAsync(local, peer);
            attemptId = start.AttemptId;
            callback = await peer.ApproveAsync(local.BaseUrl, attemptId, BrowserState(start), SessionBinding);
            if (step == "initiator-consent")
            {
                using var reviewed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback);
                Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
            }
        }
        interleaving.Arm(local.Services, attemptId);
        using var accepted = step == "responder-consent"
            ? await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                new ServiceLinkRemoteApproveRequest(attemptId, local.OrganizationId, descriptor!.RequestedGrants, peer.BrowserState))
            : step == "initiator-callback"
                ? await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback)
                : await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + attemptId + "/approve",
                    new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, interleaving.Executions);
        Assert.NotEqual(interleaving.HumanContextId, interleaving.WorkerContextId);
        interleaving.AssertAttemptUnchanged();
        await using var final = local.Services.CreateAsyncScope();
        var db = final.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var approved = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.AttemptId == attemptId);
        Assert.Equal("approved", approved.LifecycleState);
        Assert.Equal("undecided", approved.Decision);
        Assert.False(approved.LocalInboundActive);
        Assert.False(approved.LocalBusinessSenderEnabled);
        Assert.NotNull(approved.GrantHash);
        var expectedPrincipals = step == "initiator-callback" ? 0 : 1;
        Assert.Equal(expectedPrincipals, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(expectedPrincipals, await db.Set<ServicePrincipalSecret>().CountAsync());
        Assert.Equal(expectedPrincipals, await db.IncidentReceiverPrincipalBindings.CountAsync());
        Assert.All(await db.IncidentReceiverPrincipalBindings.ToListAsync(), binding => Assert.False(binding.IsEnabled));
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkRotation>().CountAsync());
    }

    [Theory]
    [InlineData("initiator-awaiting")]
    [InlineData("responder-awaiting")]
    [InlineData("initiator-approved")]
    public async Task Postgres_worker_expires_human_waits_without_extending_deadlines_or_creating_authority(string state)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres: true);
        string attemptId;
        if (state == "responder-awaiting") attemptId = (await PrepareResponderConsentAsync(local, peer)).AttemptId;
        else
        {
            var start = await StartAsync(local, peer);
            attemptId = start.AttemptId;
            if (state == "initiator-approved")
            {
                var callback = await peer.ApproveAsync(local.BaseUrl, attemptId, BrowserState(start), SessionBinding);
                using var reviewed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback);
                Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
            }
        }
        var before = await ReadHumanConsentAttemptAsync(local, attemptId);
        Assert.Null(before.InboundPrincipalId);
        Assert.Equal("undecided", before.Decision);
        // This uses the existing deterministic fixture clock for expiry coverage;
        // it does not claim real-time rotation or cross-product physical execution.
        local.Clock.Advance(TimeSpan.FromSeconds(before.ExpiresAtUnixSeconds - local.Clock.GetUtcNow().ToUnixTimeSeconds() + 1));
        await using (var work = local.Services.CreateAsyncScope())
            await work.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
        var expired = await ReadHumanConsentAttemptAsync(local, attemptId);
        Assert.Equal("expired", expired.LifecycleState);
        Assert.Equal("abort", expired.Decision);
        Assert.NotNull(expired.AbortId);
        Assert.Equal(before.ExpiresAtUnixSeconds, expired.ExpiresAtUnixSeconds);
        Assert.Equal(before.DescriptorHash, expired.DescriptorHash);
        Assert.Equal(before.GrantHash, expired.GrantHash);
        Assert.Null(expired.ActiveRelationshipKey);
        Assert.Null(expired.InboundPrincipalId);
        Assert.False(expired.LocalInboundActive);
        Assert.False(expired.LocalBusinessSenderEnabled);
        Assert.True(expired.ProtectedBrowserState is null && expired.ProtectedVerifier is null &&
            expired.ProtectedPairingCode is null && expired.ProtectedInboundEscrow is null &&
            expired.ProtectedExchangeResponse is null, "Expired unused consent retained bootstrap escrow.");
        await using (var check = local.Services.CreateAsyncScope())
            await AssertNoHumanConsentAuthorityAsync(check.ServiceProvider.GetRequiredService<HelpdeskDbContext>(), CancellationToken.None);
        await using (var repeatedWork = local.Services.CreateAsyncScope())
            await repeatedWork.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
        Assert.Equal(expired.Revision, (await ReadHumanConsentAttemptAsync(local, attemptId)).Revision);
    }

    private static async Task<ServiceLinkRequestDescriptor> PrepareResponderConsentAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer)
    {
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        Assert.NotNull(metadata);
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using var reviewed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
            new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        return descriptor;
    }

    private static async Task<ServiceLinkAttempt> ReadHumanConsentAttemptAsync(ServiceLinkKestrelPeer local, string attemptId)
    {
        await using var scope = local.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServiceLinkAttempt>()
            .AsNoTracking().SingleAsync(a => a.AttemptId == attemptId);
    }

    private static async Task AssertNoHumanConsentAuthorityAsync(HelpdeskDbContext db, CancellationToken ct)
    {
        Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync(ct));
        Assert.Equal(0, await db.Set<ServicePrincipalSecret>().CountAsync(ct));
        Assert.Equal(0, await db.IncidentReceiverPrincipalBindings.CountAsync(ct));
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync(ct));
        Assert.Equal(0, await db.Set<ServiceLinkVerificationReceipt>().CountAsync(ct));
        Assert.Equal(0, await db.Set<ServiceLinkRotation>().CountAsync(ct));
    }

    private sealed class HumanConsentWorkerInterleaving(string step) : SaveChangesInterceptor
    {
        private IServiceProvider services = null!;
        private string attemptId = "";
        private int armed;
        public int Executions { get; private set; }
        public Guid HumanContextId { get; private set; }
        public Guid WorkerContextId { get; private set; }
        private long beforeRevision, afterRevision, beforeNextWork, afterNextWork;
        private bool attemptUnchanged;
        public void Arm(IServiceProvider provider, string id) { services = provider; attemptId = id; Volatile.Write(ref armed, 1); }
        public void AssertAttemptUnchanged()
        {
            Assert.Equal(beforeRevision, afterRevision);
            Assert.Equal(beforeNextWork, afterNextWork);
            Assert.True(attemptUnchanged, "Background work changed the durable live human-consent attempt.");
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref armed) == 0 || eventData.Context is not { } human) return result;
            var attempt = human.ChangeTracker.Entries<ServiceLinkAttempt>().SingleOrDefault(e => e.Entity.AttemptId == attemptId);
            if (attempt is null || (step == "initiator-callback"
                    ? attempt.State != EntityState.Modified || attempt.Entity.LifecycleState != "approved"
                    : !human.ChangeTracker.Entries<ServicePrincipalRegistration>().Any(e => e.State == EntityState.Added && e.Entity.AttemptId == attemptId)))
                return result;
            if (Interlocked.Exchange(ref armed, 0) == 0) return result;
            HumanContextId = human.ContextId.InstanceId;
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            WorkerContextId = db.ContextId.InstanceId;
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            var before = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.AttemptId == attemptId, cancellationToken);
            Assert.Equal(step == "initiator-consent" ? "approved" : "awaiting_approval", before.LifecycleState);
            Assert.Equal("undecided", before.Decision);
            Assert.Null(before.InboundPrincipalId);
            Assert.True(before.ExpiresAtUnixSeconds > services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds());
            await AssertNoHumanConsentAuthorityAsync(db, cancellationToken);
            // The actual human scope is suspended before SaveChanges. Execute
            // the real worker in a separate PostgreSQL scope, then continue the
            // same authenticated command. Never fabricate an exception or authority.
            await scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(cancellationToken);
            var after = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.AttemptId == attemptId, cancellationToken);
            beforeRevision = before.Revision; afterRevision = after.Revision;
            beforeNextWork = before.NextWorkAtUnixSeconds; afterNextWork = after.NextWorkAtUnixSeconds;
            attemptUnchanged = string.Equals(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after), StringComparison.Ordinal);
            await AssertNoHumanConsentAuthorityAsync(db, cancellationToken);
            Executions++;
            // Defer snapshot assertions until the actual HTTP command returns;
            // the old scheduler must reach the real EF concurrency conflict,
            // not a replacement assertion thrown from this interceptor.
            return result;
        }
    }
}
