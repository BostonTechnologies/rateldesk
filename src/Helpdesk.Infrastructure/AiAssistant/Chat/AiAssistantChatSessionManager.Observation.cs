using System.Diagnostics.Metrics;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

internal static class ChatTelemetry
{
    private static readonly Meter Meter = new("RatelDesk.Chat");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("rateldesk.chat.lifecycle.events");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("rateldesk.chat.lifecycle.elapsed", "s");

    public static void Record(string phase, string? outcome = null, double? seconds = null)
    {
        Events.Add(1, new KeyValuePair<string, object?>("phase", phase), new("outcome", outcome));
        if (seconds is >= 0) Duration.Record(seconds.Value, new KeyValuePair<string, object?>("phase", phase), new("outcome", outcome));
    }
}

public sealed partial class AiAssistantChatSessionManager
{
    private bool CanObserveStoppedTurn(AiAssistantChatConversation conversation, Owner owner) =>
        owner.SendStarted && owner.MessageId == conversation.ActiveMessageId &&
        owner.SessionId == conversation.AiAssistantSessionId && owner.ProfileFingerprint == conversation.ProviderProfileFingerprint &&
        owner.ProfileFingerprint == runtime.Current.ProfileFingerprint;

    internal static bool MatchesStoppedTerminal(int? previous, JsonElement output) =>
        previous is >= 0 and < int.MaxValue && output.TryGetProperty("turnNumber", out var turn) &&
        turn.ValueKind == JsonValueKind.Number && turn.TryGetInt32(out var ordinal) && ordinal == previous + 1;

    private static async Task<int?> LatestNativeOrdinalAsync(HelpdeskDbContext db, Guid id, CancellationToken ct)
    {
        var json = await db.Set<AiAssistantChatEvent>().AsNoTracking()
            .Where(x => x.ConversationId == id && x.Type == "turn_completed")
            .OrderByDescending(x => x.Sequence).Select(x => x.MetadataJson).FirstOrDefaultAsync(ct);
        return ChatContinuationSafety.Read(json)?.TurnNumber;
    }

    private static Task<DateTimeOffset?> StopTimeAsync(HelpdeskDbContext db, AiAssistantChatConversation conversation, CancellationToken ct) =>
        db.Set<AiAssistantChatEvent>().AsNoTracking().Where(x => x.ConversationId == conversation.Id && x.Type == "observation_stopped"
            && x.Sequence > db.Set<AiAssistantChatEvent>().Where(e => e.ConversationId == conversation.Id &&
                e.Type == "operator" && e.ClientMessageId == conversation.ActiveMessageId).Max(e => e.Sequence))
            .OrderByDescending(x => x.Sequence).Select(x => (DateTimeOffset?)x.CreatedUtc).FirstOrDefaultAsync(ct);

    private async Task SweepStoppedObservationsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var attached = owners.Keys.ToArray();
        var rows = await db.Set<AiAssistantChatConversation>().AsNoTracking().Where(x => attached.Contains(x.Id)
            && x.State == ChatState.DeliveryUnknown).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (owners.TryGetValue(row.Id, out var owner) && CanObserveStoppedTurn(row, owner)
                && await StopTimeAsync(db, row, ct) is { } stopped && stopped + ChatContinuationSafety.ObservationLimit <= Now)
            {
                ObserveLifecycle("late_observation_expired", row, owner, "unknown");
                await RetireAsync(row.Id, ct);
            }
        }
    }

    private async Task CloseSavedApprovalAsync(HelpdeskDbContext db, AiAssistantChatConversation conversation,
        Owner owner, JsonElement output, string callId, CancellationToken ct)
    {
        var kind = Value(output, "interactionCloseKind");
        if (kind is not ("answered" or "withdrawn")) return;
        var active = await db.Set<AiAssistantChatEvent>().AnyAsync(x => x.ConversationId == conversation.Id &&
            x.Type == "approval_request" && x.CallId == callId &&
            x.Sequence > db.Set<AiAssistantChatEvent>().Where(e => e.ConversationId == conversation.Id &&
                e.Type == "operator" && e.ClientMessageId == conversation.ActiveMessageId).Max(e => e.Sequence), ct);
        if (!active || await db.Set<AiAssistantChatEvent>().AnyAsync(x =>
            x.ConversationId == conversation.Id && x.Type == "approval_closed" && x.CallId == callId, ct)) return;
        var interaction = await db.Set<AiAssistantChatInteraction>().SingleOrDefaultAsync(x =>
            x.ConversationId == conversation.Id && x.CallId == callId, ct);
        if (interaction is null) return;
        interaction.SelectedKey ??= "closed";
        if (conversation.State == ChatState.AwaitingApproval && !await db.Set<AiAssistantChatInteraction>().AnyAsync(x =>
            x.ConversationId == conversation.Id && x.CallId != callId && x.SelectedKey == null, ct))
            conversation.State = HasChildren(owner.Continuation) ? ChatState.WaitingForChildren : ChatState.Processing;
        AiAssistantChatStore.Append(db, conversation, "approval_closed",
            kind == "answered" ? "NetClaw closed the original approval as answered." : "NetClaw withdrew the original approval.",
            callId: callId, atUtc: Now);
    }

    private static bool IsMeaningfulOutput(string? type) => type is "text" or "text_delta" or "buffer_flush" or "turn_completed" or
        "tool_call" or "tool_result" or "tool_interaction" or "tool_interaction_closed" or "error" or "subagent" or "file";

    private static string NativeErrorCategory(JsonElement output) => Value(output, "errorCategory") switch
    {
        "ProviderFailure" => "ProviderFailure",
        "ToolFailure" => "ToolFailure",
        "SessionFailure" => "SessionFailure",
        _ => "UnknownError"
    };

    private static string? NativeErrorCorrelation(JsonElement output) =>
        Guid.TryParse(Value(output, "errorCorrelationId"), out var reference) ? reference.ToString("N") : null;

    private void ObserveLifecycle(string phase, AiAssistantChatConversation conversation, Owner owner, string? outcome = null, string? nativeCorrelation = null)
    {
        ChatTelemetry.Record(phase, outcome, conversation.TurnStartedAtUtc is { } accepted ? (Now - accepted).TotalSeconds : null);
        logger.LogInformation("NetClaw chat {Phase}: conversation {ConversationId}, organization {OrganizationId}, message {MessageId}, native session {SessionId}, turn {NativeTurn}, outcome {Outcome}, native correlation {NativeCorrelation}",
            phase, conversation.Id, conversation.OrganizationId, owner.MessageId, owner.SessionId, owner.LastCompletedTurn, outcome, nativeCorrelation);
    }

    private void ObserveLifecycle(string phase, Guid conversation, Owner owner)
    {
        ChatTelemetry.Record(phase);
        logger.LogInformation("NetClaw chat {Phase}: conversation {ConversationId}, message {MessageId}, native session {SessionId}",
            phase, conversation, owner.MessageId, owner.SessionId);
    }
}
