using Microsoft.Extensions.Http.Resilience;

namespace HelpDesk.NewWeb.Services;

public static class HelpdeskCommandHttpClients
{
    public const string CommandsClientName = "HelpdeskCommands";

    public static IHttpClientBuilder AddHelpdeskCommandsClient(this IServiceCollection services, Uri apiBaseUri)
        => services.AddHttpClient(CommandsClientName, client =>
            {
                client.BaseAddress = apiBaseUri;
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
            .AddHttpMessageHandler<TokenAuthorizationHandler>()
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                // A lost mutation response requires explicit reconciliation, especially for a
                // one-time ingestion key. Keep the same browser identity without replaying POSTs.
                for (var index = handlers.Count - 1; index >= 0; index--)
                {
                    if (handlers[index] is not ResilienceHandler) continue;
                    var handler = handlers[index];
                    handlers.RemoveAt(index);
                    handler.Dispose();
                }
            });
}
