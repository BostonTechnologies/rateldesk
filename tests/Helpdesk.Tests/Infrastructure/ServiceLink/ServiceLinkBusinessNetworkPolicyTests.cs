using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.ServiceLink;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceLink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.ServiceLink;

public sealed class ServiceLinkBusinessNetworkPolicyTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("business")]
    [InlineData("catalog")]
    public async Task Existing_managed_senders_deny_private_HTTPS_after_reload_despite_the_saved_profile_opt_in(string sender)
    {
        var configuration = Configuration();
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var settings = Settings("https://10.1.2.3");
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var factory = new RecordingFactory(http);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var tokens = new OrchestrationTokenService(factory, cache, currentLinkOptions: monitor);
        var dispatch = Dispatch(sender, factory, tokens, settings, monitor);
        await dispatch();
        Assert.Equal(1, handler.Requests);
        Assert.Equal(sender == "token" ? ServiceLinkOutboundNetwork.TokenClientName : ServiceLinkOutboundNetwork.BusinessClientName,
            Assert.Single(factory.Names));
        if (sender == "token")
        {
            await dispatch();
            Assert.Equal(1, handler.Requests); // The token is cached before the deployment opt-in changes.
        }
        RemoveOptIn(configuration);
        Assert.True(settings.AllowPrivateHttp); // The old saved profile remains in the caller's hands.
        await Assert.ThrowsAsync<InvalidOperationException>(dispatch);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("business")]
    [InlineData("catalog")]
    public async Task Removing_opt_in_during_token_acquisition_blocks_the_business_request_before_dispatch(string sender)
    {
        var configuration = Configuration();
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var issued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new FixedTokens(() => { acquiring.TrySetResult(); return issued.Task; });
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var factory = new RecordingFactory(http);
        var dispatch = Dispatch(sender, factory, tokens, Settings("https://10.1.2.3"), monitor);
        var sent = Record.ExceptionAsync(dispatch);
        await acquiring.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RemoveOptIn(configuration);
        issued.SetResult("already-issued-token");
        Assert.IsType<InvalidOperationException>(await sent);
        Assert.Equal(0, handler.Requests);
        Assert.Empty(factory.Names);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("business")]
    [InlineData("catalog")]
    public async Task Linking_disabled_after_reload_denies_managed_senders_even_with_private_opt_in_retained(string sender)
    {
        var configuration = Configuration();
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var factory = new RecordingFactory(http);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var tokens = new OrchestrationTokenService(factory, cache, currentLinkOptions: monitor);
        var dispatch = Dispatch(sender, factory, tokens, Settings("https://peer.example.test"), monitor);
        configuration["ServiceLinks:Enabled"] = "false";
        configuration.Reload();
        Assert.True(monitor.CurrentValue.AllowPrivateHttp);
        await Assert.ThrowsAsync<InvalidOperationException>(dispatch);
        Assert.Equal(0, handler.Requests);
        Assert.Empty(factory.Names);
    }

    [Fact]
    public async Task Managed_requests_require_current_options_and_manual_private_HTTPS_profiles_keep_their_existing_policy()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var factory = new RecordingFactory(http);
        var client = new OrchestrationInternalClient(factory, new FixedTokens(), NullLogger<OrchestrationInternalClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.HealthAsync(Settings("https://10.1.2.3")));
        Assert.Equal(0, handler.Requests);
        await client.HealthAsync(Settings("https://10.1.2.3", linked: false));
        Assert.Equal(1, handler.Requests);
        Assert.Equal("OrchestrationInternalApi", Assert.Single(factory.Names));
    }

    [Fact]
    public async Task Managed_business_HTTPS_client_does_not_reuse_an_approved_private_socket_after_opt_in_removal()
    {
        var configuration = Configuration();
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        await using var app = builder.Build();
        var connections = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        app.MapGet("/probe", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            connections.TryAdd(context.Connection.Id, 0);
            return Microsoft.AspNetCore.Http.Results.Json(new { ok = true });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new UriBuilder(address) { Host = "localhost" }.Uri;
        using var sockets = IntegrationSafeHttpMessageHandler.CreateServiceLink(() => monitor.CurrentValue.AllowPrivateHttp);
        // Trust only this disposable test certificate.
        sockets.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) => remote is not null &&
            remote.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData);
        using var http = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(10) };
        var factory = new RecordingFactory(http);
        var client = new OrchestrationInternalClient(factory, new FixedTokens(), NullLogger<OrchestrationInternalClient>.Instance, monitor);
        var settings = Settings(endpoint.AbsoluteUri);
        Assert.True((await client.HealthAsync(settings)).Success);
        Assert.True((await client.HealthAsync(settings)).Success);
        Assert.Equal(2, connections.Count);
        Assert.All(factory.Names, name => Assert.Equal(ServiceLinkOutboundNetwork.BusinessClientName, name));
        RemoveOptIn(configuration);
        var error = await Record.ExceptionAsync(async () => { await client.HealthAsync(settings); });
        Assert.NotNull(error);
        Assert.True(error is HttpRequestException or ArgumentException);
        Assert.Equal(2, connections.Count);
    }

    private static Func<Task> Dispatch(string sender, IHttpClientFactory factory, IOrchestrationTokenService tokens,
        OrchestrationResolvedSettings settings, IOptionsMonitor<ServiceLinkOptions> monitor)
    {
        if (sender == "token") return async () => { await tokens.GetAccessTokenAsync(settings); };
        // Business/catalog network tests use an issued token to isolate their outbound boundary.
        if (tokens is OrchestrationTokenService) tokens = new FixedTokens();
        if (sender == "business")
        {
            var client = new OrchestrationInternalClient(factory, tokens, NullLogger<OrchestrationInternalClient>.Instance, monitor);
            return async () => { await client.HealthAsync(settings); };
        }
        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        connectivity.GetResolvedOrchestrationSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var catalog = new OrchestrationCatalogService(factory, tokens, connectivity, monitor);
        return async () => { await catalog.ListJobsAsync(); };
    }

    private static OrchestrationResolvedSettings Settings(string endpoint, bool linked = true) => new()
    {
        Enabled = true, BaseUrl = endpoint, TokenEndpoint = endpoint.TrimEnd('/') + "/connect/token",
        Authority = endpoint, ClientId = "network-policy-client", ClientSecret = new string('s', 48),
        Scope = "netratel.catalog.read", Audience = "netratel.services", AllowPrivateHttp = true,
        HealthPath = "/probe", CatalogPath = "/catalog",
        // Durable authority is covered by the HTTP pair suite; these fixtures isolate the network check.
        Source = "network-policy-fixture",
        ServiceLink = linked ? new("local-org", "peer-tenant", "peer-instance", "link-id", 1, 1,
            new string('a', 64), "initiator_to_responder") : null
    };

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceIdentity:AllowPrivateHttp"] = "false", ["ServiceLinks:AllowPrivateHttp"] = "true",
            ["ServiceLinks:Enabled"] = "true", ["ServiceLinks:ApiBaseUrl"] = "https://api.example.test",
            ["ServiceLinks:WebBaseUrl"] = "https://web.example.test"
        }).Build();

    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddRatelDeskServiceIdentity(configuration);
        services.AddServiceLinkProtocol(configuration);
        return services.BuildServiceProvider();
    }

    private static void RemoveOptIn(IConfigurationRoot configuration)
    {
        configuration["ServiceIdentity:AllowPrivateHttp"] = "false";
        configuration["ServiceLinks:AllowPrivateHttp"] = "false";
        configuration.Reload();
    }

    private sealed class FixedTokens(Func<Task<string>>? issue = null) : IOrchestrationTokenService
    {
        public Task<string> GetAccessTokenAsync(OrchestrationResolvedSettings settings,
            CancellationToken cancellationToken = default, bool useCache = true)
            => issue?.Invoke() ?? Task.FromResult("network-test-token");
    }

    private sealed class RecordingFactory(HttpClient client) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];
        public HttpClient CreateClient(string name) { Names.Add(name); return client; }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = request.RequestUri!.AbsolutePath == "/connect/token" ?
                """{"access_token":"network-test-token","expires_in":300}""" :
                request.RequestUri.AbsolutePath.StartsWith("/catalog", StringComparison.Ordinal) ? "[]" : """{"ok":true}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
