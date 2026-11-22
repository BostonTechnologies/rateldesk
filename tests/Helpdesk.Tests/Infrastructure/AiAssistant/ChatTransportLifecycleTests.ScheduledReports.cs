using System.Globalization;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed partial class ChatTransportLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduled_report_persists_once_for_the_original_request(bool buffered)
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 18, 10, 30, 0, TimeSpan.Zero));
        var due = clock.GetUtcNow().AddSeconds(30).AddMilliseconds(123);
        await using var run = await Run.CreateAsync(fixture, clock, organization: buffered);
        await run.SendAsync();
        await RegisterAsync(run.Client, "one-shot", due);
        await ReplyAsync(run.Client, "The same valid text.");
        await run.Client.CompleteAsync(1, []);
        if (!buffered)
        {
            await run.WaitForAsync(events => events.Any(item => item.Type == "turn_completed"));
            var pending = await ReloadAsync(run);
            Assert.Equal(ChatState.WaitingForScheduledReports, pending.State);
            Assert.Equal("one-shot", Assert.Single(pending.Continuation!.ScheduledReports!).Id);
            Assert.Single(pending.Events, x => x.Type == "assistant");
            Assert.False(run.Client.Disposed);
            Assert.Empty(pending.BackgroundReports!);
        }
        clock.Advance(TimeSpan.FromSeconds(31));
        await ReplyAsync(run.Client, "The same valid text.");
        await ReportTerminalAsync(run.Client, 2, "one-shot", due);
        await ReportTerminalAsync(run.Client, 2, "one-shot", due);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 2);

        var reloaded = await ReloadAsync(run);
        Assert.Equal(ChatState.Idle, reloaded.State);
        Assert.Empty(reloaded.Continuation!.ScheduledReports!);
        var terminals = reloaded.Events.Where(item => item.Type == "turn_completed")
            .Select(item => ChatContinuationSafety.Read(item.MetadataJson)!).ToArray();
        Assert.Equal(2, terminals.Length);
        Assert.Single(terminals[0].ScheduledReports!);
        Assert.Equal(FireKey("one-shot", due), terminals[1].SourceReminderId);
        Assert.All(terminals, terminal => Assert.Equal(run.Request.ClientMessageId, terminal.OperatorMessageId));
        var replies = reloaded.Events.Where(item => item.Type == "assistant").ToArray();
        Assert.Equal(2, replies.Length);
        Assert.All(replies, reply => Assert.Equal("The same valid text.", reply.Text));
        var finalAssociation = JsonSerializer.Deserialize<ChatOutputAssociation>(replies[1].MetadataJson!)!;
        Assert.Equal(terminals[1].SourceReminderId, finalAssociation.SourceReminderId);
        Assert.Equal(2, finalAssociation.NativeTurnNumber);
        Assert.Equal(run.Request.ClientMessageId, finalAssociation.OperatorMessageId);
        Assert.Single(reloaded.Events, item => item.Type == "operator");
        Assert.Single(run.Client.Sent);
    }

    [Fact]
    public async Task Scheduled_report_during_new_foreground_turn_retains_parent_and_fire_key()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 18, 10, 30, 0, TimeSpan.Zero));
        var due = clock.GetUtcNow().AddMinutes(10);
        await using var run = await Run.CreateAsync(fixture, clock);
        await run.SendAsync();
        await RegisterAsync(run.Client, "status", due);
        await ReplyAsync(run.Client, "Would you like details?");
        await run.Client.CompleteAsync(1, []);
        await run.WaitForAsync(events => events.Any(item => item.Type == "turn_completed"));
        var next = run.Request with { ClientMessageId = Guid.NewGuid(), Text = "Yes, please." };
        await using (var db = fixture.Context())
            Assert.True(await fixture.Store(db).AcceptMessageAsync(run.Scope, next, "operator", default));
        await run.Manager.SendAsync(run.Conversation, next.ClientMessageId, next.Text, default);
        var running = await ReloadAsync(run);
        Assert.Equal(ChatState.Processing, running.State);
        Assert.Equal(run.Request.ClientMessageId, Assert.Single(running.BackgroundReports!).Parent.OperatorMessageId);
        clock.Advance(TimeSpan.FromMinutes(10));
        await ReplyAsync(run.Client, "Stability report");
        await ReportTerminalAsync(run.Client, 2, "status", due);
        await ReplyAsync(run.Client, "Duplicate report");
        await ReportTerminalAsync(run.Client, 2, "status", due);
        await ReplyAsync(run.Client, "Here are the details");
        await run.Client.CompleteAsync(3, []);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 3);
        var final = await ReloadAsync(run);
        Assert.Equal(ChatState.Idle, final.State);
        Assert.Empty(final.BackgroundReports!);
        var report = Assert.Single(final.Events, item => item.Type == "assistant" && item.Text == "Stability report");
        var association = JsonSerializer.Deserialize<ChatOutputAssociation>(report.MetadataJson!)!;
        Assert.Equal(run.Request.ClientMessageId, association.OperatorMessageId);
        Assert.Equal(FireKey("status", due), association.SourceReminderId);
        var answer = Assert.Single(final.Events, item => item.Type == "assistant" && item.Text == "Here are the details");
        Assert.Equal(next.ClientMessageId, JsonSerializer.Deserialize<ChatOutputAssociation>(answer.MetadataJson!)!.OperatorMessageId);
        Assert.DoesNotContain(final.Events, item => item.Text == "Duplicate report");
        Assert.Equal(2, run.Client.Sent.Count);
    }

    [Fact]
    public async Task Stopping_an_older_report_preserves_the_current_processing_request()
    {
        var due = DateTimeOffset.UtcNow.AddMinutes(10);
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        await RegisterAsync(run.Client, "status", due);
        await run.Client.CompleteAsync(1, []);
        await run.WaitForAsync(events => events.Any(item => item.Type == "turn_completed"));
        var next = run.Request with { ClientMessageId = Guid.NewGuid(), Text = "More details" };
        await using (var db = fixture.Context())
            Assert.True(await fixture.Store(db).AcceptMessageAsync(run.Scope, next, "operator", default));
        await run.Manager.SendAsync(run.Conversation, next.ClientMessageId, next.Text, default);
        await ReplyAsync(run.Client, "Answer");
        await using (var db = fixture.Context())
            await fixture.Store(db).StopWaitingAsync(run.Scope, new(run.Conversation, run.Request.ClientMessageId, true), "operator", default);
        var observed = await ReloadAsync(run);
        Assert.Equal(ChatState.Processing, observed.State);
        Assert.Equal(next.ClientMessageId, observed.ActiveMessageId);
        Assert.Empty(observed.BackgroundReports!);
        Assert.Equal(run.Request.ClientMessageId, ChatContinuationSafety.Read(
            Assert.Single(observed.Events, item => item.Type == "scheduled_observation_ended").MetadataJson)!.OperatorMessageId);
        await run.Client.CompleteAsync(2, []);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 2);
        Assert.Equal(ChatState.Idle, (await ReloadAsync(run)).State);
        Assert.Equal(2, run.Client.Sent.Count);
    }

    [Fact]
    public async Task Reports_discharge_by_fire_key_without_consuming_other_reminders_children_or_approvals()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 18, 11, 0, 0, TimeSpan.Zero));
        var due = clock.GetUtcNow().AddSeconds(30);
        await using var run = await Run.CreateAsync(fixture, clock);
        await run.SendAsync();
        await RegisterAsync(run.Client, "report-a", due);
        await RegisterAsync(run.Client, "report-b", due);
        await RegisterAsync(run.Client, "repeat", due, recurring: true);
        await run.Client.EmitApprovalAsync("approval");
        await run.Client.CompleteAsync(1, ["child"]);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 1);
        Assert.Equal(ChatState.AwaitingApproval, (await ReloadAsync(run)).State);

        await ReplyAsync(run.Client, "Wrong reminder");
        await ReportTerminalAsync(run.Client, 2, "foreign", due);
        await ReplyAsync(run.Client, "Wrong occurrence");
        await ReportTerminalAsync(run.Client, 3, "report-a", due.AddSeconds(1));
        await run.Client.EmitAsync(new { type = "text", sessionId = "signalr/foreign", text = "Wrong session" });
        await ReportTerminalAsync(run.Client, 4, "report-a", due, sessionId: "signalr/foreign");
        await ReplyAsync(run.Client, "First report");
        await ReportTerminalAsync(run.Client, 5, "report-a", due);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 2);
        var pending = await ReloadAsync(run);
        Assert.Equal(ChatState.AwaitingApproval, pending.State);
        Assert.Equal(new[] { "report-b", "repeat" }, pending.Continuation!.ScheduledReports!.Select(item => item.Id));
        Assert.Equal("child", Assert.Single(pending.Continuation.PendingChildRunIds!));
        Assert.Equal("First report", Assert.Single(pending.Events, item => item.Type == "assistant").Text);
        await using (var db = fixture.Context())
        {
            var approval = await db.Set<AiAssistantChatInteraction>().SingleAsync(x => x.ConversationId == run.Conversation);
            Assert.Null(approval.SelectedKey);
            Assert.Empty(run.Client.Responses);
        }

        await ReplyAsync(run.Client, "First recurring report");
        await ReportTerminalAsync(run.Client, 6, "repeat", due.AddMinutes(1));
        await ReplyAsync(run.Client, "Duplicate occurrence");
        await ReportTerminalAsync(run.Client, 7, "repeat", due.AddMinutes(1));
        await ReplyAsync(run.Client, "Second report");
        await ReportTerminalAsync(run.Client, 8, "report-b", due);
        await ReplyAsync(run.Client, "Child report");
        await run.Client.CompleteAsync(9, []);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 5);
        var current = await ReloadAsync(run);
        var recurring = Assert.Single(current.Continuation!.ScheduledReports!);
        Assert.True(recurring.Recurring);
        Assert.Equal(due.AddMinutes(1).ToUnixTimeMilliseconds(), recurring.LastFireAtUnixMs);
        Assert.Empty(current.Continuation.PendingChildRunIds!);
        Assert.DoesNotContain(current.Events, item => item.Text == "Duplicate occurrence");

        await run.Client.EmitAsync(new { type = "tool_interaction_closed", sessionId = run.Client.SessionId, callId = "approval", interactionCloseKind = "withdrawn" });
        await run.WaitForAsync(events => events.Any(item => item.Type == "approval_closed"));
        Assert.Equal(ChatState.WaitingForScheduledReports, (await ReloadAsync(run)).State);
        clock.Advance(TimeSpan.FromHours(1));
        await run.Manager.SweepPendingContinuationsAsync(default);
        var expired = await ReloadAsync(run);
        Assert.Equal(ChatState.DeliveryUnknown, expired.State);
        Assert.True(Assert.Single(expired.Continuation!.ScheduledReports!).Unconfirmed);
        Assert.Single(expired.Events, item => item.Type == "scheduled_observation_ended");
        Assert.Contains("window expired", expired.RecoveryResult);
        Assert.True(run.Client.Disposed);
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("completed", true)]
    [InlineData("skipped", false)]
    public async Task A_matching_terminal_without_a_final_preserves_the_unknown_report(string outcome, bool quiet)
    {
        var due = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        await RegisterAsync(run.Client, "quiet-report", due);
        await run.Client.CompleteAsync(1, []);
        await run.Client.CompleteAsync(2, []);
        await ReportTerminalAsync(run.Client, 3, "quiet-report", due, outcome: outcome, quiet: quiet);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 2);
        var snapshot = await ReloadAsync(run);
        Assert.Equal(ChatState.DeliveryUnknown, snapshot.State);
        Assert.True(Assert.Single(snapshot.Continuation!.ScheduledReports!).Unconfirmed);
        Assert.Equal(outcome, snapshot.Continuation.TurnOutcome);
        Assert.Equal(quiet, snapshot.Continuation.QuietEnd);
        Assert.Contains("could not be confirmed", snapshot.RecoveryResult);
        Assert.DoesNotContain(snapshot.Events, item => item.Type == "assistant");
        Assert.Single(run.Client.Sent);
    }

    [Fact]
    public async Task Unsupported_registration_preserves_the_reference_and_does_not_wait_for_an_unmatchable_fire()
    {
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        await run.Client.EmitAsync(new { type = "tool_call", sessionId = run.Client.SessionId, toolName = "set_reminder",
            callId = "unknown-due", argumentsJson = """{"DeliveryKind":"current_session"}""" });
        await run.Client.EmitAsync(new { type = "tool_result", sessionId = run.Client.SessionId, toolName = "set_reminder",
            callId = "unknown-due", result = "Reminder 'Fixture' scheduled. Runs once. Next execution: unknown. The reminder deletes itself after it runs or after you cancel it. Delivery: reply to this session. ID: unknown-due" });
        await run.Client.CompleteAsync(1, []);
        await run.WaitForAsync(events => events.Any(item => item.Type == "turn_completed"));
        var snapshot = await ReloadAsync(run);
        Assert.Equal(ChatState.DeliveryUnknown, snapshot.State);
        var report = Assert.Single(snapshot.Continuation!.ScheduledReports!);
        Assert.Equal("unknown-due", report.Id);
        Assert.True(report.Unconfirmed);
        Assert.Contains("originating SignalR session", snapshot.RecoveryResult);
        Assert.Single(run.Client.Sent);
    }

    [Fact]
    public async Task Disconnected_report_retains_its_reference_without_authorizing_replay_or_late_archive_output()
    {
        var due = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var run = await Run.CreateAsync(fixture, organization: true);
        await run.SendAsync();
        await RegisterAsync(run.Client, "pending-report", due);
        await run.Client.CompleteAsync(1, []);
        await run.WaitForAsync(events => events.Any(item => item.Type == "turn_completed"));
        await run.Client.EmitAsync(new { type = "transport_closed" });
        await run.WaitForAsync(events => events.Any(item => item.Type == "scheduled_observation_ended"));
        var unresolved = await ReloadAsync(run);
        Assert.Equal(ChatState.DeliveryUnknown, unresolved.State);
        Assert.Equal("pending-report", Assert.Single(unresolved.Continuation!.ScheduledReports!).Id);
        var creations = run.Factory.Creations;
        await run.Manager.ReconcileAsync(run.Conversation, default);
        Assert.Equal(creations, run.Factory.Creations);

        await using (var db = fixture.Context())
        {
            var store = fixture.Store(db);
            await Assert.ThrowsAsync<ChatConflictException>(() => store.RequestRecoveryAsync(run.Scope, run.Conversation, "operator", default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.LoadAsync(ChatScope.Organization("foreign"), run.Conversation, 0, "operator", default));
            await store.AbandonAsync(run.Scope, new(run.Conversation, Guid.NewGuid(), run.Request.ClientMessageId, true), "operator", default);
        }
        await ReplyAsync(run.Client, "Late archived reply");
        await ReportTerminalAsync(run.Client, 2, "pending-report", due);
        var archived = await ReloadAsync(run);
        Assert.Equal(ChatState.Archived, archived.State);
        Assert.DoesNotContain(archived.Events, item => item.Type == "assistant");
        Assert.Single(run.Client.Sent);
    }

    [Fact]
    public async Task A_late_old_fire_cannot_borrow_a_newer_operator_request()
    {
        var due = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        await RegisterAsync(run.Client, "old-report", due);
        await run.Client.CompleteAsync(1, []);
        await ReplyAsync(run.Client, "Original report");
        await ReportTerminalAsync(run.Client, 2, "old-report", due);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 2);
        var next = run.Request with { ClientMessageId = Guid.NewGuid(), Text = "An unrelated new question" };
        await using (var db = fixture.Context())
            Assert.True(await fixture.Store(db).AcceptMessageAsync(run.Scope, next, "operator", default));
        var nextClient = new ControlledClient();
        run.Factory.Next = nextClient;
        await run.Manager.SendAsync(run.Conversation, next.ClientMessageId, next.Text, default);
        await ReplyAsync(nextClient, "Stale replay");
        await ReportTerminalAsync(nextClient, 3, "old-report", due);
        await ReplyAsync(nextClient, "New answer");
        await nextClient.CompleteAsync(4, []);
        await run.WaitForAsync(events => events.Count(item => item.Type == "turn_completed") == 3);

        var final = await ReloadAsync(run);
        Assert.Equal(ChatState.Idle, final.State);
        Assert.Equal(next.ClientMessageId, final.Continuation!.OperatorMessageId);
        Assert.Equal(new[] { "Original report", "New answer" }, final.Events.Where(item => item.Type == "assistant").Select(item => item.Text));
        Assert.Single(run.Client.Sent);
        Assert.Single(nextClient.Sent);
    }

    private async Task<ChatSnapshot> ReloadAsync(Run run)
    {
        await using var db = fixture.Context();
        return await fixture.Store(db).LoadAsync(run.Scope, run.Conversation, 0, "operator", default);
    }

    private static async Task RegisterAsync(ControlledClient client, string id, DateTimeOffset due, bool recurring = false)
    {
        await client.EmitAsync(new { type = "tool_call", sessionId = client.SessionId, toolName = "set_reminder",
            callId = id, argumentsJson = """{"DeliveryKind":"current_session"}""" });
        var schedule = recurring ? "Runs every 1 minute." : string.Create(CultureInfo.InvariantCulture,
            $"Runs once. Next execution: Wednesday, November 18 at 10:30 AM UTC ({due:u}). The reminder deletes itself after it runs or after you cancel it.");
        await client.EmitAsync(new { type = "tool_result", sessionId = client.SessionId, toolName = "set_reminder",
            callId = id, result = $"Reminder 'Fixture report' scheduled. {schedule} Delivery: reply to this session. ID: {id}" });
    }

    private static Task ReplyAsync(ControlledClient client, string text) => client.EmitAsync(new { type = "text", sessionId = client.SessionId, text });

    private static Task ReportTerminalAsync(ControlledClient client, int turn, string id, DateTimeOffset due,
        string? sessionId = null, string outcome = "completed", bool quiet = false) =>
        client.EmitAsync(new { type = "turn_completed", sessionId = sessionId ?? client.SessionId, turnNumber = turn,
            turnOutcome = outcome, quietEnd = quiet, openChildRunIds = Array.Empty<string>(), sourceReminderId = FireKey(id, due) });

    private static string FireKey(string id, DateTimeOffset due) =>
        string.Create(CultureInfo.InvariantCulture, $"{id}:{due.ToUnixTimeMilliseconds()}");
}
