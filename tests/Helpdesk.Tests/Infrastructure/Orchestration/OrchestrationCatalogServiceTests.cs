using System.Net;
using System.Text;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Orchestration;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class OrchestrationCatalogServiceTests
{
    [Fact]
    public async Task Request_definition_catalogue_rejects_an_empty_response_body()
    {
        var sut = CreateSut(string.Empty);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ListRequestDefinitionsAsync());

        Assert.Equal("External orchestration returned an empty catalogue response.", exception.Message);
    }

    [Fact]
    public async Task Request_definition_catalogue_accepts_a_valid_empty_array()
    {
        var sut = CreateSut("[]");

        var result = await sut.ListRequestDefinitionsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task Request_definition_catalogue_rejects_missing_contract_fields()
    {
        var sut = CreateSut("""
            [{
              "requestDefinitionId": "req-1",
              "requestDefinitionName": "Provision mailbox",
              "inputs": []
            }]
            """);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ListRequestDefinitionsAsync());

        Assert.Equal("External orchestration returned an incompatible request-definition catalogue.", exception.Message);
    }

    [Fact]
    public async Task Job_and_tenant_catalogues_validate_their_required_fields()
    {
        var jobSut = CreateSut("[{\"id\":\"job-1\",\"name\":\"Provision\"}]");
        var tenantSut = CreateSut("[{\"tenantId\":7,\"name\":\"Operations\"}]");

        var jobs = await jobSut.ListJobsAsync();
        var tenants = await tenantSut.ListTenantsAsync();

        Assert.Equal("job-1", Assert.Single(jobs).Id);
        Assert.Equal(7, Assert.Single(tenants).TenantId);
    }

    private static OrchestrationCatalogService CreateSut(string responseBody)
    {
        var handler = new FixedResponseHandler(responseBody);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));

        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(
                Arg.Any<OrchestrationResolvedSettings>(),
                Arg.Any<CancellationToken>(),
                useCache: true)
            .Returns("access-token");

        var connectivity = Substitute.For<IOrchestrationConnectivityService>();
        connectivity.GetResolvedOrchestrationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new OrchestrationResolvedSettings
            {
                Enabled = true,
                BaseUrl = "https://orchestration.example.test",
                CatalogPath = "/internal/catalog",
                ClientSecret = "synthetic-secret"
            });

        return new OrchestrationCatalogService(factory, tokenService, connectivity);
    }

    private sealed class FixedResponseHandler(string body) : HttpMessageHandler
    {
        private readonly string _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
