extern alias NewWeb;

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using ServiceLinkBrowserEndpoints = NewWeb::HelpDesk.NewWeb.Services.ServiceLinkBrowserEndpoints;

namespace Helpdesk.Tests.NewWeb;

public sealed class ServiceLinkSignInContinuationTests
{
    [Fact]
    public async Task Signed_out_approval_survives_sign_in_without_peer_proofs_in_login_URL()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();
        const string approval = "/account/integration-credentials/link/approve?initiator_web_base_url=https%3A%2F%2Fpeer.example.invalid&attempt_id=fixture-attempt&browser_state=fixture-state";
        using var signedOut = await client.GetAsync(approval);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/login?ReturnUrl=%2Faccount%2Fintegration-credentials%2Flink%2Fresume-sign-in", signedOut.Headers.Location!.OriginalString);
        Assert.DoesNotContain("fixture-state", signedOut.Headers.Location.OriginalString);
        var cookie = signedOut.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.True(signedOut.Headers.CacheControl!.NoStore);
        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        client.DefaultRequestHeaders.Add("x-fixture-authenticated", "true");
        using var resumed = await client.GetAsync("/account/integration-credentials/link/resume-sign-in");
        Assert.Equal(HttpStatusCode.Redirect, resumed.StatusCode);
        Assert.Equal(approval, resumed.Headers.Location!.OriginalString);
        Assert.Contains("expires=", resumed.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant());
        using var reviewed = await client.GetAsync(resumed.Headers.Location);
        Assert.Equal("/account/integration-credentials/link/respond/fixture-attempt", reviewed.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("fixture-actor", true)]
    [InlineData("different-actor", false)]
    public async Task Signed_out_callback_resumes_only_with_the_original_actor_browser_binding(string authenticatedActor, bool sameActor)
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();
        const string callback = "/account/integration-credentials/link/callback?attempt_id=fixture-attempt&pairing_code=expiring-fixture-code&browser_state=fixture-state&responder_instance_id=fixture-peer&oauth_issuer=https%3A%2F%2Fpeer.example.invalid";
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RatelDesk.ServiceLink.BrowserSession.v1").ToTimeLimitedDataProtector();
        var originalBrowser = protector.Protect(JsonSerializer.Serialize(new { Actor = "fixture-actor", Random = new string('A', 64) }), TimeSpan.FromHours(1));
        using var signedOut = await client.GetAsync(callback);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.DoesNotContain("expiring-fixture-code", signedOut.Headers.Location!.OriginalString);
        var continuation = signedOut.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", continuation + "; __Host-RatelDesk.ServiceLink=" + originalBrowser);
        client.DefaultRequestHeaders.Add("x-fixture-authenticated", authenticatedActor);
        using var resumed = await client.GetAsync("/account/integration-credentials/link/resume-sign-in");
        Assert.Equal(callback, resumed.Headers.Location!.OriginalString);
        using var reviewed = await client.GetAsync(resumed.Headers.Location);
        if (sameActor) Assert.Equal("/account/integration-credentials/link/review/fixture-attempt", reviewed.Headers.Location!.OriginalString);
        else Assert.StartsWith("/account/integration-credentials/link/result?status=session-expired&stage=callback&correlationId=", reviewed.Headers.Location!.OriginalString);
        Assert.Equal(sameActor ? 1 : 0, host.Services.GetRequiredService<PeerFixture>().CallbackRequests);
    }

    [Fact]
    public async Task Altered_continuation_cannot_select_a_redirect_or_accept_approval()
    {
        using var host = await CreateHostAsync();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("x-fixture-authenticated", "true");
        client.DefaultRequestHeaders.Add("Cookie", "__Host-RatelDesk.ServiceLink.Continuation=altered");
        using var resumed = await client.GetAsync("/account/integration-credentials/link/resume-sign-in");
        Assert.StartsWith("/account/integration-credentials/link/result?status=session-expired&stage=request&correlationId=", resumed.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("unsupported-peer", "unsupported-peer")]
    [InlineData("organization-disabled", "invalid-organization")]
    [InlineData("network-policy-rejected", "network-policy-rejected")]
    [InlineData("https://secret.invalid/proof", "invalid-request")]
    public async Task Browser_preserves_allowlisted_API_diagnostics_without_peer_text(string code, string expected)
    {
        using var host = await CreateHostAsync();
        var peer = host.Services.GetRequiredService<PeerFixture>();
        peer.FailureCode = code;
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("x-fixture-authenticated", "true");
        using var response = await client.GetAsync("/account/integration-credentials/link/approve?initiator_web_base_url=https%3A%2F%2Fpeer.example.invalid&attempt_id=fixture-attempt&browser_state=fixture-state");
        Assert.Equal("/account/integration-credentials/link/result?status=" + expected +
            "&stage=remote-review&correlationId=0123456789abcdef0123456789abcdef", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("secret.invalid", response.Headers.Location.OriginalString);
        Assert.DoesNotContain("fixture-state", response.Headers.Location.OriginalString);
    }

    private static Task<IHost> CreateHostAsync() => new HostBuilder().ConfigureWebHost(web =>
        web.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting(); services.AddAuthorization(); services.AddAntiforgery(); services.AddDataProtection();
            services.AddSingleton<PeerFixture>();
            services.AddSingleton<IHttpClientFactory>(provider => provider.GetRequiredService<PeerFixture>());
        }).Configure(app =>
        {
            app.UseRouting();
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("x-fixture-authenticated"))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, context.Request.Headers["x-fixture-authenticated"].ToString() == "true" ? "fixture-actor" : context.Request.Headers["x-fixture-authenticated"].ToString()), new Claim(ClaimTypes.Role, "HelpdeskAdmin")], "fixture"));
                await next(context);
            });
            app.UseAuthorization();
            app.UseEndpoints(endpoints => ServiceLinkBrowserEndpoints.MapServiceLinkBrowserEndpoints(endpoints));
        })).StartAsync();

    private sealed class PeerFixture : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.invalid/") };
        public int CallbackRequests { get; private set; }
        public string? FailureCode { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailureCode is not null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { code = FailureCode, stage = "remote-review",
                    correlationId = "0123456789abcdef0123456789abcdef", title = "https://secret.invalid/proof" }))
            });
            if (request.RequestUri!.AbsolutePath.EndsWith("/callback", StringComparison.Ordinal)) CallbackRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
