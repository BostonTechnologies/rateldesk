using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Orchestration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class OrchestrationTokenExpiryTests
{
    [Fact]
    public async Task Token_is_reused_before_the_conservative_deadline_and_reacquired_at_that_deadline()
    {
        await using var fixture = await TokenEndpointFixture.CreateAsync(10);
        var first = await fixture.GetTokenAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(first, await fixture.GetTokenAsync());
        Assert.Equal(1, fixture.Requests);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await fixture.GetTokenAsync();
        Assert.NotEqual(first, second);
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(second, await fixture.GetTokenAsync());
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(0, fixture.InvalidRequests);
    }

    [Fact]
    public async Task One_second_token_has_no_forced_minimum_cache_window()
    {
        await using var fixture = await TokenEndpointFixture.CreateAsync(1);
        Assert.NotEqual(await fixture.GetTokenAsync(), await fixture.GetTokenAsync());
        Assert.Equal(2, fixture.Requests);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        _ = await fixture.GetTokenAsync();
        Assert.Equal(3, fixture.Requests);
    }

    [Fact]
    public async Task Http_response_transit_consuming_the_reuse_window_prevents_caching()
    {
        await using var fixture = await TokenEndpointFixture.CreateAsync(10);
        var start = fixture.Clock.GetUtcNow();
        fixture.AdvanceDuringNextResponse(TimeSpan.FromSeconds(9));
        var first = await fixture.GetTokenAsync();
        Assert.Equal(start.AddSeconds(9), fixture.Clock.GetUtcNow());
        var second = await fixture.GetTokenAsync();
        Assert.NotEqual(first, second);
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(second, await fixture.GetTokenAsync());
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task Legacy_missing_lifetime_retains_the_default_and_explicit_clock_deadline()
    {
        await using var fixture = await TokenEndpointFixture.CreateAsync(null);
        var first = await fixture.GetTokenAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(269));
        Assert.Equal(first, await fixture.GetTokenAsync());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(first, await fixture.GetTokenAsync());
        Assert.Equal(2, fixture.Requests);
    }

    [Fact]
    public async Task Explicit_cache_bypass_acquires_a_fresh_token_without_replacing_the_cached_token()
    {
        await using var fixture = await TokenEndpointFixture.CreateAsync(10);
        var cached = await fixture.GetTokenAsync();
        Assert.NotEqual(cached, await fixture.GetTokenAsync(useCache: false));
        Assert.Equal(cached, await fixture.GetTokenAsync());
        Assert.Equal(2, fixture.Requests);
    }

    // This fixture exercises the production token service against actual HTTP
    // through TestServer. Its draft profile and synthetic OAuth endpoint make
    // no claim about reciprocal consent or published-peer acceptance.
    private sealed class TokenEndpointFixture : IAsyncDisposable
    {
        private const string ClientId = "synthetic-client";
        private const string ClientSecret = "synthetic-client-secret";
        private const string Scope = "netratel.orchestration.invoke";
        private readonly WebApplication app;
        private readonly HttpClient client;
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly OrchestrationTokenService tokens;
        private readonly int? expiresIn;
        private int requests;
        private int invalidRequests;
        private long nextResponseAdvanceTicks;
        private readonly OrchestrationResolvedSettings settings = new()
        {
            Source = "draft", Enabled = true, Authority = "https://issuer.example.test",
            TokenEndpoint = "https://issuer.example.test/connect/token", BaseUrl = "https://api.example.test",
            ClientId = ClientId, ClientSecret = ClientSecret, Scope = Scope, Audience = "synthetic-api"
        };

        private TokenEndpointFixture(WebApplication app, int? expiresIn)
        {
            this.app = app; this.expiresIn = expiresIn;
            client = app.GetTestClient();
            client.BaseAddress = new Uri("https://issuer.example.test");
            tokens = new(new EndpointClientFactory(client), cache, Clock);
        }

        public TokenClock Clock { get; } = new();
        public int Requests => Volatile.Read(ref requests);
        public int InvalidRequests => Volatile.Read(ref invalidRequests);
        public Task<string> GetTokenAsync(bool useCache = true) => tokens.GetAccessTokenAsync(settings, useCache: useCache);
        public void AdvanceDuringNextResponse(TimeSpan elapsed) => Interlocked.Exchange(ref nextResponseAdvanceTicks, elapsed.Ticks);

        public static async Task<TokenEndpointFixture> CreateAsync(int? expiresIn)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var app = builder.Build();
            TokenEndpointFixture? fixture = null;
            app.MapPost("/connect/token", (HttpRequest request) => fixture!.RespondAsync(request));
            await app.StartAsync();
            fixture = new(app, expiresIn);
            return fixture;
        }

        private async Task<IResult> RespondAsync(HttpRequest request)
        {
            var form = await request.ReadFormAsync();
            if (form["grant_type"] != "client_credentials" || form["client_id"] != ClientId ||
                form["client_secret"] != ClientSecret || form["scope"] != Scope)
            {
                Interlocked.Increment(ref invalidRequests);
                return Results.BadRequest();
            }
            var number = Interlocked.Increment(ref requests);
            Clock.Advance(TimeSpan.FromTicks(Interlocked.Exchange(ref nextResponseAdvanceTicks, 0)));
            return expiresIn is { } lifetime
                ? Results.Json(new { access_token = "synthetic-token-" + number, token_type = "Bearer", expires_in = lifetime })
                : Results.Json(new { access_token = "synthetic-token-" + number, token_type = "Bearer" });
        }

        public async ValueTask DisposeAsync()
        {
            cache.Dispose(); client.Dispose(); await app.DisposeAsync();
        }

        private sealed class EndpointClientFactory(HttpClient client) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => name == "OrchestrationToken"
                ? client : throw new InvalidOperationException("Unexpected token client.");
        }
    }

    private sealed class TokenClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
