using System.Globalization;
using System.Net;
using Helpdesk.ServiceDefaults.Security;

namespace Helpdesk.API.Endpoints.PublicIngress;

internal enum PublicOperation
{
    Login,
    Setup,
    TicketSubmission,
    Captcha,
    LinkExchange,
    LinkReplacement
}

internal sealed class PublicAdmission(IConfiguration configuration, TimeProvider clock)
{
    private readonly Dictionary<PublicOperation, Window> _limits = new()
    {
        [PublicOperation.Login] = new(30, 1200),
        [PublicOperation.Setup] = new(5, 120),
        [PublicOperation.TicketSubmission] = new(5, 300),
        [PublicOperation.Captcha] = new(
            Limit(configuration, "Captcha:RequestsPerClientPerMinute", 15, 100),
            Limit(configuration, "Captcha:RequestsPerMinute", 300, 5000)),
        [PublicOperation.LinkExchange] = new(20, 600),
        [PublicOperation.LinkReplacement] = new(3, 100)
    };

    internal static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<PublicIngressIdentity>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PublicAdmission>();
    }

    internal static void Use(WebApplication app)
    {
        var peers = (app.Configuration.GetSection("PublicIngress:TrustedProxies").Get<string[]>() ?? []).Select(IPAddress.Parse).ToArray();
        var networks = (app.Configuration.GetSection("PublicIngress:TrustedNetworks").Get<string[]>() ?? []).Select(global::System.Net.IPNetwork.Parse).ToArray();
        PublicIngressIdentity.UseTrustedForwarding(app, peers, networks);
        PublicIngressIdentity.UseApiIngressIdentity(app, app.Configuration.GetValue<bool>("PublicIngress:TrustWebProof"));
    }

    internal bool TryAcquire(PublicOperation operation, string client, out int retryAfterSeconds)
    {
        var time = clock.GetUtcNow().ToUnixTimeSeconds();
        var minute = time / 60;
        retryAfterSeconds = (int)(60 - time % 60);
        var limit = _limits[operation];
        lock (limit.Gate)
        {
            if (minute != limit.Minute)
            {
                limit.Minute = minute;
                limit.Clients.Clear();
                limit.Accepted = 0;
            }
            if (limit.Accepted >= limit.Global || limit.Clients.GetValueOrDefault(client) >= limit.PerClient)
                return false;
            limit.Clients[client] = limit.Clients.GetValueOrDefault(client) + 1;
            limit.Accepted++;
            return true;
        }
    }

    private static int Limit(IConfiguration configuration, string key, int fallback, int maximum)
    {
        var value = configuration.GetValue(key, fallback);
        return value > 0 && value <= maximum ? value : throw new InvalidOperationException($"{key} must be between 1 and {maximum}.");
    }

    private sealed class Window(int perClient, int global)
    {
        internal Lock Gate { get; } = new();
        internal int PerClient { get; } = perClient;
        internal int Global { get; } = global;
        internal long Minute { get; set; }
        internal int Accepted { get; set; }
        internal Dictionary<string, int> Clients { get; } = new(StringComparer.Ordinal);
    }
}

internal static class PublicAdmissionEndpoints
{
    internal static RouteHandlerBuilder RequirePublicAdmission(this RouteHandlerBuilder endpoint, PublicOperation operation) =>
        endpoint.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var admission = http.RequestServices.GetRequiredService<PublicAdmission>();
            if (!admission.TryAcquire(operation, PublicIngressIdentity.Client(http), out var retryAfter))
                return TooManyRequests(http, retryAfter);
            return await next(context);
        });

    internal static IResult TooManyRequests(HttpContext http, int retryAfterSeconds)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

}
