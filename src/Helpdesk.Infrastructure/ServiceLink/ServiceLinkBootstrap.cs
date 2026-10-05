using System.Data;
using System.Security.Claims;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    public async Task<ServiceLinkNavigation> StartAsync(ServiceLinkStartRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        var local = Metadata(); var actorId = await Authorize(actor, request.LocalTenantId, ct);
        Require(request.SessionBinding.Length is >= 32 and <= 256, "invalid-browser-session", "A protected browser session binding is required.");
        var existingProfile = await providers.GetOrchestratorSettingsAsync(ct);
        Require(!existingProfile.ManagedByDeployment, "deployment-owned-profile", "The deployment owns the outbound profile. Use its explicit manual deployment values before guided linking.", 409);
        if (existingProfile.HasClientSecret || !string.IsNullOrEmpty(existingProfile.LinkId))
        {
            var prior = await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.LinkId == existingProfile.LinkId, ct);
            Require(prior is not null && prior.LocalTenantId == request.LocalTenantId && !prior.LocalInboundActive && !prior.LocalBusinessSenderEnabled && !existingProfile.Enabled &&
                (prior.LifecycleState == "revoked" || prior.LifecycleState == "expired" && prior.Decision == "abort"), "profile-ownership-conflict", "The existing outbound profile must be explicitly disconnected and its local authority disabled before a new consent ceremony.", 409);
        }
        var peer = ServiceLinkValidation.Metadata(await transport.GetAsync<ServiceLinkMetadata>(Endpoint(request.PeerWebBaseUrl, ServiceLinkContract.MetadataPath), ct), settings.AllowPrivateHttp, request.PeerWebBaseUrl);
        Require(peer.Product == "netratel" && peer.InstanceId != local.InstanceId, "unsupported-peer", "Select a distinct compatible NetRatel installation.", 422);
        Require(Guid.TryParseExact(peer.SourceInstanceId, "D", out var producer), "source-identity-unavailable", "The NetRatel metadata must advertise its exact persistent Flow producer identity.", 422);
        var customers = Set(request.LocalCustomerIds);
        Require(customers.Length == 1, "invalid-source-grant", "Select exactly one enabled local customer for this producer.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var namespaceId = await ReserveSource(producer, request.LocalTenantId, customers[0], actorId, ct);
        var attemptId = NewId(); var verifier = Proof(); var browserState = Proof();
        var grants = request.RequestedGrants.Length == 0 ? BuildProposal(local, peer, request, namespaceId) : request.RequestedGrants;
        grants = Grants(grants, local, peer, proposal: true);
        Require(grants.Single(x => x.TargetInstanceId == local.InstanceId).ResourceConstraints.OrganizationId == request.LocalTenantId &&
            grants.Single(x => x.TargetInstanceId == local.InstanceId).SourceNamespaceId == namespaceId.ToString("D"), "invalid-source-grant", "The proposal differs from its administrator-reserved local source mapping.");
        var descriptor = new ServiceLinkRequestDescriptor
        {
            AttemptId = attemptId, ExpiresAt = Timestamp(Now + settings.BootstrapLifetimeSeconds), InitiatorInstanceId = local.InstanceId,
            InitiatorTenantId = request.LocalTenantId, ExpectedResponderInstanceId = peer.InstanceId, RequestedResponderTenantId = request.RequestedResponderTenantId,
            InitiatorEndpointSnapshot = local, ResponderEndpointSnapshot = peer, CodeChallenge = Challenge(verifier), RequestedGrants = grants, InitiatorCallbackEndpoint = local.CallbackEndpoint
        };
        descriptor = ServiceLinkPayloadNormalization.Descriptor(descriptor);
        descriptor = descriptor with { DescriptorHash = ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash") };
        var a = new ServiceLinkAttempt
        {
            AttemptId = attemptId, Role = "initiator", LocalTenantId = request.LocalTenantId, LocalActorId = actorId, PeerInstanceId = peer.InstanceId,
            PeerTenantId = request.RequestedResponderTenantId, DescriptorJson = Json(descriptor), DescriptorHash = descriptor.DescriptorHash,
            SessionBindingHash = Digest(request.SessionBinding), ExpiresAtUnixSeconds = Now + settings.BootstrapLifetimeSeconds, CreatedAtUnixSeconds = Now, UpdatedAtUnixSeconds = Now, NextWorkAtUnixSeconds = Now
        };
        a.ProtectedVerifier = Protect(a, "verifier", verifier); a.ProtectedBrowserState = Protect(a, "browser-state", browserState);
        db.Set<ServiceLinkAttempt>().Add(a); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        var url = peer.ApprovalEndpoint + "?initiator_web_base_url=" + Uri.EscapeDataString(local.WebBaseUrl) + "&attempt_id=" + Uri.EscapeDataString(attemptId) + "&browser_state=" + Uri.EscapeDataString(browserState);
        return new(attemptId, url, a.LifecycleState);
    }

    public async Task<ServiceLinkNavigation> ContinueAsync(string attemptId, ServiceLinkContinueRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        Id(attemptId); var a = await Attempt(attemptId, ct); var actorId = await Authorize(actor, a.LocalTenantId, ct);
        Require(a.Role == "initiator" && a.LocalActorId == actorId && a.Decision == "undecided" && a.LifecycleState == "awaiting_approval" &&
            a.ExpiresAtUnixSeconds > Now && a.ProtectedBrowserState is not null && request.SessionBinding is { Length: >= 32 and <= 256 } &&
            Same(a.SessionBindingHash, Digest(request.SessionBinding)), "invalid-local-consent", "The original live initiating browser session is required to continue this approval.", 403);
        var descriptor = Descriptor(a);
        Require(descriptor.DescriptorHash == a.DescriptorHash && ServiceLinkPayloadNormalization.DescriptorHashMatches(descriptor, a.DescriptorHash),
            "descriptor-binding-mismatch", "The retained descriptor differs from its approved navigation.", 409);
        Require(ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(descriptor.InitiatorEndpointSnapshot), "product_version") == ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(Metadata()), "product_version"),
            "identity-configuration-drift", "Restore the approved local identity or start a new explicit consent ceremony.", 409);
        var browserState = Unprotect(a, "browser-state", a.ProtectedBrowserState!);
        var url = descriptor.ResponderEndpointSnapshot.ApprovalEndpoint + "?initiator_web_base_url=" + Uri.EscapeDataString(descriptor.InitiatorEndpointSnapshot.WebBaseUrl) +
            "&attempt_id=" + Uri.EscapeDataString(a.AttemptId) + "&browser_state=" + Uri.EscapeDataString(browserState);
        return new(a.AttemptId, url, a.LifecycleState);
    }

    private static ServiceLinkGrant[] BuildProposal(ServiceLinkMetadata local, ServiceLinkMetadata peer, ServiceLinkStartRequest request, Guid source)
    {
        var remoteTenant = request.RequestedResponderTenantId ?? "";
        var inboundScopes = Set(request.InboundScopes); var outboundScopes = Set(request.OutboundScopes);
        string[] Capabilities(ServiceLinkMetadata target, string[] scopes) => target.PermissionProfiles.Where(p => scopes.Any(s => p.Scopes.Contains(s, StringComparer.Ordinal))).Select(p => p.Capability).Order(StringComparer.Ordinal).ToArray();
        return
        [
            new() { DirectionId = ServiceLinkContract.InitiatorToResponder, CallerSnapshot = "initiator", TargetSnapshot = "responder", CallerProduct = local.Product, CallerInstanceId = local.InstanceId, CallerTenantId = request.LocalTenantId, TargetProduct = peer.Product, TargetInstanceId = peer.InstanceId, TargetTenantId = remoteTenant, Issuer = peer.OauthIssuer, Audience = peer.Audience, Scopes = outboundScopes, Capabilities = Capabilities(peer, outboundScopes), ResourceConstraints = new() { TenantId = string.IsNullOrEmpty(remoteTenant) ? null : remoteTenant, ResourceIds = request.OutboundResourceIds, RequestDefinitionIds = request.OutboundRequestDefinitionIds } },
            new() { DirectionId = ServiceLinkContract.ResponderToInitiator, CallerSnapshot = "responder", TargetSnapshot = "initiator", CallerProduct = peer.Product, CallerInstanceId = peer.InstanceId, CallerTenantId = remoteTenant, TargetProduct = local.Product, TargetInstanceId = local.InstanceId, TargetTenantId = request.LocalTenantId, Issuer = local.OauthIssuer, Audience = local.Audience, Scopes = inboundScopes, Capabilities = Capabilities(local, inboundScopes), SourceInstanceId = peer.SourceInstanceId, SourceNamespaceId = source.ToString("D"), ResourceConstraints = new() { OrganizationId = request.LocalTenantId, CustomerIds = request.LocalCustomerIds } }
        ];
    }

    public async Task<ServiceLinkRequestDescriptor> RemoteReviewAsync(ServiceLinkRemoteReviewRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        Id(request.AttemptId); var local = Metadata(); var access = await accessService.ResolveAsync(actor, ct);
        Require(actor.Identity?.IsAuthenticated == true && actor.FindFirst("integration_credential_id") is null && actor.FindFirst("service_principal_id") is null && (access.IsHelpdeskAdmin || access.ManagedOrganizationIds.Count > 0), "administrator-required", "A local product administrator is required.", 403);
        Require(request.BrowserState.Length is >= 32 and <= 256, "invalid-browser-state", "The bounded initiator correlation is required.");
        var peer = ServiceLinkValidation.Metadata(await transport.GetAsync<ServiceLinkMetadata>(Endpoint(request.InitiatorWebBaseUrl, ServiceLinkContract.MetadataPath), ct), settings.AllowPrivateHttp, request.InitiatorWebBaseUrl);
        Require(peer.Product == "netratel" && peer.InstanceId != local.InstanceId, "unsupported-peer", "Select a distinct compatible NetRatel installation.");
        var d = await transport.GetAsync<ServiceLinkRequestDescriptor>(Endpoint(peer.ServiceLinkEndpoint, "/requests/" + request.AttemptId), ct);
        ServiceLinkValidation.Metadata(d.InitiatorEndpointSnapshot, settings.AllowPrivateHttp);
        ServiceLinkValidation.Metadata(d.ResponderEndpointSnapshot, settings.AllowPrivateHttp);
        Grants(d.RequestedGrants, peer, local, proposal: true);
        Require(d.Contract == ServiceLinkContract.Version && d.AttemptId == request.AttemptId && d.InitiatorInstanceId == peer.InstanceId && d.ExpectedResponderInstanceId == local.InstanceId &&
            d.InitiatorCallbackEndpoint == peer.CallbackEndpoint && d.CodeChallengeMethod == "S256" && d.CodeChallenge.Length == 43 &&
            ServiceLinkPayloadNormalization.DescriptorHashMatches(d, d.DescriptorHash) &&
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(d.InitiatorEndpointSnapshot)) == ServiceLinkCanonicalJson.HashObject(peer) && ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(d.ResponderEndpointSnapshot)) == ServiceLinkCanonicalJson.HashObject(local),
            "descriptor-binding-mismatch", "The fixed origin descriptor does not match this peer and target.");
        Require(ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(d.ExpiresAt).ToUnixTimeSeconds() > Now, "attempt-expired", "The descriptor has expired.", 410);
        if (ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Descriptor(d), "descriptor_hash") == d.DescriptorHash)
            d = ServiceLinkPayloadNormalization.Descriptor(d);
        var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub") ?? "";
        var existing = await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.AttemptId == request.AttemptId, ct);
        if (existing is not null)
        {
            Require(existing.Role == "responder" && existing.DescriptorHash == d.DescriptorHash && existing.LocalActorId == actorId && Same(Unprotect(existing, "browser-state", existing.ProtectedBrowserState!), request.BrowserState), "attempt-conflict", "This attempt is already bound to a different origin, actor or correlation.", 409);
            return Descriptor(existing);
        }
        var a = new ServiceLinkAttempt { AttemptId = d.AttemptId, Role = "responder", LocalTenantId = d.RequestedResponderTenantId ?? "", LocalActorId = actorId, PeerInstanceId = peer.InstanceId, PeerTenantId = d.InitiatorTenantId, DescriptorJson = Json(d), DescriptorHash = d.DescriptorHash, ExpiresAtUnixSeconds = Math.Min(ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(d.ExpiresAt).ToUnixTimeSeconds(), Now + settings.BootstrapLifetimeSeconds), CreatedAtUnixSeconds = Now, UpdatedAtUnixSeconds = Now, NextWorkAtUnixSeconds = Now };
        a.ProtectedBrowserState = Protect(a, "browser-state", request.BrowserState); db.Set<ServiceLinkAttempt>().Add(a); await db.SaveChangesAsync(ct); return d;
    }

    public async Task<ServiceLinkNavigation> RemoteApproveAsync(ServiceLinkRemoteApproveRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        var a = await Attempt(request.AttemptId, ct); var actorId = await Authorize(actor, request.LocalTenantId, ct); var d = Descriptor(a);
        Require(a.Role == "responder" && a.LocalActorId == actorId && a.ExpiresAtUnixSeconds > Now && a.Decision == "undecided" && a.LifecycleState is "awaiting_approval" or "approved" or "prepared" or "verified", "attempt-expired", "The local consent attempt is unavailable.", 410);
        var browserState = Unprotect(a, "browser-state", a.ProtectedBrowserState!);
        Require(string.IsNullOrEmpty(request.BrowserState) || Same(request.BrowserState, browserState), "invalid-browser-state", "The remote approval correlation changed.");
        var selected = request.Grants;
        Require(selected.Length == 2 && selected.All(grant => grant is not null), "invalid-grant", "Two explicit directional grants are required.");
        var inbound = selected.Single(x => x.TargetInstanceId == issuer.InstanceId);
        Require(inbound.TargetTenantId == request.LocalTenantId && inbound.ResourceConstraints.CustomerIds.Length == 1 && Guid.TryParseExact(d.InitiatorEndpointSnapshot.SourceInstanceId, "D", out _), "invalid-source-grant", "Select an explicit local customer and the persisted peer producer.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var namespaceId = await ReserveSource(Guid.Parse(d.InitiatorEndpointSnapshot.SourceInstanceId!), request.LocalTenantId, inbound.ResourceConstraints.CustomerIds[0], actorId, ct);
        selected = selected.Select(g => g == inbound ? g with { SourceNamespaceId = namespaceId.ToString("D"), SourceInstanceId = d.InitiatorEndpointSnapshot.SourceInstanceId } : g).ToArray();
        selected = Grants(selected, d.InitiatorEndpointSnapshot, d.ResponderEndpointSnapshot);
        SelectedTenants(selected, d.InitiatorTenantId, request.LocalTenantId);
        foreach (var grant in selected) Narrowed(d.RequestedGrants.Single(x => x.DirectionId == grant.DirectionId), grant);
        if (a.GrantSummaryJson is not null)
        {
            Require(ServiceLinkCanonicalJson.HashObject(Summary(a).Grants) == ServiceLinkCanonicalJson.HashObject(selected), "consent-conflict", "The immutable approved grant cannot change.", 409);
            return new(a.AttemptId, CallbackNavigation(a, browserState, Unprotect(a, "pairing-code", a.ProtectedPairingCode!)), a.LifecycleState);
        }
        a.LocalTenantId = request.LocalTenantId; a.LinkId = NewId(); a.ConsentId = NewId();
        a.ActiveRelationshipKey = Digest(a.LocalTenantId + "\n" + a.PeerInstanceId + "\n" + a.PeerTenantId);
        var summary = new ServiceLinkGrantSummary { AttemptId = a.AttemptId, LinkId = a.LinkId, DescriptorHash = d.DescriptorHash, ExpiresAt = Timestamp(a.ExpiresAtUnixSeconds), InitiatorInstanceId = d.InitiatorInstanceId, ResponderInstanceId = issuer.InstanceId, InitiatorEndpointSnapshot = d.InitiatorEndpointSnapshot, ResponderEndpointSnapshot = d.ResponderEndpointSnapshot, Grants = selected };
        summary = ServiceLinkPayloadNormalization.Summary(summary);
        a.GrantSummaryJson = Json(summary); a.GrantHash = ServiceLinkCanonicalJson.HashObject(summary, "grant_hash"); a.LifecycleState = "approved";
        var pairingCode = Proof(); a.PairingCodeHash = Digest(pairingCode); a.ProtectedPairingCode = Protect(a, "pairing-code", pairingCode);
        await CreateInbound(a, actorId, ct); await Save(a, ct); await tx.CommitAsync(ct);
        return new(a.AttemptId, CallbackNavigation(a, browserState, pairingCode), a.LifecycleState);
    }
    private string CallbackNavigation(ServiceLinkAttempt a, string browser, string code) => Descriptor(a).InitiatorCallbackEndpoint + "?attempt_id=" + Uri.EscapeDataString(a.AttemptId) + "&pairing_code=" + Uri.EscapeDataString(code) + "&browser_state=" + Uri.EscapeDataString(browser) + "&responder_instance_id=" + Uri.EscapeDataString(issuer.InstanceId) + "&oauth_issuer=" + Uri.EscapeDataString(issuer.Issuer);

    private void ReviewProof(ServiceLinkAttempt a, ServiceLinkReviewRequest request)
    {
        Require(request.CodeVerifier.Length is >= 43 and <= 128 && request.CodeVerifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') && request.PairingCode.Length is >= 32 and <= 128 && request.PairingCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "invalid-pairing-proof", "The pairing proof is invalid or expired.", 403);
        Require(request.Contract == ServiceLinkContract.Version && request.AttemptId == a.AttemptId && a.Role == "responder" && a.GrantSummaryJson is not null && a.ExpiresAtUnixSeconds > Now && a.Decision != "abort" && a.LifecycleState is not ("revoked" or "revocation_pending" or "expired" or "failed") &&
            Same(a.PairingCodeHash, Digest(request.PairingCode)) && Same(Descriptor(a).CodeChallenge, Challenge(request.CodeVerifier)) && Same(a.DescriptorHash, request.DescriptorHash), "invalid-pairing-proof", "The pairing proof is invalid or expired.", 403);
    }
    public async Task<ServiceLinkReviewResponse> ReviewAsync(string attemptId, ServiceLinkReviewRequest request, CancellationToken ct)
    { var a = await Attempt(attemptId, ct); ReviewProof(a, request); return new(Summary(a), a.GrantHash!, "approved"); }

    public async Task<ServiceLinkReviewResponse> CallbackAsync(ServiceLinkCallbackRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        var a = await Attempt(request.AttemptId, ct); var actorId = await Authorize(actor, a.LocalTenantId, ct);
        Require(a.Role == "initiator" && actorId == a.LocalActorId && a.ExpiresAtUnixSeconds > Now && a.Decision == "undecided" && a.LifecycleState is "awaiting_approval" or "approved" or "prepared" or "verified" && Same(a.SessionBindingHash, Digest(request.SessionBinding)) &&
            Same(Unprotect(a, "browser-state", a.ProtectedBrowserState!), request.BrowserState) && request.ResponderInstanceId == a.PeerInstanceId && request.OauthIssuer == Peer(a).OauthIssuer,
            "invalid-browser-proof", "The callback does not match its initiating browser session or approved peer.", 403);
        if (a.GrantHash is not null)
        {
            Require(a.ProtectedPairingCode is not null && Same(Unprotect(a, "pairing-code", a.ProtectedPairingCode), request.PairingCode), "invalid-browser-proof", "The callback proof differs from the already reviewed attempt.", 403);
            return new(Summary(a), a.GrantHash, a.LifecycleState);
        }
        a.ProtectedPairingCode = Protect(a, "pairing-code", request.PairingCode); await Save(a, ct);
        var review = await transport.PostAsync<ServiceLinkReviewResponse>(Endpoint(Peer(a).ServiceLinkEndpoint, "/attempts/" + a.AttemptId + "/review"), new ServiceLinkReviewRequest(ServiceLinkContract.Version, a.AttemptId, request.PairingCode, Unprotect(a, "verifier", a.ProtectedVerifier!), a.DescriptorHash), ct);
        var summary = review.GrantSummary; var d = Descriptor(a);
        ServiceLinkValidation.Metadata(summary.InitiatorEndpointSnapshot, settings.AllowPrivateHttp);
        ServiceLinkValidation.Metadata(summary.ResponderEndpointSnapshot, settings.AllowPrivateHttp);
        var selected = Grants(summary.Grants, d.InitiatorEndpointSnapshot, d.ResponderEndpointSnapshot);
        SelectedTenants(selected, a.LocalTenantId, selected.Single(grant => grant.DirectionId == ServiceLinkContract.InitiatorToResponder).TargetTenantId);
        Require(d.InitiatorTenantId == a.LocalTenantId && (string.IsNullOrEmpty(d.RequestedResponderTenantId) ||
            d.RequestedResponderTenantId == selected.Single(grant => grant.DirectionId == ServiceLinkContract.InitiatorToResponder).TargetTenantId),
            "tenant-pair-mismatch", "The peer selection differs from the initiating administrator's retained tenant ceiling.");
        Require(review.LifecycleState == "approved" && summary.Contract == ServiceLinkContract.Version && summary.AttemptId == a.AttemptId && summary.DescriptorHash == a.DescriptorHash &&
            summary.ProposedLinkRevision == a.LinkRevision && summary.InitiatorInstanceId == issuer.InstanceId && summary.ResponderInstanceId == a.PeerInstanceId &&
            ServiceLinkPayloadNormalization.SummaryHashMatches(summary, review.GrantHash) &&
            ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(summary.InitiatorEndpointSnapshot)) == ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(d.InitiatorEndpointSnapshot)) && ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(summary.ResponderEndpointSnapshot)) == ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Metadata(d.ResponderEndpointSnapshot)), "grant-binding-mismatch", "The selected peer summary differs from the pinned proposal.");
        Id(summary.LinkId);
        foreach (var g in selected) Narrowed(d.RequestedGrants.Single(x => x.DirectionId == g.DirectionId), g);
        var expiry = ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(summary.ExpiresAt).ToUnixTimeSeconds();
        Require(expiry <= a.ExpiresAtUnixSeconds && expiry > Now, "attempt-expired", "The peer selected an invalid bootstrap expiry.");
        Require(a.GrantHash is null || a.GrantHash == review.GrantHash, "grant-conflict", "The immutable reviewed grant changed.", 409);
        if (ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Summary(summary)) == review.GrantHash)
            summary = ServiceLinkPayloadNormalization.Summary(summary);
        a.LinkId = summary.LinkId; a.GrantSummaryJson = Json(summary); a.GrantHash = review.GrantHash; a.ExpiresAtUnixSeconds = expiry;
        a.PeerTenantId = selected.Single(x => x.TargetInstanceId == a.PeerInstanceId).TargetTenantId; a.LifecycleState = "approved";
        a.ActiveRelationshipKey = Digest(a.LocalTenantId + "\n" + a.PeerInstanceId + "\n" + a.PeerTenantId);
        await Save(a, ct); return review with { GrantSummary = summary };
    }

    public async Task<ServiceLinkAdminStatus> LocalApproveAsync(string attemptId, ServiceLinkLocalApproveRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        var a = await Attempt(attemptId, ct); var actorId = await Authorize(actor, a.LocalTenantId, ct);
        Require(a.Role == "initiator" && a.LocalActorId == actorId && a.ExpiresAtUnixSeconds > Now && a.Decision == "undecided" && a.LifecycleState is "approved" or "prepared" or "verified" && Same(a.SessionBindingHash, Digest(request.SessionBinding)) && a.GrantHash == request.GrantHash, "invalid-local-consent", "The exact reviewed grant and initiating session are required.", 403);
        var inbound = InboundGrant(a);
        Require(inbound.TargetTenantId == a.LocalTenantId, "local-grant-mismatch", "The approved inbound organization changed.");
        var approved = Summary(a); var descriptor = Descriptor(a);
        Require(ServiceLinkPayloadNormalization.SummaryHashMatches(approved, a.GrantHash!), "grant-binding-mismatch", "The reviewed immutable grant changed.", 409);
        var approvedGrants = Grants(approved.Grants, descriptor.InitiatorEndpointSnapshot, descriptor.ResponderEndpointSnapshot);
        SelectedTenants(approvedGrants, a.LocalTenantId, approvedGrants.Single(grant => grant.DirectionId == ServiceLinkContract.InitiatorToResponder).TargetTenantId);
        Require(descriptor.InitiatorTenantId == a.LocalTenantId, "tenant-pair-mismatch", "The final local consent differs from its retained initiating tenant.");

        if (a.InboundPrincipalId is null)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            a.ConsentId = NewId(); await CreateInbound(a, actorId, ct); a.NextWorkAtUnixSeconds = Now; await Save(a, ct); await tx.CommitAsync(ct);
        }
        return await AdminStatus(a, ct);
    }

    public async Task<ServiceLinkExchangeResponse> ExchangeAsync(string attemptId, ServiceLinkExchangeRequest request, CancellationToken ct)
    {
        var a = await Attempt(attemptId, ct); ReviewProof(a, request);
        Require(request.GrantHash == a.GrantHash && request.InitiatorConsentId.Length is >= 16 and <= 128, "grant-binding-mismatch", "The initiator's durable consent binding is missing.", 403);
        Credential(request.CredentialForResponder, OutboundGrant(a), Peer(a));
        var normalized = ServiceLinkPayloadNormalization.Exchange(request);
        var fingerprint = ServiceLinkCanonicalJson.HashObject(normalized);
        if (a.ExchangeFingerprint is not null)
        {
            Require(ExchangeFingerprintMatches(a, request, normalized), "exchange-payload-conflict", "The accepted exchange body changed.", 409);
            Require(a.ProtectedExchangeResponse is not null, "handoff-unavailable", "The finite handoff has expired; recover the retained coordinator decision.", 410);
            return Read<ServiceLinkExchangeResponse>(Unprotect(a, "exchange-response", a.ProtectedExchangeResponse));
        }
        request = normalized;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        a.ProtectedOutboundCredential = Protect(a, "outbound-credential", Json(request.CredentialForResponder));
        a.ExchangeFingerprint = fingerprint; a.LifecycleState = "prepared";
        var response = new ServiceLinkExchangeResponse(ServiceLinkContract.Version, a.AttemptId, a.LinkId!, a.LinkRevision, a.GrantHash!, "prepared", ServiceLinkPayloadNormalization.Credential(Read<ServiceDirectionalCredential>(Unprotect(a, "inbound-escrow", a.ProtectedInboundEscrow!))));
        a.ExchangeResponseHash = ServiceLinkCanonicalJson.HashObject(response); a.ProtectedExchangeResponse = Protect(a, "exchange-response", Json(response));
        await StageOutbound(a, request.CredentialForResponder, ct); a.NextWorkAtUnixSeconds = Now; await Save(a, ct); await tx.CommitAsync(ct); return response;
    }
}
