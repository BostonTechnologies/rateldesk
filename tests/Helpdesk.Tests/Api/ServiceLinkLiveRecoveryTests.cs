using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Fact]
    public async Task Repeated_start_of_active_bidirectional_relationship_returns_exact_authorized_recovery_before_profile_conflict()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var original = await ActivateInitiatorAsync(local, peer);
        var before = await ContinuationStateAsync(local);
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var profileBefore = ServiceLinkCanonicalJson.HashObject(await db.M2MConnectivitySettings.AsNoTracking().ToArrayAsync());
        var request = new ServiceLinkStartRequest(peer.BaseUrl, local.OrganizationId, peer.TenantId, [], SessionBinding)
        {
            LocalCustomerIds = [local.CustomerId],
            InboundScopes = [ServiceIdentityScopes.IncidentCreate, ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets, ServiceIdentityScopes.Callback],
            OutboundRequestDefinitionIds = ["synthetic-request-definition"]
        };
        using (var sameRelationship = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start", request))
        {
            Assert.Equal(HttpStatusCode.Conflict, sameRelationship.StatusCode);
            var failure = await sameRelationship.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("relationship-already-exists", failure.GetProperty("code").GetString());
            Assert.Equal(original.AttemptId, failure.GetProperty("existingAttemptId").GetString());
        }
        using (var differentRelationship = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start",
                   request with { RequestedResponderTenantId = "another-peer-tenant" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, differentRelationship.StatusCode);
            var failure = await differentRelationship.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("profile-ownership-conflict", failure.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("existingAttemptId").ValueKind);
        }
        Assert.Equal(before, await ContinuationStateAsync(local));
        Assert.Equal(profileBefore, ServiceLinkCanonicalJson.HashObject(await db.M2MConnectivitySettings.AsNoTracking().ToArrayAsync()));
    }

    [Fact]
    public async Task Repeated_peer_approval_identifies_the_existing_relationship_without_replacing_consent()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        var original = peer.PrepareInitiator(metadata!, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow(), incidentOnly: true);
        await ReviewLiveAttemptAsync(local, peer, original);
        var originalApproval = new ServiceLinkRemoteApproveRequest(original.AttemptId, local.OrganizationId, original.RequestedGrants, peer.BrowserState);
        ServiceLinkNavigation navigation;
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve", originalApproval))
        {
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
            navigation = (await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>())!;
        }
        var firstState = await ReadLiveAttemptAsync(local, original.AttemptId);
        using (var replay = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve", originalApproval))
        {
            Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            Assert.Equal(navigation, await replay.Content.ReadFromJsonAsync<ServiceLinkNavigation>());
        }
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(firstState), ServiceLinkCanonicalJson.HashObject(await ReadLiveAttemptAsync(local, original.AttemptId)));

        var knownDuplicate = peer.PrepareInitiator(metadata!, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow(), incidentOnly: true);
        using (var duplicateReview = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, knownDuplicate.AttemptId, peer.BrowserState)))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicateReview.StatusCode);
            var existing = await duplicateReview.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("relationship-already-exists", existing.GetProperty("code").GetString());
            Assert.Equal(original.AttemptId, existing.GetProperty("existingAttemptId").GetString());
        }
        // With no preset, the organization remains a real responder choice. A
        // retained unapproved proposal must still get the same safe conflict at consent.
        var duplicate = peer.PrepareInitiator(metadata!, null, local.CustomerId, local.Clock.GetUtcNow(), incidentOnly: true);
        await ReviewLiveAttemptAsync(local, peer, duplicate);
        var chosen = duplicate.RequestedGrants.Select(g => g.TargetProduct == "rateldesk"
            ? g with { TargetTenantId = local.OrganizationId, ResourceConstraints = g.ResourceConstraints with { OrganizationId = local.OrganizationId } }
            : g with { CallerTenantId = local.OrganizationId }).ToArray();
        using var conflict = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
            new ServiceLinkRemoteApproveRequest(duplicate.AttemptId, local.OrganizationId, chosen, peer.BrowserState));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var failure = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("relationship-already-exists", failure.GetProperty("code").GetString());
        Assert.Equal("remote-approve", failure.GetProperty("stage").GetString());
        Assert.Equal(original.AttemptId, failure.GetProperty("existingAttemptId").GetString());
        Assert.Equal(ServiceLinkCanonicalJson.HashObject(firstState), ServiceLinkCanonicalJson.HashObject(await ReadLiveAttemptAsync(local, original.AttemptId)));
        var retained = await ReadLiveAttemptAsync(local, duplicate.AttemptId);
        Assert.Equal("awaiting_approval", retained.LifecycleState);
        Assert.Null(retained.GrantSummaryJson);
        Assert.Null(retained.LinkId);
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(2, await db.Set<ServiceLinkAttempt>().CountAsync());
        Assert.Single(await db.Set<ServicePrincipalRegistration>().ToListAsync());
        Assert.Empty(await db.M2MConnectivitySettings.ToListAsync());
    }

    [Fact]
    public async Task Approved_responder_can_return_the_same_callback_after_reload_without_reapproving()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        var descriptor = peer.PrepareInitiator(metadata!, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow(), incidentOnly: true);
        await ReviewLiveAttemptAsync(local, peer, descriptor);
        ServiceLinkNavigation original;
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                   new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState)))
        {
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
            original = (await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>())!;
        }
        var before = await ContinuationStateAsync(local);
        await local.RestartAsync();
        Assert.Equal("return", (await StatusAsync(local, descriptor.AttemptId)).AvailableAction);
        for (var replay = 0; replay < 2; replay++)
        {
            using var continuation = await local.AdminAsync(HttpMethod.Post,
                "/api/v1/admin/service-links/attempts/" + descriptor.AttemptId + "/continue", new ServiceLinkContinueRequest(""));
            Assert.True(continuation.IsSuccessStatusCode, await continuation.Content.ReadAsStringAsync());
            Assert.Equal(original, await continuation.Content.ReadFromJsonAsync<ServiceLinkNavigation>());
            Assert.Equal(before, await ContinuationStateAsync(local));
        }
        await using (var deniedScope = local.Services.CreateAsyncScope())
        {
            var identity = deniedScope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.Users.Add(new ApplicationUser { Id = "other-return-owner", UserName = "other-return-owner", Email = "other-return@example.test", IsEnabled = true, IsInstanceAdministrator = true });
            await identity.SaveChangesAsync();
            var coordinator = deniedScope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>();
            var denied = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => coordinator.ContinueAsync(descriptor.AttemptId, new(""), RecoveryActor("other-return-owner"), default));
            Assert.Equal(403, denied.StatusCode);
        }
        Assert.Equal(before, await ContinuationStateAsync(local));
        string? originalPairingHash;
        await using (var corruptScope = local.Services.CreateAsyncScope())
        {
            var db = corruptScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync(a => a.AttemptId == descriptor.AttemptId);
            originalPairingHash = attempt.PairingCodeHash;
            attempt.PairingCodeHash = new string('0', 64);
            await db.SaveChangesAsync();
        }
        var corrupted = await ContinuationStateAsync(local);
        using (var badProof = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + descriptor.AttemptId + "/continue", new ServiceLinkContinueRequest("")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, badProof.StatusCode);
            Assert.DoesNotContain("navigationUrl", await badProof.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(corrupted, await ContinuationStateAsync(local));
        await using (var restoreScope = local.Services.CreateAsyncScope())
        {
            var db = restoreScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync(a => a.AttemptId == descriptor.AttemptId);
            attempt.PairingCodeHash = originalPairingHash;
            await db.SaveChangesAsync();
        }
        Assert.Equal(before, await ContinuationStateAsync(local));
        local.Clock.Advance(TimeSpan.FromSeconds(121));
        using var expired = await local.AdminAsync(HttpMethod.Post,
            "/api/v1/admin/service-links/attempts/" + descriptor.AttemptId + "/continue", new ServiceLinkContinueRequest(""));
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.DoesNotContain("navigationUrl", await expired.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await ContinuationStateAsync(local));
    }

    [Fact]
    public async Task Repeated_start_reuses_only_the_same_unmodified_proposal_actor_and_browser()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var request = new ServiceLinkStartRequest(peer.BaseUrl, local.OrganizationId, null, [], SessionBinding)
        {
            LocalCustomerIds = [local.CustomerId],
            InboundScopes = [ServiceIdentityScopes.IncidentCreate, ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets],
            OutboundScopes = [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope]
        };
        ServiceLinkNavigation first;
        using (var started = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start", request))
        {
            Assert.True(started.IsSuccessStatusCode);
            first = (await started.Content.ReadFromJsonAsync<ServiceLinkNavigation>())!;
        }
        var before = await ContinuationStateAsync(local);
        using (var repeated = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start", request))
        {
            Assert.True(repeated.IsSuccessStatusCode, await repeated.Content.ReadAsStringAsync());
            Assert.Equal(first, await repeated.Content.ReadFromJsonAsync<ServiceLinkNavigation>());
        }
        Assert.Equal(before, await ContinuationStateAsync(local));
        using (var foreignSession = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start", request with { SessionBinding = new string('x', 64) }))
        {
            Assert.Equal(HttpStatusCode.Conflict, foreignSession.StatusCode);
            var failure = await foreignSession.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("relationship-already-exists", failure.GetProperty("code").GetString());
            Assert.Equal(first.AttemptId, failure.GetProperty("existingAttemptId").GetString());
            Assert.DoesNotContain(BrowserState(first), failure.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal(before, await ContinuationStateAsync(local));

        var descriptor = (await ReadLiveAttemptAsync(local, first.AttemptId)).DescriptorJson;
        var knownTargetGrants = ServiceLinkCanonicalJson.Deserialize<ServiceLinkRequestDescriptor>(descriptor).RequestedGrants
            .Select(g => g.TargetProduct == "netratel"
                ? g with { TargetTenantId = peer.TenantId, ResourceConstraints = g.ResourceConstraints with { TenantId = peer.TenantId } }
                : g with { CallerTenantId = peer.TenantId }).ToArray();
        var knownTargetRequest = request with { RequestedGrants = knownTargetGrants };
        ServiceLinkNavigation knownTarget;
        using (var known = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start", knownTargetRequest))
        {
            Assert.True(known.IsSuccessStatusCode, await known.Content.ReadAsStringAsync());
            knownTarget = (await known.Content.ReadFromJsonAsync<ServiceLinkNavigation>())!;
            Assert.NotEqual(first.AttemptId, knownTarget.AttemptId);
        }
        var knownTargetBefore = await ContinuationStateAsync(local);
        var changedGrants = knownTargetGrants.Select(g => g.TargetProduct == "netratel"
            ? g with { Scopes = ["netratel.orchestration.read"], Capabilities = ["netratel.orchestration.v1"] } : g).ToArray();
        using (var changed = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start",
                   knownTargetRequest with { RequestedGrants = changedGrants }))
        {
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            var failure = await changed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("relationship-already-exists", failure.GetProperty("code").GetString());
            Assert.Equal(knownTarget.AttemptId, failure.GetProperty("existingAttemptId").GetString());
        }
        Assert.Equal(knownTargetBefore, await ContinuationStateAsync(local));
    }

    private static async Task ReviewLiveAttemptAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer, ServiceLinkRequestDescriptor descriptor)
    {
        using var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
            new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState));
        Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());
    }

    private static async Task<ServiceLinkAttempt> ReadLiveAttemptAsync(ServiceLinkKestrelPeer local, string attemptId)
    {
        await using var scope = local.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServiceLinkAttempt>().AsNoTracking()
            .SingleAsync(a => a.AttemptId == attemptId);
    }
}
