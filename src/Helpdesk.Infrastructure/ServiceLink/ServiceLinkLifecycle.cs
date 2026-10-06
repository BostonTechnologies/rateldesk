using System.Data;
using System.Security.Claims;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    private async Task<(ServiceLinkAttempt Attempt, ServicePrincipalRegistration Principal)> Bound(string linkId, ClaimsPrincipal caller, string scope, CancellationToken ct)
    {
        Require(caller.FindFirstValue(ServiceIdentityClaims.LinkId) == linkId, "link-not-authorized", "This token does not authorize the requested link.", 403);
        var principal = await registry.ResolvePrincipalAsync(caller, scope, ct);
        Require(principal is not null && principal.LinkId == linkId, "link-not-authorized", "Current bound service authority is required.", 403);
        var a = await Link(linkId, ct);
        Require(a.InboundPrincipalId == principal!.Id && principal.GrantHash == a.GrantHash && principal.LinkRevision == a.LinkRevision && principal.AttemptId == a.AttemptId && principal.PeerInstanceId == a.PeerInstanceId && principal.PeerTenantId == a.PeerTenantId, "link-not-authorized", "The token's approved peer/grant binding changed.", 403);
        return (a, principal);
    }
    private Dictionary<string, object?> Common(ServiceLinkAttempt a) => new()
    {
        ["contract"] = ServiceLinkContract.Version, ["link_id"] = a.LinkId, ["link_revision"] = a.LinkRevision,
        ["grant_hash"] = a.GrantHash, ["lifecycle_state"] = a.LifecycleState
    };
    private async Task<Dictionary<string, object?>> Status(ServiceLinkAttempt a, CancellationToken ct)
    {
        var inbound = await ServiceLinkAuthority.InboundUsableAsync(db, a, clock, issuer, settings, ct);
        var sender = SenderUsable(a, await providers.GetOrchestratorSettingsAsync(ct), inbound);
        var result = Common(a);
        result["coordinator_instance_id"] = Descriptor(a).InitiatorInstanceId; result["attempt_id"] = a.AttemptId;
        result["decision"] = a.Decision; result["commit_id"] = a.CommitId; result["abort_id"] = a.AbortId;
        result["descriptor_hash"] = a.DescriptorHash; result["local_inbound_ready"] = a.InboundPrincipalId is not null;
        result["local_outbound_persisted"] = a.ProtectedOutboundCredential is not null; result["local_inbound_active"] = inbound;
        result["local_business_sender_enabled"] = sender; result["peer_active_acknowledged"] = a.PeerActiveAcknowledged;
        result["initiator_verification_receipt_id"] = a.InitiatorVerificationReceiptId;
        result["responder_verification_receipt_id"] = a.ResponderVerificationReceiptId;
        result["rotations"] = await RotationSummaries(a, ct); return result;
    }
    public async Task<object> StatusAsync(string linkId, ClaimsPrincipal caller, CancellationToken ct)
    { var (a, _) = await Bound(linkId, caller, ServiceLinkContract.ControlScope, ct); await Expire(a, ct); return await Status(a, ct); }

    public Task<object> LifecycleAsync(string pathLinkId, string kind, ServiceLinkLifecycleRequest request, ClaimsPrincipal caller, CancellationToken ct) =>
        RetrySerializableLifecycle(coordinator => coordinator.LifecycleOnceAsync(pathLinkId, kind, request, caller, ct), ct);

    private bool HasDatabaseTransaction => db.Database.CurrentTransaction is not null;

    private async Task<object> RetrySerializableLifecycle(Func<ServiceLinkCoordinator, Task<object>> action, CancellationToken ct)
    {
        // Only this local recipient operation is retried. Never wrap worker,
        // administrator, exchange or peer HTTP commands with this boundary.
        if (scopes is null || HasDatabaseTransaction || System.Transactions.Transaction.Current is not null)
            return await action(this);
        AsyncServiceScope? attemptScope = null;
        var coordinator = this;
        try
        {
            for (var retry = 0; ; retry++)
            {
                ct.ThrowIfCancellationRequested();
                // Even the first attempt uses a complete fresh scope, so caller
                // tracking cannot enter this atomic recipient operation.
                attemptScope = scopes.CreateAsyncScope();
                coordinator = attemptScope.Value.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>();
                try { return await action(coordinator); }
                catch (Exception error) when (retry < 3 && !ct.IsCancellationRequested &&
                    !coordinator.HasDatabaseTransaction && System.Transactions.Transaction.Current is null &&
                    IsSerializationFailure(error))
                {
                    // LifecycleOnce's Serializable transaction has rolled back
                    // and disposed. Drop ALL failed scoped state before rereading
                    // the same request/operation and current authority next time.
                    var failed = attemptScope.Value;
                    attemptScope = null;
                    await failed.DisposeAsync();
                }
            }
        }
        finally
        {
            if (attemptScope is { } final) await final.DisposeAsync();
        }
    }

    private static bool IsSerializationFailure(Exception error)
    {
        // Only a genuine PostgreSQL 40001 through these known EF wrappers proves
        // this transaction aborted. Business, unique/constraint, auth, cancellation,
        // optimistic-fence and deadlock failures retain their existing behavior.
        for (var depth = 0; depth < 4; depth++)
        {
            if (error is PostgresException postgres)
                return postgres.SqlState == PostgresErrorCodes.SerializationFailure;
            if (error is not (InvalidOperationException or DbUpdateException) || error.InnerException is null)
                return false;
            error = error.InnerException;
        }
        return false;
    }

    private async Task<object> LifecycleOnceAsync(string pathLinkId, string kind, ServiceLinkLifecycleRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        var (a, principal) = await Bound(pathLinkId, caller, kind == "verify" ? ServiceLinkContract.VerifyScope : ServiceLinkContract.ControlScope, ct);
        Id(request.OperationId);
        Require(request.Contract == ServiceLinkContract.Version && request.LinkId == pathLinkId && request.LinkRevision == a.LinkRevision && request.GrantHash == a.GrantHash &&
            (request.AttemptId is null || request.AttemptId == a.AttemptId), "lifecycle-binding-conflict", "The operation differs from its exact approved attempt/grant revision.", 409);
        ValidateFields(kind, request);
        if (request.WirePropertyNames is not null) Require(request.WirePropertyNames.SetEquals(ServiceLinkLifecycleProjection.Build(kind, request).Keys), "invalid-operation-fields", "The wire body must contain exactly its frozen lifecycle phase fields.");
        if (a.LifecycleState is "revoked" or "revocation_pending") Require(kind == "revoke" || kind == "ack" && request.AckPhase == "revoked", "terminal-control-restricted", "Terminal recovery permits only this link's revocation confirmation.", 403);
        if (principal.Status == "active" && long.TryParse(caller.FindFirstValue(ServiceIdentityClaims.CredentialRevision), out var tokenRevision))
        {
            var candidateRotation = await db.Set<ServiceLinkRotation>().SingleOrDefaultAsync(x => x.LinkId == a.LinkId && x.IsIssuer && x.SuccessorCredentialRevision == tokenRevision && x.CallerSwitchRevision == null && x.ActiveRotationKey != null, ct);
            if (candidateRotation is not null)
                Require(request.RotationId == candidateRotation.RotationId && (kind == "verify" || kind == "rotate" && request.RotationPhase is "verified" or "switched" or "abort"), "candidate-control-restricted", "A candidate controls only verification and completion of its own pending rotation.", 403);
        }
        var fingerprint = ServiceLinkLifecycleProjection.Hash(kind, request);
        var existing = await db.Set<ServiceLinkOperation>().SingleOrDefaultAsync(x => x.LinkId == pathLinkId && x.OperationId == request.OperationId, ct);
        if (existing is not null)
        {
            Require(!existing.Outbound && existing.Kind == kind && await LifecycleFingerprintMatches(a, existing, kind, request, ct), "operation-payload-conflict", "This durable operation identifier is already bound to a different body.", 409);
            return System.Text.Json.JsonDocument.Parse(existing.ResponseJson).RootElement.Clone();
        }
        request = ServiceLinkPayloadNormalization.Lifecycle(request);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await Expire(a, ct);
        Dictionary<string, object?> response;
        switch (kind)
        {
            case "verify": response = await Verify(a, principal, request, caller, ct); break;
            case "ack": response = await Ack(a, request, ct); break;
            case "commit": response = await Commit(a, request, ct); break;
            case "abort": response = await Abort(a, request, ct); break;
            case "revoke": response = await Revoke(a, request, ct); break;
            case "rotate": response = await Rotate(a, principal, request, caller, ct); break;
            default: throw new ServiceLinkProtocolException(404, "operation-not-found", "The lifecycle operation is unsupported.");
        }
        db.Set<ServiceLinkOperation>().Add(new ServiceLinkOperation { LinkId = pathLinkId, OperationId = request.OperationId, Kind = kind, RequestFingerprint = fingerprint, ResponseJson = Json(response), Completed = true, CreatedAtUnixSeconds = Now });
        await Save(a, ct); await tx.CommitAsync(ct); return response;
    }

    private static void ValidateFields(string kind, ServiceLinkLifecycleRequest request)
    {
        if (kind is "abort" or "revoke" || kind == "rotate" && request.RotationPhase == "abort")
            Require(request.ReasonCode is { Length: >= 1 and <= 256 } && !string.IsNullOrWhiteSpace(request.ReasonCode),
                "invalid-reason-code", "A nonempty reason code of at most 256 characters is required.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Contract", "OperationId", "AttemptId", "LinkId", "LinkRevision", "GrantHash", "WirePropertyNames" };
        foreach (var name in kind switch
        {
            "verify" => new[] { "DirectionId", "CredentialRevision", "RotationId" },
            "ack" => new[] { "AckPhase", "PeerOperationId", "CommitId", "ExchangeResponseHash", "RevocationId" },
            "commit" => new[] { "CommitId", "DescriptorHash", "InitiatorVerificationReceiptId", "ResponderVerificationReceiptId" },
            "abort" => new[] { "AbortPhase", "AbortId", "ReasonCode" },
            "revoke" => new[] { "RevocationId", "ExpectedLinkRevision", "ReasonCode" },
            "rotate" => new[] { "RotationId", "RotationPhase", "DirectionId", "ExpectedCurrentCredentialRevision", "SuccessorCredentialRevision", "RequestedByInstanceId", "RequestedPolicyRevision", "OfferExpiresAt", "CredentialForCaller", "SuccessorVerificationReceiptId", "ActivateDecisionId", "CallerSwitchRevision", "ReasonCode" },
            _ => Array.Empty<string>()
        }) allowed.Add(name);
        Require(typeof(ServiceLinkLifecycleRequest).GetProperties().All(p => allowed.Contains(p.Name) || p.GetValue(request) is null), "invalid-operation-fields", "The operation contains fields belonging to a different lifecycle phase.");
    }

    private async Task<Dictionary<string, object?>> Verify(ServiceLinkAttempt a, ServicePrincipalRegistration principal, ServiceLinkLifecycleRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        Require(a.Decision != "abort" && a.LifecycleState is not ("revoked" or "revocation_pending" or "failed" or "expired") && request.AttemptId == a.AttemptId && request.DirectionId == principal.DirectionId && request.CredentialRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) == caller.FindFirstValue(ServiceIdentityClaims.CredentialRevision), "verification-not-authorized", "The probe is not bound to this prepared directional credential.", 403);
        if (request.RotationId is not null)
        {
            var rotation = await db.Set<ServiceLinkRotation>().SingleOrDefaultAsync(x => x.RotationId == request.RotationId && x.LinkId == a.LinkId, ct);
            Require(rotation is not null && rotation.IsIssuer && rotation.DirectionId == request.DirectionId && rotation.SuccessorCredentialRevision == request.CredentialRevision && rotation.RotationState is not ("aborted" or "completed"), "rotation-not-authorized", "The probe does not belong to this pending successor.", 403);
        }
        var receipt = new ServiceLinkVerificationReceipt { VerificationReceiptId = NewId(), LinkId = a.LinkId!, AttemptId = a.AttemptId, GrantHash = a.GrantHash!, ServicePrincipalId = principal.Id, DirectionId = principal.DirectionId!, CredentialRevision = request.CredentialRevision!.Value, RotationId = request.RotationId, VerifiedAtUnixSeconds = Now };
        db.Set<ServiceLinkVerificationReceipt>().Add(receipt);
        if (request.RotationId is null)
        {
            if (receipt.DirectionId == ServiceLinkContract.InitiatorToResponder) a.InitiatorVerificationReceiptId = receipt.VerificationReceiptId; else a.ResponderVerificationReceiptId = receipt.VerificationReceiptId;
            if (a.InitiatorVerificationReceiptId is not null && a.ResponderVerificationReceiptId is not null && a.Decision == "undecided") a.LifecycleState = a.ExpiresAtUnixSeconds > Now ? "verified" : "in_doubt";
        }
        var g = InboundGrant(a); var response = Common(a);
        response["verification_receipt_id"] = receipt.VerificationReceiptId; response["caller_instance_id"] = g.CallerInstanceId; response["caller_tenant_id"] = g.CallerTenantId;
        response["target_instance_id"] = g.TargetInstanceId; response["target_tenant_id"] = g.TargetTenantId; response["credential_revision"] = receipt.CredentialRevision; response["rotation_id"] = request.RotationId; response["verified_at"] = Timestamp(Now); return response;
    }

    private async Task<Dictionary<string, object?>> Ack(ServiceLinkAttempt a, ServiceLinkLifecycleRequest request, CancellationToken ct)
    {
        Require(request.AttemptId == a.AttemptId && !string.IsNullOrEmpty(request.PeerOperationId), "invalid-acknowledgement", "The acknowledgement must identify the original phase operation.");
        switch (request.AckPhase)
        {
            case "prepared":
                Require(a.ProtectedOutboundCredential is not null && a.InboundPrincipalId is not null && request.ExchangeResponseHash == a.ExchangeResponseHash && a.Decision != "abort", "preparation-not-proven", "The prepared acknowledgement does not match the accepted durable exchange.", 409);
                a.PeerPreparedAcknowledged = true; break;
            case "active":
                Require(a.Decision == "commit" && a.LocalInboundActive && request.CommitId == a.CommitId, "activation-not-proven", "The acknowledgement does not identify the durable activation decision.", 409);
                a.PeerActiveAcknowledged = true; await EnableSender(a, ct); break;
            case "revoked":
                Require(a.RevocationId == request.RevocationId && a.LifecycleState is "revocation_pending" or "revoked", "revocation-not-proven", "The revocation acknowledgement differs from its terminal operation.", 409);
                a.PeerRevocationAcknowledged = true; a.LifecycleState = "revoked"; break;
            default: throw new ServiceLinkProtocolException(400, "invalid-acknowledgement", "The acknowledgement phase is unsupported.");
        }
        var response = Common(a); response["acknowledged_phase"] = request.AckPhase; response["acknowledgement_id"] = NewId(); return response;
    }

    private async Task<Dictionary<string, object?>> Commit(ServiceLinkAttempt a, ServiceLinkLifecycleRequest request, CancellationToken ct)
    {
        Require(a.Role == "responder" && request.AttemptId == a.AttemptId && request.DescriptorHash == a.DescriptorHash && !string.IsNullOrEmpty(request.CommitId) &&
            a.Decision != "abort" && (a.CommitId is null || a.CommitId == request.CommitId), "commit-conflict", "Only the approved initiating coordinator may deliver this matching decision.", 409);
        Require(a.ProtectedOutboundCredential is not null && a.PeerPreparedAcknowledged && a.LocalPreparedAcknowledged &&
            a.InitiatorVerificationReceiptId is not null && a.ResponderVerificationReceiptId is not null &&
            request.InitiatorVerificationReceiptId == a.InitiatorVerificationReceiptId && request.ResponderVerificationReceiptId == a.ResponderVerificationReceiptId,
            "commit-not-prepared", "Both durable handoffs, preparation acknowledgements and real directional probes are required.", 409);
        a.Decision = "commit"; a.CommitId = request.CommitId; a.LifecycleState = "commit_decided"; await ActivateInbound(a, ct);
        var response = Common(a); response["decision"] = "commit"; response["commit_id"] = a.CommitId; response["local_inbound_active"] = a.LocalInboundActive; return response;
    }

    private async Task<Dictionary<string, object?>> Abort(ServiceLinkAttempt a, ServiceLinkLifecycleRequest request, CancellationToken ct)
    {
        Require(request.AttemptId == a.AttemptId && request.AbortId is not null && request.AbortPhase is "request" or "decision", "invalid-abort", "The exact attempt and abort phase are required.");
        Require(a.Role == "initiator" ? request.AbortPhase == "request" : request.AbortPhase == "decision", "abort-not-authorized", "Only the coordinator may decide this attempt.", 403);
        if (a.Role == "responder" && a.Decision == "undecided" && a.AbortId is not null)
            await JournalParticipantCancellation(a, ct);
        if (a.Decision != "commit")
        {
            if (a.Decision != "abort")
            {
                Require(a.AbortId is null || a.AbortId == request.AbortId, "abort-conflict", "A different abort decision already exists.", 409);
                a.Decision = "abort"; a.AbortId = request.AbortId; a.LifecycleState = "expired"; await Disable(a, ct); PurgeEscrow(a);
            }
            else if (a.Role == "responder")
                Require(a.AbortId == request.AbortId, "abort-conflict", "The coordinator's immutable abort decision changed.", 409);
        }
        var response = Common(a); response["decision"] = a.Decision; response["commit_id"] = a.CommitId; response["abort_id"] = a.AbortId; return response;
    }
    private async Task<Dictionary<string, object?>> Revoke(ServiceLinkAttempt a, ServiceLinkLifecycleRequest request, CancellationToken ct)
    {
        Require(request.ExpectedLinkRevision == a.LinkRevision && request.RevocationId is not null, "revocation-conflict", "The revocation must bind the current approved revision.", 409);
        var localConfirmationPending = a.LifecycleState == "revocation_pending" && !a.PeerRevocationAcknowledged;
        a.RevocationId ??= request.RevocationId;
        a.LifecycleState = localConfirmationPending ? "revocation_pending" : "revoked";
        await Disable(a, ct); PurgeEscrow(a);
        var response = Common(a); response["revocation_id"] = request.RevocationId; response["local_business_revoked"] = true; response["local_sender_disabled"] = true; return response;
    }

    private async Task ActivateInbound(ServiceLinkAttempt a, CancellationToken ct)
    {
        if (a.LocalInboundActive) return;
        var g = InboundGrant(a); var ns = Guid.Parse(g.SourceNamespaceId!);
        var source = await db.IncidentReceiverSources.SingleAsync(x => x.SourceNamespaceId == ns, ct);
        Require(source.IsEnabled, "source-disabled", "The approved source was disabled after consent.", 403);
        await registry.ActivateAsync(a.InboundPrincipalId!.Value, ct);
        var binding = await db.IncidentReceiverPrincipalBindings.SingleAsync(x => x.SourceNamespaceId == ns && x.PrincipalKind == "service_principal" && x.PrincipalId == a.InboundPrincipalId.Value.ToString("N"), ct); binding.IsEnabled = true;
        a.LocalInboundActive = true;
    }
    private async Task Disable(ServiceLinkAttempt a, CancellationToken ct)
    {
        a.LocalInboundActive = false; a.LocalBusinessSenderEnabled = false; a.TerminalControlExpiresAtUnixSeconds ??= Now + settings.TerminalControlRecoverySeconds;
        if (a.InboundPrincipalId is not null) await registry.RevokeAsync(a.InboundPrincipalId.Value, ct);
        if (a.OutboundProfileRevision is not null) a.OutboundProfileRevision = (await providers.SetLinkedOrchestratorSenderEnabledAsync(a.LinkId!, a.LinkRevision, a.OutboundProfileRevision.Value, false, ct)).Revision;
        a.ActiveRelationshipKey = null;
        if (a.InboundPrincipalId is not null)
        {
            var bindings = await db.IncidentReceiverPrincipalBindings.Where(x => x.PrincipalKind == "service_principal" && x.PrincipalId == a.InboundPrincipalId.Value.ToString("N")).ToListAsync(ct);
            foreach (var b in bindings) b.IsEnabled = false;
        }
        await PurgeTerminalRotationEscrow(a, ct);
    }
    private async Task EnableSender(ServiceLinkAttempt a, CancellationToken ct)
    {
        if (a.LocalBusinessSenderEnabled) return;
        Require(a.Decision == "commit" && a.PeerActiveAcknowledged && a.LocalInboundActive && a.LocalPreparedAcknowledged && a.PeerPreparedAcknowledged, "activation-not-proven", "Both verified committed directions and active peer acknowledgement are required.", 409);
        if (!a.LocalActiveAcknowledged) return;
        a.OutboundProfileRevision = (await providers.SetLinkedOrchestratorSenderEnabledAsync(a.LinkId!, a.LinkRevision, a.OutboundProfileRevision!.Value, true, ct)).Revision;
        a.LocalBusinessSenderEnabled = true; a.LifecycleState = "active"; PurgeEscrow(a);
    }
    private static void PurgeEscrow(ServiceLinkAttempt a) { a.ProtectedInboundEscrow = null; a.ProtectedExchangeResponse = null; a.ProtectedPairingCode = null; a.ProtectedVerifier = null; a.ProtectedBrowserState = null; }
    private async Task Expire(ServiceLinkAttempt a, CancellationToken ct)
    {
        if (a.ExpiresAtUnixSeconds > Now) return;
        if (a.Decision == "commit" || a.ProtectedOutboundCredential is not null || a.ExchangeDispatched || a.LifecycleState is "prepared" or "verified" or "in_doubt" or "commit_decided")
        {
            if (a.Decision == "undecided") { a.LifecycleState = "in_doubt"; if (a.InboundPrincipalId is not null) await registry.SetStatusAsync(a.InboundPrincipalId.Value, "in_doubt", ct); }
        }
        else if (a.Decision == "undecided") { a.Decision = "abort"; a.AbortId = NewId(); a.LifecycleState = "expired"; a.ActiveRelationshipKey = null; if (a.InboundPrincipalId is not null) await registry.RevokeAsync(a.InboundPrincipalId.Value, ct); }
        PurgeEscrow(a); await Save(a, ct);
    }
}
