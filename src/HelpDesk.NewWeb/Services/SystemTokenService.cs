using System.Net.Http.Json;

namespace HelpDesk.NewWeb.Services;

public interface ISystemTokenService
{
    Task<string> GetTokenAsync();
}

public class SystemTokenService : ISystemTokenService
{
    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration _config;
    private string? _token;
    private DateTime _expires;

    public SystemTokenService(IHttpClientFactory factory, IConfiguration config)
    {
        _factory = factory;
        _config = config;
    }

    public async Task<string> GetTokenAsync()
    {
        if (_token != null && DateTime.UtcNow < _expires)
        {
            return _token;
        }

        var client = _factory.CreateClient("SystemApiNoAuth");
        // The named client owns the startup-resolved destination; a configuration reload cannot rebind it.
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/system/token");
        var secret = _config["SYSTEM_TOKEN_SECRET"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("SYSTEM_TOKEN_SECRET is required when using the development operator flow.");
        }

        request.Headers.Add("X-System-Secret", secret);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<SystemTokenResponse>();
        _token = token?.token ?? string.Empty;
        _expires = DateTime.UtcNow.AddMinutes(9);
        return _token!;
    }

    private record SystemTokenResponse(string token);
}
