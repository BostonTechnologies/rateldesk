extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Helpdesk.Shared.ServiceLink;
using Xunit;
using ServiceLinkBrowserEndpoints = NewWeb::HelpDesk.NewWeb.Services.ServiceLinkBrowserEndpoints;

namespace Helpdesk.Tests.NewWeb;

public sealed class ServiceLinkBrowserRecoveryTests
{
    private const string Root = "/account/integration-credentials/link";
    private const string Attempt = "0123456789abcdef0123456789abcdef";
    private const string Callback = "https://peer.example.invalid/account/integration-credentials/link/callback";
    private const string Approval = "https://peer.example.invalid/account/integration-credentials/link/approve";

    [Theory]
    [InlineData("responder", "return", true)]
    [InlineData("initiator", "continue", true)]
    [InlineData("initiator", "continue", false)]
    public async Task Continue_uses_the_selected_durable_role_without_starting_another_approval(string role, string action, bool hasSession)
    {
        using var host = await HostAsync();
        var api = host.Services.GetRequiredService<ApiFixture>();
        api.Role = role; api.Action = action;
        using var client = host.GetTestClient();
        var form = await FormAsync(host, client, hasSession);
        using var response = await client.PostAsync(Root + "/continue", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        if (!hasSession)
        {
            Assert.Contains("status=session-expired", response.Headers.Location!.OriginalString);
            Assert.Empty(api.PostPaths);
            return;
        }
        Assert.Equal((role == "responder" ? Callback : Approval) + "?attempt_id=" + Attempt, response.Headers.Location!.AbsoluteUri);
        Assert.Equal(["/api/v1/admin/service-links/attempts/" + Attempt + "/continue"], api.PostPaths);
        Assert.Equal(64, api.ContinueBinding!.Length);
    }

    [Fact]
    public async Task Approved_responder_can_return_without_an_initiator_browser_session()
    {
        using var host = await HostAsync();
        using var client = host.GetTestClient();
        var form = await FormAsync(host, client, false);
        using var response = await client.PostAsync(Root + "/continue", new FormUrlEncodedContent(form));
        Assert.Equal(Callback + "?attempt_id=" + Attempt, response.Headers.Location!.AbsoluteUri);
        Assert.Equal(["/api/v1/admin/service-links/attempts/" + Attempt + "/continue"], host.Services.GetRequiredService<ApiFixture>().PostPaths);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Existing_connection_recovery_is_linked_only_after_an_authorized_individual_status_read(bool authorized, bool expectedReference)
    {
        using var host = await HostAsync();
        var api = host.Services.GetRequiredService<ApiFixture>();
        api.Authorized = authorized;
        using var client = host.GetTestClient();
        var form = await FormAsync(host, client, false);
        using var response = await client.PostAsync(Root + "/start", new FormUrlEncodedContent(form));
        var destination = response.Headers.Location!.OriginalString;
        Assert.Contains("status=relationship-already-exists", destination);
        Assert.Equal(expectedReference, destination.Contains("&attemptId=" + Attempt, StringComparison.Ordinal));
        Assert.DoesNotContain("private-peer-proof", destination);
        Assert.DoesNotContain("secret.invalid", destination);
        Assert.Equal(["/api/v1/admin/service-links/start"], api.PostPaths);
    }

    [Fact]
    public async Task Responder_return_refuses_a_navigation_outside_the_retained_callback()
    {
        using var host = await HostAsync();
        host.Services.GetRequiredService<ApiFixture>().NavigationOverride = "https://other.example.invalid/callback";
        using var client = host.GetTestClient();
        var form = await FormAsync(host, client, true);
        using var response = await client.PostAsync(Root + "/continue", new FormUrlEncodedContent(form));
        Assert.Contains("status=invalid-proof", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain("other.example.invalid", response.Headers.Location.OriginalString);
    }

    private static async Task<Dictionary<string, string>> FormAsync(IHost host, HttpClient client, bool hasSession)
    {
        using var response = await client.GetAsync("/fixture/form");
        var token = await response.Content.ReadAsStringAsync();
        var cookies = response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]).ToList();
        if (hasSession)
        {
            var protector = host.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("RatelDesk.ServiceLink.BrowserSession.v1").ToTimeLimitedDataProtector();
            cookies.Add("__Host-RatelDesk.ServiceLink=" + protector.Protect(
                JsonSerializer.Serialize(new { Actor = "fixture-actor", Random = new string('A', 64) }), TimeSpan.FromHours(1)));
        }
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        return new() { ["__RequestVerificationToken"] = token, ["attemptId"] = Attempt,
            ["peerWebBaseUrl"] = "https://peer.example.invalid", ["localTenantId"] = "fixture-tenant" };
    }

    private static Task<IHost> HostAsync() => new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
        .ConfigureServices(services =>
        {
            services.AddRouting(); services.AddAuthorization(); services.AddAntiforgery(); services.AddDataProtection();
            services.AddSingleton<ApiFixture>();
            services.AddSingleton<IHttpClientFactory>(provider => provider.GetRequiredService<ApiFixture>());
        }).Configure(app =>
        {
            app.UseRouting();
            app.Use(async (context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture-actor"),
                    new Claim(ClaimTypes.Role, "HelpdeskAdmin")], "fixture"));
                await next(context);
            });
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/fixture/form", (HttpContext context, IAntiforgery antiforgery) =>
                    Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!));
                ServiceLinkBrowserEndpoints.MapServiceLinkBrowserEndpoints(endpoints);
            });
        })).StartAsync();

    private sealed class ApiFixture : HttpMessageHandler, IHttpClientFactory
    {
        public string Role { get; set; } = "responder";
        public string Action { get; set; } = "return";
        public bool Authorized { get; set; } = true;
        public string? NavigationOverride { get; set; }
        public string? ContinueBinding { get; private set; }
        public List<string> PostPaths { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://api.example.invalid/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                PostPaths.Add(path);
                if (path.EndsWith("/start", StringComparison.Ordinal)) return new(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new { code = "relationship-already-exists", stage = "start", existingAttemptId = Attempt,
                        correlationId = "fedcba9876543210fedcba9876543210", title = "https://secret.invalid/private-peer-proof" })
                };
                var posted = await request.Content!.ReadFromJsonAsync<ServiceLinkContinueRequest>(cancellationToken: token);
                ContinueBinding = posted!.SessionBinding;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServiceLinkNavigation(Attempt,
                    NavigationOverride ?? (Role == "responder" ? Callback : Approval) + "?attempt_id=" + Attempt, "Approved")) };
            }
            if (!Authorized) return new(HttpStatusCode.Forbidden);
            var status = new ServiceLinkAdminStatus(Attempt, null, 0, "Approved", "fixture-tenant", "fixture-peer", "fixture-peer-tenant",
                "Undecided", null, null, new ServiceLinkRequestDescriptor { AttemptId = Attempt, InitiatorCallbackEndpoint = Callback,
                    ResponderEndpointSnapshot = new ServiceLinkMetadata { ApprovalEndpoint = Approval } }, null,
                false, false, false, false, false, null, false, []) { LocalRole = Role, AvailableAction = Action };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(status) };
        }
    }
}
