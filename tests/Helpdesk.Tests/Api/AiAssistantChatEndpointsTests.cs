using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Helpdesk.API;
using Helpdesk.Application.Events;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Helpdesk.Tests.Infrastructure.AiAssistant;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class AiAssistantChatEndpointsTests(ChatPostgresFixture database, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<ChatPostgresFixture>
{
    private static string Route(string ticket) => $"/api/v1/incidents/{ticket}/ai-assistant/chat";

    [Theory]
    [InlineData("OnHold")]
    [InlineData("InProgress")]
    public async Task StateDefectReproductionNamedMcpEnumFailsBeforeTicketLookup(string state)
    {
        await using var host = new ChatApi(database);
        // Match the deployed host's non-Development JSON binder behavior. In
        // Development, the existing exception middleware wraps this as HTTP 500.
        await using var deployedMode = host.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var staff = deployedMode.CreateClient();
        staff.DefaultRequestHeaders.Add("X-Chat-Test-Role", "HelpdeskAdmin");
        staff.DefaultRequestHeaders.Add("X-Chat-Test-Tenant", "org");
        using var response = await staff.PostAsJsonAsync("/api/v1/incidents/nonexistent-ux-defect-probe/state", new { newState = state });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"State probe {state}: HTTP {(int)response.StatusCode}; body: {(body.Length == 0 ? "<empty>" : body)}");
        // Captures the actual API binder error rather than a mocked HTTP response.
        Assert.True(body.Length == 0 || body.Contains("JSON", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Bad Request", StringComparison.OrdinalIgnoreCase), body);
    }

    [Fact]
    public async Task ActualStaffPolicyTenantAndTicketBindingsAreEnforced()
    {
        var (ticket, conversation) = await database.CreateAsync();
        var (otherTicket, _) = await database.CreateAsync();
        await using var host = new ChatApi(database);
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route(ticket))).StatusCode);
        using var customer = host.Client("User");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(Route(ticket))).StatusCode);
        using var staff = host.Client();
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync(Route(ticket))).StatusCode);
        using var otherTenant = host.Client(tenant: "other");
        Assert.Equal(HttpStatusCode.Forbidden, (await otherTenant.GetAsync(Route(ticket))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherTenant.GetAsync($"{Route(ticket)}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/requests/{ticket}/ai-assistant/chat")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Route(otherTicket)}?conversationId={conversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"{Route(otherTicket)}/messages", new ChatMessageRequest(conversation, Guid.NewGuid(), "No"))).StatusCode);
    }

    [Fact]
    public async Task ScopedTechnicianCanUseChatInAssignedNonPrimaryOrganization()
    {
        var (ticket, _) = await database.CreateAsync("scoped-org");
        await using var host = new ChatApi(database);
        using var scopedTechnician = host.Client(tenant: "org", scopedTenant: "scoped-org");
        using var ungrantedTechnician = host.Client(tenant: "org");

        Assert.Equal(HttpStatusCode.OK, (await scopedTechnician.GetAsync(Route(ticket))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ungrantedTechnician.GetAsync(Route(ticket))).StatusCode);
    }

    [Fact]
    public async Task ApprovalHttpPathRequiresStaffAndAnOfferedSingleUseChoice()
    {
        var (ticket, conversation) = await database.CreateAsync();
        await using (var db = database.Context())
        {
            await database.Store(db).AcceptMessageAsync("incidents", ticket, new(conversation, Guid.NewGuid(), "Tool request"), "operator", default);
            var chat = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == conversation);
            chat.State = ChatState.AwaitingApproval;
            db.Add(new AiAssistantChatInteraction { ConversationId = conversation, CallId = "offered-call", OptionsJson = JsonSerializer.Serialize(new[] { new ChatOption("deny", "Deny") }) });
            await db.SaveChangesAsync();
        }
        await using var host = new ChatApi(database);
        using var staff = host.Client();
        using var customer = host.Client("User");
        var endpoint = $"{Route(ticket)}/interactions/offered-call";
        var choice = new ChatApprovalRequest(conversation, "deny");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(endpoint, choice)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(endpoint, choice with { SelectedKey = "not-offered" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await staff.PostAsJsonAsync(endpoint, choice)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(endpoint, choice)).StatusCode);
        await host.Transport.Received(1).RespondAsync(conversation, "offered-call", "deny", Arg.Any<CancellationToken>());
        await using var check = database.Context();
        Assert.Equal("operator", (await check.Set<AiAssistantChatInteraction>().SingleAsync(x => x.ConversationId == conversation)).AnsweredByUserId);
    }

    [Fact]
    public async Task DisabledChatReturnsUnavailable()
    {
        var (ticket, _) = await database.CreateAsync();
        await using var host = new ChatApi(database, enabled: false);
        using var staff = host.Client();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await staff.GetAsync(Route(ticket))).StatusCode);
    }

    [Fact]
    public async Task PublishedRuntimeSnapshotControlsCapabilitiesAndTheProtectedChatRoute()
    {
        var (ticket, _) = await database.CreateAsync();
        await using var host = new ChatApi(database);
        using var staff = host.Client();

        var enabled = await staff.GetFromJsonAsync<ChatCapabilities>($"/api/v1/incidents/{ticket}/ai-assistant/chat-capabilities");
        Assert.True(enabled!.Enabled);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync(Route(ticket))).StatusCode);

        host.Runtime.Publish(host.Runtime.Current with { Enabled = false, Revision = host.Runtime.Current.Revision + 1 });

        var disabled = await staff.GetFromJsonAsync<ChatCapabilities>($"/api/v1/incidents/{ticket}/ai-assistant/chat-capabilities");
        Assert.False(disabled!.Enabled);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await staff.GetAsync(Route(ticket))).StatusCode);
    }

    [Fact]
    public async Task StopWaitingRecordsUncertainDeliveryAndRetiresTheTransportOwner()
    {
        var (ticket, conversation) = await database.CreateAsync();
        var messageId = Guid.NewGuid();
        await using (var db = database.Context())
            await database.Store(db).AcceptMessageAsync("incidents", ticket, new(conversation, messageId, "Wait here"), "operator", default);
        await using var host = new ChatApi(database);
        using var customer = host.Client("User");
        using var staff = host.Client();
        var request = new ChatStopWaitingRequest(conversation, messageId);
        var endpoint = $"{Route(ticket)}/stop-waiting";
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(endpoint, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(endpoint, request with { ExpectedMessageId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await staff.PostAsJsonAsync(endpoint, request)).StatusCode);
        await host.Transport.Received(1).RetireAsync(conversation, Arg.Any<CancellationToken>());
        await host.Transport.DidNotReceive().SendAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using var check = database.Context();
        Assert.Equal(ChatState.DeliveryUnknown, (await check.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == conversation)).State);
        Assert.Contains(await check.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == conversation).ToListAsync(), x => x.Type == "delivery_unknown" && x.CreatedByUserId == "operator");
    }

    [Fact]
    public async Task DurableSseResumesAcrossClientAndApiRestartWithoutReplayingDeltas()
    {
        var (ticket, conversation) = await database.CreateAsync();
        await using (var db = database.Context())
            await database.Store(db).AcceptMessageAsync("incidents", ticket, new(conversation, Guid.NewGuid(), "Durable operator"), "operator", default);
        long cursor;
        await using (var first = new ChatApi(database))
        {
            using var client = first.Client();
            await using var stream = await SseConnection.OpenAsync(client, $"{Route(ticket)}/stream?conversationId={conversation}&cursor=0");
            var initial = await stream.ReadThroughStateAsync();
            Assert.Equal(new[] { "1", "2" }, initial.Where(x => x.Kind == "chat").Select(x => x.Id));
            cursor = long.Parse(initial.Last(x => x.Kind == "chat").Id!);
            first.Services.GetRequiredService<IChatLiveFeed>().Publish(new(conversation, "transient-only", cursor));
            var live = await stream.ReadThroughStateAsync();
            var delta = Assert.Single(live, x => x.Kind == "text-delta");
            Assert.Null(delta.Id);
            Assert.Contains("transient-only", delta.Data);
        }
        // A completely new API host owns a completely empty ChatLiveFeed.
        await using var restarted = new ChatApi(database);
        using var resumedClient = restarted.Client();
        await using (var resumed = await SseConnection.OpenAsync(resumedClient, $"{Route(ticket)}/stream?conversationId={conversation}&cursor=0", "1"))
        {
            var frames = await resumed.ReadThroughStateAsync();
            Assert.Equal("2", Assert.Single(frames, x => x.Kind == "chat").Id);
            Assert.DoesNotContain(frames, x => x.Kind == "text-delta");
        }
        await using var caughtUp = await SseConnection.OpenAsync(resumedClient, $"{Route(ticket)}/stream?conversationId={conversation}&cursor={cursor}");
        Assert.DoesNotContain(await caughtUp.ReadThroughStateAsync(), x => x.Kind is "chat" or "text-delta");
    }

    [Fact]
    public async Task AbandonmentIsAuthorizedSerializedAuditedAndIdempotent()
    {
        var (ticket, conversation) = await database.CreateAsync();
        var messageId = Guid.NewGuid();
        await using (var db = database.Context())
        {
            await database.Store(db).AcceptMessageAsync("incidents", ticket, new(conversation, messageId, "Uncertain"), "operator", default);
            var current = await db.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == conversation);
            current.State = ChatState.DeliveryUnknown;
            await db.SaveChangesAsync();
        }
        await using var host = new ChatApi(database);
        using var customer = host.Client("User");
        using var foreign = host.Client(tenant: "other");
        using var staff = host.Client();
        var request = new ChatAbandonRequest(conversation, Guid.NewGuid(), messageId, true);
        var endpoint = $"{Route(ticket)}/abandon";
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(endpoint, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsJsonAsync(endpoint, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync(endpoint, request with { AcknowledgePossibleDelivery = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync(endpoint, request with { ExpectedMessageId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync($"{Route(ticket)}/reconcile", new ChatNewRequest(conversation))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsJsonAsync($"{Route(ticket)}/reconcile", new ChatNewRequest(conversation))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await staff.PostAsJsonAsync($"{Route(ticket)}/reconcile", new ChatNewRequest(conversation))).StatusCode);
        await host.Transport.Received(1).ReconcileAsync(conversation, Arg.Any<CancellationToken>());
        var rival = request with { ResolutionId = Guid.NewGuid() };
        var responses = await Task.WhenAll(staff.PostAsJsonAsync(endpoint, request), staff.PostAsJsonAsync(endpoint, rival));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var winning = responses[0].StatusCode == HttpStatusCode.Accepted ? request : rival;
        foreach (var response in responses) response.Dispose();
        Assert.Equal(HttpStatusCode.Accepted, (await staff.PostAsJsonAsync(endpoint, winning)).StatusCode);
        await using var check = database.Context();
        var audit = Assert.Single(await check.Set<AiAssistantChatEvent>().Where(x => x.ConversationId == conversation && x.Type == "delivery_abandoned").ToListAsync());
        Assert.Equal("operator", audit.CreatedByUserId);
        Assert.Equal(winning.ResolutionId, audit.ClientMessageId);
        Assert.Equal(ChatState.Archived, (await check.Set<AiAssistantChatConversation>().SingleAsync(x => x.Id == conversation)).State);
        Assert.Single(await check.Set<AiAssistantChatConversation>().Where(x => x.TicketId == ticket && x.State != ChatState.Archived).ToListAsync());
        await host.Publisher.Received(1).PublishAsync(Arg.Is<ChatDomainEvent>(x => x.Action == "DeliveryAbandoned"), Arg.Any<CancellationToken>());
        await host.Transport.DidNotReceive().SendAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        var transcript = await staff.GetFromJsonAsync<ChatSnapshot>($"{Route(ticket)}?conversationId={conversation}");
        Assert.Equal(ChatState.Archived, transcript!.State);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"{Route(ticket)}/messages", new ChatMessageRequest(conversation, Guid.NewGuid(), "No"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"{Route(ticket)}/interactions/call", new ChatApprovalRequest(conversation, "deny"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"{Route(ticket)}/new", new ChatNewRequest(conversation))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"{Route(ticket)}/reconcile", new ChatNewRequest(conversation))).StatusCode);
        var history = await staff.GetFromJsonAsync<List<ChatConversationSummary>>($"{Route(ticket)}/history");
        Assert.Equal(2, history!.Count);
        Assert.NotEqual(ChatState.Archived, history[0].State);
        Assert.Equal(conversation, history[1].ConversationId);
    }

    private sealed class ChatApi(ChatPostgresFixture database, bool enabled = true) : WebApplicationFactory<Program>
    {
        public IAiAssistantChatTransport Transport { get; } = Substitute.For<IAiAssistantChatTransport>();
        public IDomainEventPublisher Publisher { get; } = Substitute.For<IDomainEventPublisher>();
        public AiAssistantChatRuntimeState Runtime { get; } = new(Options.Create(new AiAssistantChatOptions
        {
            Enabled = enabled,
            Endpoint = "https://chat.invalid/hub/session",
            DeviceToken = "test-only-device-token"
        }));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseIsolatedTestStorage();
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<HelpdeskDbContext>();
                services.AddScoped(sp => database.Context(sp.GetRequiredService<ITenantContext>()));
                services.AddSingleton(Transport);
                services.AddSingleton(Publisher);
                services.RemoveAll<IAiAssistantChatRuntimeState>();
                services.AddSingleton<IAiAssistantChatRuntimeState>(Runtime);
                services.RemoveAll<ICurrentUserAccessService>();
                services.AddSingleton<ICurrentUserAccessService, ChatTestAccessService>();
                services.PostConfigure<AiAssistantChatOptions>(x => { x.Enabled = enabled; x.Endpoint = "https://chat.invalid/hub/session"; x.DeviceToken = "test-only-device-token"; });
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "ChatTest";
                    options.DefaultChallengeScheme = "ChatTest";
                    options.DefaultForbidScheme = "ChatTest";
                }).AddScheme<AuthenticationSchemeOptions, ChatAuthHandler>("ChatTest", _ => { });
            });
        }
        public HttpClient Client(string role = "Technician", string tenant = "org", string? scopedTenant = null)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Chat-Test-Role", role);
            client.DefaultRequestHeaders.Add("X-Chat-Test-Tenant", tenant);
            if (!string.IsNullOrWhiteSpace(scopedTenant))
                client.DefaultRequestHeaders.Add("X-Chat-Test-Scoped-Tenant", scopedTenant);
            return client;
        }
    }

    private sealed class ChatAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Chat-Test-Role", out var role)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "operator"),
                new(ClaimTypes.Role, role.ToString()),
                new("chat_test_tenant", Request.Headers["X-Chat-Test-Tenant"].ToString())
            };
            if (Request.Headers.TryGetValue("X-Chat-Test-Scoped-Tenant", out var scopedTenant))
                claims.Add(new("chat_test_scoped_tenant", scopedTenant.ToString()));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class ChatTestAccessService : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            var roles = user.FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var organizationId = user.FindFirst("chat_test_tenant")?.Value;
            var scopedOrganizationId = user.FindFirst("chat_test_scoped_tenant")?.Value;
            var isAdmin = roles.Contains(HelpdeskPermissions.HelpdeskAdmin);
            var permissions = roles.Contains("Technician")
                ? HelpdeskPermissions.TechnicalBundle.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var allowedOrganizations = string.IsNullOrWhiteSpace(organizationId)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { organizationId };
            if (!string.IsNullOrWhiteSpace(scopedOrganizationId))
                allowedOrganizations.Add(scopedOrganizationId);
            var profile = new CurrentUserAccessProfile(
                true,
                user.Identity?.Name,
                null,
                organizationId,
                null,
                null,
                isAdmin,
                roles,
                permissions,
                allowedOrganizations,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(scopedOrganizationId) && roles.Contains("Technician"))
                profile = profile with
                {
                    ScopedPermissionGrants = HelpdeskPermissions.TechnicalBundle
                        .Select(permission => new ScopedPermissionGrant(permission, scopedOrganizationId))
                        .ToHashSet()
                };
            return Task.FromResult(profile);
        }
    }

    private sealed record Frame(string Kind, string Data, string? Id);
    private sealed class SseConnection(HttpResponseMessage response, StreamReader reader, CancellationTokenSource timeout) : IAsyncDisposable
    {
        public static async Task<SseConnection> OpenAsync(HttpClient client, string path, string? lastId = null)
        {
            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (lastId is not null) request.Headers.Add("Last-Event-ID", lastId);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            return new(response, new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)), timeout);
        }
        public async Task<List<Frame>> ReadThroughStateAsync()
        {
            var frames = new List<Frame>();
            string kind = "", data = ""; string? id = null;
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("id: ")) id = line[4..];
                else if (line.StartsWith("event: ")) kind = line[7..];
                else if (line.StartsWith("data: ")) data = line[6..];
                else if (line.Length == 0)
                {
                    frames.Add(new(kind, data, id));
                    if (kind == "state") return frames;
                    kind = ""; data = ""; id = null;
                }
            }
            throw new IOException("SSE ended before authoritative state.");
        }
        public async ValueTask DisposeAsync() { await timeout.CancelAsync(); reader.Dispose(); response.Dispose(); timeout.Dispose(); }
    }
}
