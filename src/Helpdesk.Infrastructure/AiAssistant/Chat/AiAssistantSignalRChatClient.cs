using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence.Connectivity;

namespace Helpdesk.Infrastructure.AiAssistant.Chat;

public sealed record SessionEnsureResult(string SessionId, bool Created);

public interface IAiAssistantChatClient : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct);
    Task SendAsync(string sessionId, string text, CancellationToken ct);
    Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct);
}

public interface IAiAssistantChatClientFactory
{
    IAiAssistantChatClient Create();
}

// Additive capability for factories that can bind a client to one immutable
// runtime snapshot. Older implementations remain valid through Create().
public interface IAiAssistantChatRuntimeClientFactory
{
    IAiAssistantChatClient Create(AiAssistantChatRuntimeSnapshot snapshot);
}

public sealed class AiAssistantChatClientFactory(IAiAssistantChatRuntimeState runtime) : IAiAssistantChatClientFactory, IAiAssistantChatRuntimeClientFactory
{
    public IAiAssistantChatClient Create() => Create(runtime.Current);
    public IAiAssistantChatClient Create(AiAssistantChatRuntimeSnapshot snapshot) => new AiAssistantSignalRChatClient(snapshot);
}

public sealed class AiAssistantSignalRChatClient : IAiAssistantChatClient
{
    public AiAssistantSignalRChatClient(AiAssistantChatRuntimeSnapshot options)
        : this(options, null) { }

    internal AiAssistantSignalRChatClient(AiAssistantChatRuntimeSnapshot options, IntegrationConnectionHooks? hooks)
    {
        connection = BuildConnection(options, hooks);
    }

    public bool IsConnected => connection.State == HubConnectionState.Connected;
    private readonly HubConnection connection;

    private static HubConnection BuildConnection(AiAssistantChatRuntimeSnapshot options, IntegrationConnectionHooks? hooks)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("The Netclaw endpoint must be an absolute URI.", nameof(options));

        return new HubConnectionBuilder()
            .WithUrl(endpoint, http =>
            {
                http.AccessTokenProvider = () => Task.FromResult<string?>(options.DeviceToken);
                http.HttpMessageHandlerFactory = _ => IntegrationSafeHttpMessageHandler.CreateSignalRHandler(options.AllowPrivateHttp, endpoint, hooks);
                http.WebSocketFactory = (context, cancellationToken) => CreateWebSocketAsync(context, endpoint, options.AllowPrivateHttp, cancellationToken, hooks);
            })
            .Build();
    }

    private static async ValueTask<WebSocket> CreateWebSocketAsync(
        WebSocketConnectionContext context,
        Uri configuredEndpoint,
        bool allowPrivateHttp,
        CancellationToken cancellationToken,
        IntegrationConnectionHooks? hooks)
    {
        await IntegrationSafeHttpMessageHandler.ValidateSignalRTargetAsync(
            configuredEndpoint,
            context.Uri,
            allowPrivateHttp,
            cancellationToken,
            hooks);

        var socket = new ClientWebSocket();
        var socketsHandler = IntegrationSafeHttpMessageHandler.CreateCore(allowPrivateHttp, configuredEndpoint, hooks);
        var invoker = new HttpMessageInvoker(
            IntegrationSafeHttpMessageHandler.WrapSignalRHandler(socketsHandler, configuredEndpoint, allowPrivateHttp));
        try
        {
            if (context.Options is not null)
            {
                foreach (var header in context.Options.Headers)
                    socket.Options.SetRequestHeader(header.Key, header.Value);
                if (context.Options.Cookies is not null)
                {
                    socketsHandler.UseCookies = true;
                    socketsHandler.CookieContainer = context.Options.Cookies;
                }
                if (context.Options.ClientCertificates is { Count: > 0 })
                    socketsHandler.SslOptions.ClientCertificates = context.Options.ClientCertificates;
                if (context.Options.Credentials is not null)
                    socketsHandler.Credentials = context.Options.Credentials;
                else if (context.Options.UseDefaultCredentials == true)
                    socketsHandler.Credentials = CredentialCache.DefaultCredentials;

                var token = context.Options.AccessTokenProvider is null
                    ? null
                    : await context.Options.AccessTokenProvider();
                if (!string.IsNullOrWhiteSpace(token))
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                context.Options.WebSocketConfiguration?.Invoke(socket.Options);
            }

            await socket.ConnectAsync(context.Uri, invoker, cancellationToken);
            return new OwnedWebSocket(socket, invoker);
        }
        catch
        {
            socket.Dispose();
            invoker.Dispose();
            throw;
        }
    }

    private sealed class OwnedWebSocket(ClientWebSocket socket, HttpMessageInvoker invoker) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
        public override string? CloseStatusDescription => socket.CloseStatusDescription;
        public override WebSocketState State => socket.State;
        public override string? SubProtocol => socket.SubProtocol;
        public override void Abort() => socket.Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => socket.CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => socket.ReceiveAsync(buffer, cancellationToken);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        public override void Dispose()
        {
            socket.Dispose();
            invoker.Dispose();
        }
    }

    public async Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct)
    {
        connection.On("ReceiveOutput", output);
        connection.Closed += _ => output(JsonSerializer.SerializeToElement(new { type = "transport_closed" }));
        await connection.StartAsync(ct);
        return await connection.InvokeAsync<SessionEnsureResult>("EnsureSession", sessionId, "signalr", ct);
    }

    public Task SendAsync(string sessionId, string text, CancellationToken ct) => connection.InvokeAsync("SendMessage", sessionId, text, ct);
    public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct) => connection.InvokeAsync("RespondToInteraction", sessionId, callId, key, ct);
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
