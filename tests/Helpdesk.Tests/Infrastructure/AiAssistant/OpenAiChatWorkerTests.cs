using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Application.Events;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Helpdesk.Tests.Infrastructure.AiAssistant.OpenAiAgentsProtocolTests;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class OpenAiChatWorkerTests(ChatPostgresFixture database) : IClassFixture<ChatPostgresFixture>
{
    [Fact]
    public async Task Lost_create_response_remains_unknown_and_is_never_sent_again()
    {
        var fixture = new WorkerFixture(database);
        var (id, message) = await fixture.AdmitAsync();
        var creates = 0;
        await fixture.ProcessAsync(id, (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/agents/sessions", request.RequestUri!.AbsolutePath);
            creates++;
            throw new HttpRequestException("Simulated connection loss after dispatch");
        });
        await using (var db = database.Context())
        {
            var saved = await db.Set<AiAssistantChatConversation>().SingleAsync(row => row.Id == id);
            Assert.Equal(ChatState.DeliveryUnknown, saved.State);
            Assert.Null(saved.AiAssistantSessionId);
            Assert.Equal(message, saved.ActiveMessageId);
            await fixture.Store(db).RequestRecoveryAsync(ChatScope.Organization("org"), id, "operator", default);
        }

        await fixture.ProcessAsync(id, (_, _) => throw new InvalidOperationException("An uncertain create must never be replayed."));
        Assert.Equal(1, creates);
        await using var verify = database.Context();
        var dispatch = await verify.Set<OpenAiChatDispatch>().SingleAsync(row => row.ConversationId == id);
        Assert.Equal(OpenAiDispatchState.Unknown, dispatch.State);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Decision_survives_restart_and_requires_the_original_operator_and_arguments(bool approve, bool providerChangesArguments)
    {
        var fixture = new WorkerFixture(database);
        var (id, message) = await fixture.AdmitAsync();
        var remote = new ApprovalSession(id, message);
        await fixture.ProcessAsync(id, remote.RespondAsync);

        string decisionId;
        await using (var db = database.Context())
        {
            var saved = await db.Set<AiAssistantChatConversation>().SingleAsync(row => row.Id == id);
            Assert.Equal(ChatState.AwaitingApproval, saved.State);
            var decision = await db.Set<OpenAiChatDecision>().SingleAsync(row => row.ConversationId == id);
            decisionId = decision.CallId;
            Assert.Contains("Synthetic note", decision.Arguments);
            Assert.Equal("operator", decision.ActorId);
            var store = fixture.Store(db);
            var choice = approve ? "approve_once" : "deny";
            await Assert.ThrowsAsync<ChatConflictException>(() => store.AcceptApprovalAsync(
                ChatScope.Organization("org"), decisionId, new(id, choice), "different-operator", default));
        }
        await using (var db = database.Context())
        {
            var store = fixture.Store(db);
            await store.AcceptApprovalAsync(ChatScope.Organization("org"), decisionId,
                new(id, approve ? "approve_once" : "deny"), "operator", default);
            await Assert.ThrowsAsync<ChatConflictException>(() => store.AcceptApprovalAsync(ChatScope.Organization("org"), decisionId,
                new(id, "approve_once"), "operator", default));
        }

        remote.ChangeArguments = providerChangesArguments;
        var shouldExecute = approve && !providerChangesArguments;
        await fixture.ProcessAsync(id, remote.RespondAsync);
        Assert.Equal(shouldExecute, remote.ProviderResult);
        await fixture.WakeAsync(id);
        await fixture.ProcessAsync(id, remote.RespondAsync);
        await fixture.ProcessAsync(id, remote.RespondAsync);

        await using var verify = database.Context();
        var snapshot = await fixture.Store(verify).LoadAsync(ChatScope.Organization("org"), id, 0, "operator", default);
        Assert.Equal(ChatState.Idle, snapshot.State);
        Assert.Contains("Stored final reply", Assert.Single(snapshot.Events, row => row.Type == "assistant").Text);
        var outcomes = snapshot.Events.Where(row => row.Type is "approval_executed" or "approval_denied").ToArray();
        Assert.Equal(shouldExecute ? "approval_executed" : "approval_denied", Assert.Single(outcomes).Type);
        Assert.Equal(1, remote.Creates);
        Assert.Equal(1, remote.Responses);
        await fixture.Authority.Received(shouldExecute ? 1 : 0).WriteInternalNoteAsync(
            Arg.Any<AiAssistantChatConversation>(), Arg.Any<Guid>(), "incident_fixture", "Synthetic note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Defaults_change_only_new_conversations_and_wrong_organization_cannot_select_the_profile()
    {
        var fixture = new WorkerFixture(database);
        var (id, _) = await fixture.AdmitAsync();
        await using var db = database.Context();
        var store = fixture.Store(db);
        fixture.EnableNetclaw();
        var profiles = fixture.Profiles(db);
        await profiles.SetDefaultAsync(OpenAiChatProfiles.NetclawId, default);
        var newChat = await store.CreateOrganizationAsync("org", new(Guid.NewGuid(), "Second draft", []), "operator", default);
        Assert.Equal(ChatProviderKind.Netclaw, newChat.Provider);
        Assert.Equal(ChatProviderKind.OpenAiAgents, (await store.GetOrganizationAsync("org", id, default)).Provider);

        var wrongOrganization = new AiAssistantChatConversation { OrganizationId = "another-organization", CreatedByUserId = "operator" };
        await Assert.ThrowsAsync<ChatConflictException>(() => profiles.BindNewAsync(wrongOrganization, fixture.Profile, default));
    }

    private sealed class WorkerFixture
    {
        private readonly ChatPostgresFixture database;
        private readonly EphemeralDataProtectionProvider protection = new();
        private readonly IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AiProviders:OpenAiKeys:fixture"] = "synthetic-owned-test-key" }).Build();
        private readonly AiAssistantChatRuntimeState runtime = new(Options.Create(new AiAssistantChatOptions()));
        public readonly IChatProviderMcpAuthority Authority = Substitute.For<IChatProviderMcpAuthority>();
        public Guid Profile { get; } = Guid.NewGuid();

        public WorkerFixture(ChatPostgresFixture database)
        {
            this.database = database;
            Authority.ResolveOperatorAsync("org", Arg.Any<CancellationToken>()).Returns("operator-account");
            Authority.IssueAsync(Arg.Any<AiAssistantChatConversation>(), Arg.Any<OpenAiChatProfileSettings>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>()).Returns(new IssuedChatMcpGrant("synthetic-mcp-token"));
            Authority.IsGrantUsableAsync(Arg.Any<AiAssistantChatConversation>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            Authority.WriteInternalNoteAsync(Arg.Any<AiAssistantChatConversation>(), Arg.Any<Guid>(), "incident_fixture",
                "Synthetic note", Arg.Any<CancellationToken>()).Returns(new ProviderDecision(true, "The synthetic note was added."));
        }

        public OpenAiChatProfiles Profiles(HelpdeskDbContext db) =>
            new(db, runtime, new IntegrationProviderSecretProtector(protection), configuration, TimeProvider.System, Authority);

        public AiAssistantChatStore Store(HelpdeskDbContext db) => new(db, Substitute.For<IDomainEventPublisher>(),
            Substitute.For<ICorrelationContext>(), NullLogger<AiAssistantChatStore>.Instance, TimeProvider.System, Profiles(db));

        public void EnableNetclaw() => runtime.Publish(runtime.Current with { Enabled = true, Revision = 1 });

        public async Task<(Guid Id, Guid Message)> AdmitAsync()
        {
            await using var db = database.Context();
            var profiles = Profiles(db);
            await profiles.SaveAsync(new(Profile, 0, new("Synthetic Agents", true, "org", "fixture-model", "fixture",
                null, null, null, "https://mcp.example.com/mcp", ["helpdesk_incidents.get"], true,
                ChatProviderEnvironment.None, null, null)), default);
            var id = Guid.NewGuid();
            var message = Guid.NewGuid();
            var started = await Store(db).StartOrganizationAsync("org", new(id, message, "Fixture request", [], Profile), "operator", default);
            Assert.Equal(ChatProviderKind.OpenAiAgents, started.Conversation.Provider);
            return (id, message);
        }

        public async Task ProcessAsync(Guid id, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> server)
        {
            await using var db = database.Context();
            using var http = new OpenAiAgentsHttpClient(Client(server));
            var worker = new OpenAiChatRun(db, Profiles(db), http, Authority, new ChatLiveFeed(), TimeProvider.System,
                NullLogger<OpenAiChatRun>.Instance);
            await worker.ProcessAsync(id, default);
        }

        public async Task WakeAsync(Guid id)
        {
            await using var db = database.Context();
            await db.Set<OpenAiChatDispatch>().Where(row => row.ConversationId == id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.NextObservationAtUtc, (DateTimeOffset?)null));
        }
    }

    private sealed class ApprovalSession(Guid conversation, Guid message)
    {
        public bool ProviderResult { get; private set; }
        public int Creates { get; private set; }
        public int Responses { get; private set; }
        public bool ChangeArguments { get; set; }
        private bool Replied => Responses != 0;

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                if (path == "/v1/agents/sessions")
                {
                    Creates++;
                    return Response("""{"id":"sess_approval","status":"in_progress"}""", HttpStatusCode.Created);
                }
                Responses++;
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                var result = Assert.Single(payload.GetProperty("events").EnumerateArray());
                Assert.Equal("call_fixture", result.GetProperty("call_id").GetString());
                Assert.Equal("turn_fixture", result.GetProperty("turn_id").GetString());
                ProviderResult = result.GetProperty("success").GetBoolean();
                return Response("{}", HttpStatusCode.Accepted);
            }
            return path[(path.LastIndexOf('/') + 1)..] switch
            {
                "events" => new(HttpStatusCode.OK) { Content = new StringContent(": pulse\n\n", System.Text.Encoding.UTF8, "text/event-stream") },
                "items" => Items(),
                "turns" => Response(Replied
                    ? """{"data":[{"id":"turn_fixture","created_at":1750000000,"status":"completed"}],"has_more":false}"""
                    : """{"data":[{"id":"turn_fixture","created_at":1750000000,"status":"waiting"}],"has_more":false}"""),
                "subagents" => Response("""{"data":[],"has_more":false}"""),
                _ => Response(Replied ? """{"id":"sess_approval","status":"idle","required_actions":[]}"""
                    : PendingResponse())
            };
        }

        private string PendingResponse()
        {
            const string pending = """{"id":"sess_approval","status":"in_progress","required_actions":[{"type":"function_call","turn_id":"turn_fixture","call_id":"call_fixture","name":"request_incident_internal_note","arguments":{"incident_id":"incident_fixture","note":"Synthetic note"}}]}""";
            return ChangeArguments ? pending.Replace("Synthetic note", "Changed note", StringComparison.Ordinal) : pending;
        }

        private HttpResponseMessage Items()
        {
            var text = JsonSerializer.Serialize(ChatOutboundMessage.Marker(conversation, message) + "Fixture request");
            var assistant = Replied
                ? """,{"id":"assistant_fixture","type":"message","role":"assistant","status":"completed","turn_id":"turn_fixture","content":[{"type":"output_text","text":"Stored final reply"}]}"""
                : "";
            return Response($$"""
                {"data":[{"id":"user_fixture","type":"message","role":"user","status":"completed","turn_id":"turn_fixture","content":[{"type":"input_text","text":{{text}}}]}{{assistant}}],"has_more":false}
                """);
        }
    }
}
