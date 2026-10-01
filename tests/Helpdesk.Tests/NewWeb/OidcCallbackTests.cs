extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Tests.Api;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NewWeb::HelpDesk.NewWeb.Services;
using Xunit;

namespace Helpdesk.Tests.NewWeb;

public sealed class OidcCallbackTests
{
    [Theory]
    [InlineData("Oidc")]
    [InlineData("Hybrid")]
    public async Task Code_callback_provisions_with_real_bearer_validation_and_creates_a_browser_session(string mode)
    {
        using var api = new SignedOidcApi(mode);
        await api.SeedLinkedUserAsync();
        string? nonce = null;
        using var web = CreateWeb(api, mode, () => nonce);
        using var browser = web.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });
        var challenge = await browser.GetAsync("/login-authentik");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        nonce = query["nonce"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(nonce));
        Assert.Equal("S256", query["code_challenge_method"]);
        var callback = await browser.GetAsync(QueryHelpers.AddQueryString("/signin-authentik", new Dictionary<string, string?>
        {
            ["code"] = "synthetic-code", ["state"] = query["state"].ToString()
        }));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.DoesNotContain("Authentication", callback.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains(callback.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("__Host-Helpdesk.Auth=", StringComparison.Ordinal));
        var session = await browser.GetAsync("/session/access");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var access = await session.Content.ReadFromJsonAsync<CurrentUserAccessDto>();
        Assert.Equal("contact-a", access!.CustomerId);
        Assert.False(access.IsHelpdeskAdmin);
        Assert.Equal(["tenant-a"], access.AllowedOrganizationIds);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/home")).StatusCode);
    }

    [Theory]
    [InlineData("Oidc")]
    [InlineData("Hybrid")]
    public async Task Unlinked_user_callback_fails_without_creating_an_application_session(string mode)
    {
        using var api = new SignedOidcApi(mode);
        string? nonce = null;
        using var web = CreateWeb(api, mode, () => nonce);
        using var browser = web.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var challenge = await browser.GetAsync("/login-authentik");
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        nonce = query["nonce"].ToString();
        var response = await browser.GetAsync(QueryHelpers.AddQueryString("/signin-authentik", new Dictionary<string, string?> { ["code"] = "synthetic-code", ["state"] = query["state"].ToString() }));
        Assert.Equal("/login?status=Authentication%20failed", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/session/access")).StatusCode);
    }

    private static WebApplicationFactory<TokenService> CreateWeb(SignedOidcApi api, string mode, Func<string?> nonce)
        => new WebApplicationFactory<TokenService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Authentication:Mode", mode);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = mode,
                ["Authentication:Authentik:Authority"] = SignedOidcApi.Issuer,
                ["Authentication:Authentik:ClientId"] = "browser-client",
                ["Authentication:Authentik:ApiScope"] = SignedOidcApi.Audience,
                ["AUTHENTIK_CLIENT_SECRET"] = "synthetic-client-secret",
                ["ApiBaseUrl"] = "https://localhost/"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var name in new[] { "HelpdeskApi", "SystemApiNoAuth" })
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new ApiBridge(api.Factory.Server.CreateHandler()));
                services.PostConfigure<OpenIdConnectOptions>("Authentik", options =>
                {
                    var configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = SignedOidcApi.Issuer,
                        AuthorizationEndpoint = SignedOidcApi.Issuer + "authorize",
                        TokenEndpoint = SignedOidcApi.Issuer + "token"
                    };
                    configuration.SigningKeys.Add(api.Key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    options.Backchannel = new HttpClient(new TokenEndpoint(api, nonce));
                });
            });
        });

    private sealed class ApiBridge(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/setup/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { state = "Ready" }) });
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class TokenEndpoint(SignedOidcApi api, Func<string?> nonce) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(SignedOidcApi.Issuer + "token", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    access_token = api.Token(), id_token = api.Token(audience: "browser-client", nonce: nonce()), token_type = "Bearer", expires_in = 300
                })
            });
        }
    }
}
