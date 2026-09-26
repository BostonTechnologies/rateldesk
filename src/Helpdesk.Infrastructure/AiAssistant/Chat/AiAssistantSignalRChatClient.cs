using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

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

public sealed class AiAssistantChatClientFactory(IOptions<AiAssistantChatOptions> options) : IAiAssistantChatClientFactory
{
    public IAiAssistantChatClient Create() => new AiAssistantSignalRChatClient(options.Value);
}

public sealed class AiAssistantSignalRChatClient(AiAssistantChatOptions options) : IAiAssistantChatClient
{
    public bool IsConnected => connection.State == HubConnectionState.Connected;
    private readonly HubConnection connection = new HubConnectionBuilder()
        .WithUrl(options.Endpoint, http =>
        {
            http.AccessTokenProvider = () => Task.FromResult<string?>(options.DeviceToken);
            http.HttpMessageHandlerFactory = handler =>
            {
                if (handler is HttpClientHandler clientHandler)
                    clientHandler.AllowAutoRedirect = false;
                return handler;
            };
        })
        .Build();

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
