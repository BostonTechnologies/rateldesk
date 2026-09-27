using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence.Connectivity;
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
                CanAdoptLegacySessions: false);
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

    [Fact]
    public async Task SignalR_request_guard_rejects_out_of_path_followup_before_sending_credentials_on_pooled_connection()
    {
        var observedRequests = new ConcurrentQueue<(string Path, string? Authorization)>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(async context =>
        {
            observedRequests.Enqueue((
                context.Request.Path + context.Request.QueryString,
                context.Request.Headers.Authorization.ToString()));
            context.Response.ContentLength = 2;
            await context.Response.WriteAsync("ok");
        });
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("The test SignalR server did not publish a listening address.");
            var endpoint = new UriBuilder(address) { Host = "bounded.invalid", Path = "/hub/session" }.Uri;
            var connectionAttempts = 0;
            var hooks = new IntegrationConnectionHooks(
                (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]),
                async (ip, port, ct) =>
                {
                    Assert.Equal(IPAddress.Loopback, ip);
                    Interlocked.Increment(ref connectionAttempts);
                    return await ConnectLocalAsync(port, ct);
                });

            using var invoker = new HttpMessageInvoker(IntegrationSafeHttpMessageHandler.CreateSignalRHandler(
                allowPrivateHttp: true,
                configuredEndpoint: endpoint,
                hooks: hooks));

            var negotiateUri = new UriBuilder(endpoint)
            {
                Path = "/hub/session/negotiate",
                Query = "negotiateVersion=1"
            }.Uri;
            using (var negotiateRequest = new HttpRequestMessage(HttpMethod.Get, negotiateUri))
            {
                negotiateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-device-token");
                using var response = await invoker.SendAsync(negotiateRequest, CancellationToken.None);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("ok", await response.Content.ReadAsStringAsync());
            }

            Assert.Equal(1, Volatile.Read(ref connectionAttempts));

            using var outOfPathRequest = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(endpoint)
            {
                Path = "/outside/hub",
                Query = "access_token=test-device-token"
            }.Uri);
            outOfPathRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-device-token");

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => invoker.SendAsync(outOfPathRequest, CancellationToken.None));

            Assert.Contains("configured Netclaw hub path", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref connectionAttempts));
            var observed = Assert.Single(observedRequests);
            Assert.Equal("/hub/session/negotiate?negotiateVersion=1", observed.Path);
            Assert.Equal("Bearer test-device-token", observed.Authorization);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Changed_resolution_cannot_move_authenticated_websocket_to_a_rejected_address(int rejectedResolution)
    {
        var calls = new ConcurrentQueue<HubCall>();
        var websocketRequests = new ConcurrentQueue<bool>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(calls);
        builder.Services.AddSignalR();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "Device";
            options.DefaultChallengeScheme = "Device";
        }).AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>("Device", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest) websocketRequests.Enqueue(true);
            await next();
        });
        app.MapHub<AuthenticatedChatHub>("/hub/session").RequireAuthorization();
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("The test SignalR server did not publish a listening address.");
            var endpoint = new UriBuilder(address) { Host = "rebind.invalid", Path = "/hub/session" }.Uri;
            var resolutions = 0;
            var attempts = new ConcurrentQueue<IPAddress>();
            var rejected = IPAddress.Parse("169.254.169.254");
            var hooks = new IntegrationConnectionHooks(
                (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                    [Interlocked.Increment(ref resolutions) >= rejectedResolution ? rejected : IPAddress.Loopback]),
                async (ip, port, ct) =>
                {
                    attempts.Enqueue(ip);
                    Assert.Equal(IPAddress.Loopback, ip);
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                });
            var snapshot = new AiAssistantChatRuntimeSnapshot(
                Enabled: true,
                Instance: "dev",
                Endpoint: endpoint.ToString(),
                DeviceToken: "test-device-token",
                AllowPrivateHttp: true,
                IdleMinutes: 15,
                ConnectionCapacity: 2,
                TurnInactivityTimeout: TimeSpan.FromMinutes(5),
                ActivityHeartbeatInterval: TimeSpan.FromSeconds(15),
                ProfileFingerprint: "rebind-test");
            await using var client = new AiAssistantSignalRChatClient(snapshot, hooks);

            await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(null, _ => Task.CompletedTask, CancellationToken.None));

            Assert.True(resolutions >= rejectedResolution);
            Assert.Single(attempts);
            Assert.Empty(websocketRequests);
            Assert.DoesNotContain(calls, call => call.Method == "Connected");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Pinned_websocket_connects_to_approved_address_and_cancellation_releases_transport()
    {
        var calls = new ConcurrentQueue<HubCall>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(calls);
        builder.Services.AddSignalR();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "Device";
            options.DefaultChallengeScheme = "Device";
        }).AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>("Device", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWebSockets();
        app.MapHub<AuthenticatedChatHub>("/hub/session").RequireAuthorization();
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("The test SignalR server did not publish a listening address.");
            var endpoint = new UriBuilder(address) { Host = "pinned.invalid", Path = "/hub/session" }.Uri;
            var snapshot = new AiAssistantChatRuntimeSnapshot(
                Enabled: true,
                Instance: "dev",
                Endpoint: endpoint.ToString(),
                DeviceToken: "test-device-token",
                AllowPrivateHttp: true,
                IdleMinutes: 15,
                ConnectionCapacity: 2,
                TurnInactivityTimeout: TimeSpan.FromMinutes(5),
                ActivityHeartbeatInterval: TimeSpan.FromSeconds(15),
                ProfileFingerprint: "pinned-test");
            var streams = new ConcurrentQueue<TrackedNetworkStream>();
            var approvedAttempts = new ConcurrentQueue<IPAddress>();
            var approvedHooks = new IntegrationConnectionHooks(
                (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]),
                async (ip, port, ct) =>
                {
                    approvedAttempts.Enqueue(ip);
                    Assert.Equal(IPAddress.Loopback, ip);
                    var stream = await ConnectLocalAsync(port, ct);
                    streams.Enqueue(stream);
                    return stream;
                });

            await using (var client = new AiAssistantSignalRChatClient(snapshot, approvedHooks))
            {
                var session = await client.ConnectAsync(null, _ => Task.CompletedTask, CancellationToken.None);
                await client.SendAsync(session.SessionId, "synthetic message", CancellationToken.None);
            }

            Assert.True(approvedAttempts.Count >= 2);
            Assert.All(approvedAttempts, ip => Assert.Equal(IPAddress.Loopback, ip));
            Assert.Contains(calls, call => call.Method == "SendMessage");
            Assert.All(streams, stream => Assert.True(stream.WasDisposed));

            var secondConnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionNumber = 0;
            var cancelHooks = new IntegrationConnectionHooks(
                (_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]),
                async (ip, port, ct) =>
                {
                    Assert.Equal(IPAddress.Loopback, ip);
                    if (Interlocked.Increment(ref connectionNumber) == 1)
                        return await ConnectLocalAsync(port, ct);
                    secondConnectEntered.TrySetResult();
                    try
                    {
                        await connectionGate.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        throw;
                    }
                    throw new InvalidOperationException("The cancelled connection unexpectedly continued.");
                });
            await using var cancellingClient = new AiAssistantSignalRChatClient(snapshot, cancelHooks);
            using var cancellation = new CancellationTokenSource();
            var connecting = cancellingClient.ConnectAsync(null, _ => Task.CompletedTask, cancellation.Token);
            await secondConnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => connecting);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async ValueTask<TrackedNetworkStream> ConnectLocalAsync(int port, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            return new TrackedNetworkStream(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class TrackedNetworkStream(Socket socket) : NetworkStream(socket, ownsSocket: true)
    {
        private int disposed;
        public bool WasDisposed => Volatile.Read(ref disposed) != 0;
        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref disposed, 1);
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            return base.DisposeAsync();
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
