using System.Text.Json;
using System.Threading.Channels;
using Helpdesk.Application.Events;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class ChatTransportLifecycleTests(ChatPostgresFixture fixture) : IClassFixture<ChatPostgresFixture>
{
    [Fact]
    public async Task FinalApprovalRenewsProviderProcessingLease()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        await using var run = await Run.CreateAsync(fixture, clock);
        await run.SendAsync();
        await run.Client.EmitApprovalAsync("call-one");
        await run.WaitForAsync(events => events.Any(x => x.Type == "approval_request"));

        clock.Advance(TimeSpan.FromMinutes(20));
        await using (var db = fixture.Context())
        {
            await fixture.Store(db, timeProvider: clock).AcceptApprovalAsync("incidents", run.Ticket, "call-one", new(run.Conversation, "approve_once"), "approver", default);
            var accepted = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation);
            Assert.Equal(ChatState.Processing, accepted.State);
            Assert.Equal(clock.GetUtcNow(), accepted.LastTransportActivityAtUtc);
        }
        await run.Manager.RespondAsync(run.Conversation, "call-one", "approve_once", default);
        Assert.Single(run.Client.Responses);

        await run.Manager.SweepStaleProcessingAsync(default);
        await AssertStateAsync(ChatState.Processing);
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await run.Manager.SweepStaleProcessingAsync(default);
        await AssertStateAsync(ChatState.Processing);
        clock.Advance(TimeSpan.FromSeconds(2));
        await run.Manager.SweepStaleProcessingAsync(default);
        await AssertStateAsync(ChatState.DeliveryUnknown);

        async Task AssertStateAsync(ChatState expected)
        {
            await using var check = fixture.Context();
            Assert.Equal(expected, await check.Set<AiAssistantChatConversation>().AsNoTracking()
                .Where(x => x.Id == run.Conversation).Select(x => x.State).SingleAsync());
        }
    }

    [Fact]
    public async Task ImmediateApprovalDeltaUsesCommittedDecisionBoundary()
    {
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        await run.Client.EmitApprovalAsync("call-one");
        await run.WaitForAsync(events => events.Any(x => x.Type == "approval_request"));
        long decisionHead;
        await using (var db = fixture.Context())
        {
            await fixture.Store(db).AcceptApprovalAsync("incidents", run.Ticket, "call-one", new(run.Conversation, "approve_once"), "approver", default);
            decisionHead = (await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation)).LastSequence;
        }
        run.Client.TextOnResponse = "Immediate resumed text";
        await run.Manager.RespondAsync(run.Conversation, "call-one", "approve_once", default);
        var delta = await run.WaitForDeltaAsync("Immediate resumed text");
        Assert.Equal(decisionHead, delta.AfterSequence);
        await using var check = fixture.Context();
        Assert.Equal(decisionHead, (await check.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation)).LastSequence);
    }

    [Fact]
    public async Task SharedApprovalHasOneAuditedWinnerAndCompletesThroughTransport()
    {
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        await run.Client.EmitApprovalAsync("call-one");
        await run.WaitForAsync(events => events.Any(x => x.Type == "approval_request"));

        async Task<bool> SelectAsync(string actor)
        {
            await using var db = fixture.Context();
            try
            {
                await fixture.Store(db).AcceptApprovalAsync("incidents", run.Ticket, "call-one", new(run.Conversation, "approve_once"), actor, default);
                return true;
            }
            catch (ChatConflictException) { return false; }
        }

        var winners = await Task.WhenAll(SelectAsync("operator-a"), SelectAsync("operator-b"));
        Assert.Single(winners, x => x);
        await using (var db = fixture.Context())
        {
            var selected = await db.Set<AiAssistantChatInteraction>().SingleAsync(x => x.ConversationId == run.Conversation);
            Assert.Equal(winners[0] ? "operator-a" : "operator-b", selected.AnsweredByUserId);
            await Assert.ThrowsAsync<ChatConflictException>(() => fixture.Store(db).AcceptApprovalAsync("incidents", run.Ticket, "call-one", new(run.Conversation, "deny"), "operator-c", default));
        }

        await run.Manager.RespondAsync(run.Conversation, "call-one", "approve_once", default);
        Assert.Equal((run.Client.SessionId, "call-one", "approve_once"), Assert.Single(run.Client.Responses));
        await run.Client.EmitAsync(new { type = "tool_result", sessionId = run.Client.SessionId, toolName = "safe_tool", callId = "call-one", result = "RAW_RESULT_MUST_NOT_PERSIST" });
        await run.Client.EmitAsync(new { type = "text", sessionId = run.Client.SessionId, text = "Approved work completed." });
        await run.Client.EmitAsync(new { type = "turn_completed", sessionId = run.Client.SessionId });
        var events = await run.WaitForAsync(events => events.Any(x => x.Type == "turn_completed"));
        Assert.Single(events, x => x.Type == "approval_response");
        Assert.Single(events, x => x.Type == "assistant");
        Assert.DoesNotContain("RAW_RESULT_MUST_NOT_PERSIST", JsonSerializer.Serialize(events));
        await using var check = fixture.Context();
        Assert.Equal(ChatState.Idle, (await check.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation)).State);
    }

    [Fact]
    public async Task FailedDecisionBecomesUnknownWhileAnotherApprovalRemainsPending()
    {
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        await run.Client.EmitApprovalAsync("call-one");
        await run.Client.EmitApprovalAsync("call-two");
        await run.WaitForAsync(events => events.Count(x => x.Type == "approval_request") == 2);
        await using (var db = fixture.Context())
            await fixture.Store(db).AcceptApprovalAsync("incidents", run.Ticket, "call-one", new(run.Conversation, "approve_once"), "approver", default);
        run.Client.FailResponse = true;
        await run.Manager.RespondAsync(run.Conversation, "call-one", "approve_once", default);
        await using var check = fixture.Context();
        Assert.Equal(ChatState.DeliveryUnknown, (await check.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation)).State);
        Assert.Null((await check.Set<AiAssistantChatInteraction>().SingleAsync(x => x.ConversationId == run.Conversation && x.CallId == "call-two")).SelectedKey);
        Assert.Single(run.Client.Responses);
        await Assert.ThrowsAsync<ChatConflictException>(() => fixture.Store(check).AcceptApprovalAsync("incidents", run.Ticket, "call-two", new(run.Conversation, "deny"), "other", default));
    }

    [Theory]
    [InlineData("matched", true)]
    [InlineData("assistant-quote", false)]
    [InlineData("different-message", false)]
    [InlineData("legacy", false)]
    [InlineData("duplicate", false)]
    public async Task RecoveryRequiresExactUserMarkerAndNeverInfersCompletion(string historyKind, bool admissionConfirmed)
    {
        await using var run = await Run.CreateAsync(fixture);
        run.Client.FailSend = true;
        await run.SendAsync();
        var original = Assert.Single(run.Client.Sent);
        var history = historyKind switch
        {
            "matched" => new[] { new { role = "user", content = original }, new { role = "assistant", content = "Potentially intermediate response" } },
            "assistant-quote" => new[] { new { role = "assistant", content = original } },
            "different-message" => new[] { new { role = "user", content = original.Replace(run.Request.ClientMessageId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal) } },
            "legacy" => new[] { new { role = "user", content = run.Request.Text } },
            _ => new[] { new { role = "user", content = original }, new { role = "user", content = original } }
        };
        var resumed = new ControlledClient { RecentMessages = JsonSerializer.SerializeToElement(history) };
        run.Factory.Next = resumed;
        await run.Manager.ReconcileAsync(run.Conversation, default);
        Assert.Equal(run.Client.SessionId, resumed.RequestedSessionId);
        Assert.True(run.Client.Disposed);
        Assert.Single(run.Client.Sent);
        Assert.Empty(resumed.Sent);
        await using var db = fixture.Context();
        Assert.Equal(ChatState.DeliveryUnknown, (await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation)).State);
        var events = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == run.Conversation).ToListAsync();
        Assert.Contains(events, x => x.Type == (admissionConfirmed ? "recovery_admission_confirmed" : "recovery_inconclusive"));
        Assert.DoesNotContain(events, x => x.Type is "assistant" or "turn_completed");
        if (admissionConfirmed)
        {
            await resumed.EmitAsync(new { type = "text", sessionId = resumed.SessionId, text = "Authoritative final output" });
            await resumed.EmitAsync(new { type = "turn_completed", sessionId = resumed.SessionId });
            var completed = await run.WaitForAsync(x => x.Any(e => e.Type == "turn_completed"));
            Assert.Equal("Authoritative final output", Assert.Single(completed, x => x.Type == "assistant").Text);
        }
    }

    [Fact]
    public async Task ArchiveDuringConnectionCannotWriteSessionBindingOrSend()
    {
        await using var run = await Run.CreateAsync(fixture);
        run.Client.ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = run.SendAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await run.Client.ConnectStarted.Task.WaitAsync(timeout.Token);
        await using (var db = fixture.Context())
        {
            var conversation = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation, timeout.Token);
            conversation.State = ChatState.Archived;
            await db.SaveChangesAsync(timeout.Token);
        }
        run.Client.ConnectGate.TrySetResult();
        await sending.WaitAsync(timeout.Token);
        await using var check = fixture.Context();
        var archived = await check.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation, timeout.Token);
        Assert.Equal(ChatState.Archived, archived.State);
        Assert.Null(archived.AiAssistantSessionId);
        Assert.Empty(run.Client.Sent);
        Assert.True(run.Client.Disposed);
    }

    [Fact]
    public async Task FailedReconnectPersistsSafeOutcomeWithoutResending()
    {
        await using var run = await Run.CreateAsync(fixture);
        run.Client.FailSend = true;
        await run.SendAsync();
        var failed = new ControlledClient { FailConnect = true };
        run.Factory.Next = failed;
        await run.Manager.ReconcileAsync(run.Conversation, default);
        await using var db = fixture.Context();
        Assert.Equal(ChatState.DeliveryUnknown, (await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation)).State);
        var events = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == run.Conversation).ToListAsync();
        Assert.Contains(events, x => x.Type == "recovery_inconclusive");
        Assert.DoesNotContain("PRIVATE_PROVIDER_DETAIL", JsonSerializer.Serialize(events));
        Assert.Single(run.Client.Sent);
        Assert.Empty(failed.Sent);
        Assert.True(failed.Disposed);
    }

    [Fact]
    public async Task ArchivedConversationRejectsLateOutputAndFurtherSending()
    {
        await using var run = await Run.CreateAsync(fixture);
        await run.SendAsync();
        long archivedSequence;
        await using (var db = fixture.Context())
        {
            var conversation = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation);
            conversation.State = ChatState.Archived;
            archivedSequence = conversation.LastSequence;
            await db.SaveChangesAsync();
        }
        await run.Client.EmitAsync(new { type = "text", sessionId = run.Client.SessionId, text = "Late remote response" });
        await run.Client.EmitAsync(new { type = "turn_completed", sessionId = run.Client.SessionId });
        // This attempt retires and drains the cached owner, providing a deterministic
        // barrier before checking that late callbacks left the archive untouched.
        await run.Manager.SendAsync(run.Conversation, run.Request.ClientMessageId, run.Request.Text, default);
        await using var check = fixture.Context();
        var archived = await check.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == run.Conversation);
        Assert.Equal(ChatState.Archived, archived.State);
        Assert.Equal(archivedSequence, archived.LastSequence);
        Assert.Single(run.Client.Sent);
        Assert.True(run.Client.Disposed);
    }

    [Fact]
    public async Task Trusted_legacy_session_is_adopted_without_speculative_durable_rewrite()
    {
        var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions
        {
            Enabled = true,
            Endpoint = "https://chat.invalid/hub/session",
            DeviceToken = "device-token"
        }));
        await using var run = await Run.CreateAsync(fixture, runtimeState: runtime);
        await using (var db = fixture.Context())
        {
            var conversation = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation);
            conversation.AiAssistantSessionId = "legacy-session";
            conversation.ProviderProfileFingerprint = null;
            await db.SaveChangesAsync();
        }

        await run.SendAsync();

        Assert.Equal("legacy-session", run.Client.RequestedSessionId);
        await using var check = fixture.Context();
        var adopted = await check.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == run.Conversation);
        Assert.Equal("legacy-session", adopted.AiAssistantSessionId);
        Assert.Equal(runtime.Current.ProfileFingerprint, adopted.ProviderProfileFingerprint);
    }

    [Fact]
    public async Task Legacy_session_without_trusted_runtime_authority_is_not_reused()
    {
        var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions { Enabled = true }));
        await using var run = await Run.CreateAsync(fixture, runtimeState: runtime);
        await using (var db = fixture.Context())
        {
            var conversation = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation);
            conversation.AiAssistantSessionId = "legacy-session";
            conversation.ProviderProfileFingerprint = null;
            await db.SaveChangesAsync();
        }

        await run.SendAsync();

        Assert.Null(run.Client.RequestedSessionId);
        await using var check = fixture.Context();
        Assert.Equal(ChatState.DeliveryUnknown, (await check.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == run.Conversation)).State);
        Assert.Equal("legacy-session", (await check.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == run.Conversation)).AiAssistantSessionId);
    }

    [Fact]
    public async Task Active_turn_from_a_different_provider_is_fenced_before_connecting()
    {
        var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions
        {
            Enabled = true,
            Endpoint = "https://chat.invalid/hub/session",
            DeviceToken = "device-token"
        }));
        runtime.Publish(runtime.Current with
        {
            Revision = 2,
            ProfileFingerprint = "provider-new",
            Source = "database",
            SourceKey = "database"
        });
        await using var run = await Run.CreateAsync(fixture, runtimeState: runtime);
        await using (var db = fixture.Context())
        {
            var conversation = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation);
            conversation.AiAssistantSessionId = "old-session";
            conversation.ProviderProfileFingerprint = "provider-old";
            await db.SaveChangesAsync();
        }

        await run.SendAsync();

        Assert.Null(run.Client.RequestedSessionId);
        await using var check = fixture.Context();
        var fenced = await check.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == run.Conversation);
        Assert.Equal(ChatState.DeliveryUnknown, fenced.State);
        Assert.Equal("old-session", fenced.AiAssistantSessionId);
        Assert.Equal("provider-old", fenced.ProviderProfileFingerprint);
    }

    [Fact]
    public async Task Same_provider_token_rotation_reuses_durable_session_and_uses_new_runtime_credential()
    {
        var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
        var initial = new AiAssistantChatRuntimeSnapshot(
            Enabled: true,
            Instance: "dev",
            Endpoint: "https://chat.invalid/hub/session",
            DeviceToken: "old-device-token",
            AllowPrivateHttp: false,
            IdleMinutes: 15,
            ConnectionCapacity: 2,
            TurnInactivityTimeout: TimeSpan.FromMinutes(5),
            ActivityHeartbeatInterval: TimeSpan.FromSeconds(15),
            ProfileFingerprint: "provider-same",
            Revision: 1,
            Source: "database",
            ManagedByDeployment: false,
            SourceKey: "database",
            CanAdoptLegacySessions: true);
        Assert.True(runtime.TryPublish(initial));

        await using var run = await Run.CreateAsync(fixture, runtimeState: runtime);
        await using (var db = fixture.Context())
        {
            var conversation = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == run.Conversation);
            conversation.AiAssistantSessionId = "same-provider-session";
            conversation.ProviderProfileFingerprint = initial.ProfileFingerprint;
            await db.SaveChangesAsync();
        }

        var rotated = initial with
        {
            Revision = 2,
            DeviceToken = "rotated-device-token"
        };
        Assert.True(await run.Manager.TryReconfigureAsync(rotated, default));

        await run.SendAsync();

        Assert.Equal("same-provider-session", run.Client.RequestedSessionId);
        Assert.Equal("rotated-device-token", Assert.Single(run.Factory.Snapshots).DeviceToken);
        await using var check = fixture.Context();
        var persisted = await check.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == run.Conversation);
        Assert.Equal("same-provider-session", persisted.AiAssistantSessionId);
        Assert.Equal(initial.ProfileFingerprint, persisted.ProviderProfileFingerprint);
    }

    private sealed class Run : IAsyncDisposable
    {
        private readonly ChatPostgresFixture fixture;
        private readonly ServiceProvider services;
        private readonly ChatLiveFeed feed;
        private readonly ChannelReader<ChatDelta> reader;
        private readonly TimeProvider? timeProvider;
        public AiAssistantChatSessionManager Manager { get; }
        public ControlledClient Client { get; }
        public ControlledFactory Factory { get; }
        public string Ticket { get; }
        public Guid Conversation { get; }
        public ChatMessageRequest Request { get; }

        private Run(ChatPostgresFixture fixture, ServiceProvider services, ChatLiveFeed feed, AiAssistantChatSessionManager manager, ControlledFactory factory, string ticket, Guid conversation, TimeProvider? timeProvider)
        {
            this.fixture = fixture;
            this.services = services;
            this.feed = feed;
            Manager = manager;
            Factory = factory;
            Client = factory.Next;
            Ticket = ticket;
            Conversation = conversation;
            this.timeProvider = timeProvider;
            Request = new(conversation, Guid.NewGuid(), "Investigate this ticket.");
            reader = feed.Subscribe(conversation);
        }

        public static async Task<Run> CreateAsync(
            ChatPostgresFixture fixture,
            TimeProvider? timeProvider = null,
            IAiAssistantChatRuntimeState? runtimeState = null)
        {
            var services = new ServiceCollection().AddScoped(_ => fixture.Context())
                .AddSingleton(Substitute.For<IDomainEventPublisher>())
                .AddSingleton(Substitute.For<ICorrelationContext>()).BuildServiceProvider();
            var factory = new ControlledFactory();
            var feed = new ChatLiveFeed();
            var manager = new AiAssistantChatSessionManager(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AiAssistantChatOptions { Enabled = true }),
                feed,
                NullLogger<AiAssistantChatSessionManager>.Instance,
                factory,
                timeProvider,
                runtimeState);
            await manager.StartAsync(default);
            var (ticket, conversation) = await fixture.CreateAsync();
            return new(fixture, services, feed, manager, factory, ticket, conversation, timeProvider);
        }

        public async Task SendAsync()
        {
            await using var db = fixture.Context();
            Assert.True(await fixture.Store(db, timeProvider: timeProvider).AcceptMessageAsync("incidents", Ticket, Request, "operator", default));
            await Manager.SendAsync(Conversation, Request.ClientMessageId, Request.Text, default);
        }

        public async Task<List<AiAssistantChatEvent>> WaitForAsync(Func<List<AiAssistantChatEvent>, bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                await using var db = fixture.Context();
                var events = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == Conversation).OrderBy(x => x.Sequence).ToListAsync(timeout.Token);
                if (condition(events)) return events;
                await reader.ReadAsync(timeout.Token);
            }
        }

        public async Task<ChatDelta> WaitForDeltaAsync(string text)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var delta = await reader.ReadAsync(timeout.Token);
                if (delta.Text == text) return delta;
            }
        }

        public async ValueTask DisposeAsync()
        {
            feed.Unsubscribe(Conversation, reader);
            await Manager.StopAsync(default);
            Manager.Dispose();
            await services.DisposeAsync();
        }
    }

    private sealed class ControlledFactory : IAiAssistantChatClientFactory, IAiAssistantChatRuntimeClientFactory
    {
        public ControlledClient Next { get; set; } = new();
        public List<AiAssistantChatRuntimeSnapshot> Snapshots { get; } = [];
        public IAiAssistantChatClient Create() => Next;
        public IAiAssistantChatClient Create(AiAssistantChatRuntimeSnapshot snapshot)
        {
            Snapshots.Add(snapshot);
            return Next;
        }
    }

    private sealed class ControlledClient : IAiAssistantChatClient
    {
        private Func<JsonElement, Task>? output;
        public string SessionId { get; private set; } = "signalr/lifecycle-test";
        public string? RequestedSessionId { get; private set; }
        public JsonElement? RecentMessages { get; init; }
        public bool FailSend { get; set; }
        public bool FailResponse { get; set; }
        public string? TextOnResponse { get; set; }
        public bool FailConnect { get; init; }
        public TaskCompletionSource? ConnectGate { get; set; }
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public bool IsConnected => !Disposed;
        public List<string> Sent { get; } = [];
        public List<(string Session, string Call, string Key)> Responses { get; } = [];
        public async Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> callback, CancellationToken ct)
        {
            output = callback;
            RequestedSessionId = sessionId;
            SessionId = sessionId ?? SessionId;
            ConnectStarted.TrySetResult();
            if (ConnectGate is not null) await ConnectGate.Task.WaitAsync(ct);
            if (FailConnect) throw new IOException("PRIVATE_PROVIDER_DETAIL");
            if (RecentMessages is { } history) await EmitAsync(new { type = "session_joined", sessionId = SessionId, recentMessages = history });
            return new(SessionId, sessionId is null);
        }
        public Task EmitAsync(object value) => output!(JsonSerializer.SerializeToElement(value));
        public Task EmitApprovalAsync(string callId) => EmitAsync(new { type = "tool_interaction", sessionId = SessionId, callId, toolName = "safe_tool", interactionOptions = new[] { new { key = "approve_once", label = "RAW_UNTRUSTED_LABEL" }, new { key = "deny", label = "Deny" } } });
        public Task SendAsync(string sessionId, string text, CancellationToken ct)
        {
            Sent.Add(text);
            return FailSend ? Task.FromException(new IOException("Acknowledgement lost")) : Task.CompletedTask;
        }
        public async Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct)
        {
            Responses.Add((sessionId, callId, key));
            if (TextOnResponse is not null) await EmitAsync(new { type = "text_delta", sessionId, text = TextOnResponse });
            if (FailResponse) throw new IOException("Decision acknowledgement lost");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
