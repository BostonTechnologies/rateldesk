using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.ServiceLink;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.ServiceLink;

public sealed class ServiceLinkDynamicNetworkPolicyTests
{
    [Theory]
    [InlineData("http://[fd00:ec2::254]/")]
    [InlineData("https://[fd00:ec2::254]/")]
    public void Metadata_IPv6_is_denied_for_literals_and_every_DNS_answer_even_with_private_opt_in(string endpoint)
    {
        var metadata = IPAddress.Parse("fd00:ec2::254");
        var uri = new Uri(endpoint);
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.Validate(uri, "peer", true));
        Assert.False(IntegrationEndpointPolicy.IsAllowed(uri, true));
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateServiceLink(uri, "peer", true));
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(
            new Uri("https://peer.example.test/"), [metadata], "peer", true));
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(
            new Uri("https://peer.example.test/"), [IPAddress.Parse("10.2.3.4"), metadata], "peer", true));
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(
            new Uri("https://peer.example.test/"), [new IPAddress(metadata.GetAddressBytes(), 7)], "peer", true));
        IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(new Uri("https://peer.example.test/"),
            [IPAddress.Parse("10.2.3.4")], "peer", true);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("fd12::1234")]
    [InlineData("127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    public void Private_HTTPS_literals_and_mixed_DNS_answers_need_the_current_service_link_opt_in(string address)
    {
        var ip = IPAddress.Parse(address);
        var host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address;
        var literal = new Uri($"https://{host}/");
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateServiceLink(literal, "peer", false));
        Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(
            new Uri("https://peer.example.test/"), [IPAddress.Parse("8.8.8.8"), ip], "peer", false));
        IntegrationEndpointPolicy.ValidateServiceLink(literal, "peer", true);
        IntegrationEndpointPolicy.ValidateServiceLinkResolvedAddresses(new Uri("https://peer.example.test/"), [ip], "peer", true);
        // Other deliberately configured integrations retain their existing private HTTPS behavior.
        IntegrationEndpointPolicy.Validate(literal, "integration", false);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Either_deployment_opt_in_is_normalized_before_both_URL_validators(bool identity, bool linking)
    {
        var configuration = Configuration(identity, linking);
        EnabledConfiguration(configuration, "http://10.1.2.3", "http://10.1.2.4");
        using var provider = Services(configuration);
        var linkOptions = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue;
        var identityOptions = provider.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>().CurrentValue;
        Assert.True(linkOptions.AllowPrivateHttp);
        Assert.True(identityOptions.AllowPrivateHttp);
        Assert.Equal("http://10.1.2.3", linkOptions.ApiBaseUrl);
        Assert.Equal(linkOptions.ApiBaseUrl, identityOptions.ApiBaseUrl);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Existing_transport_uses_reloaded_common_options_and_cannot_dispatch_private_HTTPS(bool identity, bool linking)
    {
        var configuration = Configuration(identity, linking);
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var issuer = provider.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>();
        Assert.True(monitor.CurrentValue.AllowPrivateHttp);
        Assert.True(issuer.CurrentValue.AllowPrivateHttp);
        var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var transport = new ServiceLinkTransport(client, provider.GetRequiredService<IOptions<ServiceLinkOptions>>(), monitor);
        using (await transport.GetAsync<JsonDocument>("https://10.1.2.3/probe", CancellationToken.None)) { }
        Assert.Equal(1, handler.Requests);
        RemoveOptIn(configuration);
        Assert.False(monitor.CurrentValue.AllowPrivateHttp);
        Assert.False(issuer.CurrentValue.AllowPrivateHttp);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.GetAsync<JsonDocument>(
            "https://10.1.2.3/probe", CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void Existing_coordinator_observes_linking_disabled_after_reload()
    {
        var configuration = Configuration(false, false);
        EnabledConfiguration(configuration, "https://api.example.test", "https://web.example.test");
        using var provider = Services(configuration);
        var coordinator = new ServiceLinkCoordinator(null!, null!, null!, null!, null!, null!,
            provider.GetRequiredService<IOptions<ServiceLinkOptions>>(),
            provider.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>(), TimeProvider.System,
            provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());
        Assert.Equal("rd-network-policy", coordinator.Metadata().InstanceId);
        configuration["ServiceLinks:Enabled"] = "false";
        configuration.Reload();
        var error = Assert.Throws<ServiceLinkProtocolException>(() => coordinator.Metadata());
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("service-link-unavailable", error.Code);
    }

    [Theory]
    [InlineData(true, "fd00:ec2::254")]
    [InlineData(false, "127.0.0.1")]
    public async Task Service_link_handler_rejects_the_entire_mixed_DNS_set_before_opening_any_socket(bool allowPrivate, string denied)
    {
        var connections = 0;
        var hooks = new IntegrationConnectionHooks((_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>(
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse(denied)]), (_, _, _) =>
        {
            Interlocked.Increment(ref connections);
            return ValueTask.FromException<Stream>(new InvalidOperationException("The whole DNS answer must be checked before connecting."));
        });
        using var sockets = IntegrationSafeHttpMessageHandler.CreateServiceLinkCore(() => allowPrivate, false, hooks);
        using var client = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(10) };
        var error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync("https://peer.example.test/probe"); });
        Assert.NotNull(error);
        Assert.True(error is HttpRequestException or ArgumentException);
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Opt_in_removed_during_DNS_resolution_prevents_any_socket_connection()
    {
        var configuration = Configuration(false, true);
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved = new TaskCompletionSource<IReadOnlyList<IPAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        var hooks = new IntegrationConnectionHooks((_, ct) =>
        {
            resolving.TrySetResult();
            return new ValueTask<IReadOnlyList<IPAddress>>(resolved.Task.WaitAsync(ct));
        }, (_, _, _) =>
        {
            Interlocked.Increment(ref connections);
            return ValueTask.FromException<Stream>(new InvalidOperationException("An unapproved address must not connect."));
        });
        using var sockets = IntegrationSafeHttpMessageHandler.CreateServiceLinkCore(() => monitor.CurrentValue.AllowPrivateHttp, false, hooks);
        using var client = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(10) };
        using var stale = new HttpRequestMessage(HttpMethod.Get, "https://peer.example.test/probe");
        stale.Options.Set(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, true);
        var sent = Record.ExceptionAsync(async () => { using var response = await client.SendAsync(stale); });
        await resolving.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RemoveOptIn(configuration);
        resolved.SetResult([IPAddress.Loopback]);
        var error = await sent;
        Assert.NotNull(error);
        Assert.True(error is HttpRequestException or ArgumentException);
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Removing_opt_in_after_a_failed_socket_prevents_private_fallback()
    {
        var configuration = Configuration(false, true);
        using var provider = Services(configuration);
        var monitor = provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var connections = 0;
        var hooks = new IntegrationConnectionHooks((_, _) => ValueTask.FromResult<IReadOnlyList<IPAddress>>(
            [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2")]), (_, _, _) =>
        {
            Interlocked.Increment(ref connections);
            RemoveOptIn(configuration);
            return ValueTask.FromException<Stream>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
        });
        using var sockets = IntegrationSafeHttpMessageHandler.CreateServiceLinkCore(() => monitor.CurrentValue.AllowPrivateHttp, false, hooks);
        using var client = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(10) };

        var error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync("https://peer.example.test/probe"); });

        Assert.NotNull(error);
        Assert.True(error is HttpRequestException or ArgumentException);
        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task Existing_HTTPS_client_opens_checked_sockets_and_rejects_a_stale_request_option_after_reload()
    {
        var configuration = Configuration(false, true);
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
            return Microsoft.AspNetCore.Http.Results.Json(new { accepted = true });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new UriBuilder(address) { Host = "localhost", Path = "/probe" }.Uri;
        using var sockets = IntegrationSafeHttpMessageHandler.CreateServiceLink(currentAllowPrivateHttp: () => monitor.CurrentValue.AllowPrivateHttp);
        // Pin this disposable server's certificate; production TLS validation is unchanged.
        sockets.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) => remote is not null &&
            remote.GetRawCertData().AsSpan().SequenceEqual(certificate.RawData);
        using var client = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(10) };
        var transport = new ServiceLinkTransport(client, provider.GetRequiredService<IOptions<ServiceLinkOptions>>(), monitor);
        using (await transport.GetAsync<JsonDocument>(endpoint.AbsoluteUri, CancellationToken.None)) { }
        using (await transport.GetAsync<JsonDocument>(endpoint.AbsoluteUri, CancellationToken.None)) { }
        Assert.Equal(2, connections.Count);

        using var stale = new HttpRequestMessage(HttpMethod.Get, endpoint);
        stale.Options.Set(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, true);
        RemoveOptIn(configuration);
        var error = await Record.ExceptionAsync(async () => { using var response = await client.SendAsync(stale); });
        Assert.NotNull(error);
        Assert.True(error is HttpRequestException or ArgumentException);
        Assert.Equal(2, connections.Count);
    }

    private static IConfigurationRoot Configuration(bool identity, bool linking) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceIdentity:AllowPrivateHttp"] = identity.ToString(),
            ["ServiceLinks:AllowPrivateHttp"] = linking.ToString()
        }).Build();

    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddRatelDeskServiceIdentity(configuration);
        services.AddServiceLinkProtocol(configuration);
        return services.BuildServiceProvider();
    }

    private static void EnabledConfiguration(IConfigurationRoot configuration, string api, string web)
    {
        configuration["ServiceIdentity:Enabled"] = "true";
        configuration["ServiceIdentity:InstanceId"] = "rd-network-policy";
        configuration["ServiceIdentity:Issuer"] = api;
        configuration["ServiceIdentity:ApiBaseUrl"] = api;
        configuration["ServiceIdentity:WebBaseUrl"] = web;
        configuration["ServiceLinks:Enabled"] = "true";
        configuration["ServiceLinks:ApiBaseUrl"] = api;
        configuration["ServiceLinks:WebBaseUrl"] = web;
        configuration.Reload();
    }

    private static void RemoveOptIn(IConfigurationRoot configuration)
    {
        configuration["ServiceIdentity:AllowPrivateHttp"] = "false";
        configuration["ServiceLinks:AllowPrivateHttp"] = "false";
        configuration.Reload();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
