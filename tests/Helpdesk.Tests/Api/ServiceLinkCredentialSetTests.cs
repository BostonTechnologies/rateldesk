using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Exchange_scope_reordering_reuses_same_handoff_and_preserves_legacy_fingerprint(bool postgres, bool legacy)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var navigation = await PrepareCredentialResponderAsync(local, peer);
        await peer.PrepareInitiatorExchangeAsync(navigation);
        var before = await CredentialStateAsync(local);
        using (var invalidNumber = await peer.RepeatInitiatorExchangeAsync(quoteCredentialRevision: true))
            Assert.Equal(HttpStatusCode.BadRequest, invalidNumber.StatusCode);
        Assert.Equal(before, await CredentialStateAsync(local));
        ServiceLinkExchangeResponse accepted;
        using (var initial = await peer.RepeatInitiatorExchangeAsync(reorderCredentialScopes: true))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            accepted = await initial.Content.ReadFromJsonAsync<ServiceLinkExchangeResponse>() ?? throw new InvalidOperationException("Missing exchange.");
        }
        if (legacy)
        {
            // Seed the exact historical raw fingerprint/material; an upgrade must not rewrite either.
            await using var scope = local.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
            var old = peer.InitiatorExchangeRequest with { CredentialForResponder = peer.InitiatorExchangeRequest.CredentialForResponder with
            { Scopes = peer.InitiatorExchangeRequest.CredentialForResponder.Scopes.Reverse().ToArray() } };
            attempt.ExchangeFingerprint = ServiceLinkCanonicalJson.HashObject(old);
            attempt.ProtectedOutboundCredential = CredentialProtector(scope.ServiceProvider, attempt, "outbound-credential")
                .Protect(JsonSerializer.Serialize(old.CredentialForResponder));
            await db.SaveChangesAsync();
        }
        var retained = await CredentialStateAsync(local);
        using (var reordered = await peer.RepeatInitiatorExchangeAsync())
        {
            Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
            var replay = await reordered.Content.ReadFromJsonAsync<ServiceLinkExchangeResponse>();
            Assert.True(ServiceLinkCanonicalJson.HashObject(accepted) == ServiceLinkCanonicalJson.HashObject(replay),
                "An equivalent retry changed the reveal-once handoff.");
        }
        using (var changedSecret = await peer.RepeatInitiatorExchangeAsync(changeCredentialSecret: true))
            Assert.Equal(HttpStatusCode.Conflict, changedSecret.StatusCode);
        using (var changedConsent = await peer.RepeatInitiatorExchangeAsync(changeBody: true))
            Assert.Equal(HttpStatusCode.Conflict, changedConsent.StatusCode);
        Assert.Equal(retained, await CredentialStateAsync(local));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quoted_revisions_and_null_proof_or_operation_fields_return_400_before_any_journal_or_receipt(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var navigation = await PrepareCredentialResponderAsync(local, peer);
        await peer.ExchangeAsInitiatorAsync(navigation);
        var token = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.Verify);
        var verify = new ServiceLinkLifecycleRequest
        {
            OperationId = ServiceLinkValidation.NewId(), AttemptId = peer.Summary.AttemptId, LinkId = peer.LinkId,
            LinkRevision = 1, GrantHash = peer.GrantHash, DirectionId = ServiceLinkContract.InitiatorToResponder,
            CredentialRevision = 1
        };
        var before = await CredentialStateAsync(local);
        foreach (var field in new[] { "link_revision", "credential_revision", "operation_id" })
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(ServiceLinkLifecycleProjection.Build("verify", verify)))!;
            node[field] = field == "operation_id" ? null : JsonValue.Create("1");
            using var denied = await RawCredentialPostAsync(local, "verify", node.ToJsonString(), token);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal(before, await CredentialStateAsync(local));
        }
        var proof = peer.InitiatorExchangeRequest;
        var review = JsonNode.Parse(JsonSerializer.Serialize(new ServiceLinkReviewRequest(proof.Contract,
            proof.AttemptId, proof.PairingCode, proof.CodeVerifier, proof.DescriptorHash)))!;
        review["pairing_code"] = null;
        using (var message = new HttpRequestMessage(HttpMethod.Post,
                   ServiceLinkContract.EndpointPath + "/attempts/" + proof.AttemptId + "/review")
               { Content = new StringContent(review.ToJsonString(), Encoding.UTF8, "application/json") })
        using (var denied = await local.Client.SendAsync(message))
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(before, await CredentialStateAsync(local));
        await using var final = local.Services.CreateAsyncScope();
        var db = final.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Rotation_scope_reordering_is_equivalent_but_changed_secret_or_deadline_conflicts(bool postgres, bool legacy)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        await ActivateCredentialInitiatorAsync(local, peer);
        var control = await TokenAsync(peer.OutboundCredential, ServiceIdentityScopes.Control);
        var candidate = peer.InboundCredential with { CredentialRevision = 2, ClientSecret = ServiceLinkValidation.Proof(),
            Scopes = peer.InboundCredential.Scopes.Reverse().ToArray() };
        var offer = new ServiceLinkLifecycleRequest
        {
            OperationId = ServiceLinkValidation.NewId(), LinkId = peer.LinkId, LinkRevision = 1, GrantHash = peer.GrantHash,
            RotationId = ServiceLinkValidation.NewId(), RotationPhase = "offer", DirectionId = ServiceLinkContract.InitiatorToResponder,
            ExpectedCurrentCredentialRevision = 1, SuccessorCredentialRevision = 2,
            OfferExpiresAt = ServiceLinkValidation.Timestamp(local.Clock.GetUtcNow().ToUnixTimeSeconds() + 120), CredentialForCaller = candidate
        };
        var beforeOffer = await CredentialStateAsync(local);
        foreach (var field in new[] { "expected_current_credential_revision", "successor_credential_revision" })
        {
            var invalid = JsonNode.Parse(OfferJson(offer))!;
            invalid[field] = "1";
            using var denied = await RawCredentialPostAsync(local, "rotate", invalid.ToJsonString(), control);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal(beforeOffer, await CredentialStateAsync(local));
        }
        using (var nullScope = await RawCredentialPostAsync(local, "rotate", OfferJson(offer with
                   { CredentialForCaller = candidate with { Scopes = [null!] } }), control))
            Assert.Equal(HttpStatusCode.BadRequest, nullScope.StatusCode);
        Assert.Equal(beforeOffer, await CredentialStateAsync(local));
        using (var initial = await RawCredentialPostAsync(local, "rotate", OfferJson(offer), control))
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        if (legacy)
        {
            await using var scope = local.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
            var rotation = await db.Set<ServiceLinkRotation>().SingleAsync();
            rotation.ProtectedCandidate = CredentialProtector(scope.ServiceProvider, attempt, "rotation-candidate/" + rotation.RotationId)
                .Protect(JsonSerializer.Serialize(candidate));
            (await db.Set<ServiceLinkOperation>().SingleAsync(operation => operation.OperationId == offer.OperationId)).RequestFingerprint =
                ServiceLinkLifecycleProjection.Hash("rotate", offer, false);
            await db.SaveChangesAsync();
        }
        var before = await CredentialStateAsync(local);
        var reordered = offer with { CredentialForCaller = candidate with { Scopes = candidate.Scopes.Reverse().ToArray() } };
        using (var replay = await RawCredentialPostAsync(local, "rotate", OfferJson(reordered), control))
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var changedSecret = await RawCredentialPostAsync(local, "rotate", OfferJson(reordered with
                   { CredentialForCaller = reordered.CredentialForCaller! with { ClientSecret = ServiceLinkValidation.Proof() } }), control))
            await AssertCredentialOperationConflictAsync(changedSecret);
        using (var changedDeadline = await RawCredentialPostAsync(local, "rotate", OfferJson(reordered with
                   { OfferExpiresAt = ServiceLinkValidation.Timestamp(local.Clock.GetUtcNow().ToUnixTimeSeconds() + 121) }), control))
            await AssertCredentialOperationConflictAsync(changedDeadline);
        Assert.Equal(before, await CredentialStateAsync(local));
        // A distinct operation retry still compares the full retained candidate semantically.
        using (var newOperation = await RawCredentialPostAsync(local, "rotate", OfferJson(reordered with
                   { OperationId = ServiceLinkValidation.NewId() }), control))
            Assert.Equal(HttpStatusCode.OK, newOperation.StatusCode);
        var afterEquivalent = await CredentialStateAsync(local);
        using (var changedNewDeadline = await RawCredentialPostAsync(local, "rotate", OfferJson(reordered with
                   { OperationId = ServiceLinkValidation.NewId(), OfferExpiresAt = ServiceLinkValidation.Timestamp(local.Clock.GetUtcNow().ToUnixTimeSeconds() + 121) }), control))
            Assert.Equal(HttpStatusCode.Conflict, changedNewDeadline.StatusCode);
        Assert.Equal(afterEquivalent, await CredentialStateAsync(local));
        if (legacy)
        {
            // Preserve the original exact candidate as permanent material, matching the historical
            // post-promotion escrow purge. This seeds compatibility data, not a new activation decision.
            await using (var historical = local.Services.CreateAsyncScope())
            {
                var db = historical.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync();
                attempt.ProtectedOutboundCredential = CredentialProtector(historical.ServiceProvider, attempt, "outbound-credential")
                    .Protect(JsonSerializer.Serialize(candidate));
                (await db.Set<ServiceLinkRotation>().SingleAsync()).ProtectedCandidate = null;
                await db.SaveChangesAsync();
            }
            var purged = await CredentialStateAsync(local);
            using (var recoveredLegacy = await RawCredentialPostAsync(local, "rotate", OfferJson(reordered), control))
                Assert.Equal(HttpStatusCode.OK, recoveredLegacy.StatusCode);
            Assert.Equal(purged, await CredentialStateAsync(local));
        }
        await using var check = local.Services.CreateAsyncScope();
        var finalDb = check.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(1, await finalDb.Set<ServiceLinkRotation>().CountAsync());
        Assert.Equal(0, await finalDb.Set<ServiceLinkVerificationReceipt>().CountAsync(receipt => receipt.RotationId != null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_metadata_members_fail_closed_without_creating_an_attempt_or_principal(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var before = await CredentialStateAsync(local);
        foreach (var field in new[] { "instance_id", "profile", "operation", "oauth_metadata_url", "jwks_uri" })
        {
            peer.InvalidMetadataMember = field;
            using var denied = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/start",
                new ServiceLinkStartRequest(peer.BaseUrl, local.OrganizationId, null, [], SessionBinding)
                { LocalCustomerIds = [local.CustomerId] });
            Assert.Equal(field is "oauth_metadata_url" or "jwks_uri" ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.BadRequest,
                denied.StatusCode);
            Assert.Equal(before, await CredentialStateAsync(local));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutually_consistent_foreign_initiator_tenants_cannot_provision_pending_grants(bool postgres)
    {
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres);
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath)
            ?? throw new InvalidOperationException("Missing metadata.");
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId,
            local.Clock.GetUtcNow(), mismatchInitiatorTenant: true);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var before = await CredentialStateAsync(local);
        using (var denied = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
                   new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Equal("tenant-pair-mismatch", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(before, await CredentialStateAsync(local));
    }

    private static async Task<ServiceLinkNavigation> PrepareCredentialResponderAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer)
    {
        var metadata = await local.Client.GetFromJsonAsync<ServiceLinkMetadata>(ServiceLinkContract.MetadataPath)
            ?? throw new InvalidOperationException("Missing metadata.");
        var descriptor = peer.PrepareInitiator(metadata, local.OrganizationId, local.CustomerId, local.Clock.GetUtcNow());
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-review",
                   new ServiceLinkRemoteReviewRequest(peer.BaseUrl, descriptor.AttemptId, peer.BrowserState)))
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using var approval = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/remote-approve",
            new ServiceLinkRemoteApproveRequest(descriptor.AttemptId, local.OrganizationId, descriptor.RequestedGrants, peer.BrowserState));
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        return await approval.Content.ReadFromJsonAsync<ServiceLinkNavigation>() ?? throw new InvalidOperationException("Missing approval.");
    }

    private static async Task ActivateCredentialInitiatorAsync(ServiceLinkKestrelPeer local, NetRatelServiceLinkContractPeer peer)
    {
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using (var approval = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve", new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var actor = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, "HelpdeskAdmin"),
            new Claim("auth_mode", "local")], "LifecycleAdmin"));
        for (var phase = 0; phase < 12; phase++)
        {
            await using var scope = local.Services.CreateAsyncScope();
            var current = await scope.ServiceProvider.GetRequiredService<ICurrentUserAccessService>()
                .ResolveAsync(actor, CancellationToken.None);
            Assert.True(current.IsHelpdeskAdmin, "The normal fixture account lost its current administrative authority.");
            var status = await scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>()
                .ResumeAsync(peer.LinkId, actor, CancellationToken.None);
            if (status.LifecycleState == "active") return;
        }
        Assert.Fail("The authenticated lifecycle did not complete its active preparation fixture.");
    }

    private static string OfferJson(ServiceLinkLifecycleRequest request) => JsonSerializer.Serialize(ServiceLinkLifecycleProjection.Build("rotate", request, false));

    private static async Task AssertCredentialOperationConflictAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.True(problem is not null && problem.RootElement.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.String && code.GetString() == "operation-payload-conflict",
            "The changed body did not conflict with its recorded operation fingerprint.");
    }

    private static async Task<HttpResponseMessage> RawCredentialPostAsync(ServiceLinkKestrelPeer local, string operation, string json, string token)
    {
        string linkId;
        using (var body = JsonDocument.Parse(json)) linkId = body.RootElement.GetProperty("link_id").GetString()!;
        using var message = new HttpRequestMessage(HttpMethod.Post, ServiceLinkContract.EndpointPath + "/links/" + linkId + "/" + operation)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await local.Client.SendAsync(message);
    }

    private static IDataProtector CredentialProtector(IServiceProvider services, ServiceLinkAttempt attempt, string purpose) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RatelDesk.ServiceLink.v1", attempt.AttemptId, attempt.PeerInstanceId,
            purpose, attempt.LocalTenantId + "/" + attempt.LinkId + "/" + attempt.GrantHash + "/" + attempt.LinkRevision);

    private static async Task<string> CredentialStateAsync(ServiceLinkKestrelPeer local)
    {
        await using var scope = local.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        return ServiceLinkCanonicalJson.HashObject(new
        {
            Attempts = await db.Set<ServiceLinkAttempt>().AsNoTracking().OrderBy(value => value.AttemptId).ToListAsync(),
            Principals = await db.Set<ServicePrincipalRegistration>().AsNoTracking().OrderBy(value => value.Id).ToListAsync(),
            Secrets = await db.Set<ServicePrincipalSecret>().AsNoTracking().OrderBy(value => value.ServicePrincipalId).ThenBy(value => value.CredentialRevision).ToListAsync(),
            Operations = await db.Set<ServiceLinkOperation>().AsNoTracking().OrderBy(value => value.OperationId).ToListAsync(),
            Receipts = await db.Set<ServiceLinkVerificationReceipt>().AsNoTracking().OrderBy(value => value.VerificationReceiptId).ToListAsync(),
            Rotations = await db.Set<ServiceLinkRotation>().AsNoTracking().OrderBy(value => value.RotationId).ToListAsync(),
            Sources = await db.IncidentReceiverSources.AsNoTracking().OrderBy(value => value.SourceNamespaceId).ToListAsync(),
            Bindings = await db.IncidentReceiverPrincipalBindings.AsNoTracking().OrderBy(value => value.SourceNamespaceId)
                .ThenBy(value => value.PrincipalKind).ThenBy(value => value.PrincipalId).ToListAsync()
        });
    }
}
