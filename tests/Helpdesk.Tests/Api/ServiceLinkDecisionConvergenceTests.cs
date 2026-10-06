using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Coordinator_returns_its_immutable_abort_decision_to_a_different_participant_request_and_replays_exactly(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await PreparedCoordinatorAsync(local, peer);
        var token = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.Control);
        using (var cancelled = await local.AdminAsync(HttpMethod.Post,
                   $"/api/v1/admin/service-links/attempts/{start.AttemptId}/cancel", new ServiceLinkAdminAction("coordinator-cancel")))
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var before = await DecisionAttemptAsync(local);
        Assert.Equal("abort", before.Decision); Assert.NotNull(before.AbortId);
        var request = TerminalRequest(before) with
        { AbortPhase = "request", AbortId = ServiceLinkValidation.NewId(), ReasonCode = "participant-cancel" };
        Assert.NotEqual(before.AbortId, request.AbortId);
        var route = ServiceLinkContract.EndpointPath + "/links/" + before.LinkId + "/abort";
        string accepted;
        using (var response = await local.ServiceAsync(HttpMethod.Post, route, token, request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            accepted = await response.Content.ReadAsStringAsync();
            using var result = JsonDocument.Parse(accepted);
            Assert.Equal(before.AbortId, result.RootElement.GetProperty("abort_id").GetString());
        }
        await local.RestartAsync();
        using (var replay = await local.ServiceAsync(HttpMethod.Post, route, token, request))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(accepted, await replay.Content.ReadAsStringAsync());
        }
        using (var changed = await local.ServiceAsync(HttpMethod.Post, route, token, request with { ReasonCode = "changed-body" }))
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var after = await DecisionAttemptAsync(local);
        Assert.Equal(before.AbortId, after.AbortId); Assert.Equal(before.GrantHash, after.GrantHash); Assert.Equal(before.LinkRevision, after.LinkRevision);
        Assert.False(after.LocalInboundActive || after.LocalBusinessSenderEnabled);
        var operation = Assert.Single(await DecisionOperationsAsync(local), x => !x.Outbound && x.Kind == "abort");
        Assert.Equal(ServiceLinkLifecycleProjection.Hash("abort", request), operation.RequestFingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deployed_undecided_responder_request_is_preserved_before_adopting_a_different_coordinator_decision(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var descriptor = await PreparedResponderAsync(local, peer);
        var token = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.Control);
        var legacyId = ServiceLinkValidation.NewId();
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
            // Fault injection of the deployed beta10 cancellation storage shape:
            // this undecided responder ID is a request, never a coordinator decision.
            attempt.AbortId = legacyId; attempt.LifecycleState = "in_doubt"; attempt.Revision++;
            await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().SetStatusAsync(attempt.InboundPrincipalId!.Value, "in_doubt");
        }
        var before = await DecisionAttemptAsync(local);
        Assert.Equal("responder", before.Role); Assert.Equal("undecided", before.Decision);
        var decisionId = ServiceLinkValidation.NewId();
        var decision = TerminalRequest(before) with { AbortPhase = "decision", AbortId = decisionId, ReasonCode = "coordinator-abort" };
        var route = ServiceLinkContract.EndpointPath + "/links/" + before.LinkId + "/abort";
        using (var accepted = await local.ServiceAsync(HttpMethod.Post, route, token, decision))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = await DecisionAttemptAsync(local);
        Assert.Equal("abort", after.Decision); Assert.Equal(decisionId, after.AbortId);
        Assert.Equal(descriptor.DescriptorHash, after.DescriptorHash);
        Assert.Equal(before.GrantHash, after.GrantHash); Assert.Equal(before.LinkRevision, after.LinkRevision);
        Assert.Equal(before.InboundPrincipalId, after.InboundPrincipalId);
        Assert.False(after.LocalInboundActive || after.LocalBusinessSenderEnabled);
        var journal = Assert.Single(await DecisionOperationsAsync(local), x => x.Outbound && x.Kind == "abort-request");
        Assert.False(journal.Completed); Assert.NotNull(journal.ProtectedRequestJson);
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("RatelDesk.ServiceLink.v1", after.AttemptId,
                after.PeerInstanceId, "operation/" + journal.OperationId, after.LocalTenantId + "/" + after.LinkId + "/" + after.GrantHash + "/" + after.LinkRevision);
            var request = ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(protector.Unprotect(journal.ProtectedRequestJson!));
            Assert.Equal(legacyId, request.AbortId);
            Assert.Equal(ServiceLinkLifecycleProjection.Hash("abort", request), journal.RequestFingerprint);
        }
        await local.RestartAsync();
        using (var replay = await local.ServiceAsync(HttpMethod.Post, route, token, decision))
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var replacement = await local.ServiceAsync(HttpMethod.Post, route, token,
                   decision with { OperationId = ServiceLinkValidation.NewId(), AbortId = ServiceLinkValidation.NewId() }))
            Assert.Equal(HttpStatusCode.Conflict, replacement.StatusCode);
        Assert.Equal(decisionId, (await DecisionAttemptAsync(local)).AbortId);
        var retained = (await DecisionOperationsAsync(local)).Single(x => x.OperationId == journal.OperationId);
        Assert.Equal(journal.RequestFingerprint, retained.RequestFingerprint);
        Assert.True(string.Equals(journal.ProtectedRequestJson, retained.ProtectedRequestJson, StringComparison.Ordinal), "The original encrypted participant request changed.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_peer_unlink_is_acknowledged_without_replacing_local_tombstone_or_reopening_authority(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        await ActivateInitiatorAsync(local, peer);
        var business = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        var control = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.Control);
        using (var accepted = await CapabilitiesAsync(local, peer, business)) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using (var cancelled = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/links/" + peer.LinkId + "/revoke", new ServiceLinkAdminAction("local-unlink")))
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var before = await DecisionAttemptAsync(local);
        var incoming = TerminalRequest(before) with
        { RevocationId = ServiceLinkValidation.NewId(), ExpectedLinkRevision = before.LinkRevision, ReasonCode = "peer-unlink" };
        Assert.NotEqual(before.RevocationId, incoming.RevocationId);
        var route = ServiceLinkContract.EndpointPath + "/links/" + before.LinkId + "/revoke";
        string originalResponse;
        using (var response = await local.ServiceAsync(HttpMethod.Post, route, control, incoming))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            originalResponse = await response.Content.ReadAsStringAsync();
            using var result = JsonDocument.Parse(originalResponse);
            Assert.Equal(incoming.RevocationId, result.RootElement.GetProperty("revocation_id").GetString());
            Assert.True(result.RootElement.GetProperty("local_business_revoked").GetBoolean());
        }
        var after = await DecisionAttemptAsync(local);
        Assert.Equal(before.RevocationId, after.RevocationId); Assert.Equal("revocation_pending", after.LifecycleState);
        Assert.Equal(before.TerminalControlExpiresAtUnixSeconds, after.TerminalControlExpiresAtUnixSeconds);
        using (var denied = await CapabilitiesAsync(local, peer, business)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await local.RestartAsync();
        using (var replay = await local.ServiceAsync(HttpMethod.Post, route, control, incoming))
        { Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(originalResponse, await replay.Content.ReadAsStringAsync()); }
        using (var changed = await local.ServiceAsync(HttpMethod.Post, route, control, incoming with { RevocationId = ServiceLinkValidation.NewId() }))
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using (var delivered = await ResumeAsync(local, peer.LinkId)) Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        var final = await DecisionAttemptAsync(local);
        Assert.Equal("revoked", final.LifecycleState); Assert.True(final.PeerRevocationAcknowledged);
        Assert.Equal(before.RevocationId, final.RevocationId);
        Assert.Equal(before.GrantHash, final.GrantHash); Assert.Equal(before.LinkRevision, final.LinkRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linking_disable_denies_cached_business_controls_and_sender_until_the_exact_snapshot_is_restored(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        await ActivateInitiatorAsync(local, peer);
        var business = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        var control = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.Control);
        var before = await DecisionAttemptAsync(local);
        await using var scope = local.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>();
        Assert.True((await provider.GetResolvedOrchestratorSettingsAsync()).Enabled);
        var configuration = local.Services.GetRequiredService<IConfiguration>();
        var cache = local.Services.GetRequiredService<IOptionsMonitorCache<ServiceLinkOptions>>();
        var original = configuration["ServiceLinks:Enabled"];
        try
        {
            configuration["ServiceLinks:Enabled"] = "false"; cache.Clear();
            using (var denied = await CapabilitiesAsync(local, peer, business)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await local.ServiceAsync(HttpMethod.Get, ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/status", control))
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var issuance = await TokenResponseAsync(peer.OutboundCredential, ServiceIdentityScopes.Control)) Assert.Equal(HttpStatusCode.Unauthorized, issuance.StatusCode);
            Assert.False((await provider.GetResolvedOrchestratorSettingsAsync()).Enabled);
            var unchanged = await DecisionAttemptAsync(local);
            Assert.Equal(before.GrantHash, unchanged.GrantHash); Assert.Equal(before.LinkRevision, unchanged.LinkRevision);
            Assert.Equal(before.Decision, unchanged.Decision);
        }
        finally { configuration["ServiceLinks:Enabled"] = original; cache.Clear(); }
        using (var restored = await CapabilitiesAsync(local, peer, business)) Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using (var restored = await local.ServiceAsync(HttpMethod.Get, ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/status", control))
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.True((await provider.GetResolvedOrchestratorSettingsAsync()).Enabled);
    }

    private static ServiceLinkLifecycleRequest TerminalRequest(ServiceLinkAttempt attempt) => new()
    { OperationId = ServiceLinkValidation.NewId(), AttemptId = attempt.AttemptId, LinkId = attempt.LinkId!, LinkRevision = attempt.LinkRevision, GrantHash = attempt.GrantHash! };

    private static async Task<ServiceLinkAttempt> DecisionAttemptAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
    }

    private static async Task<ServiceLinkOperation[]> DecisionOperationsAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServiceLinkOperation>().AsNoTracking().ToArrayAsync();
    }

    private static async Task<ServiceLinkNavigation> PreparedCoordinatorAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer)
    {
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback)) Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
                   new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding))) Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        using (var exchange = await ResumeAsync(local, peer.LinkId)) Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.NotNull((await DecisionAttemptAsync(local)).ProtectedOutboundCredential);
        return start;
    }

    private static async Task<ServiceLinkRequestDescriptor> PreparedResponderAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer)
    {
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        var descriptor = peer.PrepareInitiator(metadata!, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState))) Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
            new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState));
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var navigation = await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>();
        await peer.ExchangeAsInitiatorAsync(navigation!);
        return descriptor;
    }
}
