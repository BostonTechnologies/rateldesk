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

public sealed class AiAssistantSignalRChatClient(AiAssistantChatRuntimeSnapshot options) : IAiAssistantChatClient
{
    public bool IsConnected => connection.State == HubConnectionState.Connected;
    private readonly HubConnection connection = BuildConnection(options);

    private static HubConnection BuildConnection(AiAssistantChatRuntimeSnapshot options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("The Netclaw endpoint must be an absolute URI.", nameof(options));

        return new HubConnectionBuilder()
            .WithUrl(endpoint, http =>
            {
                http.AccessTokenProvider = () => Task.FromResult<string?>(options.DeviceToken);
                http.HttpMessageHandlerFactory = _ => IntegrationSafeHttpMessageHandler.Create(options.AllowPrivateHttp, endpoint);
                http.WebSocketFactory = (context, cancellationToken) => CreateWebSocketAsync(context, endpoint, options.AllowPrivateHttp, cancellationToken);
            })
            .Build();
    }

    private static async ValueTask<WebSocket> CreateWebSocketAsync(
        WebSocketConnectionContext context,
        Uri configuredEndpoint,
        bool allowPrivateHttp,
        CancellationToken cancellationToken)
    {
        await IntegrationSafeHttpMessageHandler.ValidateSignalRTargetAsync(
            configuredEndpoint,
            context.Uri,
            allowPrivateHttp,
            cancellationToken);

        var socket = new ClientWebSocket();
        try
        {
            socket.Options.Proxy = null;
            if (context.Options is not null)
            {
                foreach (var header in context.Options.Headers)
                    socket.Options.SetRequestHeader(header.Key, header.Value);
                if (context.Options.Cookies is not null)
                    socket.Options.Cookies = context.Options.Cookies;
                if (context.Options.ClientCertificates is { Count: > 0 })
                    socket.Options.ClientCertificates.AddRange(context.Options.ClientCertificates);
                if (context.Options.Credentials is not null)
                    socket.Options.Credentials = context.Options.Credentials;
                if (context.Options.UseDefaultCredentials is not null)
                    socket.Options.UseDefaultCredentials = context.Options.UseDefaultCredentials.Value;

                var token = context.Options.AccessTokenProvider is null
                    ? null
                    : await context.Options.AccessTokenProvider();
                if (!string.IsNullOrWhiteSpace(token))
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                context.Options.WebSocketConfiguration?.Invoke(socket.Options);
            }

            await socket.ConnectAsync(context.Uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
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
