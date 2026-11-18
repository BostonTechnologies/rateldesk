using System.Net;
using System.Threading.Channels;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed partial class OpenAiChatRun(HelpdeskDbContext db, OpenAiChatProfiles profiles, OpenAiAgentsHttpClient http,
    IChatProviderMcpAuthority authority, IChatLiveFeed feed, TimeProvider clock, ILogger<OpenAiChatRun> logger)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    internal async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        var conversation = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(row => row.Id == id, ct);
        var grant = await db.Set<OpenAiChatSessionGrant>().AsNoTracking().SingleOrDefaultAsync(row => row.ConversationId == id, ct);
        if (grant is { RemoteDeletedAtUtc: null } && (grant.CleanupRequestedAtUtc is not null || grant.ExpiresAtUtc <= Now))
        {
            await CleanupAsync(conversation, grant, ct);
            return;
        }
        if (conversation.State == ChatState.Archived || conversation.ActiveMessageId is not { } messageId)
            return;
        var dispatch = await db.Set<OpenAiChatDispatch>().AsNoTracking().SingleOrDefaultAsync(
            row => row.ConversationId == id && row.MessageId == messageId, ct);
        if (dispatch is null || dispatch.State is OpenAiDispatchState.Completed or OpenAiDispatchState.Rejected ||
            dispatch.NextObservationAtUtc > Now) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));

        var observationMinutes = 30;
        try
        {
            var token = deadline.Token;
            var (profile, revision) = await profiles.ResolveAsync(conversation, token);
            observationMinutes = profile.ObservationMinutes;
            var client = http.Client;
            if (dispatch.State == OpenAiDispatchState.Queued && dispatch.CancelRequestedAtUtc is not null)
            {
                await MarkTerminalAsync(dispatch, "turn_cancelled", "The OpenAI turn was cancelled before it was submitted.", token);
                return;
            }
            if (conversation.AiAssistantSessionId is null)
            {
                var remote = await CreateSessionAsync(client, conversation, dispatch, profile, revision, token);
                if (remote is null) return;
                conversation.AiAssistantSessionId = remote;
            }

            var adapter = new OpenAiAgentsClient(client, profiles.CreateConfiguration(profile, revision, null));
            await ObserveAsync(adapter, conversation, dispatch, profile, token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException
            or System.Text.Json.JsonException or ChatConflictException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            await SaveObservationFailureAsync(dispatch, ex, observationMinutes, ct);
        }
    }

    private async Task<string?> CreateSessionAsync(HttpClient client, AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        OpenAiChatProfileSettings profile, OpenAiChatProfileRevision revision, CancellationToken ct)
    {
        if (dispatch.State != OpenAiDispatchState.Queued)
        {
            await ChangeAsync(dispatch, (row, current) =>
            {
                row.State = OpenAiDispatchState.Unknown;
                row.NextObservationAtUtc = DateTimeOffset.MaxValue;
                Unknown(current, "The OpenAI create request has no confirmed session ID. It will not be submitted again.");
            }, ct);
            return null;
        }
        if (!await profiles.IsCurrentAsync(conversation, ct))
        {
            await MarkTerminalAsync(dispatch, "error", "The OpenAI profile changed before this request was sent.", ct);
            return null;
        }
        var credentialId = Guid.NewGuid();
        var recorded = await ChangeAsync(dispatch, (row, current) =>
        {
            if (row.State != OpenAiDispatchState.Queued || row.CancelRequestedAtUtc is not null)
                return false;
            row.State = OpenAiDispatchState.Dispatching;
            row.DispatchedAtUtc = Now;
            db.Add(new OpenAiChatSessionGrant { ConversationId = current.Id, CredentialId = credentialId,
                ExpiresAtUtc = Now.AddHours(8) });
            return true;
        }, ct);
        if (!recorded) return null;
        var sent = false;
        try
        {
            var grant = await authority.IssueAsync(conversation, profile, credentialId, ct);
            var config = profiles.CreateConfiguration(profile, revision, grant.Token);
            var adapter = new OpenAiAgentsClient(client, config);
            var outbound = await OutboundAsync(conversation, dispatch, ct);
            if (!await authority.IsGrantUsableAsync(conversation, credentialId, ct))
                throw new ChatConflictException("The session's MCP authority changed before dispatch.");
            sent = true;
            var remote = await adapter.CreateAsync(conversation.Id, outbound, ct);
            await ChangeAsync(dispatch, (row, current) =>
            {
                if (current.AiAssistantSessionId is not null && current.AiAssistantSessionId != remote)
                    throw new ChatConflictException("The OpenAI session binding changed.");
                current.AiAssistantSessionId = remote;
                row.State = OpenAiDispatchState.Admitted;
                AiAssistantChatStore.Append(db, current, "provider_admitted", "OpenAI created the session and admitted the first message.", atUtc: Now);
            }, ct, allowArchived: true);
            dispatch.State = OpenAiDispatchState.Admitted;
            return remote;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (!sent || IsRejection(ex)))
        {
            await authority.RevokeAsync(credentialId, ct);
            await ChangeAsync(dispatch, (_, _) => db.Set<OpenAiChatSessionGrant>().Remove(
                new OpenAiChatSessionGrant { ConversationId = conversation.Id }), ct, allowArchived: true);
            await MarkTerminalAsync(dispatch, "error", SafeFailure(ex), ct);
            return null;
        }
    }

    private async Task ObserveAsync(OpenAiAgentsClient adapter, AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch,
        OpenAiChatProfileSettings profile, CancellationToken ct)
    {
        var sessionId = conversation.AiAssistantSessionId!;
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IChatProviderObservation? connected = null;
        try
        {
            connected = await adapter.ObserveAsync(sessionId, streamLifetime.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && dispatch.State != OpenAiDispatchState.Queued &&
            ex is HttpRequestException or IOException or InvalidDataException)
        {
            logger.LogDebug("OpenAI observation stream was unavailable for {ConversationId}: {FailureType}", conversation.Id, ex.GetType().Name);
        }
        await using var observation = connected;
        var channel = Channel.CreateBounded<ProviderStreamEvent>(32);
        var stream = connected is null ? Task.CompletedTask : PumpAsync(connected, channel.Writer, streamLifetime.Token);
        try
        {
            var history = await adapter.RetrieveAsync(sessionId, ct);
            if (dispatch.State == OpenAiDispatchState.Queued)
            {
                if (connected is null) return;
                if (!await SubmitAsync(adapter, conversation, dispatch, history, ct)) return;
                history = await adapter.RetrieveAsync(sessionId, ct);
            }
            if (await ReconcileAsync(adapter, conversation, dispatch, profile, history, ct)) return;

            // Catch live output while the saved history catches up. A new slice
            // opens a new stream and reconciles again, without a remote cursor.
            if (connected is null) return;
            using var slice = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slice.CancelAfter(TimeSpan.FromSeconds(8));
            var finalized = history.Items.Where(item => item.Id is not null && item.Status == "completed")
                .Select(item => item.Id!).ToHashSet(StringComparer.Ordinal);
            var drafts = new OpenAiTextDrafts();
            try
            {
                await foreach (var next in channel.Reader.ReadAllAsync(slice.Token))
                {
                    if (next.Kind == ProviderStreamEventKind.HistoryChanged) break;
                    if (dispatch.TurnId is not null && next.SubagentId is null &&
                        (next.TurnId is null || next.TurnId == dispatch.TurnId) && next.ItemId is { } itemId &&
                        !finalized.Contains(itemId) && drafts.Apply(next) is { } draft)
                        feed.Publish(new(conversation.Id, draft, conversation.LastSequence, dispatch.MessageId));
                }
            }
            catch (OperationCanceledException) when (slice.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                logger.LogTrace("Checking OpenAI saved history after the observation interval.");
            }

            history = await adapter.RetrieveAsync(sessionId, ct);
            await ReconcileAsync(adapter, conversation, dispatch, profile, history, ct);
        }
        finally
        {
            await streamLifetime.CancelAsync();
            await stream;
        }
    }

    private static async Task PumpAsync(IChatProviderObservation observation, ChannelWriter<ProviderStreamEvent> output, CancellationToken ct)
    {
        try
        {
            await foreach (var item in observation.ReadAsync(ct)) await output.WriteAsync(item, ct);
            output.TryComplete();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { output.TryComplete(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or System.Text.Json.JsonException)
        {
            // The consumer retrieves authoritative state after disconnection or malformed data.
            output.TryComplete();
        }
    }

    private async Task<bool> SubmitAsync(OpenAiAgentsClient adapter, AiAssistantChatConversation conversation,
        OpenAiChatDispatch dispatch, ProviderSessionHistory history, CancellationToken ct)
    {
        if (history.Status == "failed")
        {
            await MarkTerminalAsync(dispatch, "error", "The OpenAI session failed. Start a new conversation to continue.", ct);
            return false;
        }
        if (history.Status != "idle" || history.PendingActions.Count != 0 || history.Turns.Any(IsRunning))
        {
            await ChangeAsync(dispatch, (row, _) => row.NextObservationAtUtc = Now.AddSeconds(5), ct);
            return false;
        }
        var credentialId = await CredentialIdAsync(conversation.Id, ct);
        if (!await profiles.IsCurrentAsync(conversation, ct) || !await authority.IsGrantUsableAsync(conversation, credentialId, ct))
        {
            await MarkTerminalAsync(dispatch, "error", "The OpenAI profile or scoped tool access changed before this request was sent.", ct);
            return false;
        }
        var outbound = await OutboundAsync(conversation, dispatch, ct);
        var claimed = await ChangeAsync(dispatch, (row, _) =>
        {
            if (row.CancelRequestedAtUtc is not null || row.State != OpenAiDispatchState.Queued) return false;
            row.State = OpenAiDispatchState.Dispatching;
            row.DispatchedAtUtc = Now;
            return true;
        }, ct);
        if (!claimed) return false;

        await adapter.SubmitAsync(conversation.AiAssistantSessionId!, "message-" + dispatch.MessageId.ToString("N"), outbound, ct);
        await ChangeAsync(dispatch, (row, current) =>
        {
            row.State = OpenAiDispatchState.Admitted;
            AiAssistantChatStore.Append(db, current, "provider_admitted", "OpenAI accepted the message for processing.", atUtc: Now);
        }, ct);
        dispatch.State = OpenAiDispatchState.Admitted;
        return true;
    }

    private async Task<string> OutboundAsync(AiAssistantChatConversation conversation, OpenAiChatDispatch dispatch, CancellationToken ct)
    {
        var message = await db.Set<AiAssistantChatEvent>().AsNoTracking().SingleAsync(row =>
            row.ConversationId == conversation.Id && row.ClientMessageId == dispatch.MessageId && row.Type == "operator", ct);
        if (message.CreatedByUserId != dispatch.ActorId)
            throw new ChatConflictException("The admitted message actor no longer matches the provider binding.");
        return await ChatOutboundMessage.BuildAsync(db, conversation, message, dispatch.MessageId, ct);
    }

    private async Task SaveObservationFailureAsync(OpenAiChatDispatch dispatch, Exception error, int observationMinutes, CancellationToken ct)
    {
        logger.LogWarning("OpenAI request for {ConversationId} reported {FailureType}", dispatch.ConversationId, error.GetType().Name);
        await ChangeAsync(dispatch, (row, current) =>
        {
            if (row.State is OpenAiDispatchState.Completed or OpenAiDispatchState.Rejected) return;
            if (row.State == OpenAiDispatchState.Dispatching && current.AiAssistantSessionId is null)
            {
                row.State = OpenAiDispatchState.Unknown;
                row.NextObservationAtUtc = DateTimeOffset.MaxValue;
                Unknown(current, "The OpenAI create request has no confirmed session ID. It will not be submitted again.");
                return;
            }
            if (row.State == OpenAiDispatchState.Dispatching && IsRejection(error) ||
                error is OpenAiRequestException { StatusCode: HttpStatusCode.NotFound } && current.AiAssistantSessionId is not null)
            {
                Complete(row, current, "error", SafeFailure(error));
                return;
            }
            if (row.State == OpenAiDispatchState.Dispatching) row.State = OpenAiDispatchState.Unknown;
            var retry = error is OpenAiRequestException { RetryAfter: { } delay }
                ? TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 5, 300)) : TimeSpan.FromSeconds(10);
            var limit = (row.DispatchedAtUtc ?? row.AdmittedAtUtc).AddMinutes(observationMinutes);
            if (current.RecoveryLastAttemptUtc > limit) limit = current.RecoveryLastAttemptUtc.Value.AddMinutes(2);
            row.NextObservationAtUtc = Now < limit ? Now + retry : DateTimeOffset.MaxValue;
            Unknown(current, SafeFailure(error));
        }, ct);
    }

    private async Task CleanupAsync(AiAssistantChatConversation conversation, OpenAiChatSessionGrant grant, CancellationToken ct)
    {
        if (grant.CleanupNextAttemptAtUtc > Now) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string result;
        var completed = false;
        try
        {
            await authority.RevokeAsync(grant.CredentialId, timeout.Token);
            if (conversation.AiAssistantSessionId is { } session)
            {
                var (settings, revision) = await profiles.ResolveAsync(conversation, timeout.Token);
                var client = http.Client;
                var adapter = new OpenAiAgentsClient(client, profiles.CreateConfiguration(settings, revision, null));
                await adapter.DeleteAsync(session, timeout.Token);
            }
            completed = true;
            result = conversation.AiAssistantSessionId is null
                ? "The local tool grant was revoked. No remote session ID was confirmed."
                : "The OpenAI session was deleted. The local transcript and saved artifacts remain available.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI cleanup for {ConversationId} reported {FailureType}", conversation.Id, ex.GetType().Name);
            result = "OpenAI cleanup is pending and will be checked again.";
        }
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await LockAsync(conversation.Id, ct);
        var row = await db.Set<OpenAiChatSessionGrant>().SingleAsync(x => x.ConversationId == current.Id, ct);
        if (completed)
        {
            row.RemoteDeletedAtUtc = Now;
            var active = await db.Set<OpenAiChatDispatch>().SingleOrDefaultAsync(x => x.ConversationId == current.Id &&
                x.MessageId == current.ActiveMessageId && x.State != OpenAiDispatchState.Completed && x.State != OpenAiDispatchState.Rejected, ct);
            if (active is not null && current.State != ChatState.Archived)
            {
                active.NextObservationAtUtc = DateTimeOffset.MaxValue;
                Unknown(current, "The OpenAI session lifetime expired. Remote work outcomes could not be confirmed.");
            }
        }
        row.CleanupNextAttemptAtUtc = Now.AddMinutes(15);
        AiAssistantChatStore.Append(db, current, completed ? "remote_cleanup" : "remote_cleanup_pending", result, atUtc: Now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        feed.Publish(new(current.Id, null));
    }

    private Task<Guid> CredentialIdAsync(Guid id, CancellationToken ct) =>
        db.Set<OpenAiChatSessionGrant>().AsNoTracking().Where(row => row.ConversationId == id).Select(row => row.CredentialId).SingleAsync(ct);

    private async Task<bool> ChangeAsync(OpenAiChatDispatch target, Func<OpenAiChatDispatch, AiAssistantChatConversation, bool> change,
        CancellationToken ct, bool allowArchived = false)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await LockAsync(target.ConversationId, ct);
        if (current.ActiveMessageId != target.MessageId || (!allowArchived && current.State == ChatState.Archived)) return false;
        var row = await db.Set<OpenAiChatDispatch>().SingleAsync(item =>
            item.ConversationId == target.ConversationId && item.MessageId == target.MessageId, ct);
        if (!change(row, current)) return false;
        var updated = db.ChangeTracker.HasChanges();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (updated) feed.Publish(new(current.Id, null));
        return true;
    }

    private Task<bool> ChangeAsync(OpenAiChatDispatch dispatch, Action<OpenAiChatDispatch, AiAssistantChatConversation> change,
        CancellationToken ct, bool allowArchived = false) => ChangeAsync(dispatch, (row, conversation) =>
        {
            change(row, conversation);
            return true;
        }, ct, allowArchived);

    private Task<AiAssistantChatConversation> LockAsync(Guid id, CancellationToken ct) =>
        db.Set<AiAssistantChatConversation>().FromSqlInterpolated(
            $"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);

    private Task<bool> MarkTerminalAsync(OpenAiChatDispatch dispatch, string type, string result, CancellationToken ct)
        => ChangeAsync(dispatch, (row, current) => Complete(row, current, type, result), ct);

    private void Complete(OpenAiChatDispatch row, AiAssistantChatConversation conversation, string type, string result)
    {
        row.State = OpenAiDispatchState.Completed;
        conversation.State = ChatState.Idle;
        conversation.RecoveryResult = null;
        AiAssistantChatStore.Append(db, conversation, type, result, atUtc: Now);
    }

    private void Unknown(AiAssistantChatConversation conversation, string reason)
    {
        if (conversation.State == ChatState.DeliveryUnknown && conversation.RecoveryResult == reason) return;
        conversation.State = ChatState.DeliveryUnknown;
        conversation.RecoveryResult = reason;
        AiAssistantChatStore.Append(db, conversation, "delivery_unknown", reason, atUtc: Now);
    }

    private static bool IsRejection(Exception error) => error is OpenAiRequestException
    {
        StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests
    };

    private static string SafeFailure(Exception error) => error switch
    {
        OpenAiRequestException request => request.Message,
        ChatConflictException conflict => conflict.Message,
        _ => "OpenAI observation was interrupted. The worker checks saved history without resending the request."
    };

    private static bool IsRunning(ProviderTurn turn) => turn.Status is "queued" or "in_progress" or "waiting";
}
