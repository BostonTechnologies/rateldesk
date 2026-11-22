using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Helpdesk.Infrastructure.AiOps;
using System.Text.Json;
using System.Threading.Channels;
using Helpdesk.Application.Events;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

// PostgreSQL owns durable state; this service only serializes transport work and buffers.
public sealed partial class AiAssistantChatSessionManager(IServiceScopeFactory scopes, IOptions<AiAssistantChatOptions> settings, IChatLiveFeed feed, ILogger<AiAssistantChatSessionManager> logger, IAiAssistantChatClientFactory clients, TimeProvider? timeProvider = null, IAiAssistantChatRuntimeState? runtimeState = null)
    : BackgroundService, IAiAssistantChatTransport
{
    private readonly ConcurrentDictionary<Guid, Owner> owners = new();
    private readonly SemaphoreSlim acquisition = new(1);
    private readonly SemaphoreSlim ownership = new(1);
    private readonly ConcurrentDictionary<Guid, RecoveryAttempt> recoveryAttempts = new();
    private sealed record RecoveryAttempt(int Count, DateTimeOffset NextUtc);
    private const int AutomaticRecoveryLimit = 6;
    private readonly IAiAssistantChatRuntimeState runtime = runtimeState ?? new AiAssistantChatRuntimeState(settings);
    private CancellationToken stopping;
    private NpgsqlConnection? singleOwner;
    private volatile bool ready;
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    public bool ConnectionObservationsAvailable => ready && runtime.Current.Enabled;

    public ChatSessionConnection? ObserveConnection(Guid conversation)
    {
        if (!ConnectionObservationsAvailable || !owners.TryGetValue(conversation, out var owner) || owner.Retired ||
            !string.Equals(owner.ProfileFingerprint, runtime.Current.ProfileFingerprint, StringComparison.Ordinal)) return null;
        return new ChatSessionConnection(conversation, owner.SessionId,
            owner.Client.IsConnected && !owner.Consumer.IsCompleted && !owner.Overflowed
                ? ChatTransportConnectionState.Connected : ChatTransportConnectionState.Disconnected,
            owner.AuthenticatedAtUtc, owner.LastObservedAtUtc);
    }

    private sealed class Owner(IAiAssistantChatClient client, string profileFingerprint)
    {
        public IAiAssistantChatClient Client { get; } = client;
        public string ProfileFingerprint { get; } = profileFingerprint;
        public string SessionId { get; set; } = string.Empty;
        public Guid? MessageId { get; set; }
        public int? LastCompletedTurn { get; set; }
        public int? BaselineTurn { get; set; }
        public bool SendStarted { get; set; }
        public volatile bool AdmissionProven;
        public volatile bool ObservingStoppedTurn;
        public bool FirstReplyObserved;
        public bool FirstMeaningfulObserved;
        public bool CreatedSession { get; set; }
        public HashSet<string> ReminderCalls { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ChatScheduledReport> ReminderRegistrations { get; } = new(StringComparer.Ordinal);
        public bool UnconfirmedReminderRegistration { get; set; }
        public ChatTurnCompletion? Continuation { get; set; }
        public Channel<NetclawOutput> Outputs { get; } = Channel.CreateBounded<NetclawOutput>(256);
        public StringBuilder Draft { get; } = new();
        public List<string> StagedReplies { get; } = [];
        public int StagedReplyLength { get; set; }
        public bool HasFinalText { get; set; }
        private long durableSequence;
        public long DurableSequence => Volatile.Read(ref durableSequence);
        public void AdvanceSequence(long sequence)
        {
            var observed = Volatile.Read(ref durableSequence);
            while (sequence > observed)
            {
                var previous = Interlocked.CompareExchange(ref durableSequence, sequence, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
        public Dictionary<(int? Turn, string Call), long> ToolStarts { get; } = new();
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Consumer { get; set; } = Task.CompletedTask;
        public int Uses;
        public volatile bool Overflowed;
        public volatile bool Retired;
        public volatile bool ReconciliationConfirmed;
        public Guid? QuietMessageId;
        private long lastObservedUtcTicks;
        private long lastCheckpointUtcTicks;
        public DateTimeOffset AuthenticatedAtUtc { get; set; }
        public DateTimeOffset? LastObservedAtUtc => Volatile.Read(ref lastObservedUtcTicks) is > 0 and var ticks
            ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        public void Observe(DateTimeOffset now) => Interlocked.Exchange(ref lastObservedUtcTicks, now.UtcTicks);
        public bool WasObservedSince(DateTimeOffset cutoff) => Volatile.Read(ref lastObservedUtcTicks) >= cutoff.UtcTicks;
        public void InitializeActivity(DateTimeOffset now)
        {
            Interlocked.Exchange(ref lastObservedUtcTicks, now.UtcTicks);
            Interlocked.Exchange(ref lastCheckpointUtcTicks, now.UtcTicks);
        }
        public bool ShouldCheckpoint(DateTimeOffset now, TimeSpan interval)
        {
            var observed = Volatile.Read(ref lastCheckpointUtcTicks);
            while (now.UtcTicks - observed >= interval.Ticks)
            {
                var previous = Interlocked.CompareExchange(ref lastCheckpointUtcTicks, now.UtcTicks, observed);
                if (previous == observed) return true;
                observed = previous;
            }
            return false;
        }
    }

    public async Task SendAsync(Guid conversation, Guid messageId, string text, CancellationToken ct)
    {
        Owner? owner = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var persisted = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == conversation, ct);
            if (persisted.ProviderKind is ChatProviderKind.OpenAiAgents or ChatProviderKind.CodexAppServer) return; // The durable provider outbox owns dispatch.
            var message = await db.Set<AiAssistantChatEvent>().AsNoTracking().SingleAsync(x => x.ConversationId == conversation && x.ClientMessageId == messageId && x.Type == "operator", ct);
            if (message.Text != text) throw new InvalidOperationException("Outbound message does not match its admitted event.");
            var outbound = await ChatOutboundMessage.BuildAsync(db, persisted, message, messageId, ct);
            owner = await GetAsync(conversation, ct);
            owner.InitializeActivity(Now);
            owner.AdvanceSequence(persisted.LastSequence);
            // Reattachment may consume completion output while this request waits for
            // its owner. Never send an obsolete admission into the resumed session.
            if (!await db.Set<AiAssistantChatConversation>().AsNoTracking().AnyAsync(x => x.Id == conversation && x.State == ChatState.Processing && x.ActiveMessageId == messageId, ct))
            {
                logger.LogInformation("Chat admission is no longer active for {ConversationId}", conversation);
                return;
            }
            owner.SendStarted = true;
            await owner.Client.SendAsync(owner.SessionId, outbound, ct);
            owner.AdmissionProven = true;
            ObserveLifecycle("native_admitted", persisted, owner);

        }
        catch (Exception ex)
        {
            logger.LogWarning("AiAssistant message transport failed for {ConversationId}: {ExceptionType}", conversation, ex.GetType().Name);
            if (await UnknownAsync(conversation, messageId)) await RetireAsync(conversation, CancellationToken.None);
        }
        finally { if (owner is not null) Interlocked.Decrement(ref owner.Uses); }
    }

    public async Task RespondAsync(Guid conversation, string callId, string key, CancellationToken ct)
    {
        if (await UsesOpenAiAsync(conversation, ct)) return;

        Owner? owner = null;
        Guid? messageId = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            // Bind failures to the operator turn that owned this decision, even if the
            // response invocation finishes after a later turn has already started.
            var decisionSequence = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == conversation && x.Type == "approval_response" && x.CallId == callId).Select(x => x.Sequence).SingleAsync(ct);
            messageId = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == conversation && x.Type == "operator" && x.Sequence < decisionSequence).OrderByDescending(x => x.Sequence).Select(x => x.ClientMessageId).FirstOrDefaultAsync(ct);
            owner = await GetAsync(conversation, ct);
            var active = await db.Set<AiAssistantChatConversation>().AsNoTracking()
                .Where(x => x.Id == conversation && x.ActiveMessageId == messageId && (x.State == ChatState.Processing || x.State == ChatState.AwaitingApproval))
                .Select(x => new { x.LastSequence }).SingleOrDefaultAsync(ct);
            if (active is null)
                throw new ChatConflictException("The approval's turn is no longer active.");
            // Approval decisions are committed by the store, outside the output
            // consumer. Tag immediate response deltas with that durable boundary.
            owner.AdvanceSequence(active.LastSequence);
            // This is a fresh provider-processing lease after the human decision.
            // Initialize it only after the active turn has been revalidated.
            owner.InitializeActivity(Now);
            await owner.Client.RespondAsync(owner.SessionId, callId, key, ct);
            ObserveLifecycle("approval_rpc_acknowledged", conversation, owner);
        }
        catch (Exception ex)
        {
            logger.LogWarning("AiAssistant approval transport failed for {ConversationId}: {ExceptionType}", conversation, ex.GetType().Name);
            if (messageId.HasValue && await UnknownAsync(conversation, messageId, callId)) await RetireAsync(conversation, CancellationToken.None);
        }
        finally { if (owner is not null) Interlocked.Decrement(ref owner.Uses); }
    }

    public async Task ReconcileAsync(Guid conversation, CancellationToken ct)
    {
        if (await UsesOpenAiAsync(conversation, ct)) return;

        recoveryAttempts.TryRemove(conversation, out _);
        await ReconcileCoreAsync(conversation, ct);
    }

    private async Task ReconcileCoreAsync(Guid conversation, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var current = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == conversation, ct);
        if (current.State != ChatState.DeliveryUnknown) throw new ChatConflictException("Only an unresolved conversation can be reconciled.");
        if (current.AiAssistantSessionId is null) { await RecordRecoveryFailureAsync(conversation, current.ActiveMessageId, ct); return; }
        Owner? owner = null;
        try
        {
            if (owners.TryGetValue(conversation, out var stoppedOwner) && IsObserving(stoppedOwner)
                && CanObserveStoppedTurn(current, stoppedOwner)
                && await StopTimeAsync(db, current, ct) is { } stoppedAt)
            {
                if (stoppedAt + ChatContinuationSafety.ObservationLimit > Now)
                {
                    stoppedOwner.ObservingStoppedTurn = true;
                    await RecordRecoveryResultAsync(conversation, current.ActiveMessageId, stoppedOwner.AdmissionProven, stillObserving: true);
                    return;
                }
                await RetireAsync(conversation, ct);
            }
            if (HasScheduledWork(await LatestCompletionAsync(db, current, ct))) return;
            // Preserve a useful attached stream after a completed admission check.
            if (runtime.Current.Enabled && owners.TryGetValue(conversation, out var attached)
                && string.Equals(attached.ProfileFingerprint, runtime.Current.ProfileFingerprint, StringComparison.Ordinal)
                && string.Equals(current.ProviderProfileFingerprint, attached.ProfileFingerprint, StringComparison.Ordinal)
                && attached.ReconciliationConfirmed && attached.Client.IsConnected && !attached.Retired
                && !attached.Consumer.IsCompleted && attached.Joined.Task.IsCompletedSuccessfully)
            {
                await RecordRecoveryResultAsync(conversation, current.ActiveMessageId, attached.ReconciliationConfirmed);
                return;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider ?? TimeProvider.System);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping, deadline.Token);
            owner = await GetAsync(conversation, attempt.Token, forceReconnect: true);
            // EnsureSession acknowledgement and SessionJoined output are separate
            // messages. Return only after the history outcome has been committed.
            await owner.Joined.Task.WaitAsync(attempt.Token);
            ObserveLifecycle("recovery_attached", conversation, owner);
        }
        catch (ChatConflictException) when (!ct.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            await RecordRecoveryFailureAsync(conversation, current.ActiveMessageId, ct);
            throw;
        }
        catch (Exception ex) when (ex is not ChatConflictException && !ct.IsCancellationRequested && !stopping.IsCancellationRequested)
        {
            logger.LogWarning("Chat reconciliation unavailable for {ConversationId}: {ExceptionType}", conversation, ex.GetType().Name);
            await RecordRecoveryFailureAsync(conversation, current.ActiveMessageId, ct);
        }
        finally { if (owner is not null) Interlocked.Decrement(ref owner.Uses); }
    }

    private static string RecoveryResult(bool admitted) => admitted
        ? "Admission confirmed; outcome unknown. This view remains attached to the original saved session. Its history supplies no exact-message terminal outcome or approval replay guarantee. Connected transport does not mean completion. No message was resent. You can check again later or explicitly abandon this unresolved turn."
        : "Session resumed, but history does not prove this exact message was admitted. Outcome remains unknown; no message was resent. Check later or acknowledge possible remote execution and archive this transcript.";

    private async Task RecordRecoveryResultAsync(Guid id, Guid? messageId, bool admitted, bool stillObserving = false)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(stopping);
        var current = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(stopping);
        if (current.State != ChatState.DeliveryUnknown || current.ActiveMessageId != messageId) return;
        current.RecoveryLastAttemptUtc = Now;
        current.RecoveryResult = stillObserving
            ? (admitted ? "Admission confirmed. The original attached session is still being observed for its exact result."
                : "The original session is still attached. Admission and the outcome remain unconfirmed.")
            : RecoveryResult(admitted);
        await db.SaveChangesAsync(stopping);
        await tx.CommitAsync(stopping);
        feed.Publish(new(id, null));
    }

    private async Task RecordRecoveryFailureAsync(Guid id, Guid? messageId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
            if (current.State != ChatState.DeliveryUnknown || current.ActiveMessageId != messageId) return;
            current.RecoveryLastAttemptUtc = Now;
            current.RecoveryResult = "Saved session is unavailable. No message was resent. Try again later, or acknowledge possible remote execution and archive this transcript.";
            if (!await db.Set<AiAssistantChatEvent>().AnyAsync(x => x.ConversationId == id && x.Type == "recovery_inconclusive" && x.Sequence > db.Set<AiAssistantChatEvent>().Where(e => e.ConversationId == id && e.Type == "operator").Max(e => e.Sequence), ct))
                AiAssistantChatStore.Append(db, current, "recovery_inconclusive", current.RecoveryResult);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            feed.Publish(new(id, null));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Chat recovery outcome could not be saved for {ConversationId}: {ExceptionType}", id, ex.GetType().Name);
            throw new InvalidOperationException("Chat recovery is temporarily unavailable. No message was resent.");
        }
    }

    private async Task<Owner> GetAsync(Guid id, CancellationToken ct, bool forceReconnect = false)
    {
        if (!ready)
        {
            if (!runtime.Current.Enabled)
                throw new InvalidOperationException("Netclaw chat is disabled.");
            await EnsureOwnershipAsync(ct);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
        ct = linked.Token;
        await acquisition.WaitAsync(ct);
        try
        {
            stopping.ThrowIfCancellationRequested();
            var snapshot = runtime.Current;
            if (!ready || !snapshot.Enabled) throw new InvalidOperationException("Netclaw chat is disabled or transport ownership is unavailable.");
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var conversation = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == id, ct);
            if (conversation.State == ChatState.Archived)
            {
                if (owners.TryRemove(id, out var archivedOwner)) await RetireAsync(archivedOwner);
                throw new ChatConflictException("Archived conversations cannot use the AiAssistant transport.");
            }
            var providerChanged = !string.IsNullOrWhiteSpace(conversation.ProviderProfileFingerprint) &&
                !string.Equals(conversation.ProviderProfileFingerprint, snapshot.ProfileFingerprint, StringComparison.Ordinal);
            var unboundLegacySession = string.IsNullOrWhiteSpace(conversation.ProviderProfileFingerprint) &&
                !string.IsNullOrWhiteSpace(conversation.AiAssistantSessionId);
            if (providerChanged || unboundLegacySession)
            {
                throw new ChatConflictException("This conversation belongs to a different Netclaw provider profile. Start a new conversation instead of reusing its remote session.");
            }
            // A saved remote session is authorized only by its durable provider
            // fingerprint, which legacy confirmation writes per conversation.
            var requestedSessionId = conversation.AiAssistantSessionId;
            var pendingCompletion = await LatestCompletionAsync(db, conversation, ct);
            if (HasPendingWork(pendingCompletion) && pendingCompletion!.ObservationExpiresAtUtc <= Now)
                throw new ChatConflictException("The follow-up observation deadline expired. Its outcomes remain unknown.");
            if (owners.TryGetValue(id, out var existing))
            {
                if (!string.Equals(existing.ProfileFingerprint, snapshot.ProfileFingerprint, StringComparison.Ordinal))
                    throw new ChatConflictException("The Netclaw transport was reconfigured; retry after the conversation is reloaded.");
                if (forceReconnect && Volatile.Read(ref existing.Uses) != 0) throw new ChatConflictException("A transport operation is still completing. Reconcile again after it finishes.");
                if (!forceReconnect && existing.Client.IsConnected && !existing.Consumer.IsCompleted && !existing.Overflowed && !existing.Retired) { Interlocked.Increment(ref existing.Uses); return existing; }
                owners.TryRemove(id, out _);
                await RetireAsync(existing);
            }
            if (owners.Count >= snapshot.ConnectionCapacity) throw new ChatConflictException("Chat connection capacity reached.");
            // Keep the legacy factory entry point for test doubles and external
            // implementations that predate immutable runtime snapshots. The built-in
            // factory advertises the additive capability and captures this snapshot.
            var client = clients is IAiAssistantChatRuntimeClientFactory snapshotFactory
                ? snapshotFactory.Create(snapshot)
                : clients.Create();
            var owner = new Owner(client, snapshot.ProfileFingerprint);
            try
            {
                var ensured = await owner.Client.ConnectObservedAsync(requestedSessionId,
                    output =>
                    {
                        if (owner.Retired || owner.ProfileFingerprint != runtime.Current.ProfileFingerprint) return Task.CompletedTask;
                        if (IsMeaningfulOutput(output.Type)) owner.Observe(Now);
                        if (!owner.Outputs.Writer.TryWrite(output)) owner.Overflowed = true;
                        return Task.CompletedTask;
                    }, ct);
                if (owner.Overflowed) throw new InvalidOperationException("Session recovery exceeded the bounded output buffer.");
                if (requestedSessionId is not null && (ensured.Created || ensured.SessionId != requestedSessionId))
                    throw new InvalidOperationException("The original AiAssistant session could not be resumed.");
                // A network round trip does not reserve the conversation. Refresh
                // under its row lock before binding the transport so abandonment
                // cannot be overwritten by the tracked pre-connect snapshot.
                db.ChangeTracker.Clear();
                await using var binding = await db.Database.BeginTransactionAsync(ct);
                conversation = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
                if (conversation.State == ChatState.Archived) throw new ChatConflictException("The conversation was archived while its transport connected.");
                var durableProviderChanged = !string.IsNullOrWhiteSpace(conversation.ProviderProfileFingerprint) &&
                    !string.Equals(conversation.ProviderProfileFingerprint, snapshot.ProfileFingerprint, StringComparison.Ordinal);
                if (durableProviderChanged)
                    throw new ChatConflictException("The conversation's provider profile changed while its transport connected.");
                if (string.IsNullOrWhiteSpace(conversation.ProviderProfileFingerprint) &&
                    !string.IsNullOrWhiteSpace(conversation.AiAssistantSessionId))
                    throw new ChatConflictException("The legacy conversation has no trusted provider binding. Start a new conversation instead of reusing its remote session.");
                if (conversation.AiAssistantSessionId is not null && conversation.AiAssistantSessionId != ensured.SessionId)
                    throw new ChatConflictException("The conversation's session binding changed.");
                owner.SessionId = ensured.SessionId;
                owner.CreatedSession = ensured.Created;
                owner.MessageId = conversation.ActiveMessageId;
                owner.Continuation = await LatestCompletionAsync(db, conversation, ct);
                owner.LastCompletedTurn = owner.Continuation?.TurnNumber;
                owner.BaselineTurn = ensured.Created ? 0 : await LatestNativeOrdinalAsync(db, conversation.Id, ct);
                owner.AuthenticatedAtUtc = Now;
                owner.AdvanceSequence(conversation.LastSequence);
                conversation.ProviderProfileFingerprint = snapshot.ProfileFingerprint;
                conversation.ProviderProfileId ??= OpenAi.OpenAiChatProfiles.NetclawId;
                conversation.AiAssistantSessionId = ensured.SessionId;
                await db.SaveChangesAsync(ct);
                await binding.CommitAsync(ct);
                owners[id] = owner;
                owner.Consumer = ConsumeAsync(id, owner);
                Interlocked.Increment(ref owner.Uses);
                return owner;
            }
            catch
            {
                await RetireAsync(owner);
                throw;
            }
        }
        finally { acquisition.Release(); }
    }

    private async Task<bool> UnknownAsync(Guid id, Guid? messageId = null, string? callId = null, string reason = "Delivery could not be confirmed. The message will not be resent automatically.")
    {
        if (stopping.IsCancellationRequested) return false; // Startup recovery handles interrupted durable turns.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(stopping);
        var conversation = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(stopping);
        // A completed output may have won the race with a failed invocation acknowledgement.
        // Any uncertain decision affects the whole turn, including when another call
        // still awaits approval. An ordinary disconnected parked approval can resume.
        var uncertainDecision = callId is not null && messageId.HasValue && conversation.State == ChatState.AwaitingApproval;
        var changed = false;
        if (messageId is null || conversation.ActiveMessageId == messageId)
        {
            var previous = await LatestCompletionAsync(db, conversation, stopping);
            if (HasPendingReports(previous) && conversation.State != ChatState.Archived)
            {
                if (conversation.State != ChatState.AwaitingApproval || uncertainDecision)
                    conversation.State = ChatState.DeliveryUnknown;
                RecordScheduledObservationEnd(db, conversation, EndScheduledObservation(previous!),
                    previous!.ObservationExpiresAtUtc <= Now ? "The scheduled report observation window expired."
                        : "The original SignalR observation was interrupted.");
                changed = true;
            }
            else if (conversation.State is ChatState.Processing or ChatState.WaitingForChildren || uncertainDecision
                || conversation.State == ChatState.AwaitingApproval && HasChildren(previous))
            {
                conversation.State = ChatState.DeliveryUnknown;
                AiAssistantChatStore.Append(db, conversation, "delivery_unknown", reason, atUtc: Now);
                changed = true;
            }
            if (changed) await db.SaveChangesAsync(stopping);
        }
        await tx.CommitAsync(stopping);
        if (changed)
        {
            if (owners.TryGetValue(id, out var owner) && owner.MessageId == conversation.ActiveMessageId && HasPendingReports(owner.Continuation))
                ObserveLifecycle("scheduled_observation_ended", conversation, owner,
                    owner.Continuation!.ObservationExpiresAtUtc <= Now ? "expired" : "disconnected");
            feed.Publish(new(id, null));
        }
        return changed;
    }

    private async Task<bool> UsesOpenAiAsync(Guid conversation, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        return await db.Set<AiAssistantChatConversation>().AnyAsync(x =>
            x.Id == conversation && (x.ProviderKind == ChatProviderKind.OpenAiAgents || x.ProviderKind == ChatProviderKind.CodexAppServer), ct);
    }

    public async Task RetireAsync(Guid conversation, CancellationToken ct)
    {
        if (owners.TryRemove(conversation, out var owner)) await RetireAsync(owner);
        feed.Publish(new(conversation, null));
    }

    public Task ReconfigureAsync(CancellationToken ct)
        => ReconfigureAsync(runtime.Current, ct);

    public async Task ReconfigureAsync(AiAssistantChatRuntimeSnapshot snapshot, CancellationToken ct)
        => await TryReconfigureAsync(snapshot, ct);

    public async Task<bool> TryReconfigureAsync(AiAssistantChatRuntimeSnapshot snapshot, CancellationToken ct)
    {
        await ownership.WaitAsync(ct);
        try
        {
            if (!runtime.CanApply(snapshot)) return false;
            await acquisition.WaitAsync(ct);
            try
            {
                if (!runtime.CanApply(snapshot)) return false;
                foreach (var (id, owner) in owners.ToArray())
                {
                    try
                    {
                        await UnknownAsync(
                            id,
                            reason: "The Netclaw transport was reconfigured while this turn was active. Delivery could not be confirmed; the message will not be resent automatically.");
                    }
                    catch (Exception exception) when (!ct.IsCancellationRequested)
                    {
                        logger.LogWarning(exception, "Chat reconfiguration outcome could not be saved for {ConversationId}.", id);
                    }

                    if (owners.TryRemove(id, out var current))
                        await RetireAsync(current);
                }

                if (!runtime.TryPublish(snapshot)) return false;
                if (!snapshot.Enabled)
                {
                    ready = false;
                    if (singleOwner is not null)
                    {
                        await singleOwner.DisposeAsync();
                        singleOwner = null;
                    }
                }
            }
            finally { acquisition.Release(); }
        }
        finally { ownership.Release(); }

        if (snapshot.Enabled && !ready && ReferenceEquals(runtime.Current, snapshot))
            await EnsureOwnershipAsync(ct);
        return true;
    }

    private static string Bounded(string? text, int maximum) => text is null ? string.Empty : text.Length <= maximum ? text : text[..maximum];

    private static bool ProvesAdmission(NetclawOutput output, AiAssistantChatConversation conversation)
        => conversation.ActiveMessageId is { } message && output.ProvesAdmission(conversation.Id, message);

    private async Task CheckpointTransportActivityAsync(Guid id, Owner owner, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (!owner.Retired && conversation.State is ChatState.Processing or ChatState.WaitingForChildren or ChatState.WaitingForScheduledReports
            && owners.TryGetValue(id, out var activeOwner) && ReferenceEquals(owner, activeOwner)
            && conversation.AiAssistantSessionId == owner.SessionId
            && conversation.ActiveMessageId == owner.MessageId
            && conversation.ProviderProfileFingerprint == owner.ProfileFingerprint)
        {
            ClearQuietObservation(db, conversation, owner);
            AiAssistantChatStore.RecordTransportActivity(conversation, Now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            owner.AdvanceSequence(conversation.LastSequence);
            return;
        }
        await tx.CommitAsync(ct);
    }

    private async Task ConsumeAsync(Guid id, Owner owner)
    {
        try
        {
            await foreach (var output in owner.Outputs.Reader.ReadAllAsync(stopping))
            {
                if (owner.Retired) break;
                if (owner.Overflowed) throw new InvalidOperationException("Chat output buffer overflowed; the session must be reconciled.");
                if (output.Type == "transport_closed")
                    throw new InvalidOperationException("Chat transport disconnected; the session must be reconciled.");
                if (output.SessionId != owner.SessionId)
                {
                    if (output.Type == "turn_completed") ChatTelemetry.Record("terminal_rejected", "foreign_session");
                    continue;
                }
                var type = output.Type;
                if (type == "text_delta")
                {
                    // Ephemeral display data is filtered against authoritative state
                    // by SSE; token streaming must not issue a database read per delta.
                    if (owner.Draft.Length < 128000) owner.Draft.Append(Bounded(output.Text, 128000 - owner.Draft.Length));
                    if (owner.ShouldCheckpoint(Now, runtime.Current.ActivityHeartbeatInterval))
                        await CheckpointTransportActivityAsync(id, owner, stopping);
                    if (owner.CreatedSession && !HasScheduledWork(owner.Continuation) && !owner.ObservingStoppedTurn)
                        feed.Publish(new(id, owner.Draft.ToString(), owner.DurableSequence, owner.MessageId));
                    continue;
                }
                if (type is "thinking" or "thinking_delta") continue;
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(stopping);
                var conversation = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(stopping);
                if (owner.Retired || !owners.TryGetValue(id, out var activeOwner) || !ReferenceEquals(owner, activeOwner)
                    || conversation.AiAssistantSessionId != owner.SessionId
                    || conversation.ActiveMessageId != owner.MessageId
                    || conversation.ProviderProfileFingerprint != owner.ProfileFingerprint
                    || owner.ProfileFingerprint != runtime.Current.ProfileFingerprint)
                {
                    await tx.CommitAsync(stopping);
                    continue;
                }
                if (conversation.State == ChatState.Archived)
                {
                    if (type == "session_joined") owner.Joined.TrySetResult();
                    continue;
                }
                if (HasPendingWork(owner.Continuation) && owner.Continuation!.ObservationExpiresAtUtc <= Now)
                    throw new InvalidOperationException("Follow-up observation limit expired.");
                var stoppedObservation = false;
                if (conversation.State == ChatState.DeliveryUnknown && type != "session_joined")
                {
                    stoppedObservation = CanObserveStoppedTurn(conversation, owner)
                        && await StopTimeAsync(db, conversation, stopping) is { } stoppedAt
                        && stoppedAt + ChatContinuationSafety.ObservationLimit > Now;
                    if (!stoppedObservation)
                    {
                        await tx.CommitAsync(stopping);
                        continue;
                    }
                    owner.ObservingStoppedTurn = true;
                    if (type is not ("text" or "buffer_flush") && (type != "turn_completed"
                        || !output.HasReminderSource && !MatchesStoppedTerminal(owner.LastCompletedTurn ?? owner.BaselineTurn, output)))
                    {
                        if (type == "turn_completed") ResetTurnBuffers(owner);
                        await tx.CommitAsync(stopping);
                        continue;
                    }
                }
                if (conversation.State is not (ChatState.Processing or ChatState.AwaitingApproval or ChatState.WaitingForChildren
                    or ChatState.WaitingForScheduledReports or ChatState.DeliveryUnknown))
                {
                    await tx.CommitAsync(stopping);
                    continue;
                }
                if (conversation.State != ChatState.DeliveryUnknown && IsMeaningfulOutput(type))
                {
                    ClearQuietObservation(db, conversation, owner);
                    AiAssistantChatStore.RecordTransportActivity(conversation, Now);
                }
                var replyPersisted = false;
                if (type is "text" or "buffer_flush")
                {
                    var text = output.Text ?? owner.Draft.ToString();
                    var stage = !owner.CreatedSession || owner.Continuation is not null || stoppedObservation;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (stage) StageReply(owner, text, canonical: type == "text");
                        else
                        {
                            AppendReply(db, conversation, owner, text);
                            replyPersisted = true;
                        }
                    }
                    if (type == "buffer_flush") owner.HasFinalText = false;
                    owner.Draft.Clear();
                }
                else if (type == "turn_completed")
                {
                    if (!await CompleteNativeTurnAsync(db, conversation, owner, output, stopping))
                    {
                        await tx.CommitAsync(stopping);
                        continue;
                    }
                }
                else if (type == "tool_interaction_closed" && output.CallId is { } closedCall)
                {
                    await CloseSavedApprovalAsync(db, conversation, owner, output, closedCall, stopping);
                }
                else if (type == "tool_interaction" && output.CallId is { } callId)
                {
                    if (!await db.Set<AiAssistantChatInteraction>().AnyAsync(x => x.ConversationId == id && x.CallId == callId, stopping))
                    {
                        var options = output.Options();
                        if (options.Count == 0) throw new InvalidOperationException("Approval has no supported options.");
                        var json = JsonSerializer.Serialize(options);
                        db.Add(new AiAssistantChatInteraction { ConversationId = id, CallId = callId, OptionsJson = json });
                        conversation.State = ChatState.AwaitingApproval;
                        AiAssistantChatStore.Append(db, conversation, "approval_request", $"AiAssistant requests approval for {ChatOutputSafety.Identifier(output.ToolName)}.", callId: callId, options: json, atUtc: Now);
                    }
                }
                else if (type is "tool_call" or "tool_result")
                {
                    if (type == "tool_call") owner.HasFinalText = false;
                    ObserveReminderTool(owner, output);
                    var toolCallId = output.CallId;
                    long? duration = null;
                    if (toolCallId is not null)
                    {
                        if (type == "tool_call" && owner.ToolStarts.Count < 1024) owner.ToolStarts.TryAdd((output.TurnOrdinal ?? owner.LastCompletedTurn, toolCallId), Stopwatch.GetTimestamp());
                        else if (type == "tool_result" && owner.ToolStarts.Remove((output.TurnOrdinal ?? owner.LastCompletedTurn, toolCallId), out var started))
                            duration = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                    var metadata = output.Activity(duration) with
                    {
                        Association = new(owner.MessageId!.Value, owner.LastCompletedTurn, output.TurnOrdinal),
                        Child = output.ChildObservation()
                    };
                    AiAssistantChatStore.AppendActivity(db, conversation, type, toolCallId, metadata, Now);
                }
                else if (type is "usage" or "processing_state" or "compaction" or "subagent")
                {
                    var details = output.Trace;
                    AiAssistantChatStore.AppendActivity(db, conversation, type, null,
                        new ChatActivityMetadata(type == "subagent" ? output.AgentName : type,
                            type, null, null, null, "observed", details,
                            Association: new(owner.MessageId!.Value, owner.LastCompletedTurn, output.TurnOrdinal),
                            Child: output.ChildObservation()), Now);
                }
                else if (type == "file")
                    AiAssistantChatStore.Append(db, conversation, "file", $"Artifact produced: {output.FileName} ({output.MimeType}).", atUtc: Now);
                else if (type == "error")
                {
                    var category = output.ErrorCategory;
                    var correlation = output.ErrorCorrelation;
                    var detail = correlation is null ? "" : $" NetClaw reference {correlation}.";
                    AiAssistantChatStore.Append(db, conversation, "error", $"NetClaw reported {category}.{detail}", atUtc: Now);
                    ObserveLifecycle("native_error", conversation, owner, category, correlation);
                }
                else if (type == "session_joined" && conversation.State == ChatState.DeliveryUnknown)
                {
                    var admissionConfirmed = ProvesAdmission(output, conversation);
                    owner.ReconciliationConfirmed = admissionConfirmed;
                    conversation.RecoveryLastAttemptUtc = Now;
                    conversation.RecoveryResult = RecoveryResult(admissionConfirmed);
                    var resultType = admissionConfirmed ? "recovery_admission_confirmed" : "recovery_inconclusive";
                    var operatorSequence = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == id && x.Type == "operator" && x.ClientMessageId == conversation.ActiveMessageId).Select(x => x.Sequence).SingleAsync(stopping);
                    if (!await db.Set<AiAssistantChatEvent>().AnyAsync(x => x.ConversationId == id && x.Type == resultType && x.Sequence > operatorSequence, stopping))
                        AiAssistantChatStore.Append(db, conversation, resultType, conversation.RecoveryResult, atUtc: Now);
                }
                if (stoppedObservation && conversation.State != ChatState.DeliveryUnknown)
                {
                    AiAssistantChatStore.Append(db, conversation, "observation_reconciled",
                        "The original attached session delivered its matching terminal after you stopped waiting.", atUtc: Now);
                }
                await db.SaveChangesAsync(stopping);
                await tx.CommitAsync(stopping);
                if (IsMeaningfulOutput(type) && !owner.FirstMeaningfulObserved)
                {
                    owner.FirstMeaningfulObserved = true;
                    ObserveLifecycle("first_meaningful_frame_persisted", conversation, owner);
                }
                if (type == "turn_completed")
                {
                    ObserveLifecycle(stoppedObservation ? "late_terminal_persisted" : "terminal_persisted", conversation, owner,
                        owner.Continuation?.TurnOutcome ?? "unknown");
                    if (owner.Continuation?.SourceReminderId is { } fireKey)
                        ObserveLifecycle(owner.HasFinalText && owner.Continuation is { TurnOutcome: "completed", QuietEnd: false }
                            ? "scheduled_report_persisted" : "scheduled_report_unconfirmed", conversation, owner, nativeCorrelation: fireKey);
                    if (HasPendingReports(owner.Continuation))
                        ObserveLifecycle("scheduled_report_pending", conversation, owner, "signalr_live");
                    else if (owner.Continuation?.ScheduledWorkUnconfirmed == true)
                        ObserveLifecycle("scheduled_observation_ended", conversation, owner, "unconfirmed");
                    ResetTurnBuffers(owner);
                }
                else if (replyPersisted && !owner.FirstReplyObserved)
                {
                    owner.FirstReplyObserved = true;
                    ObserveLifecycle("first_reply_persisted", conversation, owner);
                }
                else if (type == "tool_interaction") ObserveLifecycle("approval_offered", conversation, owner);
                else if (type == "subagent") ObserveLifecycle("child_activity", conversation, owner);
                owner.AdvanceSequence(conversation.LastSequence);
                feed.Publish(new(id, null));
                if (owner.Retired && owners.TryRemove(new KeyValuePair<Guid, Owner>(id, owner)))
                    await RetireFromConsumerAsync(owner);
                if (type == "session_joined") owner.Joined.TrySetResult();
                if (type is "turn_completed" or "tool_interaction" or "error" or "session_joined")
                {
                    try
                    {
                        var publisher = scope.ServiceProvider.GetRequiredService<IDomainEventPublisher>();
                        var correlation = scope.ServiceProvider.GetRequiredService<ICorrelationContext>();
                        var action = type switch { "turn_completed" => "TurnCompleted", "tool_interaction" => "ApprovalRequested", "session_joined" => "SessionReconciled", _ => "Error" };
                        await publisher.PublishAsync(new ChatDomainEvent(action, conversation.OrganizationId, id.ToString(), correlation.GetCorrelationId()), stopping);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Chat domain event publication failed for {ConversationId}: {ExceptionType}", id, ex.GetType().Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { logger.LogDebug("Chat output consumer stopped."); }
        catch (Exception ex)
        {
            owner.Retired = true;
            owner.Joined.TrySetException(new InvalidOperationException("Session recovery output could not be persisted."));
            owner.Outputs.Writer.TryComplete();
            logger.LogWarning("Chat output persistence failed for {ConversationId}: {ExceptionType}", id, ex.GetType().Name);
            try { await UnknownAsync(id, owner.MessageId); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { logger.LogDebug("Chat recovery deferred until restart."); }
            catch (Exception recoveryError) { logger.LogError("Chat recovery persistence failed for {ConversationId}: {ExceptionType}", id, recoveryError.GetType().Name); }
            if (owners.TryRemove(new KeyValuePair<Guid, Owner>(id, owner)))
                await RetireFromConsumerAsync(owner);
        }
    }

    private async Task RetireAsync(Owner owner)
    {
        ChatTelemetry.Record("receiver_retired");
        owner.Retired = true;
        owner.Joined.TrySetCanceled();
        owner.Outputs.Writer.TryComplete();
        try { await owner.Client.DisposeAsync(); }
        catch (Exception ex) { logger.LogWarning("Chat connection disposal failed: {ExceptionType}", ex.GetType().Name); }
        await owner.Consumer;
        owner.Draft.Clear();
    }

    private async Task RetireFromConsumerAsync(Owner owner)
    {
        owner.Retired = true;
        owner.Joined.TrySetCanceled();
        owner.Outputs.Writer.TryComplete();
        try { await owner.Client.DisposeAsync(); }
        catch (Exception ex) { logger.LogWarning("Chat connection disposal failed: {ExceptionType}", ex.GetType().Name); }
        owner.Draft.Clear();
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        stopping = cancellationToken;
        if (runtime.Current.Enabled) await EnsureOwnershipAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    private async Task EnsureOwnershipAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Current.Enabled || ready) return;

        await ownership.WaitAsync(cancellationToken);
        try
        {
            if (!runtime.Current.Enabled || ready) return;

            NpgsqlConnection? candidate = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                // Session-level advisory ownership must end when this socket is disposed,
                // rather than survive on an idle pooled PostgreSQL connection.
                var providerConnection = db.Database.GetDbConnection() as NpgsqlConnection
                    ?? throw new InvalidOperationException("AiAssistant chat ownership requires an Npgsql database connection.");
                var ownershipConnection = new NpgsqlConnectionStringBuilder(providerConnection.ConnectionString) { Pooling = false };
                candidate = providerConnection.CloneWith(ownershipConnection.ConnectionString);
                await candidate.OpenAsync(cancellationToken);
                await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(794, 1)", candidate);
                if (await claim.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new InvalidOperationException("Only one enabled AiAssistant chat API instance may own this database.");

                singleOwner = candidate;
                candidate = null;
                // This process cannot own any transports left by a prior API process.
                // Resolve those turns before accepting new transport work; the periodic
                // sweep remains solely the silent-live-transport watchdog.
                await RecoverInterruptedProcessingAsync(cancellationToken);
                ready = true;
            }
            catch
            {
                ready = false;
                if (candidate is not null) await candidate.DisposeAsync();
                if (singleOwner is not null)
                {
                    await singleOwner.DisposeAsync();
                    singleOwner = null;
                }
                throw;
            }
        }
        finally { ownership.Release(); }
    }

    private async Task RecoverInterruptedProcessingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var interrupted = await db.Set<AiAssistantChatConversation>()
            .FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"ProviderKind\" = 0 AND \"State\" IN ({(int)ChatState.Processing}, {(int)ChatState.WaitingForChildren}, {(int)ChatState.WaitingForScheduledReports}, {(int)ChatState.AwaitingApproval}) FOR UPDATE")
            .ToListAsync(ct);
        foreach (var conversation in interrupted)
        {
            var previous = await LatestCompletionAsync(db, conversation, ct);
            if (HasPendingReports(previous))
            {
                RecordScheduledObservationEnd(db, conversation, EndScheduledObservation(previous!),
                    "The API restarted and lost the original SignalR observation.");
                if (conversation.State != ChatState.AwaitingApproval || HasChildren(previous))
                    conversation.State = ChatState.DeliveryUnknown;
                ChatTelemetry.Record("scheduled_observation_ended", "api_restarted");
                continue;
            }
            if (conversation.State == ChatState.AwaitingApproval && !HasChildren(previous))
                continue;
            conversation.State = ChatState.DeliveryUnknown;
            AiAssistantChatStore.Append(db, conversation, "transport_restarted",
                "The API restarted while this request or delegated follow-up was active. Its prior transport is gone; outcomes remain unknown and the request will not be resent automatically.", atUtc: Now);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var conversation in interrupted)
            feed.Publish(new(conversation.Id, null));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        try { await RunAsync(stoppingToken); }
        finally
        {
            await ReleaseOwnershipAsync(CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!runtime.Current.Enabled)
            {
                await ReleaseOwnershipAsync(stoppingToken);
                continue;
            }

            if (!ready)
            {
                try { await EnsureOwnershipAsync(stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Netclaw chat ownership could not be established after runtime enablement: {ExceptionType}", ex.GetType().Name);
                }
                continue;
            }

            var releaseAfterGate = false;
            await ownership.WaitAsync(stoppingToken);
            try
            {
                if (!runtime.Current.Enabled)
                {
                    releaseAfterGate = true;
                }
                else if (!ready)
                {
                    continue;
                }
                else
                {
                    // Loss of this connection terminates the service before another owner can be used.
                    var ownerConnection = singleOwner
                        ?? throw new InvalidOperationException("Netclaw chat ownership connection is unavailable.");
                    await using var heartbeat = new NpgsqlCommand("SELECT 1", ownerConnection);
                    await heartbeat.ExecuteScalarAsync(stoppingToken);
                    await SweepPendingContinuationsAsync(stoppingToken);
                    await SweepStaleProcessingAsync(stoppingToken);
                    await SweepStoppedObservationsAsync(stoppingToken);
                    await acquisition.WaitAsync(stoppingToken);
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                        var cutoff = Now.AddMinutes(-runtime.Current.IdleMinutes);
                        var ids = owners.Keys.ToArray();
                        foreach (var id in ids)
                            if (owners.TryGetValue(id, out var disconnected) && (!disconnected.Client.IsConnected || disconnected.Consumer.IsCompleted || disconnected.Overflowed))
                                if (await UnknownAsync(id)) await RetireAsync(id, stoppingToken);
                        var idle = await db.Set<AiAssistantChatConversation>().Where(x => ids.Contains(x.Id) && (x.State == ChatState.Idle || x.State == ChatState.AwaitingApproval || x.State == ChatState.Archived) && x.LastActivityUtc < cutoff).Select(x => x.Id).ToListAsync(stoppingToken);
                        foreach (var id in idle)
                        {
                            if (!owners.TryGetValue(id, out var owner) || Volatile.Read(ref owner.Uses) != 0) continue;
                            await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);
                            var current = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(stoppingToken);
                            if (current.State is ChatState.Processing or ChatState.WaitingForChildren or ChatState.WaitingForScheduledReports
                                or ChatState.DeliveryUnknown || current.LastActivityUtc >= cutoff
                                || HasPendingWork(await LatestCompletionAsync(db, current, stoppingToken))) continue;
                            owners.TryRemove(id, out _);
                            await tx.CommitAsync(stoppingToken);
                            // Never wait for a consumer while holding the row it needs to finish.
                            await RetireAsync(owner);
                        }
                    }
                    finally { acquisition.Release(); }
                }
            }
            finally { ownership.Release(); }
            if (releaseAfterGate)
                await ReleaseOwnershipAsync(stoppingToken);
            await ReconcilePendingAsync(stoppingToken);
        }
    }

    internal async Task ReconcilePendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var unresolved = await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .Where(x => x.ProviderKind == ChatProviderKind.Netclaw && x.State == ChatState.DeliveryUnknown && x.AiAssistantSessionId != null)
            .OrderBy(x => x.RecoveryLastAttemptUtc ?? x.LastActivityUtc).ToListAsync(ct);
        foreach (var current in unresolved)
        {
            var id = current.Id;
            if (owners.TryGetValue(id, out var attached) && !attached.Retired && !attached.Consumer.IsCompleted
                && attached.Client.IsConnected && (attached.Joined.Task.IsCompletedSuccessfully || attached.ObservingStoppedTurn)) continue;
            if (HasScheduledWork(await LatestCompletionAsync(db, current, ct))) continue;
            if (recoveryAttempts.TryGetValue(id, out var previous) && (previous.Count >= AutomaticRecoveryLimit || previous.NextUtc > Now)) continue;
            var count = (previous?.Count ?? 0) + 1;
            var inFlight = new RecoveryAttempt(count, DateTimeOffset.MaxValue);
            recoveryAttempts[id] = inFlight;
            try { await ReconcileCoreAsync(id, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { logger.LogWarning("Chat resume unavailable for {ConversationId}: {ExceptionType}", id, ex.GetType().Name); }
            var attempt = inFlight with { NextUtc = Now.AddSeconds(Math.Min(300, 15 * (1 << (count - 1)))) };
            // Backoff starts after the check ends. A manual check may have reset its budget meanwhile.
            if (recoveryAttempts.TryUpdate(id, attempt, inFlight))
                await RecordAutomaticRecoveryAsync(id, attempt, ct);
            break; // One bounded attempt per sweep keeps database ownership checks responsive.
        }
        foreach (var id in recoveryAttempts.Keys.Except(unresolved.Select(row => row.Id))) recoveryAttempts.TryRemove(id, out _);
    }

    internal async Task SweepStaleProcessingAsync(CancellationToken ct)
    {
        var cutoff = Now - runtime.Current.TurnInactivityTimeout;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var candidates = await db.Set<AiAssistantChatConversation>().AsNoTracking()
            .Where(x => x.ProviderKind == ChatProviderKind.Netclaw && x.State == ChatState.Processing && (x.LastTransportActivityAtUtc == null || x.LastTransportActivityAtUtc < cutoff))
            .Select(x => x.Id)
            .ToListAsync(ct);
        foreach (var id in candidates)
        {
            if (owners.TryGetValue(id, out var owner) && owner.WasObservedSince(cutoff)) continue;
            var transitioned = false;
            await using (var updateScope = scopes.CreateAsyncScope())
            {
                var updateDb = updateScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await using var tx = await updateDb.Database.BeginTransactionAsync(ct);
                var current = await updateDb.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
                if (current.State == ChatState.Processing
                    && (current.LastTransportActivityAtUtc is null || current.LastTransportActivityAtUtc < cutoff)
                    && (!owners.TryGetValue(id, out var currentOwner) || !currentOwner.WasObservedSince(cutoff)))
                {
                    var observation = ObserveConnection(id);
                    if (current.ActiveMessageId.HasValue && observation is { State: ChatTransportConnectionState.Connected }
                        && observation.SessionId == current.AiAssistantSessionId
                        && current.ProviderProfileFingerprint == runtime.Current.ProfileFingerprint
                        && owners.TryGetValue(id, out var live))
                    {
                        var announce = live.QuietMessageId != current.ActiveMessageId || current.RecoveryResult is null;
                        if (announce)
                        {
                            live.QuietMessageId = current.ActiveMessageId;
                            current.RecoveryResult = "Waiting for NetClaw with no recent model or tool output. RatelDesk keeps observing the original request. An available connection does not prove completion.";
                            AiAssistantChatStore.Append(updateDb, current, "observation_waiting", current.RecoveryResult, atUtc: Now);
                            await updateDb.SaveChangesAsync(ct);
                        }
                        await tx.CommitAsync(ct);
                        live.AdvanceSequence(current.LastSequence);
                        if (announce) feed.Publish(new(id, null));
                        continue;
                    }
                    current.State = ChatState.DeliveryUnknown;
                    AiAssistantChatStore.Append(updateDb, current, "delivery_unknown", "The remote assistant stopped reporting activity. Delivery could not be confirmed; the message will not be resent automatically.", atUtc: Now);
                    await updateDb.SaveChangesAsync(ct);
                    transitioned = true;
                }
                await tx.CommitAsync(ct);
            }
            if (!transitioned) continue;
            feed.Publish(new(id, null));
            await RetireAsync(id, ct);
        }
    }

    private async Task RecordAutomaticRecoveryAsync(Guid id, RecoveryAttempt attempt, CancellationToken ct)
    {
        if (ObserveConnection(id) is { State: ChatTransportConnectionState.Connected }) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (current.State != ChatState.DeliveryUnknown) return;
        current.RecoveryResult = attempt.Count >= AutomaticRecoveryLimit
            ? "Automatic checks paused after six unsuccessful attempts. Check the saved session to try again. The original request was not resent; its remote outcome remains unknown."
            : $"Saved session is unavailable. Next automatic check is due at {attempt.NextUtc:O}. No request or approval was resent. You can also check the saved session manually.";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        feed.Publish(new(id, null));
    }

    private void ClearQuietObservation(HelpdeskDbContext db, AiAssistantChatConversation conversation, Owner owner)
    {
        if (owner.QuietMessageId is null) return;
        owner.QuietMessageId = null;
        conversation.RecoveryResult = null;
        AiAssistantChatStore.Append(db, conversation, "observation_resumed", "NetClaw output resumed for the original request.", atUtc: Now);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken); }
        finally
        {
            await ReleaseOwnershipAsync(CancellationToken.None);
        }
    }

    private async Task ReleaseOwnershipAsync(CancellationToken cancellationToken)
    {
        await ownership.WaitAsync(cancellationToken);
        try
        {
            if (!ready && singleOwner is null && owners.IsEmpty) return;
            ready = false;

            await acquisition.WaitAsync(cancellationToken);
            try
            {
                var activeOwners = owners.Values.ToArray();
                foreach (var owner in activeOwners)
                {
                    try { await RetireAsync(owner); }
                    catch (Exception exception) { logger.LogWarning(exception, "Netclaw chat connection disposal failed during runtime disablement."); }
                }
                owners.Clear();
            }
            finally { acquisition.Release(); }

            if (singleOwner is not null)
            {
                await singleOwner.DisposeAsync();
                singleOwner = null;
            }
        }
        finally { ownership.Release(); }
    }
}
