namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

/// <summary>
/// A dedicated connection pool for managed Agents requests. Dispatch is never
/// replayed by the general API client's resilience pipeline.
/// </summary>
public sealed class OpenAiAgentsHttpClient : IDisposable
{
    public OpenAiAgentsHttpClient() : this(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan })
    {
    }

    internal OpenAiAgentsHttpClient(HttpClient client) => Client = client;

    public HttpClient Client { get; }
    public void Dispose() => Client.Dispose();
}
