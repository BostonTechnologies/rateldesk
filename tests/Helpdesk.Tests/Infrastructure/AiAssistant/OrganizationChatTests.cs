using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiOps;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Helpdesk.Application.Events;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class OrganizationChatTests(ChatPostgresFixture fixture) : IClassFixture<ChatPostgresFixture>
{
    [Fact]
    public async Task Pending_child_chain_blocks_new_organization_input_but_preserves_original_admission_idempotency_and_scope()
    {
        await using var db = fixture.Context();
        var store = fixture.Store(db);
        var chat = await store.CreateOrganizationAsync("org", new(Guid.NewGuid(), "Synthetic delegated queue report", []), "operator", default);
        var request = new ChatMessageRequest(chat.ConversationId, Guid.NewGuid(), "Read-only delegated report");
        Assert.True(await store.AcceptMessageAsync(ChatScope.Organization("org"), request, "operator", default));
        var current = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == chat.ConversationId);
        current.State = ChatState.WaitingForChildren;
        await db.SaveChangesAsync();
        Assert.False(await store.AcceptMessageAsync(ChatScope.Organization("org"), request, "operator", default));
        await Assert.ThrowsAsync<ChatConflictException>(() => store.AcceptMessageAsync(ChatScope.Organization("org"),
            request with { ClientMessageId = Guid.NewGuid(), Text = "Unrelated newer task" }, "operator", default));
        await Assert.ThrowsAsync<ChatConflictException>(() => store.ArchiveAsync(ChatScope.Organization("org"), chat.ConversationId, "operator", default));
        await store.StopWaitingAsync(ChatScope.Organization("org"), new(chat.ConversationId, request.ClientMessageId), "operator", default);
        var stopped = await store.LoadAsync(ChatScope.Organization("org"), chat.ConversationId, 0, "operator", default);
        Assert.Equal(ChatState.DeliveryUnknown, stopped.State);
        Assert.Equal(request.ClientMessageId, stopped.ActiveMessageId);
        Assert.Single(stopped.Events, x => x.Type == "operator");
        Assert.Contains(stopped.Events, x => x.Type == "delivery_unknown");
    }

    [Fact]
    public async Task Organization_message_uses_existing_native_session_manager_without_loading_a_ticket()
    {
        await using var services = new ServiceCollection().AddScoped(_ => fixture.Context())
            .AddSingleton(Substitute.For<IDomainEventPublisher>()).AddSingleton(Substitute.For<ICorrelationContext>()).BuildServiceProvider();
        var client = Substitute.For<IAiAssistantChatClient>(); client.IsConnected.Returns(true);
        client.ConnectAsync(Arg.Any<string?>(), Arg.Any<Func<System.Text.Json.JsonElement, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new SessionEnsureResult("signalr/general-fixture", true));
        var factory = Substitute.For<IAiAssistantChatClientFactory>(); factory.Create().Returns(client);
        using var manager = new AiAssistantChatSessionManager(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AiAssistantChatOptions { Enabled = true, Endpoint = "https://chat.invalid/hub/session", DeviceToken = "fixture-only" }),
            new ChatLiveFeed(), NullLogger<AiAssistantChatSessionManager>.Instance, factory);
        await manager.StartAsync(default);
        try
        {
            await using var db = fixture.Context(); var store = fixture.Store(db);
            var chat = await store.CreateOrganizationAsync("org", new(Guid.NewGuid(), "Native general", []), "operator", default);
            var message = new ChatMessageRequest(chat.ConversationId, Guid.NewGuid(), "Plan safely");
            await store.AcceptMessageAsync(ChatScope.Organization("org"), message, "operator", default);
            await manager.SendAsync(chat.ConversationId, message.ClientMessageId, message.Text, default);
            await client.Received(1).SendAsync("signalr/general-fixture", Arg.Is<string>(text =>
                text.StartsWith($"[helpdesk-chat:{chat.ConversationId:D}:{message.ClientMessageId:D}]\n")
                && text.Contains("Authenticated organization invocation") && text.Contains("Plan safely") && !text.Contains("Ticket context")), Arg.Any<CancellationToken>());
            var observation = Assert.IsType<ChatSessionConnection>(manager.ObserveConnection(chat.ConversationId));
            Assert.Equal(ChatTransportConnectionState.Connected, observation.State);
            Assert.Equal("signalr/general-fixture", observation.SessionId);
            await client.Received(1).ConnectAsync(Arg.Any<string?>(), Arg.Any<Func<System.Text.Json.JsonElement, Task>>(), Arg.Any<CancellationToken>());
            await manager.RetireAsync(chat.ConversationId, default);
            Assert.Null(manager.ObserveConnection(chat.ConversationId));
        }
        finally { await manager.StopAsync(default); }
    }

    [Fact]
    public async Task First_message_creates_and_names_one_conversation_with_one_admission()
    {
        var request = new ChatStartRequest(Guid.NewGuid(), Guid.NewGuid(), "  Help me\nreview this queue  ", []);
        await using var db = fixture.Context(); var store = fixture.Store(db);
        var result = await store.StartOrganizationAsync("org", request, "operator", default);
        Assert.True(result.Accepted);
        Assert.Equal("Help me review this queue", result.Conversation.Title);
        Assert.Equal(ChatState.Processing, result.Conversation.State);
        Assert.False((await store.StartOrganizationAsync("org", request, "operator", default)).Accepted);
        await Assert.ThrowsAsync<ChatConflictException>(() => store.StartOrganizationAsync("org", request with { ClientMessageId = Guid.NewGuid() }, "operator", default));
        var snapshot = await store.LoadAsync(ChatScope.Organization("org"), request.ConversationId, 0, "operator", default);
        var message = Assert.Single(snapshot.Events, x => x.Type == "operator");
        Assert.Equal(request.ClientMessageId, message.ClientMessageId);
        Assert.Equal(request.Text, message.Text);
        var row = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == request.ConversationId);
        Assert.Null(row.TicketId); Assert.Null(row.TicketType);
    }

    [Fact]
    public async Task Rejected_first_message_rolls_back_the_conversation_and_its_saved_guidance()
    {
        var prompt = new AiGuidancePrompt { OrganizationId = "org", Name = "Bounded context", Content = new string('g', 15000) };
        await using (var seed = fixture.Context()) { seed.Add(prompt); await seed.SaveChangesAsync(); }
        var request = new ChatStartRequest(Guid.NewGuid(), Guid.NewGuid(), new string('q', 16000), [prompt.Id]);
        await using (var db = fixture.Context())
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store(db).StartOrganizationAsync("org", request, "operator", default));
        await using var check = fixture.Context();
        Assert.False(await check.Set<AiAssistantChatConversation>().AnyAsync(x => x.Id == request.ConversationId));
        Assert.False(await check.Set<AiAssistantChatEvent>().AnyAsync(x => x.ConversationId == request.ConversationId));
        check.Remove(await check.Set<AiGuidancePrompt>().SingleAsync(x => x.Id == prompt.Id)); await check.SaveChangesAsync();
    }

    [Fact]
    public async Task General_chats_have_no_ticket_and_pin_actual_guidance_with_idempotent_creation_and_send()
    {
        await using var db = fixture.Context();
        var prompt = new AiGuidancePrompt { OrganizationId = "org", Name = "Optional", Content = "Pinned guidance", Revision = 7 };
        db.Add(prompt); await db.SaveChangesAsync();
        var store = fixture.Store(db);
        var request = new ChatCreateRequest(Guid.NewGuid(), "Synthetic planning", [prompt.Id]);
        var chat = await store.CreateOrganizationAsync("org", request, "operator", default);
        Assert.Equal(chat, await store.CreateOrganizationAsync("org", request, "operator", default));
        await Assert.ThrowsAsync<ChatConflictException>(() => store.CreateOrganizationAsync("org", request with { Title = "Changed" }, "operator", default));
        var row = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == chat.ConversationId);
        Assert.Equal(ChatConversationScope.Organization, row.Scope);
        Assert.Null(row.TicketId); Assert.Null(row.TicketType); Assert.False(row.IsInvestigation);
        prompt.Content = "Changed guidance"; prompt.Revision++; await db.SaveChangesAsync();
        Assert.Contains("Pinned guidance", chat.GuidanceSnapshot); Assert.DoesNotContain("Changed guidance", chat.GuidanceSnapshot);
        var message = new ChatMessageRequest(chat.ConversationId, Guid.NewGuid(), "Help plan");
        Assert.True(await store.AcceptMessageAsync(ChatScope.Organization("org"), message, "operator", default));
        Assert.False(await store.AcceptMessageAsync(ChatScope.Organization("org"), message, "operator", default));
        var snapshot = await store.LoadAsync(ChatScope.Organization("org"), chat.ConversationId, 0, "reader", default);
        Assert.Single(snapshot.Events, x => x.Type == "operator");
    }

    [Fact]
    public async Task Multiple_chats_search_page_and_archive_without_changing_ticket_current_conversation()
    {
        var (ticket, current) = await fixture.CreateAsync();
        await using var db = fixture.Context(); var store = fixture.Store(db);
        var prefix = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 53; i++) await store.CreateOrganizationAsync("org", new(Guid.NewGuid(), $"{prefix} plan {i}", []), "operator", default);
        var first = await store.HistoryAsync(ChatScope.Organization("org"), 0, prefix, true, default);
        var second = await store.HistoryAsync(ChatScope.Organization("org"), 50, prefix, true, default);
        Assert.Equal(50, first.Count); Assert.Equal(3, second.Count); Assert.Empty(first.Select(x => x.ConversationId).Intersect(second.Select(x => x.ConversationId)));
        await store.ArchiveAsync(ChatScope.Organization("org"), first[0].ConversationId, "operator", default);
        await store.ArchiveAsync(ChatScope.Organization("org"), first[0].ConversationId, "operator", default);
        Assert.DoesNotContain(await store.HistoryAsync(ChatScope.Organization("org"), 0, prefix, false, default), x => x.ConversationId == first[0].ConversationId);
        Assert.Equal(current, (await store.LoadAsync("incidents", ticket, null, 0, "operator", default)).ConversationId);
        Assert.DoesNotContain(await store.HistoryAsync("incidents", ticket, 0, default), x => first.Any(c => c.ConversationId == x.ConversationId));
    }

    [Fact]
    public async Task Guessed_scope_cannot_read_or_mutate_and_abandon_keeps_transcript_without_creating_fake_ticket()
    {
        var (ticket, ticketChat) = await fixture.CreateAsync();
        await using var db = fixture.Context(); var store = fixture.Store(db);
        var chat = await store.CreateOrganizationAsync("org", new(Guid.NewGuid(), "Uncertain", []), "operator", default);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.LoadAsync("incidents", ticket, chat.ConversationId, 0, "operator", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.LoadAsync(ChatScope.Organization("org"), ticketChat, 0, "operator", default));
        var message = new ChatMessageRequest(chat.ConversationId, Guid.NewGuid(), "Uncertain request");
        await store.AcceptMessageAsync(ChatScope.Organization("org"), message, "operator", default);
        await store.StopWaitingAsync(ChatScope.Organization("org"), new(chat.ConversationId, message.ClientMessageId), "operator", default);
        var resolution = new ChatAbandonRequest(chat.ConversationId, Guid.NewGuid(), message.ClientMessageId, true);
        await store.AbandonAsync(ChatScope.Organization("org"), resolution, "operator", default);
        await store.AbandonAsync(ChatScope.Organization("org"), resolution, "operator", default);
        var snapshot = await store.LoadAsync(ChatScope.Organization("org"), chat.ConversationId, 0, "operator", default);
        Assert.Equal(ChatState.Archived, snapshot.State);
        Assert.Single(snapshot.Events, x => x.Type == "delivery_abandoned");
        Assert.Single(snapshot.Events, x => x.Type == "operator");
        Assert.Equal(chat.GuidanceSnapshot, (await store.GetOrganizationAsync("org", chat.ConversationId, default)).GuidanceSnapshot);
    }
}
