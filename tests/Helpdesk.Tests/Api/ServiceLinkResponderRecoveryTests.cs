using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responder_valid_or_absent_organization_reopens_explicit_consent_before_preparation(bool absent)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        var descriptor = peer.PrepareInitiator(metadata!, absent ? null : local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.True(review.IsSuccessStatusCode, await review.Content.ReadAsStringAsync());

        await local.RestartAsync();
        var status = await StatusAsync(local, descriptor.AttemptId);
        Assert.Equal(absent ? "" : local.OrganizationId, status.LocalTenantId);
        Assert.Equal("responder", status.LocalRole);
        Assert.Equal("respond", status.AvailableAction);
        Assert.Null(status.GrantSummary);
        Assert.False(status.LocalInboundReady);
        Assert.False(status.OrganizationBindingInvalid);
        using (var noConsent = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + descriptor.AttemptId + "/resume", new ServiceLinkAdminAction()))
        {
            Assert.Equal(HttpStatusCode.Conflict, noConsent.StatusCode);
            Assert.Equal("approval-required", (await noConsent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var list = await local.AdminAsync(HttpMethod.Get, "/api/v1/admin/service-links/"))
            Assert.Equal(status.AvailableAction, Assert.Single((await list.Content.ReadFromJsonAsync<ServiceLinkAdminStatus[]>())!).AvailableAction);

        var selected = descriptor.RequestedGrants.Select(grant => grant.TargetProduct == "rateldesk"
            ? grant with { TargetTenantId = local.OrganizationId, ResourceConstraints = grant.ResourceConstraints with { OrganizationId = local.OrganizationId } }
            : grant with { CallerTenantId = local.OrganizationId }).ToArray();
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                   new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, selected, peer.BrowserState)))
            Assert.True(approval.IsSuccessStatusCode, await approval.Content.ReadAsStringAsync());
        var approved = await StatusAsync(local, descriptor.AttemptId);
        Assert.Equal(local.OrganizationId, approved.LocalTenantId);
        Assert.Equal("resume", approved.AvailableAction);
        Assert.NotNull(approved.GrantSummary);
        Assert.Equal(descriptor.DescriptorHash, approved.Descriptor.DescriptorHash);
    }

    [Fact]
    public async Task Responder_rejects_any_invalid_explicit_organization_before_persisting_a_binding()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        foreach (var invalid in new[] { "arbitrary-nonexistent-organization", "c90b4574-f5f7-4434-b5b5-20c66225b956" })
        {
            var descriptor = peer.PrepareInitiator(metadata!, invalid, local.CustomerId, local.Clock.GetUtcNow());
            using var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState));
            Assert.Equal(HttpStatusCode.Forbidden, review.StatusCode);
            Assert.Equal("organization-disabled", (await review.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Empty(await db.Set<ServiceLinkAttempt>().ToListAsync());
        Assert.Empty(await db.Set<ServicePrincipalRegistration>().ToListAsync());
        Assert.Empty(await db.IncidentReceiverSources.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responder_rechecks_enabled_organization_and_current_administrator_before_binding(bool removeAuthority)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        var descriptor = peer.PrepareInitiator(metadata!, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        await using (var change = local.Services.CreateAsyncScope())
        {
            if (removeAuthority)
            {
                var identity = change.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                (await identity.Users.SingleAsync(u => u.Id == "owner")).IsInstanceAdministrator = false;
                await identity.SaveChangesAsync();
            }
            else
            {
                var db = change.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                (await db.Organizations.SingleAsync(o => o.Id == local.OrganizationId)).IsEnabled = false;
                await db.SaveChangesAsync();
            }
        }
        using var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
            new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState));
        Assert.Equal(HttpStatusCode.Forbidden, review.StatusCode);
        await using var scope = local.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServiceLinkAttempt>().ToListAsync());
    }

    [Theory]
    [InlineData("awaiting_approval", false)]
    [InlineData("expired", false)]
    [InlineData("expired", true)]
    [InlineData("failed", false)]
    public async Task Retained_unconsented_malformed_proposal_can_only_be_inspected_and_cancelled_without_rewriting_evidence(string state, bool aborted)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var retained = await SeedMalformedProposalAsync(local, peer, state, aborted);
        var status = await StatusAsync(local, retained.AttemptId);
        Assert.True(status.OrganizationBindingInvalid);
        Assert.Equal("none", status.AvailableAction);
        foreach (var forbidden in new[] { "resume", "rotate", "revoke" })
        {
            using var denied = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + retained.AttemptId + "/" + forbidden, new ServiceLinkAdminAction());
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        string? terminalHash = null;
        for (var retry = 0; retry < 2; retry++)
        {
            using var response = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + retained.AttemptId + "/cancel", new ServiceLinkAdminAction());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var cancelled = await response.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>();
            Assert.Equal("abort", cancelled!.Decision);
            Assert.Equal("expired", cancelled.LifecycleState);
            Assert.True(cancelled.CanStartFresh);
            Assert.False(cancelled.CanCancel);
            await using var scope = local.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var persisted = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
            Assert.Equal(retained.AttemptId, persisted.AttemptId);
            Assert.Equal(retained.DescriptorJson, persisted.DescriptorJson);
            Assert.Equal(retained.DescriptorHash, persisted.DescriptorHash);
            Assert.Equal(retained.LocalTenantId, persisted.LocalTenantId);
            Assert.Equal(retained.CreatedAtUnixSeconds, persisted.CreatedAtUnixSeconds);
            Assert.Equal(retained.LastErrorCode, persisted.LastErrorCode);
            Assert.Null(persisted.GrantSummaryJson);
            Assert.Empty(await db.Set<ServicePrincipalRegistration>().ToListAsync());
            Assert.Empty(await db.Set<ServiceLinkOperation>().ToListAsync());
            var hash = ServiceLinkCanonicalJson.HashObject(persisted);
            if (terminalHash is not null) Assert.Equal(terminalHash, hash);
            terminalHash = hash;
            await local.RestartAsync();
        }
        Assert.True((await StatusAsync(local, retained.AttemptId)).CanStartFresh);
    }

    [Fact]
    public async Task Malformed_cleanup_never_bypasses_existing_preparation_or_commit_authority()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        foreach (var fence in new[] { "summary", "principal", "credential", "exchange", "journal", "receipt", "prepared", "committed" })
        {
            var retained = await SeedMalformedProposalAsync(local, peer);
            await using (var seed = local.Services.CreateAsyncScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                var a = await db.Set<ServiceLinkAttempt>().SingleAsync(a => a.AttemptId == retained.AttemptId);
                switch (fence)
                {
                    case "summary": a.GrantSummaryJson = "{}"; break;
                    case "principal":
                        db.Set<ServicePrincipalRegistration>().Add(new() { AttemptId = a.AttemptId, ClientId = "retained-client", NormalizedClientId = "RETAINED-CLIENT", OrganizationId = local.OrganizationId });
                        break;
                    case "credential": a.ProtectedOutboundCredential = "opaque-retained-credential"; break;
                    case "exchange": a.ExchangeDispatched = true; break;
                    case "journal":
                        db.Set<ServiceLinkOperation>().Add(new() { LinkId = a.AttemptId, OperationId = ServiceLinkValidation.NewId(), Kind = "exchange", Outbound = true });
                        break;
                    case "receipt":
                        db.Set<ServiceLinkVerificationReceipt>().Add(new() { AttemptId = a.AttemptId, VerificationReceiptId = ServiceLinkValidation.NewId(), LinkId = "retained-link" });
                        break;
                    case "prepared": a.LifecycleState = "prepared"; break;
                    case "committed": a.Decision = "commit"; a.LifecycleState = "active"; break;
                }
                await db.SaveChangesAsync();
            }
            var before = await ContinuationStateAsync(local);
            using (var inspection = await local.AdminAsync(HttpMethod.Get, "/api/v1/admin/service-links/attempts/" + retained.AttemptId))
                Assert.Equal(HttpStatusCode.Forbidden, inspection.StatusCode);
            using (var cancellation = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + retained.AttemptId + "/cancel", new ServiceLinkAdminAction()))
                Assert.Equal(HttpStatusCode.Forbidden, cancellation.StatusCode);
            Assert.Equal(before, await ContinuationStateAsync(local));
        }
    }

    [Fact]
    public async Task Malformed_cleanup_requires_both_original_actor_and_current_installation_administration()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var retained = await SeedMalformedProposalAsync(local, peer);
        await using (var seed = local.Services.CreateAsyncScope())
        {
            var identity = seed.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.Users.Add(new ApplicationUser { Id = "other-owner", UserName = "other-owner", Email = "other@example.test", IsEnabled = true, IsInstanceAdministrator = true });
            await identity.SaveChangesAsync();
        }
        var before = await ContinuationStateAsync(local);
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>();
            var other = RecoveryActor("other-owner");
            Assert.Equal(403, (await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => coordinator.AdminStatusAsync(retained.AttemptId, other, default))).StatusCode);
            Assert.Equal(403, (await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => coordinator.AdminActionAsync(retained.AttemptId, "cancel", new(), other, default))).StatusCode);
            Assert.Empty(await coordinator.AdminListAsync(other, default));
            var service = RecoveryActor("owner");
            ((ClaimsIdentity)service.Identity!).AddClaim(new(ServiceIdentityClaims.PrincipalId, "synthetic-principal"));
            Assert.Equal(403, (await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => coordinator.AdminActionAsync(retained.AttemptId, "cancel", new(), service, default))).StatusCode);
        }
        await using (var demote = local.Services.CreateAsyncScope())
        {
            var identity = demote.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            (await identity.Users.SingleAsync(u => u.Id == "owner")).IsInstanceAdministrator = false;
            await identity.SaveChangesAsync();
        }
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>();
            Assert.Equal(403, (await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => coordinator.AdminActionAsync(retained.AttemptId, "cancel", new(), RecoveryActor("owner"), default))).StatusCode);
        }
        Assert.Equal(before, await ContinuationStateAsync(local));
    }

    [Fact]
    public async Task Initiator_projects_browser_continuation_final_review_then_durable_reconciliation()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var start = await StartAsync(local, peer);
        Assert.Equal("continue", (await StatusAsync(local, start.AttemptId)).AvailableAction);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.True(review.IsSuccessStatusCode);
        Assert.Equal("review", (await StatusAsync(local, start.AttemptId)).AvailableAction);
        using (var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve", new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.True(approval.IsSuccessStatusCode);
        Assert.Equal("resume", (await StatusAsync(local, start.AttemptId)).AvailableAction);
    }

    [Fact]
    public async Task Initiator_with_a_dispatched_abort_still_projects_reconciliation_until_peer_confirmation()
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        var start = await StartAsync(local, peer);
        await using (var scope = local.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var a = await db.Set<ServiceLinkAttempt>().SingleAsync();
            a.Decision = "abort"; a.LifecycleState = "in_doubt";
            a.ExchangeDispatched = true; a.ProtectedOutboundCredential = "opaque-recovery-credential";
            await db.SaveChangesAsync();
        }
        var status = await StatusAsync(local, start.AttemptId);
        Assert.Equal("resume", status.AvailableAction);
        Assert.False(status.CanStartFresh);
    }

    [Theory]
    [InlineData("wrong-product")]
    [InlineData("same-instance")]
    public async Task Reverse_discovery_rejects_actual_wrong_product_or_self_metadata_before_creating_an_attempt(string condition)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(false);
        peer.DiscoveryMetadata = condition == "wrong-product" ? peer.Metadata with { Product = "rateldesk" }
            : peer.Metadata with { InstanceId = local.InstanceId.ToString("D") };
        using (var response = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start",
                   new ServiceLinkStartRequest(peer.BaseUrl, local.OrganizationId, null, [], SessionBinding) { LocalCustomerIds = [local.CustomerId] }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("unsupported-peer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var response = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, ServiceLinkValidation.NewId(), ServiceLinkValidation.Proof())))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("unsupported-peer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        await using (var scope = local.Services.CreateAsyncScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<ServiceLinkAttempt>().ToListAsync());
        peer.DiscoveryMetadata = null;
        Assert.Equal("continue", (await StatusAsync(local, (await StartAsync(local, peer)).AttemptId)).AvailableAction);
    }

    private static ClaimsPrincipal RecoveryActor(string id) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, "HelpdeskAdmin"), new Claim("auth_mode", "local")], "LifecycleAdmin"));

    private static async Task<ServiceLinkAttempt> SeedMalformedProposalAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer,
        string state = "awaiting_approval", bool aborted = false)
    {
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath);
        var descriptor = peer.PrepareInitiator(metadata!, "missing-retained-organization", local.CustomerId, local.Clock.GetUtcNow());
        var now = local.Clock.GetUtcNow().ToUnixTimeSeconds();
        var attempt = new ServiceLinkAttempt
        {
            AttemptId = descriptor.AttemptId, Role = "responder", LocalTenantId = descriptor.RequestedResponderTenantId!, LocalActorId = "owner",
            PeerInstanceId = peer.InstanceId, PeerTenantId = peer.TenantId, DescriptorJson = JsonSerializer.Serialize(descriptor), DescriptorHash = descriptor.DescriptorHash,
            CreatedAtUnixSeconds = now - 10, UpdatedAtUnixSeconds = now, ExpiresAtUnixSeconds = state == "awaiting_approval" ? now + 120 : now - 1,
            LifecycleState = state, Decision = aborted ? "abort" : "undecided", AbortId = aborted ? ServiceLinkValidation.NewId() : null,
            ProtectedBrowserState = "opaque-retained-browser-state", LastErrorCode = "organization-disabled"
        };
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        db.Set<ServiceLinkAttempt>().Add(attempt); await db.SaveChangesAsync();
        return attempt;
    }
}
