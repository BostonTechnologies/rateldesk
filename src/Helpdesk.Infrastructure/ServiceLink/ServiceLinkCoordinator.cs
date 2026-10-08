using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Build;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator(
    HelpdeskDbContext db, IServicePrincipalRegistry registry, ICurrentUserAccessService accessService,
    IIntegrationProviderSettingsService providers, ServiceLinkTransport transport, IDataProtectionProvider protection,
    IOptions<ServiceLinkOptions> options, IOptionsMonitor<ServiceIdentityOptions> identityOptions, TimeProvider clock,
    ServiceLinkProtocolTokenCache protocolTokens,
    IOptionsMonitor<ServiceLinkOptions>? currentOptions = null,
    IServiceScopeFactory? scopes = null,
    IServicePublicSettingsResolver? publicSettings = null)
{
    private ServicePublicSettingsEffective? _publicSettings;
    private ServiceLinkOptions settings => _publicSettings?.Linking ?? currentOptions?.CurrentValue ?? options.Value;
    private ServiceIdentityOptions issuer => _publicSettings?.Identity ?? identityOptions.CurrentValue;
    public async Task RefreshSettingsAsync(CancellationToken ct)
    {
        if (publicSettings is not null) _publicSettings = await publicSettings.ResolveAsync(ct);
    }
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Read<T>(string value) => ServiceLinkCanonicalJson.Deserialize<T>(value);
    private IDataProtector Protector(ServiceLinkAttempt a, string purpose) => protection.CreateProtector("RatelDesk.ServiceLink.v1", a.AttemptId, a.PeerInstanceId, purpose,
        purpose is "verifier" or "browser-state" or "pairing-code" ? "bootstrap" : a.LocalTenantId + "/" + a.LinkId + "/" + a.GrantHash + "/" + a.LinkRevision);
    private string Protect(ServiceLinkAttempt a, string purpose, string clear) => Protector(a, purpose).Protect(clear);
    private string Unprotect(ServiceLinkAttempt a, string purpose, string cipher) => Protector(a, purpose).Unprotect(cipher);
    private async Task Save(ServiceLinkAttempt a, CancellationToken ct) { a.Revision++; a.UpdatedAtUnixSeconds = Now; await db.SaveChangesAsync(ct); }
    private async Task<ServiceLinkAttempt> Attempt(string id, CancellationToken ct) => await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.AttemptId == id, ct) ?? throw new ServiceLinkProtocolException(404, "attempt-not-found", "The attempt does not exist.");
    private async Task<ServiceLinkAttempt> Link(string id, CancellationToken ct) => await db.Set<ServiceLinkAttempt>().SingleOrDefaultAsync(x => x.LinkId == id, ct) ?? throw new ServiceLinkProtocolException(404, "link-not-found", "The approved link does not exist.");
    private static ServiceLinkRequestDescriptor Descriptor(ServiceLinkAttempt a) => Read<ServiceLinkRequestDescriptor>(a.DescriptorJson);
    private static ServiceLinkGrantSummary Summary(ServiceLinkAttempt a) => Read<ServiceLinkGrantSummary>(a.GrantSummaryJson ?? throw new ServiceLinkProtocolException(409, "grant-not-approved", "The exact grant is not approved."));
    private ServiceLinkMetadata Local(ServiceLinkAttempt a) => a.Role == "initiator" ? Descriptor(a).InitiatorEndpointSnapshot : Descriptor(a).ResponderEndpointSnapshot;
    private ServiceLinkMetadata Peer(ServiceLinkAttempt a) => a.Role == "initiator" ? Descriptor(a).ResponderEndpointSnapshot : Descriptor(a).InitiatorEndpointSnapshot;
    private ServiceLinkGrant InboundGrant(ServiceLinkAttempt a) => Summary(a).Grants.Single(x => x.DirectionId == (a.Role == "initiator" ? ServiceLinkContract.ResponderToInitiator : ServiceLinkContract.InitiatorToResponder));
    private ServiceLinkGrant OutboundGrant(ServiceLinkAttempt a) => Summary(a).Grants.Single(x => x.DirectionId == (a.Role == "initiator" ? ServiceLinkContract.InitiatorToResponder : ServiceLinkContract.ResponderToInitiator));

    public ServiceLinkMetadata Metadata()
    {
        var settings = this.settings;
        var issuer = this.issuer;
        Require(settings.Enabled && issuer.Enabled, "service-link-unavailable", "The deployment has not enabled its configured service issuer and reciprocal linking.", 503);
        Require(issuer.ApiBaseUrl.TrimEnd('/') == settings.ApiBaseUrl.TrimEnd('/') && issuer.WebBaseUrl.TrimEnd('/') == settings.WebBaseUrl.TrimEnd('/'), "service-link-configuration-invalid", "Issuer and link canonical addresses must agree.", 503);
        return ServiceLinkPayloadNormalization.Metadata(new ServiceLinkMetadata
        {
            ProductVersion = BuildInfoProvider.FromAssembly(typeof(ServiceLinkCoordinator).Assembly, "runtime").Version,
            InstanceId = issuer.InstanceId, WebBaseUrl = settings.WebBaseUrl.TrimEnd('/'), ApiBaseUrl = settings.ApiBaseUrl.TrimEnd('/'), GatewayBaseUrl = settings.GatewayBaseUrl,
            OauthIssuer = issuer.Issuer, OauthMetadataUrl = Endpoint(settings.ApiBaseUrl, "/.well-known/oauth-authorization-server"),
            TokenEndpoint = Endpoint(settings.ApiBaseUrl, "/connect/token"), JwksUri = Endpoint(settings.ApiBaseUrl, "/.well-known/jwks.json"), Audience = issuer.Audience,
            ServiceLinkEndpoint = Endpoint(settings.ApiBaseUrl, ServiceLinkContract.EndpointPath),
            ApprovalEndpoint = Endpoint(settings.WebBaseUrl, "/account/integration-credentials/link/approve"), CallbackEndpoint = Endpoint(settings.WebBaseUrl, "/account/integration-credentials/link/callback"),
            PermissionProfiles =
            [
                new("rateldesk.incident-create.v1", [ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets, ServiceIdentityScopes.IncidentCreate],
                [new("POST", "/api/v1/incidents/", ServiceIdentityScopes.IncidentCreate), new("GET", "/api/v1/integrations/netratel/capabilities", ServiceIdentityScopes.IncidentReceipts), new("GET", "/api/v1/integrations/netratel/incident-receipts/{key}", ServiceIdentityScopes.IncidentReceipts), new("POST", "/api/v1/integrations/netratel/targets/validate", ServiceIdentityScopes.IncidentTargets)]),
                new("rateldesk.orchestration.callback.v1", [ServiceIdentityScopes.Callback], [new("POST", "/api/v1/orchestration/provider/callback", ServiceIdentityScopes.Callback), new("GET", "/api/v1/orchestration/provider/m2m/ping", ServiceIdentityScopes.Callback)]),
                new(ServiceLinkContract.IncidentOnlyCapability, [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope],
                [new("POST", ServiceLinkContract.EndpointPath + "/links/{link_id}/verify", ServiceLinkContract.VerifyScope), new("GET", ServiceLinkContract.EndpointPath + "/links/{link_id}/status", ServiceLinkContract.ControlScope)]),
                new(ServiceLinkContract.Version, [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope],
                [new("POST", ServiceLinkContract.EndpointPath + "/links/{link_id}/verify", ServiceLinkContract.VerifyScope), new("GET", ServiceLinkContract.EndpointPath + "/links/{link_id}/status", ServiceLinkContract.ControlScope), .. new[] { "ack", "commit", "abort", "revoke", "rotate" }.Select(x => new ServiceLinkResourceOperation("POST", ServiceLinkContract.EndpointPath + "/links/{link_id}/" + x, ServiceLinkContract.ControlScope))])
            ]
        });
    }

    private async Task<string> Authorize(ClaimsPrincipal actor, string organization, CancellationToken ct)
    {
        Require(actor.Identity?.IsAuthenticated == true && actor.FindFirst("integration_credential_id") is null && actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null, "administrator-required", "A product administrator session is required.", 403);
        var access = await accessService.ResolveAsync(actor, ct);
        Require(access.IsHelpdeskAdmin || access.ManagedOrganizationIds.Contains(organization), "administrator-required", "The selected organization requires its current administrative authority.", 403);
        Require(await db.Organizations.IgnoreQueryFilters().AnyAsync(x => x.Id == organization && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct), "organization-disabled", "An enabled local organization is required.", 403);
        return actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub") ?? throw new ServiceLinkProtocolException(403, "administrator-required", "The approving actor is unidentified.");
    }

    public async Task<ServiceLinkRequestDescriptor> PublicDescriptorAsync(string attemptId, CancellationToken ct)
    {
        Id(attemptId); var a = await Attempt(attemptId, ct);
        Require(a.Role == "initiator" && a.ExpiresAtUnixSeconds > Now && a.Decision == "undecided" && a.LifecycleState is not ("revoked" or "failed"), "attempt-expired", "The public descriptor is unavailable.", 404);
        return Descriptor(a);
    }

    private async Task<Guid> ReserveSource(Guid producer, string org, string customer, string actor, CancellationToken ct)
    {
        Require(await db.Customers.IgnoreQueryFilters().AnyAsync(x => x.Id == customer && x.OrganizationId == org && x.State == Helpdesk.Shared.Models.EntityState.Enabled, ct), "invalid-customer", "The selected local customer is not enabled in this organization.", 403);
        var existing = await db.IncidentReceiverSources.SingleOrDefaultAsync(x => x.SourceInstanceId == producer, ct);
        if (existing is not null)
        {
            Require(existing.IsEnabled && existing.OrganizationId == org && existing.CustomerId == customer, "source-mapping-conflict", "The stable producer already owns a disabled or different approved receiver mapping.", 409);
            return existing.SourceNamespaceId;
        }
        var source = new IncidentReceiverSource { SourceNamespaceId = Guid.NewGuid(), SourceInstanceId = producer, OrganizationId = org, CustomerId = customer, IsEnabled = true, CreatedBy = actor, UpdatedBy = actor, CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow() };
        db.IncidentReceiverSources.Add(source); await db.SaveChangesAsync(ct); return source.SourceNamespaceId;
    }

    private async Task<CreatedServiceClient> CreateInbound(ServiceLinkAttempt a, string actor, CancellationToken ct)
    {
        var g = InboundGrant(a);
        var created = await registry.CreatePendingAsync(new ServiceClientCreateRequest("NetRatel reciprocal service", g.TargetTenantId, g.CallerInstanceId, g.CallerTenantId, g.Scopes, g.ResourceConstraints.CustomerIds,
            Guid.Parse(g.SourceInstanceId!), Guid.Parse(g.SourceNamespaceId!), a.LinkId, a.AttemptId, a.GrantHash, a.DescriptorHash, g.DirectionId, a.LinkRevision, Json(g.ResourceConstraints)), actor, ct);
        a.InboundPrincipalId = created.Principal.Id;
        var sourceId = Guid.Parse(g.SourceNamespaceId!);
        var binding = await db.IncidentReceiverPrincipalBindings.SingleOrDefaultAsync(x => x.SourceNamespaceId == sourceId && x.PrincipalKind == "service_principal" && x.PrincipalId == created.Principal.Id.ToString("N"), ct);
        if (binding is null) db.IncidentReceiverPrincipalBindings.Add(new IncidentReceiverPrincipalBinding { SourceNamespaceId = sourceId, PrincipalKind = "service_principal", PrincipalId = created.Principal.Id.ToString("N"), IsEnabled = false });
        a.ProtectedInboundEscrow = Protect(a, "inbound-escrow", Json(CredentialFrom(created, g, Local(a))));
        return created;
    }

    private static ServiceDirectionalCredential CredentialFrom(CreatedServiceClient created, ServiceLinkGrant g, ServiceLinkMetadata target) => new()
    {
        ClientId = created.Principal.ClientId, ClientSecret = created.ClientSecret, CredentialRevision = created.CredentialRevision,
        Issuer = g.Issuer, TokenEndpoint = target.TokenEndpoint, Audience = g.Audience, Scopes = g.Scopes,
        CallerInstanceId = g.CallerInstanceId, CallerTenantId = g.CallerTenantId, TargetInstanceId = g.TargetInstanceId, TargetTenantId = g.TargetTenantId
    };

    private async Task<ServiceLinkAdminStatus> AdminStatus(ServiceLinkAttempt a, ClaimsPrincipal actor, CancellationToken ct)
    {
        var settings = this.settings;
        var issuer = this.issuer;
        var inbound = await ServiceLinkAuthority.InboundUsableAsync(db, a, clock, issuer, settings, ct);
        var provider = await providers.GetOrchestratorSettingsAsync(ct);
        var sender = SenderUsable(a, provider, inbound);
        var customerIds = a.GrantSummaryJson is null ? Array.Empty<string>() : InboundGrant(a).ResourceConstraints.CustomerIds;
        var invalidOrganization = !string.IsNullOrEmpty(a.LocalTenantId) &&
            !await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == a.LocalTenantId && o.State == Helpdesk.Shared.Models.EntityState.Enabled, ct);
        var originalActor = a.LocalActorId == (actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub"));
        var terminal = a.LifecycleState is "expired" or "failed" or "revoked";
        var expiredUnprepared = a.Decision != "commit" && a.ExpiresAtUnixSeconds <= Now && a.InboundPrincipalId is null &&
            a.ProtectedOutboundCredential is null && !a.ExchangeDispatched && a.LifecycleState is "awaiting_approval" or "approved" or "expired" or "failed";
        var action = "none";
        if (!terminal && !invalidOrganization && !expiredUnprepared && a.LifecycleState != "active")
        {
            if (originalActor && a.Decision == "undecided" && a.LifecycleState == "awaiting_approval" && a.GrantSummaryJson is null && a.ProtectedBrowserState is not null)
                action = a.Role == "initiator" ? "continue" : "respond";
            else if (originalActor && a.Role == "initiator" && a.Decision == "undecided" && a.GrantSummaryJson is not null && a.InboundPrincipalId is null)
                action = "review";
            else if (a.InboundPrincipalId is not null || a.ProtectedOutboundCredential is not null || a.ExchangeDispatched)
                action = "resume";
        }
        return new(a.AttemptId, a.LinkId, a.LinkRevision, a.LifecycleState,
            a.LocalTenantId, a.PeerInstanceId, a.PeerTenantId, a.Decision, a.CommitId, a.GrantHash, Descriptor(a), a.GrantSummaryJson is null ? null : Summary(a),
            a.InboundPrincipalId is not null, a.ProtectedOutboundCredential is not null, inbound, sender, a.PeerActiveAcknowledged, EffectiveError(a, inbound, sender),
            provider.ManagedByDeployment, await RotationSummaries(a, ct))
            { LocalRole = a.Role, AvailableAction = action, OrganizationBindingInvalid = invalidOrganization,
                CanCancel = a.Decision is not ("commit" or "abort") && a.LifecycleState != "revoked",
                CanStartFresh = a.LifecycleState is "expired" or "revoked" || expiredUnprepared ||
                    a.LifecycleState == "failed" && a.InboundPrincipalId is null && a.ProtectedOutboundCredential is null && !a.ExchangeDispatched,
                LocalTenantName = await db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == a.LocalTenantId).Select(o => o.Name).SingleOrDefaultAsync(ct),
                LocalCustomerName = a.GrantSummaryJson is null ? null : await db.Customers.IgnoreQueryFilters().AsNoTracking()
                    .Where(c => customerIds.Contains(c.Id) && c.OrganizationId == a.LocalTenantId)
                    .Select(c => c.Name).SingleOrDefaultAsync(ct),
                AutomaticRotationEnabled = settings.AutomaticRotationEnabled, RotationAgeDays = settings.RotationAgeDays, RotationOverlapSeconds = settings.RotationOverlapSeconds };
    }
    private static bool SenderUsable(ServiceLinkAttempt a, Helpdesk.Shared.DTOs.Orchestration.OrchestrationConnectivitySettingsDto provider, bool inbound) =>
        inbound && a.LocalBusinessSenderEnabled && a.PeerActiveAcknowledged && provider.Enabled && provider.ManagedSenderEnabled && provider.LinkId == a.LinkId &&
        provider.LinkRevision == a.LinkRevision && provider.LocalTenantId == a.LocalTenantId && provider.PeerInstanceId == a.PeerInstanceId && provider.PeerTenantId == a.PeerTenantId;
    private static bool ControlOnlyOutbound(ServiceLinkAttempt a) => a.GrantSummaryJson is not null &&
        IncidentOnlyGrant(Summary(a).Grants.Single(g => g.DirectionId ==
            (a.Role == "initiator" ? ServiceLinkContract.InitiatorToResponder : ServiceLinkContract.ResponderToInitiator)));
    private static string? EffectiveError(ServiceLinkAttempt a, bool inbound, bool sender) => a.LastErrorCode ??
        (a.Decision == "commit" && a.LifecycleState == "active" && (!inbound || !sender && !ControlOnlyOutbound(a)) ? "grant-unavailable" : null);

    public async Task<ServiceLinkAdminStatus> AdminStatusAsync(string attemptId, ClaimsPrincipal actor, CancellationToken ct)
    { await RefreshSettingsAsync(ct); var a = await Attempt(attemptId, ct); await AuthorizeInspection(a, actor, ct); return await AdminStatus(a, actor, ct); }

    // This exception is restricted to reading or terminally cancelling an
    // unbound, never-consented responder proposal. It never authorizes resume,
    // approval, credential exchange, revocation or a replacement organization.
    private async Task<bool> CanInspectUnboundProposal(ServiceLinkAttempt a, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (actor.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(a.LocalActorId) || actor.FindFirst("integration_credential_id") is not null ||
            actor.FindFirst(ServiceIdentityClaims.PrincipalId) is not null ||
            a.LocalActorId != (actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub")) ||
            a.Role != "responder" || a.Decision is not ("undecided" or "abort") ||
            a.LifecycleState is not ("awaiting_approval" or "expired" or "failed") ||
            a.GrantSummaryJson is not null || a.GrantHash is not null || a.ConsentId is not null || a.LinkId is not null ||
            a.InboundPrincipalId is not null || a.ProtectedInboundEscrow is not null || a.ProtectedOutboundCredential is not null ||
            a.OutboundProfileRevision is not null || a.ExchangeDispatched || a.ExchangeFingerprint is not null ||
            a.ProtectedExchangeResponse is not null || a.ExchangeResponseHash is not null || a.CommitId is not null ||
            a.ActiveRelationshipKey is not null || a.PairingCodeHash is not null || a.ProtectedPairingCode is not null ||
            a.InitiatorVerificationReceiptId is not null || a.ResponderVerificationReceiptId is not null || a.RevocationId is not null ||
            a.LocalPreparedAcknowledged || a.PeerPreparedAcknowledged || a.LocalInboundActive || a.LocalBusinessSenderEnabled ||
            a.LocalActiveAcknowledged || a.PeerActiveAcknowledged) return false;
        var access = await accessService.ResolveAsync(actor, ct);
        var absentOrganization = string.IsNullOrEmpty(a.LocalTenantId);
        if (!(absentOrganization ? access.IsHelpdeskAdmin || access.ManagedOrganizationIds.Count > 0 : access.IsHelpdeskAdmin)) return false;
        if (!absentOrganization && await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == a.LocalTenantId && o.State == Helpdesk.Shared.Models.EntityState.Enabled, ct)) return false;
        var descriptor = Descriptor(a);
        return descriptor.AttemptId == a.AttemptId && descriptor.DescriptorHash == a.DescriptorHash &&
            (descriptor.RequestedResponderTenantId ?? "") == a.LocalTenantId &&
            ServiceLinkPayloadNormalization.DescriptorHashMatches(descriptor, a.DescriptorHash) &&
            !await db.Set<ServicePrincipalRegistration>().AnyAsync(p => p.AttemptId == a.AttemptId, ct) &&
            !await db.Set<ServiceLinkVerificationReceipt>().AnyAsync(r => r.AttemptId == a.AttemptId, ct) &&
            // Supported exchange/lifecycle journals are keyed by the approved
            // LinkId (which must be absent above); also reject an attempt-keyed
            // journal rather than treating unexpected retained work as empty.
            !await db.Set<ServiceLinkOperation>().AnyAsync(o => o.LinkId == a.AttemptId || o.LinkId == a.LinkId, ct);
    }

    private async Task AuthorizeInspection(ServiceLinkAttempt a, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (!await CanInspectUnboundProposal(a, actor, ct)) await AuthorizeAttempt(a, actor, ct);
    }
    private async Task AuthorizeAttempt(ServiceLinkAttempt a, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(a.LocalTenantId)) { await Authorize(actor, a.LocalTenantId, ct); return; }
        var access = await accessService.ResolveAsync(actor, ct); var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub");
        Require(a.Role == "responder" && a.LifecycleState == "awaiting_approval" && a.LocalActorId == actorId && actor.FindFirst("integration_credential_id") is null && actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null && (access.IsHelpdeskAdmin || access.ManagedOrganizationIds.Count > 0), "administrator-required", "The initiating local administrator is required to review this unapproved descriptor.", 403);
    }
    public async Task<ServiceLinkAdminStatus[]> AdminListAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        await RefreshSettingsAsync(ct);
        var access = await accessService.ResolveAsync(actor, ct);
        Require(actor.Identity?.IsAuthenticated == true && actor.FindFirst("integration_credential_id") is null && actor.FindFirst(ServiceIdentityClaims.PrincipalId) is null && (access.IsHelpdeskAdmin || access.ManagedOrganizationIds.Count > 0), "administrator-required", "Administrator authority is required.", 403);
        var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub");
        var all = await db.Set<ServiceLinkAttempt>().Where(x => access.IsHelpdeskAdmin || access.ManagedOrganizationIds.Contains(x.LocalTenantId) ||
            x.LocalTenantId == "" && x.Role == "responder" && x.LocalActorId == actorId).OrderByDescending(x => x.CreatedAtUnixSeconds).Take(100).ToListAsync(ct);
        var result = new List<ServiceLinkAdminStatus>();
        foreach (var a in all)
        {
            try { await AuthorizeInspection(a, actor, ct); }
            catch (ServiceLinkProtocolException error) when (error.StatusCode == 403) { continue; }
            result.Add(await AdminStatus(a, actor, ct));
        }
        return result.ToArray();
    }

    private async Task<IReadOnlyList<ServiceLinkRotationSummary>> RotationSummaries(ServiceLinkAttempt a, CancellationToken ct) => (await db.Set<ServiceLinkRotation>().Where(x => x.LinkId == a.LinkId).ToListAsync(ct)).Select(x => new ServiceLinkRotationSummary(x.RotationId, x.DirectionId, x.RotationState, x.ExpectedCurrentCredentialRevision, x.SuccessorCredentialRevision, x.OfferExpiresAtUnixSeconds is null ? null : Timestamp(x.OfferExpiresAtUnixSeconds.Value), x.SuccessorVerificationReceiptId, x.ActivateDecisionId, x.CallerSwitchRevision, x.PredecessorRetireAtUnixSeconds is null ? null : Timestamp(x.PredecessorRetireAtUnixSeconds.Value))).ToArray();
}
