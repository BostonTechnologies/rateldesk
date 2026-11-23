using System.Net;
using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.ServiceDefaults.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed class PublicIngressAdmissionTests
{
    [Fact]
    public async Task Web_relays_share_one_client_budget_while_peers_and_bootstrap_have_independent_budgets()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["PublicIngress:TrustWebProof"] = "true" });
        PublicAdmission.Configure(builder);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<TimeProvider>(new FixtureClock());
        await using var app = builder.Build();
        app.Use((http, next) =>
        {
            http.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
            return next(http);
        });
        PublicAdmission.Use(app);
        app.MapPost("/form", () => Results.NoContent()).RequirePublicAdmission(PublicOperation.Login);
        app.MapPost("/proxy", () => Results.NoContent()).RequirePublicAdmission(PublicOperation.Login);
        app.MapPost("/setup", () => Results.NoContent()).RequirePublicAdmission(PublicOperation.Setup);
        await app.StartAsync();

        using var client = app.GetTestClient();
        var issuer = app.Services.GetRequiredService<PublicIngressIdentity>();
        async Task<HttpResponseMessage> Post(string path, string? address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            issuer.AddProof(request, address);
            return await client.SendAsync(request);
        }
        for (var index = 0; index < 30; index++)
        {
            using var accepted = await Post(index % 2 == 0 ? "/proxy" : "/form", "192.0.2.5");
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }
        using var exhausted = await Post("/form", "192.0.2.5");
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
        Assert.NotNull(exhausted.Headers.RetryAfter);
        using var independent = await Post("/proxy", "192.0.2.6");
        Assert.Equal(HttpStatusCode.NoContent, independent.StatusCode);

        for (var index = 0; index < 5; index++)
        {
            using var accepted = await Post("/setup", "192.0.2.5");
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post("/setup", "192.0.2.5")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/setup", "192.0.2.6")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/setup", null)).StatusCode);
    }

    [Theory]
    [InlineData(false, "192.0.2.4")]
    [InlineData(true, "198.51.100.6")]
    public async Task Client_partition_uses_only_bounded_trusted_hops(bool trusted, string expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        PublicAdmission.Configure(builder);
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        await using var app = builder.Build();
        var peer = IPAddress.Parse("192.0.2.4");
        app.Use((http, next) =>
        {
            http.Connection.RemoteIpAddress = peer;
            return next(http);
        });
        PublicIngressIdentity.UseTrustedForwarding(app,
            trusted ? [peer] : [], trusted ? [System.Net.IPNetwork.Parse("192.0.2.8/30")] : []);
        PublicIngressIdentity.UseApiIngressIdentity(app, trustWeb: false);
        app.MapGet("/test", (HttpContext context) => PublicIngressIdentity.Client(context));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test");
        request.Headers.Add("X-Forwarded-For", "203.0.113.16, 198.51.100.6, 192.0.2.9");
        request.Headers.Add(PublicIngressIdentity.RelayHeader, "fixture-untrusted-value");
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal("192.0.2.4", PublicIngressIdentity.FromAddress(IPAddress.Parse("::ffff:192.0.2.4")));
        Assert.Equal("2001:db8:1:2::", PublicIngressIdentity.FromAddress(IPAddress.Parse("2001:db8:1:2:3:4:5:6")));
    }

    [Fact]
    public void Captcha_global_rate_is_separate_from_per_client_and_reopens_at_the_next_window()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Captcha:RequestsPerClientPerMinute"] = "1", ["Captcha:RequestsPerMinute"] = "2"
        }).Build();
        var clock = new FixtureClock();
        var admission = new PublicAdmission(settings, clock);
        Assert.True(admission.TryAcquire(PublicOperation.Captcha, "first", out _));
        Assert.False(admission.TryAcquire(PublicOperation.Captcha, "first", out var clientRetry));
        Assert.True(admission.TryAcquire(PublicOperation.Captcha, "second", out _));
        Assert.False(admission.TryAcquire(PublicOperation.Captcha, "third", out var globalRetry));
        Assert.Equal(clientRetry, globalRetry);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(admission.TryAcquire(PublicOperation.Captcha, "third", out _));
    }

    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
