using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.ServiceLink;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_worker_purges_expired_bootstrap_material_in_bounded_batches_across_restart_without_changing_authority(bool postgres)
    {
        var configuration = new BootstrapSweepConfiguration();
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await ServiceLinkKestrelPeer.CreateAsync(postgres, (services, settings) =>
        {
            settings["ServiceLinks:Enabled"] = configuration.Enabled.ToString();
            services.AddRatelDeskServiceIdentity(settings);
            services.AddServiceLinkProtocol(settings);
            services.AddHttpClient<ServiceLinkTransport>()
                .AddHttpMessageHandler(() => new BootstrapSweepCountingHandler(configuration));
        }, app =>
        {
            app.MapServiceIdentityEndpoints();
            app.MapServiceLinkEndpoints();
        });

        // These two rows originate in actual HTTP ceremonies. Neither ceremony
        // has activated business authority or completed the reciprocal link.
        var awaiting = await StartAsync(local, peer);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        Assert.NotNull(metadata);
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var reviewed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.True(reviewed.IsSuccessStatusCode);
        ServiceLinkNavigation prepared;
        using (var approved = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                   new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState)))
        {
            Assert.True(approved.IsSuccessStatusCode);
            prepared = await approved.Content.ReadFromJsonAsync<ServiceLinkNavigation>()
                ?? throw new InvalidOperationException("Missing prepared navigation.");
        }
        await peer.ExchangeAsInitiatorAsync(prepared);

        var futureId = ServiceLinkValidation.NewId();
        await using (var seed = local.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var realPrepared = await db.Set<ServiceLinkAttempt>().SingleAsync(row => row.AttemptId == descriptor.AttemptId);
            Assert.Equal("prepared", realPrepared.LifecycleState);
            Assert.NotNull(realPrepared.ProtectedOutboundCredential);
            Assert.NotNull(realPrepared.ProtectedExchangeResponse);
            Assert.NotNull(realPrepared.ProtectedInboundEscrow);
            Assert.Equal("awaiting_approval", (await db.Set<ServiceLinkAttempt>().SingleAsync(row => row.AttemptId == awaiting.AttemptId)).LifecycleState);

            // Additional rows deliberately exercise relational batch disposal,
            // not consent or provisioning. Their opaque material is never used
            // as a credential; the disabled worker may only erase five fields.
            for (var index = 0; index < 20; index++)
                db.Set<ServiceLinkAttempt>().Add(SyntheticBootstrapAttempt(local, "expired-" + index,
                    local.Clock.GetUtcNow().ToUnixTimeSeconds() + 120, index));
            db.Set<ServiceLinkAttempt>().Add(SyntheticBootstrapAttempt(local, futureId,
                local.Clock.GetUtcNow().ToUnixTimeSeconds() + 3600, 2));
            await db.SaveChangesAsync();
        }

        var before = await ReadBootstrapAttemptsAsync(local);
        Assert.Equal(23, before.Length);
        var otherBefore = await BootstrapOtherDurableStateAsync(local);
        var futureBefore = ServiceLinkCanonicalJson.HashObject(before.Single(row => row.AttemptId == futureId));
        configuration.Enabled = false;
        local.Clock.Advance(TimeSpan.FromSeconds(121));
        await local.RestartAsync();
        Assert.False(local.Services.GetRequiredService<IOptions<ServiceLinkOptions>>().Value.Enabled);
        var outgoingBefore = configuration.OutgoingRequests;
        var tokensBefore = peer.TokenAcquisitions;

        await RunBootstrapSweepAsync(local);
        var first = await ReadBootstrapAttemptsAsync(local);
        Assert.Equal(20, first.Count(row => row.AttemptId != futureId && !HasBootstrapMaterial(row)));
        Assert.Equal(2, first.Count(row => row.AttemptId != futureId && HasBootstrapMaterial(row)));
        AssertBootstrapPreserved(before, first, futureId);
        Assert.Equal(otherBefore, await BootstrapOtherDurableStateAsync(local));
        Assert.Equal(outgoingBefore, configuration.OutgoingRequests);
        Assert.Equal(tokensBefore, peer.TokenAcquisitions);

        await local.RestartAsync();
        Assert.False(local.Services.GetRequiredService<IOptions<ServiceLinkOptions>>().Value.Enabled);
        await RunBootstrapSweepAsync(local);
        var second = await ReadBootstrapAttemptsAsync(local);
        Assert.All(second.Where(row => row.AttemptId != futureId), row => Assert.False(HasBootstrapMaterial(row)));
        AssertBootstrapPreserved(before, second, futureId);
        Assert.Equal(futureBefore, ServiceLinkCanonicalJson.HashObject(second.Single(row => row.AttemptId == futureId)));
        Assert.Equal(otherBefore, await BootstrapOtherDurableStateAsync(local));
        Assert.Equal(outgoingBefore, configuration.OutgoingRequests);
        Assert.Equal(tokensBefore, peer.TokenAcquisitions);

        await RunBootstrapSweepAsync(local);
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(second), ServiceLinkCanonicalJson.HashObject(await ReadBootstrapAttemptsAsync(local)));
        Assert.Equal(outgoingBefore, configuration.OutgoingRequests);
    }

    private static ServiceLinkAttempt SyntheticBootstrapAttempt(ServiceLinkKestrelPeer local, string id, long expiry, int state) => new()
    {
        AttemptId = id, Role = "responder", LocalTenantId = local.OrganizationId, LocalActorId = "owner",
        PeerInstanceId = "synthetic-peer", PeerTenantId = "synthetic-tenant", LinkId = "synthetic-link-" + id,
        LifecycleState = (state % 4) switch { 0 => "awaiting_approval", 1 => "prepared", 2 => "in_doubt", _ => "commit_decided" },
        Decision = state % 4 == 3 ? "commit" : "undecided", CommitId = state % 4 == 3 ? "synthetic-commit-" + id : null,
        DescriptorJson = "{}", DescriptorHash = "synthetic-descriptor-" + id, GrantSummaryJson = "{}", GrantHash = "synthetic-grant-" + id,
        ConsentId = "synthetic-consent-" + id, ExchangeFingerprint = "synthetic-fingerprint-" + id,
        ExchangeResponseHash = "synthetic-response-hash-" + id, InitiatorVerificationReceiptId = "synthetic-receipt-" + id,
        ProtectedBrowserState = "synthetic-browser", ProtectedVerifier = "synthetic-verifier", ProtectedPairingCode = "synthetic-code",
        ProtectedInboundEscrow = "synthetic-inbound", ProtectedExchangeResponse = "synthetic-response",
        ProtectedOutboundCredential = state % 4 == 0 ? null : "synthetic-permanent-recovery",
        ExpiresAtUnixSeconds = expiry, CreatedAtUnixSeconds = local.Clock.GetUtcNow().ToUnixTimeSeconds(),
        UpdatedAtUnixSeconds = local.Clock.GetUtcNow().ToUnixTimeSeconds(), NextWorkAtUnixSeconds = 0
    };

    private static async Task RunBootstrapSweepAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
    }

    private static async Task<ServiceLinkAttempt[]> ReadBootstrapAttemptsAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServiceLinkAttempt>()
            .AsNoTracking().OrderBy(row => row.AttemptId).ToArrayAsync();
    }

    private static bool HasBootstrapMaterial(ServiceLinkAttempt row) => row.ProtectedBrowserState is not null ||
        row.ProtectedVerifier is not null || row.ProtectedPairingCode is not null || row.ProtectedInboundEscrow is not null ||
        row.ProtectedExchangeResponse is not null;

    private static void AssertBootstrapPreserved(ServiceLinkAttempt[] before, ServiceLinkAttempt[] after, string futureId)
    {
        Assert.Equal(before.Length, after.Length);
        foreach (var old in before)
        {
            var current = after.Single(row => row.AttemptId == old.AttemptId);
            Assert.Equal(BootstrapStableHash(old), BootstrapStableHash(current));
            if (old.AttemptId == futureId || HasBootstrapMaterial(current))
                Assert.Equal(ServiceLinkCanonicalJson.HashObject(old), ServiceLinkCanonicalJson.HashObject(current));
            else
            {
                Assert.Equal(old.Revision + 1, current.Revision);
                Assert.Null(current.ProtectedBrowserState); Assert.Null(current.ProtectedVerifier);
                Assert.Null(current.ProtectedPairingCode); Assert.Null(current.ProtectedInboundEscrow);
                Assert.Null(current.ProtectedExchangeResponse);
            }
        }
    }

    private static string BootstrapStableHash(ServiceLinkAttempt row)
    {
        var projection = JsonSerializer.SerializeToNode(row)!.AsObject();
        foreach (var field in new[] { nameof(row.ProtectedBrowserState), nameof(row.ProtectedVerifier), nameof(row.ProtectedPairingCode),
                     nameof(row.ProtectedInboundEscrow), nameof(row.ProtectedExchangeResponse), nameof(row.Revision), nameof(row.UpdatedAtUnixSeconds) })
            projection.Remove(field);
        return ServiceLinkCanonicalJson.Hash(projection.ToJsonString());
    }

    private static async Task<string> BootstrapOtherDurableStateAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        return ServiceLinkCanonicalJson.HashObject(new
        {
            Principals = await db.Set<Helpdesk.Infrastructure.ServiceIdentity.ServicePrincipalRegistration>().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Credentials = await db.Set<Helpdesk.Infrastructure.ServiceIdentity.ServicePrincipalSecret>().AsNoTracking().OrderBy(row => row.ServicePrincipalId).ThenBy(row => row.CredentialRevision).ToArrayAsync(),
            Journals = await db.Set<ServiceLinkOperation>().AsNoTracking().OrderBy(row => row.LinkId).ThenBy(row => row.OperationId).ToArrayAsync(),
            Rotations = await db.Set<ServiceLinkRotation>().AsNoTracking().OrderBy(row => row.RotationId).ToArrayAsync(),
            Receipts = await db.Set<ServiceLinkVerificationReceipt>().AsNoTracking().OrderBy(row => row.VerificationReceiptId).ToArrayAsync(),
            Sources = await db.IncidentReceiverSources.AsNoTracking().OrderBy(row => row.SourceNamespaceId).ToArrayAsync(),
            Bindings = await db.IncidentReceiverPrincipalBindings.AsNoTracking().OrderBy(row => row.SourceNamespaceId).ThenBy(row => row.PrincipalKind).ThenBy(row => row.PrincipalId).ToArrayAsync(),
            Provider = await scope.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                .GetOrchestratorSettingsAsync()
        });
    }

    private sealed class BootstrapSweepConfiguration
    {
        public bool Enabled { get; set; } = true;
        public int OutgoingRequests;
    }

    private sealed class BootstrapSweepCountingHandler(BootstrapSweepConfiguration configuration) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref configuration.OutgoingRequests);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
