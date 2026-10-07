using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Shared.ServiceLink;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    public async Task<ServiceLinkTestResult> TestAsync(string linkId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await RefreshSettingsAsync(ct);
        var a = await Link(linkId, ct);
        await Authorize(actor, a.LocalTenantId, ct);
        if (a.LifecycleState != "active" || a.ProtectedOutboundCredential is null)
            return new(false, false, false, "connection-pending");
        var credential = Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential));
        Credential(credential, OutboundGrant(a), Peer(a));
        var token = await ProtocolToken(a, ServiceLinkContract.ControlScope, "status", null, credential, ct);
        // Existing protected credentials and a bounded authenticated GET. No lifecycle command or incident/task creation.
        using var peerStatus = await transport.GetAsync<JsonDocument>(Endpoint(Peer(a).ServiceLinkEndpoint,
            "/links/" + linkId + "/status"), ct, token);
        var status = peerStatus.RootElement;
        ValidatePeerResult(a, status);
        var acknowledged = a.PeerActiveAcknowledged && a.CommitId is not null &&
            String(status, "commit_id") == a.CommitId && String(status, "lifecycle_state") == "active" &&
            Boolean(status, "local_inbound_active");
        var local = await ServiceLinkAuthority.InboundUsableAsync(db, a, clock, issuer, settings, ct);
        var ready = acknowledged && local && Boolean(status, "local_business_sender_enabled") &&
            Boolean(status, "incident_delivery_ready");
        return new(true, acknowledged, ready,
            !acknowledged ? "peer-acknowledgement-pending" : !local ? "grant-unavailable" : !ready ? "connector-readiness-required" : null);
    }
}
