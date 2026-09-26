using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class SignalRChatClientIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_client_uses_authenticated_signalr_negotiate_websocket_or_fallback_and_hub_invocations(bool rejectWebSockets)
    {
        var calls = new ConcurrentQueue<HubCall>();
        var fallbackRequests = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(calls);
        builder.Services.AddSignalR();
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Device";
                options.DefaultChallengeScheme = "Device";
            })
            .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>("Device", _ => { });
        builder.Services.AddAuthorization();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (!context.WebSockets.IsWebSocketRequest &&
                context.Request.Path == "/hub/session" &&
                context.Request.Query.ContainsKey("id"))
                fallbackRequests.Enqueue(context.Request.Method);

            if (rejectWebSockets && context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next();
        });
        app.MapHub<AuthenticatedChatHub>("/hub/session").RequireAuthorization();
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("The test SignalR server did not publish a listening address.");
            var endpoint = new Uri(new Uri(address), "/hub/session");
            var options = new AiAssistantChatRuntimeSnapshot(
                Enabled: true,
                Instance: "dev",
                Endpoint: endpoint.ToString(),
                DeviceToken: "test-device-token",
                AllowPrivateHttp: true,
                IdleMinutes: 15,
                ConnectionCapacity: 2,
                TurnInactivityTimeout: TimeSpan.FromMinutes(5),
                ActivityHeartbeatInterval: TimeSpan.FromSeconds(15),
                ProfileFingerprint: "signalr-test",
                Revision: 1,
                Source: "test",
                ManagedByDeployment: false,
                SourceKey: "signalr-test",
                CanAdoptLegacySessions: true);
            var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
            var factory = new AiAssistantChatClientFactory(runtime);
            await using var client = factory.Create(options);
            var output = new List<JsonElement>();

            var ensured = await client.ConnectAsync(null, value =>
            {
                output.Add(value);
                return Task.CompletedTask;
            }, CancellationToken.None);
            await client.SendAsync(ensured.SessionId, "operator message", CancellationToken.None);

            Assert.Equal("server-session", ensured.SessionId);
            Assert.Empty(output);
            Assert.Contains(calls, call => call.Method == "EnsureSession" && call.User == "device");
            Assert.Contains(calls, call => call.Method == "SendMessage" && call.Arguments.SequenceEqual(["server-session", "operator message"]));
            if (rejectWebSockets)
                Assert.NotEmpty(fallbackRequests);
            else
                Assert.Empty(fallbackRequests);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class AuthenticatedChatHub(ConcurrentQueue<HubCall> calls) : Hub
    {
        public override Task OnConnectedAsync()
        {
            calls.Enqueue(new("Connected", Context.User?.Identity?.Name ?? string.Empty, []));
            return base.OnConnectedAsync();
        }

        public Task<SessionEnsureResult> EnsureSession(string? sessionId, string protocol)
        {
            calls.Enqueue(new("EnsureSession", Context.User?.Identity?.Name ?? string.Empty, [sessionId ?? string.Empty, protocol]));
            return Task.FromResult(new SessionEnsureResult("server-session", sessionId is null));
        }

        public Task SendMessage(string sessionId, string text)
        {
            calls.Enqueue(new("SendMessage", Context.User?.Identity?.Name ?? string.Empty, [sessionId, text]));
            return Task.CompletedTask;
        }

        public Task RespondToInteraction(string sessionId, string callId, string key)
        {
            calls.Enqueue(new("RespondToInteraction", Context.User?.Identity?.Name ?? string.Empty, [sessionId, callId, key]));
            return Task.CompletedTask;
        }
    }

    private sealed class DeviceTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization)
                || !string.Equals(authorization.ToString(), "Bearer test-device-token", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.Fail("Invalid device token."));

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "device"),
                    new Claim(ClaimTypes.Name, "device")
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed record HubCall(string Method, string User, IReadOnlyList<string> Arguments);
}
