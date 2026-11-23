using Helpdesk.ServiceDefaults.Security;
using Helpdesk.Shared.DTOs.PublicAccess;

namespace HelpDesk.NewWeb.Services;

public sealed class PublicBrowserAccess(IHttpClientFactory clients, PublicIngressIdentity origin)
{
    internal const string BrowserCookie = "__Host-RatelDeskPublicBrowser";
    internal const string GrantCookie = "__Secure-RatelDeskPublicGrant";

    internal static string AccessPath(Guid id) => $"{PublicAccessProtocol.BrowserPrefix}/{id:D}";

    internal async Task<HttpResponseMessage> ExchangeAsync(HttpContext context, PublicLinkExchangeRequest request, bool replacement)
    {
        using var client = clients.CreateClient("PublicAccessApi");
        using var outgoing = new HttpRequestMessage(HttpMethod.Post,
            $"{PublicAccessProtocol.ApiPrefix}/{(replacement ? "replacement" : "exchange")}")
        {
            Content = JsonContent.Create(request)
        };
        origin.AddProof(outgoing, PublicIngressIdentity.FromAddress(context.Connection.RemoteIpAddress));
        return await client.SendAsync(outgoing, context.RequestAborted);
    }

    public async Task<T?> ReadAsync<T>(HttpContext? context, Guid id, string resource)
    {
        if (context is null || !context.Request.Cookies.ContainsKey(GrantCookie))
            return default;
        try
        {
            using var response = await SendWithGrantAsync(context, id, HttpMethod.Get, resource);
            return response is { IsSuccessStatusCode: true }
                ? await response.Content.ReadFromJsonAsync<T>(context.RequestAborted) : default;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        {
            return default;
        }
    }

    internal async Task<HttpResponseMessage?> SendWithGrantAsync(HttpContext context, Guid id, HttpMethod method,
        string resource, HttpContent? content = null)
    {
        if (context.Request.Cookies[GrantCookie] is not { Length: > 0 and <= 512 } grant)
        {
            content?.Dispose();
            return null;
        }

        using var request = new HttpRequestMessage(method, $"{PublicAccessProtocol.ApiPrefix}/{id:D}/{resource}") { Content = content };
        request.Headers.TryAddWithoutValidation(PublicAccessProtocol.GrantHeader, grant);
        using var client = clients.CreateClient("PublicAccessApi");
        return await client.SendAsync(request, context.RequestAborted);
    }
}
