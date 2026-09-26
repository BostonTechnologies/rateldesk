using System.Text.Json;
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
    private readonly HubConnection connection = new HubConnectionBuilder()
        .WithUrl(options.Endpoint, http =>
        {
            http.AccessTokenProvider = () => Task.FromResult<string?>(options.DeviceToken);
            http.HttpMessageHandlerFactory = _ => IntegrationSafeHttpMessageHandler.Create(options.AllowPrivateHttp);
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
