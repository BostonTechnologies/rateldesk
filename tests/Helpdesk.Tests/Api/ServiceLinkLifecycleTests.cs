using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Background;
using Helpdesk.API.DependencyInjection;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Integrations;
using Helpdesk.API.Endpoints.ServiceLink;
using Helpdesk.API.Services;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed partial class ServiceLinkLifecycleTests
{
    private const string SessionBinding = "synthetic-session-bound-to-the-authenticated-browser";
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_and_remote_selection_require_final_local_consent_before_any_local_client(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await StartAsync(local, peer);
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync());
            Assert.Equal(0, await db.IncidentReceiverPrincipalBindings.CountAsync());
            var reservedSource = await db.IncidentReceiverSources.SingleAsync();
            Assert.Equal(local.OrganizationId, reservedSource.OrganizationId);
            Assert.Equal(local.CustomerId, reservedSource.CustomerId);
        }
        using var descriptorResponse = await local.Client.GetAsync(ServiceLinkContract.EndpointPath + "/requests/" + start.AttemptId);
        Assert.Equal(HttpStatusCode.OK, descriptorResponse.StatusCode);
        var descriptor = await descriptorResponse.Content.ReadFromJsonAsync<ServiceLinkRequestDescriptor>();
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash"), descriptor.DescriptorHash);
        var descriptorText = await descriptorResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("browser_state", descriptorText, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", descriptorText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"client_secret\":", descriptorText, StringComparison.Ordinal);

        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using var returned = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback);
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var reviewResponse = await returned.Content.ReadFromJsonAsync<ServiceLinkReviewResponse>();
        Assert.NotNull(reviewResponse?.GrantSummary);
        var reviewed = await StatusAsync(local, start.AttemptId);
        Assert.NotNull(reviewed?.GrantSummary);
        Assert.Equal(peer.TenantId, reviewed.PeerTenantId);
        Assert.Equal(peer.GrantHash, reviewed.GrantHash);
        Assert.False(reviewed.LocalInboundReady);
        Assert.False(reviewed.LocalInboundActive);
        Assert.False(reviewed.LocalBusinessSenderEnabled);
        await using var check = local.Services.CreateAsyncScope();
        Assert.Equal(0, await check.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServicePrincipalRegistration>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Callback_proof_tampering_never_approves_or_provisions_the_local_grant(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        foreach (var invalid in new[]
        {
            callback with { BrowserState = "foreign-browser-state" }, callback with { SessionBinding = "foreign-session" },
            callback with { ResponderInstanceId = Guid.NewGuid().ToString("D") }, callback with { OauthIssuer = peer.BaseUrl + "/other" },
            callback with { PairingCode = "wrong-pairing-code" }
        })
        {
            using var denied = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", invalid);
            Assert.False(denied.IsSuccessStatusCode);
            var text = await denied.Content.ReadAsStringAsync();
            Assert.DoesNotContain(callback.PairingCode, text, StringComparison.Ordinal);
            Assert.DoesNotContain(callback.BrowserState, text, StringComparison.Ordinal);
        }
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(0, await db.IncidentReceiverPrincipalBindings.CountAsync());
        Assert.False((await db.Set<ServiceLinkAttempt>().SingleAsync()).LocalInboundActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_oauth_probes_and_lost_exchange_response_recover_the_same_pair_after_restart(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using (var approved = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
                   new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        peer.LoseExchangeResponseOnce = true;
        using (var first = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        var original = peer.InboundCredential;
        Assert.NotNull(original);
        Assert.False((await StatusAsync(local, start.AttemptId)).LocalBusinessSenderEnabled);
        using (var pendingBusiness = await TokenResponseAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts))
            Assert.Equal(HttpStatusCode.BadRequest, pendingBusiness.StatusCode);

        await local.RestartAsync();
        ServiceLinkAdminStatus status = await StatusAsync(local, start.AttemptId);
        for (var step = 0; step < 12 && status.LifecycleState != "active"; step++)
        {
            using var resumed = await ResumeAsync(local, peer.LinkId);
            Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
            status = await resumed.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>() ?? throw new InvalidOperationException("Missing resume status.");
        }
        Assert.Equal("active", status.LifecycleState);
        Assert.True(status.LocalInboundActive);
        Assert.True(status.LocalBusinessSenderEnabled);
        Assert.True(status.PeerActiveAcknowledged);
        Assert.True(peer.TokenAcquisitions > 0);
        Assert.True(peer.AuthenticatedVerifications > 0);
        Assert.Equal(original.ClientId, peer.InboundCredential.ClientId);
        Assert.Equal(original.ClientSecret, peer.InboundCredential.ClientSecret);
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(1, await db.Set<ServicePrincipalRegistration>().CountAsync());
            Assert.Equal(1, await db.IncidentReceiverPrincipalBindings.CountAsync());
            Assert.Equal(peer.SourceInstanceId, (await db.IncidentReceiverSources.SingleAsync()).SourceInstanceId.ToString("D"));
        }
        var token = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        using var capabilityRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        capabilityRequest.Headers.Authorization = new("Bearer", token);
        capabilityRequest.Headers.Add(IncidentReceiverContract.SourceHeader, peer.SourceInstanceId);
        using var capabilities = await local.Client.SendAsync(capabilityRequest);
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        var capability = await capabilities.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("oauth_client_credentials", capability.GetProperty("authenticationModes").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responder_retains_prepared_participant_after_expiry_and_recovers_late_coordinator_commit(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        Assert.NotNull(metadata);
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
        await using (var beforeApproval = local.Services.CreateAsyncScope())
        {
            var db = beforeApproval.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync());
            Assert.Equal(0, await db.IncidentReceiverSources.CountAsync());
        }
        ServiceLinkNavigation navigation;
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                   new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState)))
        {
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
            navigation = await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>() ?? throw new InvalidOperationException("Missing callback.");
        }
        await peer.ExchangeAsInitiatorAsync(navigation);
        ServiceLinkAdminStatus status = await StatusAsync(local, descriptor.AttemptId);
        for (var step = 0; step < 8 && status.LifecycleState != "verified"; step++)
        {
            using var response = await ResumeAsync(local, peer.LinkId);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            status = await response.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>() ?? throw new InvalidOperationException("Missing status.");
        }
        Assert.Equal("verified", status.LifecycleState);
        Assert.False(status.LocalInboundActive);
        Assert.False(status.LocalBusinessSenderEnabled);
        peer.DecideCommit();
        peer.HoldLifecycleStatus = true;
        local.Clock.Advance(TimeSpan.FromSeconds(121));
        await local.RestartAsync();
        using (var expired = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.BadGateway, expired.StatusCode);
        status = await StatusAsync(local, descriptor.AttemptId);
        Assert.Equal("in_doubt", status.LifecycleState);
        Assert.False(status.LocalInboundActive);
        Assert.False(status.LocalBusinessSenderEnabled);
        await using (var retained = local.Services.CreateAsyncScope())
        {
            var db = retained.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
            Assert.NotNull(attempt.ProtectedOutboundCredential);
            Assert.Null(attempt.ProtectedInboundEscrow);
            Assert.Null(attempt.ProtectedExchangeResponse);
            Assert.Equal(1, await db.Set<ServicePrincipalRegistration>().CountAsync());
        }
        peer.HoldLifecycleStatus = false;
        await peer.DeliverDecidedCommitAsync();
        for (var step = 0; step < 8 && status.LifecycleState != "active"; step++)
        {
            using var resumed = await ResumeAsync(local, peer.LinkId);
            Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
            status = await resumed.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>() ?? throw new InvalidOperationException("Missing recovered status.");
        }
        Assert.Equal("active", status.LifecycleState);
        Assert.Equal("commit", status.Decision);
        Assert.True(status.LocalBusinessSenderEnabled);
        Assert.True(status.PeerActiveAcknowledged);
        Assert.Equal(peer.SourceInstanceId, (await ReadSourceAsync(local)).SourceInstanceId.ToString("D"));
    }

    private static async Task<Helpdesk.Shared.Models.IncidentReceiverSource> ReadSourceAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().IncidentReceiverSources.AsNoTracking().SingleAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responder_exchange_retries_preserve_the_same_handoff_and_changed_body_conflicts(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath)
            ?? throw new InvalidOperationException("Missing metadata.");
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
        var approvalRequest = new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId,
            descriptor.RequestedGrants, peer.BrowserState);
        ServiceLinkNavigation first;
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve", approvalRequest))
        {
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
            first = await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>() ?? throw new InvalidOperationException("Missing navigation.");
        }
        using (var retry = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve", approvalRequest))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
            Assert.Equal(first, await retry.Content.ReadFromJsonAsync<ServiceLinkNavigation>());
        }
        await peer.ExchangeAsInitiatorAsync(first);
        var credential = peer.OutboundCredential;
        await local.RestartAsync();
        var retries = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => peer.RepeatInitiatorExchangeAsync(reorderJson: index % 2 == 0)));
        foreach (var response in retries)
        {
            using (response)
            {
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                var repeated = await response.Content.ReadFromJsonAsync<ServiceLinkExchangeResponse>();
                Assert.Equal(credential.ClientId, repeated?.CredentialForInitiator.ClientId);
                Assert.Equal(credential.ClientSecret, repeated?.CredentialForInitiator.ClientSecret);
                Assert.Equal(peer.GrantHash, repeated?.GrantHash);
            }
        }
        using (var conflict = await peer.RepeatInitiatorExchangeAsync(changeBody: true))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.DoesNotContain(credential.ClientSecret, await conflict.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(1, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(1, await db.Set<ServicePrincipalSecret>().CountAsync());
        Assert.Equal(1, await db.IncidentReceiverPrincipalBindings.CountAsync());
        Assert.False((await db.Set<ServiceLinkAttempt>().SingleAsync()).LocalInboundActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_discovery_creates_no_authority_and_restart_reuses_the_reserved_source_namespace(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var first = await StartAsync(local, peer);
        var originalNamespace = (await ReadSourceAsync(local)).SourceNamespaceId;
        using (var cancelled = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + first.AttemptId + "/cancel", new ServiceLinkAdminAction("administrator-declined")))
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        using (var descriptor = await local.Client.GetAsync(ServiceLinkContract.EndpointPath + "/requests/" + first.AttemptId))
            Assert.Equal(HttpStatusCode.NotFound, descriptor.StatusCode);
        await local.RestartAsync();
        var second = await StartAsync(local, peer);
        Assert.NotEqual(first.AttemptId, second.AttemptId);
        Assert.Equal(originalNamespace, (await ReadSourceAsync(local)).SourceNamespaceId);
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(0, await db.IncidentReceiverPrincipalBindings.CountAsync());
        var previous = await db.Set<ServiceLinkAttempt>().SingleAsync(x => x.AttemptId == first.AttemptId);
        Assert.Equal("abort", previous.Decision);
        Assert.Null(previous.ProtectedVerifier);
        Assert.Null(previous.ProtectedBrowserState);
        Assert.Null(previous.ProtectedInboundEscrow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_initiator_consent_cannot_be_replayed_to_provision_a_client(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await StartAsync(local, peer);
        var source = await ReadSourceAsync(local);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
        using (var cancelled = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/cancel", new ServiceLinkAdminAction("administrator-declined")))
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        await local.RestartAsync();
        using (var replay = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
                   new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        using (var callbackReplay = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.Forbidden, callbackReplay.StatusCode);
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
        Assert.Equal("abort", attempt.Decision);
        Assert.False(attempt.LocalInboundActive);
        Assert.False(attempt.LocalBusinessSenderEnabled);
        Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(0, await db.IncidentReceiverPrincipalBindings.CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
        Assert.Equal(source.SourceNamespaceId, (await ReadSourceAsync(local)).SourceNamespaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_responder_consent_rejects_review_exchange_and_approval_replays(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath)
            ?? throw new InvalidOperationException("Missing metadata.");
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
        var request = new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId,
            descriptor.RequestedGrants, peer.BrowserState);
        ServiceLinkNavigation navigation;
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve", request))
        {
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
            navigation = await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>() ?? throw new InvalidOperationException("Missing callback.");
        }
        await peer.PrepareInitiatorExchangeAsync(navigation);
        var source = await ReadSourceAsync(local);
        Guid principalId;
        await using (var before = local.Services.CreateAsyncScope())
            principalId = (await before.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServicePrincipalRegistration>().SingleAsync()).Id;
        using (var cancelled = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + descriptor.AttemptId + "/cancel", new ServiceLinkAdminAction("administrator-declined")))
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        await local.RestartAsync();
        using (var approvalReplay = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve", request))
            Assert.Equal(HttpStatusCode.Gone, approvalReplay.StatusCode);
        using (var reviewReplay = await peer.ReviewAsInitiatorAsync(navigation))
            Assert.Equal(HttpStatusCode.Forbidden, reviewReplay.StatusCode);
        using (var exchangeReplay = await peer.RepeatInitiatorExchangeAsync())
            Assert.Equal(HttpStatusCode.Forbidden, exchangeReplay.StatusCode);
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
        Assert.Equal("abort", attempt.Decision);
        Assert.False(attempt.LocalInboundActive);
        Assert.False(attempt.LocalBusinessSenderEnabled);
        Assert.Null(attempt.ProtectedInboundEscrow);
        Assert.Null(attempt.ProtectedOutboundCredential);
        Assert.Null(attempt.ProtectedExchangeResponse);
        var principal = await db.Set<ServicePrincipalRegistration>().SingleAsync();
        Assert.Equal(principalId, principal.Id);
        Assert.Equal("revoked", principal.Status);
        Assert.All(await db.IncidentReceiverPrincipalBindings.ToListAsync(), binding => Assert.False(binding.IsEnabled));
        Assert.Equal(1, await db.Set<ServicePrincipalSecret>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
        Assert.Equal(source.SourceNamespaceId, (await ReadSourceAsync(local)).SourceNamespaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responder_recovers_coordinator_abort_after_exchange_response_loss_without_probes_or_sender(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath)
            ?? throw new InvalidOperationException("Missing metadata.");
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
        ServiceLinkNavigation navigation;
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                   new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState)))
        {
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
            navigation = await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>() ?? throw new InvalidOperationException("Missing callback.");
        }
        await Assert.ThrowsAsync<HttpRequestException>(() => peer.ExchangeAsInitiatorAsync(navigation, loseResponse: true));
        Assert.False(peer.HasOutboundCredential);
        var source = await ReadSourceAsync(local);
        Guid principalId;
        await using (var prepared = local.Services.CreateAsyncScope())
        {
            var db = prepared.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
            Assert.Equal("prepared", attempt.LifecycleState);
            Assert.NotNull(attempt.ProtectedOutboundCredential);
            Assert.NotNull(attempt.ProtectedExchangeResponse);
            principalId = (await db.Set<ServicePrincipalRegistration>().SingleAsync()).Id;
        }
        peer.DecideAbort();
        await local.RestartAsync();
        using (var recovered = await ResumeAsync(local, peer.LinkId))
            Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync());
        var status = await StatusAsync(local, descriptor.AttemptId);
        Assert.Equal("abort", status.Decision);
        Assert.Equal("expired", status.LifecycleState);
        Assert.False(status.LocalInboundActive);
        Assert.False(status.LocalBusinessSenderEnabled);
        Assert.True(peer.TokenAcquisitions > 0);
        Assert.True(peer.AuthenticatedStatusReads > 0);
        Assert.Equal(0, peer.AuthenticatedVerifications);
        await using var final = local.Services.CreateAsyncScope();
        var finalDb = final.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var tombstone = await finalDb.Set<ServiceLinkAttempt>().SingleAsync();
        Assert.Null(tombstone.ProtectedInboundEscrow);
        Assert.Null(tombstone.ProtectedExchangeResponse);
        Assert.Null(tombstone.ProtectedPairingCode);
        Assert.Null(tombstone.ProtectedVerifier);
        Assert.Null(tombstone.ProtectedBrowserState);
        Assert.NotNull(tombstone.AbortId);
        var principal = await finalDb.Set<ServicePrincipalRegistration>().SingleAsync();
        Assert.Equal(principalId, principal.Id);
        Assert.Equal("revoked", principal.Status);
        Assert.Equal(source.SourceNamespaceId, principal.SourceNamespaceId);
        Assert.Equal(source.SourceNamespaceId, (await ReadSourceAsync(local)).SourceNamespaceId);
        Assert.Equal(source.Revision, (await ReadSourceAsync(local)).Revision);
        Assert.All(await finalDb.IncidentReceiverPrincipalBindings.ToListAsync(), binding => Assert.False(binding.IsEnabled));
        Assert.Equal(0, await finalDb.Set<ServiceLinkVerificationReceipt>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invocation_without_an_explicit_reverse_callback_grant_is_rejected_before_provisioning(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath)
            ?? throw new InvalidOperationException("Missing metadata.");
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow(), includeCallback: false);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                   new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, approval.StatusCode);
            var problem = await approval.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("callback-required", problem.GetProperty("code").GetString());
        }
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(0, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(0, await db.Set<ServicePrincipalSecret>().CountAsync());
        Assert.Equal(0, await db.IncidentReceiverPrincipalBindings.CountAsync());
        var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
        Assert.Equal("undecided", attempt.Decision);
        Assert.False(attempt.LocalInboundActive);
        Assert.False(attempt.LocalBusinessSenderEnabled);
        Assert.Null(attempt.LinkId);
        Assert.Null(attempt.ProtectedOutboundCredential);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Manual_and_policy_rotation_verify_successor_before_switch_and_retire_with_stable_source(bool postgres, bool automatic)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres, automatic);
        var start = await ActivateInitiatorAsync(local, peer);
        var source = await ReadSourceAsync(local);
        var predecessor = peer.OutboundCredential;
        if (automatic) local.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromSeconds(1)));
        if (!automatic)
        {
            using var requested = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/links/" + peer.LinkId + "/rotate",
                new ServiceLinkAdminAction(DirectionId: ServiceLinkContract.ResponderToInitiator));
            Assert.True(requested.IsSuccessStatusCode, await requested.Content.ReadAsStringAsync());
        }
        using (var progress = await ResumeAsync(local, peer.LinkId))
            Assert.True(progress.IsSuccessStatusCode, await progress.Content.ReadAsStringAsync());
        var candidate = peer.RotationCandidate;
        Assert.Equal(predecessor.ClientId, candidate.ClientId);
        Assert.Equal(predecessor.CredentialRevision + 1, candidate.CredentialRevision);
        Assert.NotEqual(predecessor.ClientSecret, candidate.ClientSecret);
        using (var pendingBusiness = await TokenResponseAsync(candidate, ServiceIdentityScopes.IncidentReceipts))
            Assert.Equal(HttpStatusCode.BadRequest, pendingBusiness.StatusCode);
        var candidateControl = await TokenAsync(candidate, ServiceIdentityScopes.Control);
        using (var escalation = await local.ServiceAsync(HttpMethod.Post,
                   ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/rotate", candidateControl,
                   new ServiceLinkLifecycleRequest
                   {
                       OperationId = ServiceLinkValidation.NewId(), LinkId = peer.LinkId, LinkRevision = 1,
                       GrantHash = peer.GrantHash, RotationId = ServiceLinkValidation.NewId(), RotationPhase = "request",
                       DirectionId = ServiceLinkContract.ResponderToInitiator, ExpectedCurrentCredentialRevision = 1,
                       RequestedByInstanceId = peer.InstanceId
                   }))
            Assert.Equal(HttpStatusCode.Forbidden, escalation.StatusCode);
        _ = await TokenAsync(predecessor, ServiceIdentityScopes.IncidentReceipts);
        await local.RestartAsync();
        await peer.CompleteOfferedRotationAsync(local.RestartAsync, local.RestartAsync);
        var successorToken = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        Assert.NotEmpty(successorToken);
        var status = await StatusAsync(local, start.AttemptId);
        var rotation = Assert.Single(status.Rotations);
        Assert.Equal("retiring", rotation.RotationState);
        Assert.NotNull(rotation.ActivateDecisionId);
        Assert.NotNull(rotation.PredecessorRetireAt);
        Assert.Equal(1, status.LinkRevision);
        Assert.Equal(peer.GrantHash, status.GrantHash);
        Assert.Equal(source.SourceNamespaceId, (await ReadSourceAsync(local)).SourceNamespaceId);
        Assert.Equal(source.Revision, (await ReadSourceAsync(local)).Revision);
        local.Clock.Advance(TimeSpan.FromSeconds(61));
        using (var retired = await ResumeAsync(local, peer.LinkId))
            Assert.True(retired.IsSuccessStatusCode, await retired.Content.ReadAsStringAsync());
        using (var old = await TokenResponseAsync(predecessor, ServiceIdentityScopes.IncidentReceipts))
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        _ = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        Assert.Equal(source.SourceNamespaceId, (await ReadSourceAsync(local)).SourceNamespaceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_blocks_cached_business_tokens_immediately_and_keeps_truthful_remote_outage_state(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await ActivateInitiatorAsync(local, peer);
        var source = await ReadSourceAsync(local);
        var token = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        using (var before = await CapabilitiesAsync(local, peer, token))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var controlToken = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.Control);
        // Separate malformed-control scenarios from the completed consent burst;
        // the production rate limiter remains enabled on the restarted replica.
        await local.RestartAsync();
        int acceptedOperations;
        await using (var beforeInvalidControl = local.Services.CreateAsyncScope())
            acceptedOperations = await beforeInvalidControl.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
                .Set<ServiceLinkOperation>().CountAsync();
        foreach (var reason in new string?[] { null, "", new string('r', 257) })
        {
            using var invalidControl = await local.ServiceAsync(HttpMethod.Post,
                ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/revoke", controlToken,
                new ServiceLinkLifecycleRequest
                {
                    OperationId = ServiceLinkValidation.NewId(), LinkId = peer.LinkId, LinkRevision = 1,
                    GrantHash = peer.GrantHash, RevocationId = ServiceLinkValidation.NewId(),
                    ExpectedLinkRevision = 1, ReasonCode = reason
                });
            Assert.Equal(HttpStatusCode.BadRequest, invalidControl.StatusCode);
            Assert.Contains("invalid-reason-code", await invalidControl.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await using var afterInvalidControl = local.Services.CreateAsyncScope();
            Assert.Equal(acceptedOperations, await afterInvalidControl.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
                .Set<ServiceLinkOperation>().CountAsync());
            var unchanged = await StatusAsync(local, start.AttemptId);
            Assert.True(unchanged.LocalInboundActive);
            Assert.True(unchanged.LocalBusinessSenderEnabled);
        }
        await peer.DisconnectAsync();
        using (var unlink = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/links/" + peer.LinkId + "/revoke", new ServiceLinkAdminAction("administrator-unlink")))
            Assert.True(unlink.IsSuccessStatusCode, await unlink.Content.ReadAsStringAsync());
        using (var after = await CapabilitiesAsync(local, peer, token))
            Assert.True(after.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        using (var peerAttempt = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.BadGateway, peerAttempt.StatusCode);
        var status = await StatusAsync(local, start.AttemptId);
        Assert.Equal("revocation_pending", status.LifecycleState);
        Assert.False(status.LocalInboundActive);
        Assert.False(status.LocalBusinessSenderEnabled);
        await local.RestartAsync();
        using (var restarted = await CapabilitiesAsync(local, peer, token))
            Assert.True(restarted.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal(source.SourceNamespaceId, (await ReadSourceAsync(local)).SourceNamespaceId);
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.NotNull((await db.Set<ServiceLinkAttempt>().SingleAsync()).RevocationId);
        Assert.Equal(1, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.All(await db.IncidentReceiverPrincipalBindings.ToListAsync(), binding => Assert.False(binding.IsEnabled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_registry_revocation_of_linked_inbound_client_disables_cached_business_and_sender_across_restart(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await ActivateInitiatorAsync(local, peer);
        var source = await ReadSourceAsync(local);
        var token = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.IncidentReceipts);
        using (var before = await CapabilitiesAsync(local, peer, token))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        Guid principalId;
        OrchestrationResolvedSettings priorSettings;
        await using (var before = local.Services.CreateAsyncScope())
        {
            var db = before.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            principalId = (await db.Set<ServicePrincipalRegistration>().SingleAsync()).Id;
            priorSettings = await before.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                .GetResolvedOrchestratorSettingsAsync();
        }
        Assert.True(priorSettings.Enabled);
        var tokenService = local.Services.GetRequiredService<IOrchestrationTokenService>();
        Assert.NotEmpty(await tokenService.GetAccessTokenAsync(priorSettings));
        var acquisitionsBeforeRevocation = peer.TokenAcquisitions;

        await using (var revocation = local.Services.CreateAsyncScope())
            await revocation.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().RevokeAsync(principalId);
        using (var cachedBusiness = await CapabilitiesAsync(local, peer, token))
            Assert.True(cachedBusiness.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokenService.GetAccessTokenAsync(priorSettings));
        Assert.Equal(acquisitionsBeforeRevocation, peer.TokenAcquisitions);
        var status = await StatusAsync(local, start.AttemptId);
        Assert.False(status.LocalInboundActive);
        Assert.False(status.LocalBusinessSenderEnabled);
        Assert.Equal("grant-unavailable", status.LastErrorCode);
        Assert.Equal("commit", status.Decision);
        Assert.Equal(peer.LinkId, status.LinkId);

        await local.RestartAsync();
        using (var cachedAfterRestart = await CapabilitiesAsync(local, peer, token))
            Assert.True(cachedAfterRestart.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.Services.GetRequiredService<IOrchestrationTokenService>()
            .GetAccessTokenAsync(priorSettings));
        Assert.Equal(acquisitionsBeforeRevocation, peer.TokenAcquisitions);
        var restarted = await StatusAsync(local, start.AttemptId);
        Assert.False(restarted.LocalInboundActive);
        Assert.False(restarted.LocalBusinessSenderEnabled);
        Assert.Equal("grant-unavailable", restarted.LastErrorCode);
        Assert.Equal(status.LinkId, restarted.LinkId);
        Assert.Equal(status.LinkRevision, restarted.LinkRevision);
        Assert.Equal(status.GrantHash, restarted.GrantHash);
        var retainedSource = await ReadSourceAsync(local);
        Assert.Equal(source.SourceNamespaceId, retainedSource.SourceNamespaceId);
        Assert.Equal(source.SourceInstanceId, retainedSource.SourceInstanceId);
        Assert.Equal(source.Revision, retainedSource.Revision);
        await using var final = local.Services.CreateAsyncScope();
        var finalDb = final.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var principal = await finalDb.Set<ServicePrincipalRegistration>().SingleAsync();
        Assert.Equal(principalId, principal.Id);
        Assert.Equal("revoked", principal.Status);
        Assert.Equal(source.SourceNamespaceId, principal.SourceNamespaceId);
        Assert.Equal(peer.LinkId, principal.LinkId);
        Assert.All(await finalDb.IncidentReceiverPrincipalBindings.ToListAsync(), binding => Assert.False(binding.IsEnabled));
        var attempt = await finalDb.Set<ServiceLinkAttempt>().SingleAsync();
        Assert.False(attempt.LocalInboundActive);
        Assert.False(attempt.LocalBusinessSenderEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pinned_active_grants_refuse_live_local_identity_drift_for_business_and_control(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await ActivateInitiatorAsync(local, peer);
        var credential = peer.OutboundCredential;
        var businessToken = await TokenAsync(credential, ServiceIdentityScopes.IncidentReceipts);
        var controlToken = await TokenAsync(credential, ServiceIdentityScopes.Control);
        var changes = new Dictionary<string, string>
        {
            ["Audience"] = "rateldesk-different-api",
            ["InstanceId"] = Guid.NewGuid().ToString("D"),
            ["ApiBaseUrl"] = local.BaseUrl + "/different-api",
            ["WebBaseUrl"] = local.BaseUrl + "/different-web",
            ["Issuer"] = local.BaseUrl + "/different-issuer",
            ["Enabled"] = "false"
        };
        foreach (var change in changes)
        {
            // Each independent drift scenario uses a fresh replica with the same durable
            // grant and cached JWTs, keeping the production issuance limiter in force.
            await local.RestartAsync();
            var configuration = local.Services.GetRequiredService<IConfiguration>();
            var cache = local.Services.GetRequiredService<IOptionsMonitorCache<ServiceIdentityOptions>>();
            var key = "ServiceIdentity:" + change.Key;
            var original = configuration[key];
            try
            {
                configuration[key] = change.Value;
                cache.Clear();
                using (var cachedBusiness = await CapabilitiesAsync(local, peer, businessToken))
                    Assert.Equal(HttpStatusCode.Unauthorized, cachedBusiness.StatusCode);
                using (var cachedControl = await local.ServiceAsync(HttpMethod.Get,
                           ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/status", controlToken))
                    Assert.Equal(HttpStatusCode.Unauthorized, cachedControl.StatusCode);
                foreach (var scope in new[] { ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.Control })
                {
                    using var issuance = await TokenResponseAsync(credential, scope);
                    Assert.Equal(change.Key == "Enabled" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized,
                        issuance.StatusCode);
                }
                Assert.Equal(peer.GrantHash, (await StatusAsync(local, start.AttemptId)).GrantHash);
            }
            finally
            {
                configuration[key] = original;
                cache.Clear();
            }
            using (var restoredBusiness = await CapabilitiesAsync(local, peer, businessToken))
                Assert.Equal(HttpStatusCode.OK, restoredBusiness.StatusCode);
            using (var restoredControl = await local.ServiceAsync(HttpMethod.Get,
                       ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/status", controlToken))
                Assert.Equal(HttpStatusCode.OK, restoredControl.StatusCode);
            _ = await TokenAsync(credential, ServiceIdentityScopes.IncidentReceipts);
            _ = await TokenAsync(credential, ServiceIdentityScopes.Control);
        }
        Assert.Equal(peer.GrantHash, (await StatusAsync(local, start.AttemptId)).GrantHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unlink_purges_both_pending_rotation_handoffs_and_lost_offer_journal_without_erasing_tombstones(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await ActivateInitiatorAsync(local, peer);
        var source = await ReadSourceAsync(local);
        await local.RestartAsync();
        var predecessor = peer.OutboundCredential;
        var cachedBusiness = await TokenAsync(predecessor, ServiceIdentityScopes.IncidentReceipts);
        using (var requested = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/links/" + peer.LinkId + "/rotate",
                   new ServiceLinkAdminAction(DirectionId: ServiceLinkContract.ResponderToInitiator)))
            Assert.True(requested.IsSuccessStatusCode, await requested.Content.ReadAsStringAsync());
        peer.LoseRotationOfferResponseOnce = true;
        using (var lost = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.BadGateway, lost.StatusCode);
        var issuerCandidate = peer.RotationCandidate;

        // Receive the opposite direction through its real authenticated HTTP operation as well.
        var callerRotationId = ServiceLinkValidation.NewId();
        var callerCandidate = peer.InboundCredential with
        {
            ClientSecret = ServiceLinkValidation.Proof(), CredentialRevision = peer.InboundCredential.CredentialRevision + 1
        };
        using (var received = await local.ServiceAsync(HttpMethod.Post,
                   ServiceLinkContract.EndpointPath + "/links/" + peer.LinkId + "/rotate",
                   await TokenAsync(predecessor, ServiceIdentityScopes.Control),
                   new ServiceLinkLifecycleRequest
                   {
                       OperationId = ServiceLinkValidation.NewId(), LinkId = peer.LinkId, LinkRevision = 1,
                       GrantHash = peer.GrantHash, RotationId = callerRotationId, RotationPhase = "offer",
                       DirectionId = ServiceLinkContract.InitiatorToResponder,
                       ExpectedCurrentCredentialRevision = peer.InboundCredential.CredentialRevision,
                       SuccessorCredentialRevision = callerCandidate.CredentialRevision,
                       OfferExpiresAt = ServiceLinkValidation.Timestamp(local.Clock.GetUtcNow().ToUnixTimeSeconds() + 120),
                       CredentialForCaller = callerCandidate
                   }))
            Assert.True(received.IsSuccessStatusCode, await received.Content.ReadAsStringAsync());
        string journalId;
        string journalFingerprint;
        await using (var pending = local.Services.CreateAsyncScope())
        {
            var db = pending.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var issuer = await db.Set<ServiceLinkRotation>().SingleAsync(x => x.IsIssuer);
            var caller = await db.Set<ServiceLinkRotation>().SingleAsync(x => !x.IsIssuer);
            Assert.NotNull(issuer.ProtectedOffer);
            Assert.NotNull(caller.ProtectedCandidate);
            Assert.Null(issuer.ActivateDecisionId);
            Assert.Null(caller.ActivateDecisionId);
            var journal = await db.Set<ServiceLinkOperation>().SingleAsync(x => x.Kind == "rotate-offer/" + issuer.RotationId);
            Assert.False(journal.Completed);
            Assert.NotNull(journal.ProtectedRequestJson);
            journalId = journal.OperationId;
            journalFingerprint = journal.RequestFingerprint;
        }
        using (var unlink = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/links/" + peer.LinkId + "/revoke",
                   new ServiceLinkAdminAction("administrator-unlink")))
            Assert.True(unlink.IsSuccessStatusCode, await unlink.Content.ReadAsStringAsync());
        using (var denied = await CapabilitiesAsync(local, peer, cachedBusiness))
            Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        using (var candidateBusiness = await TokenResponseAsync(issuerCandidate, ServiceIdentityScopes.IncidentReceipts))
            Assert.Equal(HttpStatusCode.Unauthorized, candidateBusiness.StatusCode);
        using (var acknowledged = await ResumeAsync(local, peer.LinkId))
            Assert.True(acknowledged.IsSuccessStatusCode, await acknowledged.Content.ReadAsStringAsync());
        await local.RestartAsync();
        await using (var recovery = local.Services.CreateAsyncScope())
            await recovery.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
        var status = await StatusAsync(local, start.AttemptId);
        Assert.Equal("revoked", status.LifecycleState);
        Assert.False(status.LocalInboundActive);
        Assert.False(status.LocalBusinessSenderEnabled);
        Assert.Equal(peer.GrantHash, status.GrantHash);
        await using var retained = local.Services.CreateAsyncScope();
        var retainedDb = retained.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var rotations = await retainedDb.Set<ServiceLinkRotation>().ToListAsync();
        Assert.Equal(2, rotations.Count);
        Assert.All(rotations, rotation =>
        {
            Assert.Equal("aborted", rotation.RotationState);
            Assert.Null(rotation.ProtectedOffer);
            Assert.Null(rotation.ProtectedCandidate);
            Assert.Null(rotation.ActiveRotationKey);
            Assert.NotNull(rotation.SuccessorCredentialRevision);
        });
        var retainedJournal = await retainedDb.Set<ServiceLinkOperation>().SingleAsync(x => x.OperationId == journalId);
        Assert.Equal(journalFingerprint, retainedJournal.RequestFingerprint);
        Assert.Null(retainedJournal.ProtectedRequestJson);
        Assert.False(retainedJournal.Completed);
        var attempt = await retainedDb.Set<ServiceLinkAttempt>().SingleAsync();
        Assert.NotNull(attempt.RevocationId);
        Assert.NotNull(attempt.ProtectedOutboundCredential);
        Assert.True(attempt.TerminalControlExpiresAtUnixSeconds > local.Clock.GetUtcNow().ToUnixTimeSeconds());
        Assert.Equal("revoked", (await retainedDb.Set<ServicePrincipalRegistration>().SingleAsync()).Status);
        Assert.Equal("revoked", (await retainedDb.Set<ServicePrincipalSecret>().SingleAsync(x => x.CredentialRevision == issuerCandidate.CredentialRevision)).Status);
        Assert.Equal("active", (await retainedDb.Set<ServicePrincipalSecret>().SingleAsync(x => x.CredentialRevision == predecessor.CredentialRevision)).Status);
        Assert.All(await retainedDb.IncidentReceiverPrincipalBindings.ToListAsync(), binding => Assert.False(binding.IsEnabled));
        var stableSource = await ReadSourceAsync(local);
        Assert.Equal(source.SourceNamespaceId, stableSource.SourceNamespaceId);
        Assert.Equal(source.Revision, stableSource.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_rotation_offer_is_purged_by_worker_after_source_authority_is_disabled(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var start = await ActivateInitiatorAsync(local, peer);
        var source = await ReadSourceAsync(local);
        await local.RestartAsync();
        var predecessor = peer.OutboundCredential;
        var cachedBusiness = await TokenAsync(predecessor, ServiceIdentityScopes.IncidentReceipts);
        using (var requested = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/links/" + peer.LinkId + "/rotate",
                   new ServiceLinkAdminAction(DirectionId: ServiceLinkContract.ResponderToInitiator)))
            Assert.True(requested.IsSuccessStatusCode, await requested.Content.ReadAsStringAsync());
        peer.LoseRotationOfferResponseOnce = true;
        using (var lost = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.BadGateway, lost.StatusCode);
        var candidate = peer.RotationCandidate;
        long deadline;
        string rotationId;
        string journalId;
        DateTimeOffset predecessorExpiry;
        await using (var disabled = local.Services.CreateAsyncScope())
        {
            var db = disabled.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var rotation = await db.Set<ServiceLinkRotation>().SingleAsync();
            deadline = rotation.OfferExpiresAtUnixSeconds!.Value;
            rotationId = rotation.RotationId;
            Assert.NotNull(rotation.ProtectedOffer);
            var journal = await db.Set<ServiceLinkOperation>().SingleAsync(x => x.Kind == "rotate-offer/" + rotationId);
            Assert.False(journal.Completed);
            Assert.NotNull(journal.ProtectedRequestJson);
            journalId = journal.OperationId;
            predecessorExpiry = (await db.Set<ServicePrincipalSecret>().SingleAsync(x => x.CredentialRevision == predecessor.CredentialRevision)).ExpiresAtUtc;
            (await db.IncidentReceiverSources.SingleAsync()).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        local.Clock.Advance(TimeSpan.FromSeconds(deadline - local.Clock.GetUtcNow().ToUnixTimeSeconds() + 1));
        await local.RestartAsync();
        var acquisitionsBeforeCleanup = peer.TokenAcquisitions;
        await using (var recovery = local.Services.CreateAsyncScope())
            await recovery.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(CancellationToken.None);
        Assert.Equal(acquisitionsBeforeCleanup, peer.TokenAcquisitions);
        var status = await StatusAsync(local, start.AttemptId);
        Assert.False(status.LocalInboundActive);
        Assert.False(status.LocalBusinessSenderEnabled);
        Assert.Equal("grant-unavailable", status.LastErrorCode);
        Assert.Equal("commit", status.Decision);
        using (var denied = await CapabilitiesAsync(local, peer, cachedBusiness))
            Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        using (var unusedSuccessor = await TokenResponseAsync(candidate, ServiceIdentityScopes.Control))
            Assert.Equal(HttpStatusCode.Unauthorized, unusedSuccessor.StatusCode);
        await using var retained = local.Services.CreateAsyncScope();
        var retainedDb = retained.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var expired = await retainedDb.Set<ServiceLinkRotation>().SingleAsync(x => x.RotationId == rotationId);
        Assert.Equal("aborted", expired.RotationState);
        Assert.Null(expired.ProtectedOffer);
        Assert.Null(expired.ProtectedCandidate);
        Assert.Null(expired.ActiveRotationKey);
        Assert.Null(expired.ActivateDecisionId);
        Assert.Null((await retainedDb.Set<ServiceLinkOperation>().SingleAsync(x => x.OperationId == journalId)).ProtectedRequestJson);
        var old = await retainedDb.Set<ServicePrincipalSecret>().SingleAsync(x => x.CredentialRevision == predecessor.CredentialRevision);
        Assert.Equal("active", old.Status);
        Assert.Equal(predecessorExpiry, old.ExpiresAtUtc);
        Assert.Null(old.RetireAtUtc);
        Assert.Equal("revoked", (await retainedDb.Set<ServicePrincipalSecret>().SingleAsync(x => x.CredentialRevision == candidate.CredentialRevision)).Status);
        var stableSource = await ReadSourceAsync(local);
        Assert.False(stableSource.IsEnabled);
        Assert.Equal(source.SourceNamespaceId, stableSource.SourceNamespaceId);
        Assert.Equal(source.Revision, stableSource.Revision);
    }

    private static async Task<HttpResponseMessage> CapabilitiesAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add(IncidentReceiverContract.SourceHeader, peer.SourceInstanceId);
        return await local.Client.SendAsync(request);
    }

    private static async Task<ServiceLinkNavigation> ActivateInitiatorAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer)
    {
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
        using (var approval = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
                   new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
        var status = await StatusAsync(local, start.AttemptId);
        for (var step = 0; step < 12 && status.LifecycleState != "active"; step++)
        {
            using var resumed = await ResumeAsync(local, peer.LinkId);
            Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
            status = await resumed.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>() ?? throw new InvalidOperationException("Missing status.");
        }
        Assert.Equal("active", status.LifecycleState);
        return start;
    }

    private static Task<ServiceLinkKestrelPeer> LocalAsync(bool postgres, bool automaticRotation = false,
        Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor? saveInterceptor = null,
        Action<IServiceCollection, IConfiguration>? configure = null) => ServiceLinkKestrelPeer.CreateAsync(postgres,
        (services, configuration) =>
        {
            configuration["ServiceLinks:AutomaticRotationEnabled"] = automaticRotation.ToString();
            configuration["ServiceLinks:RotationAgeDays"] = "1";
            configuration["ServiceLinks:RotationOverlapSeconds"] = "60";
            services.AddRatelDeskServiceIdentity(configuration);
            services.AddServiceLinkProtocol(configuration);
            services.AddRequestBus(typeof(CreateIncidentCommand).Assembly);
            services.AddSingleton<ITicketRefGeneratorService, TicketRefGeneratorService>();
            services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
            services.AddScoped<IIncidentReceiverAuthorization, IncidentReceiverAuthorization>();
            services.AddScoped<IncidentReceiver>();
            if (saveInterceptor is not null)
                services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(saveInterceptor));
            configure?.Invoke(services, configuration);
        }, app =>
        {
            app.MapServiceIdentityEndpoints(); app.MapServiceLinkEndpoints(); app.MapIncidentReceiverEndpoints();
        });

    private static async Task<ServiceLinkNavigation> StartAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer)
    {
        using var response = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start",
            new ServiceLinkStartRequest(peer.BaseUrl, local.OrganizationId, null, [], SessionBinding)
            {
                LocalCustomerIds = [local.CustomerId],
                InboundScopes = [ServiceIdentityScopes.IncidentCreate, ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets, ServiceIdentityScopes.Callback],
                OutboundRequestDefinitionIds = ["synthetic-request-definition"]
            });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<ServiceLinkNavigation>() ?? throw new InvalidOperationException("Missing start result.");
    }

    private static string BrowserState(ServiceLinkNavigation navigation) =>
        QueryHelpers.ParseQuery(new Uri(navigation.NavigationUrl).Query)["browser_state"].ToString();

    private static Task<HttpResponseMessage> ResumeAsync(ServiceLinkKestrelPeer local, string linkId) =>
        local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/links/" + linkId + "/resume", new ServiceLinkAdminAction());

    private static async Task<ServiceLinkAdminStatus> StatusAsync(ServiceLinkKestrelPeer local, string attemptId)
    {
        using var status = await local.AdminAsync(HttpMethod.Get, "/api/v1/admin/service-links/attempts/" + attemptId);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        return await status.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>() ?? throw new InvalidOperationException("Missing status.");
    }

    private static async Task<HttpResponseMessage> TokenResponseAsync(ServiceDirectionalCredential credential, string scope)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        return await http.PostAsync(credential.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = credential.ClientId,
            ["client_secret"] = credential.ClientSecret, ["scope"] = scope
        }));
    }

    private static async Task<string> TokenAsync(ServiceDirectionalCredential credential, string scope)
    {
        using var token = await TokenResponseAsync(credential, scope);
        Assert.True(token.IsSuccessStatusCode, await token.Content.ReadAsStringAsync());
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
}
