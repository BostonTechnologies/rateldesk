using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Helpdesk.Infrastructure.ServiceLink.ServiceLinkValidation;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed partial class ServiceLinkCoordinator
{
    private async Task StageOutbound(ServiceLinkAttempt a, ServiceDirectionalCredential credential, CancellationToken ct)
    {
        var peer = Peer(a); var grant = OutboundGrant(a); var inbound = InboundGrant(a);
        var existing = await providers.GetOrchestratorSettingsAsync(ct);
        var binding = new ServiceLinkOrchestratorBinding(a.LocalTenantId, grant.TargetTenantId, a.PeerInstanceId, a.LinkId!, a.LinkRevision, credential.CredentialRevision, a.GrantHash!, grant.DirectionId, inbound.SourceInstanceId, inbound.SourceNamespaceId);
        var result = await providers.StageLinkedOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = existing.Revision, Enabled = false, BaseUrl = peer.ApiBaseUrl, Authority = credential.Issuer, TokenEndpoint = credential.TokenEndpoint,
            Audience = credential.Audience, Scope = string.Join(' ', credential.Scopes), ClientId = credential.ClientId, ClientSecret = credential.ClientSecret, AllowPrivateHttp = settings.AllowPrivateHttp,
            RemoteSystemName = "NetRatel reciprocal service", HealthPath = "/internal/health", CatalogPath = "/internal/catalog", IngestPath = "/internal/ingest"
        }, binding, ct);
        a.OutboundProfileRevision = result.Revision;
    }

    private ServiceLinkLifecycleRequest Request(ServiceLinkAttempt a) => new() { OperationId = NewId(), AttemptId = a.AttemptId, LinkId = a.LinkId!, LinkRevision = a.LinkRevision, GrantHash = a.GrantHash! };
    private async Task JournalParticipantCancellation(ServiceLinkAttempt a, CancellationToken ct)
    {
        // Deployed undecided responders used AbortId for a request, not a decision.
        // Preserve that exact request identity in the existing encrypted journal
        // before reserving AbortId for the coordinator's immutable decision.
        var legacyRequestId = a.AbortId;
        await OutboundOperation(a, "abort-request", "abort", Request(a) with
        { AbortPhase = "request", AbortId = legacyRequestId ?? NewId(), ReasonCode = "participant-cancel" }, ct);
        if (legacyRequestId is not null) { a.AbortId = null; await Save(a, ct); }
    }
    private async Task<ServiceLinkOperation> OutboundOperation(ServiceLinkAttempt a, string kind, string route, ServiceLinkLifecycleRequest request, CancellationToken ct)
    {
        var existing = await db.Set<ServiceLinkOperation>().SingleOrDefaultAsync(x => x.LinkId == a.LinkId && x.Outbound && x.Kind == kind, ct);
        if (existing is not null) return existing;
        request = ServiceLinkPayloadNormalization.Lifecycle(request);
        var operation = new ServiceLinkOperation { LinkId = a.LinkId!, OperationId = request.OperationId, Kind = kind, RequestFingerprint = ServiceLinkLifecycleProjection.Hash(route, request), ProtectedRequestJson = Protect(a, "operation/" + request.OperationId, Json(request)), Outbound = true, CreatedAtUnixSeconds = Now };
        db.Set<ServiceLinkOperation>().Add(operation); await Save(a, ct); return operation;
    }
    private async Task<JsonElement> SendOperation(ServiceLinkAttempt a, string kind, string route, ServiceLinkLifecycleRequest request, CancellationToken ct, ServiceDirectionalCredential? candidate = null)
    {
        var operation = await OutboundOperation(a, kind, route, request, ct);
        if (operation.Completed) return JsonDocument.Parse(operation.ResponseJson).RootElement.Clone();
        var durable = Read<ServiceLinkLifecycleRequest>(Unprotect(a, "operation/" + operation.OperationId, operation.ProtectedRequestJson!));
        var normalizedFingerprint = ServiceLinkLifecycleProjection.Hash(route, durable);
        var normalizeScopes = operation.RequestFingerprint == normalizedFingerprint;
        Require(normalizeScopes || operation.RequestFingerprint == ServiceLinkLifecycleProjection.Hash(route, durable, false),
            "operation-payload-conflict", "The protected durable request differs from its original operation fingerprint.", 409);
        var credential = candidate ?? Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential!));
        var token = await ProtocolToken(a, route == "verify" ? ServiceLinkContract.VerifyScope : ServiceLinkContract.ControlScope,
            route, durable, credential, ct, candidate is not null);
        using var response = await transport.PostAsync<JsonDocument>(Endpoint(Peer(a).ServiceLinkEndpoint, "/links/" + a.LinkId + "/" + route), ServiceLinkLifecycleProjection.Build(route, durable, normalizeScopes), ct, token);
        var result = response.RootElement.Clone(); ValidatePeerResult(a, result);
        operation.ResponseJson = result.GetRawText(); operation.Completed = true;
        // Offer payloads are permanent candidate material only at the designated recipient; avoid retaining journal plaintext.
        operation.ProtectedRequestJson = null;
        await db.SaveChangesAsync(ct); return result;
    }
    private static string? String(JsonElement e, string field) => e.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Boolean(JsonElement e, string field) => e.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
    private static void ValidatePeerResult(ServiceLinkAttempt a, JsonElement e) => Require(String(e, "contract") == ServiceLinkContract.Version && String(e, "link_id") == a.LinkId && e.GetProperty("link_revision").GetInt64() == a.LinkRevision && String(e, "grant_hash") == a.GrantHash, "peer-lifecycle-binding-mismatch", "The authenticated peer response differs from this approved link.");

    public async Task<ServiceLinkAdminStatus> ResumeAsync(string linkId, ClaimsPrincipal actor, CancellationToken ct)
    { var a = await Link(linkId, ct); await Authorize(actor, a.LocalTenantId, ct); a = await ProgressWithRetry(a, ct); return await AdminStatus(a, ct); }

    private async Task<ServiceLinkAttempt> ProgressWithRetry(ServiceLinkAttempt a, CancellationToken ct)
    {
        for (var retry = 0; ; retry++)
        {
            try { await Progress(a, ct); return a; }
            catch (Exception error) when (retry < 3 &&
                (error is DbUpdateConcurrencyException || !ct.IsCancellationRequested && ServiceLinkDatabaseConflict.IsAbortedTransaction(error)))
            {
                // Progress uses the existing protected handoff and durable operation journals.
                // Only a known database abort may replay that recovery; an unknown commit,
                // transport failure, or arbitrary transient error must leave this scope.
                if (db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null) throw;
                var attemptId = a.AttemptId; db.ChangeTracker.Clear(); a = await Attempt(attemptId, ct);
            }
        }
    }

    public async Task WorkAsync(CancellationToken ct)
    {
        await CleanupBootstrapEscrow(ct);
        await CleanupRotationEscrow(ct);
        if (!settings.Enabled) return;
        // Live attempts without a local principal are waiting for human consent,
        // not background work. Scheduling a no-op would still increment their
        // optimistic revision and race the original approval/callback scope.
        // Expired attempts remain eligible for the existing abort and escrow cleanup.
        var ids = await db.Set<ServiceLinkAttempt>().AsNoTracking().Where(x => x.NextWorkAtUnixSeconds <= Now && x.LifecycleState != "failed" && x.LifecycleState != "expired" && x.LifecycleState != "revoked" &&
            !(x.Decision == "undecided" && x.InboundPrincipalId == null &&
              (x.LifecycleState == "awaiting_approval" || x.LifecycleState == "approved") && x.ExpiresAtUnixSeconds > Now))
            .OrderBy(x => x.NextWorkAtUnixSeconds).Select(x => x.AttemptId).Take(20).ToListAsync(ct);
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear(); var a = await Attempt(id, ct);
            // Recheck after loading: a concurrent local action may have changed
            // the state since the bounded candidate query.
            if (a.Decision == "undecided" && a.InboundPrincipalId is null &&
                (a.LifecycleState is "awaiting_approval" or "approved") && a.ExpiresAtUnixSeconds > Now) continue;
            try { a = await ProgressWithRetry(a, ct); a.LastErrorCode = null; }
            catch (Exception e) when (e is HttpRequestException or ServiceLinkProtocolException or TaskCanceledException)
            {
                // A database retry may have replaced the tracked attempt before
                // the peer failed, preventing the awaited assignment above.
                // Resolve that instance without discarding its pending changes.
                a = await Attempt(id, ct);
                a.LastErrorCode = e is ServiceLinkProtocolException protocol ? protocol.Code : "peer-unavailable";
            }
            a.NextWorkAtUnixSeconds = Now + settings.WorkerIntervalSeconds; await Save(a, ct);
        }
    }

    private async Task Progress(ServiceLinkAttempt a, CancellationToken ct)
    {
        await Expire(a, ct);
        if (a.LifecycleState is "expired" or "failed" or "revoked") return;
        if (a.LifecycleState == "revocation_pending") { await DeliverRevocation(a, ct); return; }
        if (a.LifecycleState == "active")
        {
            if (await ServiceLinkAuthority.InboundUsableAsync(db, a, clock, issuer, settings, ct)) await ProgressRotations(a, ct);
            return;
        }
        if (a.InboundPrincipalId is null) return;
        if (a.Role == "responder" && a.Decision == "undecided" && a.AbortId is not null)
            await JournalParticipantCancellation(a, ct);
        var participantAbort = a.Role == "responder" && a.Decision == "undecided" &&
            await db.Set<ServiceLinkOperation>().AnyAsync(x => x.LinkId == a.LinkId && x.Outbound && x.Kind == "abort-request", ct);
        if ((a.AbortId is not null || participantAbort) && a.ProtectedOutboundCredential is not null)
        {
            if (a.Role == "initiator" && a.Decision == "abort")
            {
                var abortResult = await SendOperation(a, "abort-decision", "abort", Request(a) with { AbortPhase = "decision", AbortId = a.AbortId, ReasonCode = "coordinator-abort" }, ct);
                Require(String(abortResult, "decision") == "abort" && String(abortResult, "abort_id") == a.AbortId, "abort-recovery-pending", "The participant has not confirmed the durable abort decision.", 409);
                a.LifecycleState = "expired"; await Save(a, ct); return;
            }
            if (a.Role == "responder" && a.Decision == "undecided")
            {
                var abortResult = await SendOperation(a, "abort-request", "abort", Request(a) with { AbortPhase = "request", AbortId = a.AbortId, ReasonCode = "participant-cancel" }, ct);
                if (String(abortResult, "decision") == "abort") { await Abort(a, Request(a) with { AbortPhase = "decision", AbortId = String(abortResult, "abort_id"), ReasonCode = "coordinator-abort" }, ct); await Save(a, ct); return; }
                Require(String(abortResult, "decision") == "commit", "abort-recovery-pending", "The coordinator has not supplied a durable decision.", 409);
            }
        }
        if (a.Role == "responder" && a.Decision == "undecided" && a.ProtectedOutboundCredential is not null)
        {
            var recoveryCredential = Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential));
            var recoveryToken = await ProtocolToken(a, ServiceLinkContract.ControlScope, "status", null, recoveryCredential, ct);
            using var recoveryStatus = await transport.GetAsync<JsonDocument>(Endpoint(Peer(a).ServiceLinkEndpoint, "/links/" + a.LinkId + "/status"), ct, recoveryToken);
            var decision = recoveryStatus.RootElement; ValidatePeerResult(a, decision);
            Require(String(decision, "descriptor_hash") == a.DescriptorHash && String(decision, "attempt_id") == a.AttemptId && String(decision, "coordinator_instance_id") == Descriptor(a).InitiatorInstanceId, "peer-lifecycle-binding-mismatch", "The recovery decision belongs to another attempt.");
            if (String(decision, "decision") == "abort") { await Abort(a, Request(a) with { AbortPhase = "decision", AbortId = String(decision, "abort_id"), ReasonCode = "coordinator-abort" }, ct); await Save(a, ct); return; }
            if (String(decision, "decision") == "commit")
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await Commit(a, Request(a) with { CommitId = String(decision, "commit_id"), DescriptorHash = a.DescriptorHash, InitiatorVerificationReceiptId = String(decision, "initiator_verification_receipt_id"), ResponderVerificationReceiptId = String(decision, "responder_verification_receipt_id") }, ct);
                await Save(a, ct); await tx.CommitAsync(ct); return;
            }
        }
        if (a.Role == "initiator" && a.ProtectedOutboundCredential is null)
        {
            Require(a.ExpiresAtUnixSeconds > Now && a.ProtectedInboundEscrow is not null, "handoff-unavailable", "The handoff expired; retain control-only recovery and request the coordinator decision.", 410);
            var credentialForResponder = Read<ServiceDirectionalCredential>(Unprotect(a, "inbound-escrow", a.ProtectedInboundEscrow!));
            if (!a.ExchangeDispatched)
            {
                credentialForResponder = ServiceLinkPayloadNormalization.Credential(credentialForResponder);
                a.ProtectedInboundEscrow = Protect(a, "inbound-escrow", Json(credentialForResponder));
            }
            // An already dispatched legacy handoff must retain its original exact credential body.
            a.ExchangeDispatched = true; await Save(a, ct);
            var request = new ServiceLinkExchangeRequest(ServiceLinkContract.Version, a.AttemptId, Unprotect(a, "pairing-code", a.ProtectedPairingCode!), Unprotect(a, "verifier", a.ProtectedVerifier!), a.DescriptorHash, a.GrantHash!, a.ConsentId!, credentialForResponder);
            var result = await transport.PostAsync<ServiceLinkExchangeResponse>(Endpoint(Peer(a).ServiceLinkEndpoint, "/attempts/" + a.AttemptId + "/exchange"), request, ct);
            Require(result.Contract == ServiceLinkContract.Version && result.AttemptId == a.AttemptId && result.LinkId == a.LinkId && result.LinkRevision == a.LinkRevision && result.GrantHash == a.GrantHash && result.LifecycleState == "prepared", "exchange-binding-mismatch", "The returned handoff differs from the approved attempt.");
            Credential(result.CredentialForInitiator, OutboundGrant(a), Peer(a));
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var receivedCredential = ServiceLinkPayloadNormalization.Credential(result.CredentialForInitiator);
            a.ProtectedOutboundCredential = Protect(a, "outbound-credential", Json(receivedCredential)); a.ExchangeResponseHash = ServiceLinkCanonicalJson.HashObject(result); a.LifecycleState = "prepared";
            await StageOutbound(a, receivedCredential, ct); await Save(a, ct); await tx.CommitAsync(ct); return;
        }
        if (a.ProtectedOutboundCredential is null) return;
        if (!a.LocalPreparedAcknowledged)
        {
            var result = await SendOperation(a, "ack-prepared", "ack", Request(a) with { AckPhase = "prepared", PeerOperationId = a.AttemptId, ExchangeResponseHash = a.ExchangeResponseHash }, ct);
            Require(String(result, "acknowledged_phase") == "prepared", "invalid-acknowledgement", "The peer did not acknowledge durable preparation.");
            a.LocalPreparedAcknowledged = true; await Save(a, ct); return;
        }
        var ownReceipt = a.Role == "initiator" ? a.InitiatorVerificationReceiptId : a.ResponderVerificationReceiptId;
        if (ownReceipt is null)
        {
            var outbound = Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential)); var g = OutboundGrant(a);
            var result = await SendOperation(a, "verify-bootstrap", "verify", Request(a) with { DirectionId = g.DirectionId, CredentialRevision = outbound.CredentialRevision }, ct);
            Require(String(result, "caller_instance_id") == g.CallerInstanceId && String(result, "caller_tenant_id") == g.CallerTenantId && String(result, "target_instance_id") == g.TargetInstanceId && String(result, "target_tenant_id") == g.TargetTenantId && result.GetProperty("credential_revision").GetInt64() == outbound.CredentialRevision && String(result, "rotation_id") is null && String(result, "verification_receipt_id") is not null, "invalid-verification-receipt", "The authenticated read-only probe returned a foreign binding.");
            if (a.Role == "initiator") a.InitiatorVerificationReceiptId = String(result, "verification_receipt_id"); else a.ResponderVerificationReceiptId = String(result, "verification_receipt_id");
            if (a.InitiatorVerificationReceiptId is not null && a.ResponderVerificationReceiptId is not null) a.LifecycleState = a.ExpiresAtUnixSeconds > Now ? "verified" : "in_doubt";
            await Save(a, ct); return;
        }
        if (a.Decision == "undecided")
        {
            var outbound = Read<ServiceDirectionalCredential>(Unprotect(a, "outbound-credential", a.ProtectedOutboundCredential));
            var token = await ProtocolToken(a, ServiceLinkContract.ControlScope, "status", null, outbound, ct);
            using var peerStatus = await transport.GetAsync<JsonDocument>(Endpoint(Peer(a).ServiceLinkEndpoint, "/links/" + a.LinkId + "/status"), ct, token);
            var status = peerStatus.RootElement; ValidatePeerResult(a, status);
            Require(String(status, "descriptor_hash") == a.DescriptorHash && String(status, "attempt_id") == a.AttemptId && String(status, "coordinator_instance_id") == Descriptor(a).InitiatorInstanceId, "peer-lifecycle-binding-mismatch", "The coordinator's stored decision binding differs.");
            if (a.Role == "responder" && String(status, "decision") == "abort")
            { await Abort(a, Request(a) with { AbortPhase = "decision", AbortId = String(status, "abort_id"), ReasonCode = "coordinator-abort" }, ct); await Save(a, ct); return; }
            if (a.Role == "responder" && String(status, "decision") == "commit")
            {
                await Commit(a, Request(a) with { CommitId = String(status, "commit_id"), DescriptorHash = a.DescriptorHash, InitiatorVerificationReceiptId = String(status, "initiator_verification_receipt_id"), ResponderVerificationReceiptId = String(status, "responder_verification_receipt_id") }, ct);
                await Save(a, ct); return;
            }
            Require(a.Role == "initiator" && a.PeerPreparedAcknowledged && a.LocalPreparedAcknowledged && a.InitiatorVerificationReceiptId is not null && a.ResponderVerificationReceiptId is not null && Boolean(status, "local_outbound_persisted") && Boolean(status, "local_inbound_ready") && String(status, "initiator_verification_receipt_id") == a.InitiatorVerificationReceiptId && String(status, "responder_verification_receipt_id") == a.ResponderVerificationReceiptId, "verification-pending", "Awaiting both authenticated probes and durable handoff acknowledgements.", 409);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            a.Decision = "commit"; a.CommitId = NewId(); a.LifecycleState = "commit_decided"; await Save(a, ct); await tx.CommitAsync(ct); return;
        }
        if (a.Decision == "commit" && !a.LocalInboundActive && a.Role == "initiator")
        {
            var result = await SendOperation(a, "commit-bootstrap", "commit", Request(a) with { CommitId = a.CommitId, DescriptorHash = a.DescriptorHash, InitiatorVerificationReceiptId = a.InitiatorVerificationReceiptId, ResponderVerificationReceiptId = a.ResponderVerificationReceiptId }, ct);
            Require(String(result, "decision") == "commit" && String(result, "commit_id") == a.CommitId && Boolean(result, "local_inbound_active"), "activation-pending", "The peer has not acknowledged the durable activation decision.", 409);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await ActivateInbound(a, ct); a.PeerActiveAcknowledged = true; await Save(a, ct); await tx.CommitAsync(ct); return;
        }
        if (a.Decision == "commit" && a.LocalInboundActive && !a.LocalActiveAcknowledged)
        {
            var result = await SendOperation(a, "ack-active", "ack", Request(a) with { AckPhase = "active", CommitId = a.CommitId, PeerOperationId = a.CommitId }, ct);
            Require(String(result, "acknowledged_phase") == "active", "activation-pending", "The peer has not acknowledged active completion.", 409);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            a.LocalActiveAcknowledged = true; a.PeerActiveAcknowledged = true; await EnableSender(a, ct); await Save(a, ct); await tx.CommitAsync(ct);
        }
        else if (a.Decision == "commit" && a.LocalInboundActive && a.LocalActiveAcknowledged && a.PeerActiveAcknowledged && !a.LocalBusinessSenderEnabled)
        { await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct); await EnableSender(a, ct); await Save(a, ct); await tx.CommitAsync(ct); }
    }
}

public sealed class ServiceLinkWorker(IServiceScopeFactory scopes, IOptions<ServiceLinkOptions> options, ILogger<ServiceLinkWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.WorkerIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await using var scope = scopes.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>().WorkAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Service-link recovery work could not complete. Failure type: {FailureType}", e.GetType().Name); }
        }
    }
}
