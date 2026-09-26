using System.Net;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class OrchestrationConnectivityRuntimeOptionsTests
{
    [Fact]
    public async Task GetOrchestrationSettingsAsync_ResolvesFromRuntimeOptions()
    {
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        var orchestrationClient = Substitute.For<IOrchestrationInternalClient>();
        var sut = new OrchestrationConnectivityService(
            Options.Create(new OrchestrationM2MOptions
            {
                Enabled = true,
                BaseUrl = "https://orchestration.local/",
                Authority = "https://issuer.local",
                Audience = "orchestrator.api",
                Scope = "orchestrator.m2m",
                TokenEndpoint = "https://issuer.local/connect/token"
            }),
            Options.Create(new M2MClientOptions
            {
                ClientId = "helpdesk.api",
                ClientSecret = "secret"
            }),
            tokenService,
            orchestrationClient);

        var settings = await sut.GetOrchestrationSettingsAsync();

        Assert.True(settings.Enabled);
        Assert.Equal("https://orchestration.local/", settings.BaseUrl);
        Assert.Equal("https://issuer.local", settings.Authority);
        Assert.Equal("orchestrator.api", settings.Audience);
        Assert.Equal("orchestrator.m2m", settings.Scope);
        Assert.Equal("https://issuer.local/connect/token", settings.TokenEndpoint);
    }

    [Fact]
    public async Task TestOrchestrationConnectivityAsync_UsesRuntimeOptionsForTokenAndHealth()
    {
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        var orchestrationClient = Substitute.For<IOrchestrationInternalClient>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>())
            .Returns("access-token");
        orchestrationClient.HealthAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>())
            .Returns(new OrchestrationHealthResult { Success = true, StatusCode = 200, Message = "OK" });

        var sut = new OrchestrationConnectivityService(
            Options.Create(new OrchestrationM2MOptions
            {
                Enabled = true,
                BaseUrl = "https://orchestration.local",
                Audience = "orchestrator.api",
                Scope = "orchestrator.m2m"
            }),
            Options.Create(new M2MClientOptions
            {
                ClientId = "helpdesk.api",
                ClientSecret = "secret"
            }),
            tokenService,
            orchestrationClient);

        var result = await sut.TestOrchestrationConnectivityAsync();

        Assert.True(result.Success);
        await tokenService.Received(1).GetAccessTokenAsync(
            Arg.Is<OrchestrationResolvedSettings>(x => x.Scope == "orchestrator.m2m" && x.Audience == "orchestrator.api"),
            Arg.Any<CancellationToken>());
        await orchestrationClient.Received(1).HealthAsync(
            Arg.Is<OrchestrationResolvedSettings>(x => x.BaseUrl == "https://orchestration.local"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrationTokenService_SendsConfiguredScope()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = "token-123",
                expires_in = 300
            }))
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("OrchestrationToken").Returns(new HttpClient(handler));
        var sut = new OrchestrationTokenService(factory, new MemoryCache(new MemoryCacheOptions()));

        await sut.GetAccessTokenAsync(new OrchestrationResolvedSettings
        {
            TokenEndpoint = "https://issuer.local/connect/token",
            Audience = "orchestrator.api",
            Scope = "orchestrator.m2m",
            ClientId = "helpdesk.api",
            ClientSecret = "secret"
        });

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("scope=orchestrator.m2m", body);
        Assert.DoesNotContain("scope=orchestrator.api", body);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                RequestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            }

            return Task.FromResult(_responder(request));
        }
    }
}
