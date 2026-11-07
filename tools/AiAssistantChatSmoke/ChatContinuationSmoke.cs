using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Events;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static class ChatContinuationSmoke
{
    // Explicit acceptance executable, outside routine tests. Supply only a
    // disposable PostgreSQL database and a paired disposable native daemon.
    public static async Task<int> RunAsync(AiAssistantChatOptions options)
    {
        var connection = Environment.GetEnvironmentVariable("AI_ASSISTANT_CHAT_POSTGRES")
            ?? throw new InvalidOperationException("Supply AI_ASSISTANT_CHAT_POSTGRES for a disposable acceptance database.");
        var organization = "native-child-" + Guid.NewGuid().ToString("N");
        HelpdeskDbContext Context() => new(new DbContextOptionsBuilder<HelpdeskDbContext>().UseNpgsql(connection).Options,
            new SyntheticTenant(organization), new HttpContextAccessor());
        await using var services = new ServiceCollection().AddScoped(_ => Context())
            .AddSingleton<IDomainEventPublisher, AcceptanceEvents>().AddSingleton<ICorrelationContext, AcceptanceCorrelation>()
            .BuildServiceProvider();
        var runtime = new AiAssistantChatRuntimeState(Options.Create(options));
        var feed = new ChatLiveFeed();
        using var manager = new AiAssistantChatSessionManager(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options), feed, NullLogger<AiAssistantChatSessionManager>.Instance,
            new AiAssistantChatClientFactory(runtime), runtimeState: runtime);
        await using (var db = Context())
        {
            await db.Database.EnsureCreatedAsync();
            db.Organizations.Add(new Organization { Id = organization, Name = "Synthetic native continuation acceptance" });
            await db.SaveChangesAsync();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await manager.StartAsync(deadline.Token);
        try
        {
            foreach (var kind in new[] { ChatConversationScope.Ticket, ChatConversationScope.Organization })
            {
                Guid conversation;
                ChatScope scope;
                await using (var db = Context())
                {
                    var store = Store(db);
                    if (kind == ChatConversationScope.Ticket)
                    {
                        var incident = new Incident { Id = Guid.NewGuid().ToString(), TrackingId = Guid.NewGuid().ToString(),
                            Title = "Synthetic delegated arithmetic", OrganizationId = organization };
                        db.Add(incident);
                        await db.SaveChangesAsync(deadline.Token);
                        scope = ChatScope.Ticket("incidents", incident.Id);
                        conversation = (await store.LoadAsync(scope, null, 0, "synthetic-operator", deadline.Token)).ConversationId;
                    }
                    else
                    {
                        scope = ChatScope.Organization(organization);
                        conversation = (await store.CreateOrganizationAsync(organization,
                            new(Guid.NewGuid(), "Synthetic delegated arithmetic", []), "synthetic-operator", deadline.Token)).ConversationId;
                    }
                }
                var reader = feed.Subscribe(conversation);
                var request = new ChatMessageRequest(conversation, Guid.NewGuid(),
                    "Ask synthetic-reader to compute 2 + 2 with harmless disposable delegated work, then report its result. Do not access customer records, send messages or change anything.");
                try
                {
                    await using (var db = Context())
                        Require(await Store(db).AcceptMessageAsync(scope, request, "synthetic-operator", deadline.Token), "The original request was not admitted.");
                    await manager.SendAsync(conversation, request.ClientMessageId, request.Text, deadline.Token);
                    var observedPending = false;
                    var releasedChild = false;
                    while (true)
                    {
                        await using var db = Context();
                        var snapshot = await Store(db).LoadAsync(scope, conversation, 0, "synthetic-operator", deadline.Token);
                        if (snapshot.State == ChatState.WaitingForChildren)
                        {
                            observedPending = true;
                            Require(snapshot.Continuation?.OpenChildRunIds is { Length: > 0 }, "Pending state has no native child identity.");
                            if (manager.ObserveConnection(conversation)?.State != ChatTransportConnectionState.Connected)
                            {
                                // The final transaction can retire its owner after
                                // this snapshot read but before this observation.
                                db.ChangeTracker.Clear();
                                await using var completion = await db.Database.BeginTransactionAsync(deadline.Token);
                                var current = await db.Set<AiAssistantChatConversation>()
                                    .FromSqlInterpolated($"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {conversation} FOR UPDATE")
                                    .SingleAsync(deadline.Token);
                                var latest = await db.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == conversation && x.Type == "turn_completed")
                                    .OrderByDescending(x => x.Sequence).Select(x => x.MetadataJson).FirstAsync(deadline.Token);
                                Require(current.State == ChatState.Idle && ChatContinuationSafety.Read(latest)?.OpenChildRunIds is { Length: 0 },
                                    "Parent completion retired its native subscription before a final clear completion.");
                                await completion.CommitAsync(deadline.Token);
                            }
                            if (!releasedChild && Environment.GetEnvironmentVariable("AI_ASSISTANT_CHAT_CHILD_RELEASE_URL") is { } release)
                            {
                                var target = new Uri(release);
                                Require(target.IsLoopback && target.Scheme == "http", "The synthetic model release gate must use loopback HTTP.");
                                using var control = new HttpClient();
                                using var response = await control.PostAsync(target, null, deadline.Token);
                                response.EnsureSuccessStatusCode();
                                releasedChild = true;
                            }
                        }
                        if (snapshot.State == ChatState.Idle && snapshot.Events.Any(x => x.Type == "turn_completed"))
                        {
                            Require(observedPending, "The connected path never observed the pending child state.");
                            Require(snapshot.Events.Count(x => x.Type == "operator") == 1, "Expected one original operator input.");
                            Require(snapshot.Events.Count(x => x.Type == "assistant") == 2, "Expected the initial and final native replies exactly once.");
                            var completions = snapshot.Events.Where(x => x.Type == "turn_completed")
                                .Select(x => ChatContinuationSafety.Read(x.MetadataJson)!).ToArray();
                            Require(completions.Length == 2 && completions.All(x => x is not null), "Expected two persisted native completion boundaries.");
                            Require(completions[0].OpenChildRunIds is { Length: > 0 } && completions[1].OpenChildRunIds is { Length: 0 }, "Native child snapshots did not progress from pending to clear.");
                            Require(completions[1].TurnNumber > completions[0].TurnNumber, "Native turn ordinals did not advance.");
                            Require(completions.All(x => x.OperatorMessageId == request.ClientMessageId), "A continuation changed its original admission association.");
                            Require(snapshot.Events.Any(x => x.Type == "assistant" && x.Text.Contains("2 + 2 = 4", StringComparison.Ordinal)), "The native final arithmetic report was not saved.");
                            Require(snapshot.Events.Where(x => x.Type != "operator").All(x => x.ClientMessageId is null), "Continuation events reused the unique admission identity.");
                            Console.WriteLine(JsonSerializer.Serialize(new { Scope = kind.ToString(), Passed = true, OperatorInputs = 1, Replies = 2,
                                NativeTurns = completions.Select(x => x.TurnNumber), ObservedPending = observedPending,
                                Cleared = true, DurableReload = snapshot.Events.Count, AutomaticResends = 0 }));
                            break;
                        }
                        Require(snapshot.State != ChatState.DeliveryUnknown, "Native delivery became uncertain: " + snapshot.RecoveryResult);
                        await reader.ReadAsync(deadline.Token);
                    }
                }
                finally { feed.Unsubscribe(conversation, reader); }
            }
        }
        finally { await manager.StopAsync(CancellationToken.None); }
        return 0;

        AiAssistantChatStore Store(HelpdeskDbContext db) => new(db, new AcceptanceEvents(), new AcceptanceCorrelation(), NullLogger<AiAssistantChatStore>.Instance);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SyntheticTenant(string organization) : ITenantContext
    {
        public string TenantId => organization;
        public string UserId => "synthetic-operator";
        public bool IsHelpdeskAdmin => true;
    }
    private sealed class AcceptanceEvents : IDomainEventPublisher
    {
        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class AcceptanceCorrelation : ICorrelationContext
    {
        public string GetCorrelationId() => "synthetic-native-continuation";
    }
}
