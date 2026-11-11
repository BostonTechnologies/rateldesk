using System.Net;
using System.Text;
using Helpdesk.Application.Orchestration;
using Helpdesk.Shared.DTOs.Orchestration;
using System.Text.Json;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Pairing;
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
        var jobSut = CreateSut("[{\"id\":\"job-1\",\"name\":\"Provision\",\"tenantId\":17}]");
        var tenantSut = CreateSut("[{\"tenantId\":17,\"name\":\"Operations\"}]");

        var jobs = await jobSut.ListJobsAsync();
        var tenants = await tenantSut.ListTenantsAsync();

        Assert.Equal("job-1", Assert.Single(jobs).Id);
        Assert.Equal(17, Assert.Single(tenants).TenantId);
    }

    [Theory]
    [InlineData("executionGroup", 1)]
    [InlineData("explicitNodes", 2)]
    [InlineData("nodeFilter", 3)]
    [InlineData("runtimeInput", 4)]
    public async Task Modern_targets_validate_without_legacy_identity_and_preserve_large_canonical_ids(string kind, int source)
    {
        var target = new OrchestrationJobTargetDefinitionDto { Source = source };
        if (source == 1) target.ExecutionGroupId = "11111111-1111-1111-1111-111111111111";
        if (source == 2) target.AgentIds = ["22222222-2222-2222-2222-222222222222"];
        if (source == 3) target.Filter = "tag = 'synthetic'";
        if (source == 4) target.RuntimeInput = new() { Kind = 2, Name = "workers" };
        var sut = CreateSut(JsonSerializer.Serialize(new[] { new
        {
            contractVersion = 2, requestDefinitionId = "9007199254740993", netRatelJobDefinitionId = "9007199254740993",
            requestDefinitionName = "Canonical job", tenantId = 17, clientIdentity = "", inputs = Array.Empty<object>(),
            target = new OrchestrationAutomationTargetDto { Version = 1, Kind = kind, Summary = "Synthetic execution target",
                Readiness = "worker-check-at-run", Definition = target }
        } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var definition = Assert.Single(await sut.ListRequestDefinitionsAsync());
        Assert.Equal("9007199254740993", definition.RequestDefinitionId);
        Assert.Equal(definition.RequestDefinitionId, definition.OrchestrationJobDefinitionId);
        Assert.Equal(kind, definition.Target!.Kind);
        Assert.Empty(definition.ClientIdentity);
    }

    [Fact]
    public async Task Unknown_target_contract_is_explicit_and_provider_errors_keep_sanitized_operation_code_and_reference()
    {
        var sut = CreateSut("""
            [{"contractVersion":2,"requestDefinitionId":"41","requestDefinitionName":"Synthetic job","tenantId":17,
              "inputs":[],"target":{"version":99,"kind":"future","summary":"Future target"}}]
            """);
        var error = await Assert.ThrowsAsync<OrchestrationProviderException>(() => sut.ListRequestDefinitionsAsync());
        Assert.Equal("unsupported_contract", error.Error.Code);
        var denied = CreateSut("""{"code":"target_invalid","detail":"Choose a current tenant node","correlationId":"synthetic-reference"}""", HttpStatusCode.UnprocessableEntity);
        error = await Assert.ThrowsAsync<OrchestrationProviderException>(() => denied.ListRequestDefinitionsAsync());
        Assert.Equal(422, error.Error.Status);
        Assert.Equal("catalogue", error.Error.Operation);
        Assert.Equal("target_invalid", error.Error.Code);
        Assert.Equal("synthetic-reference", error.Error.CorrelationId);
    }

    private static OrchestrationCatalogService CreateSut(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FixedResponseHandler(responseBody, status);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(PairingTransport.ClientName).Returns(new HttpClient(handler));

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
                Pairing = new("synthetic-tenant", "17", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), 1),
                CatalogPath = "/internal/catalog",
                ClientSecret = "synthetic-secret"
            });

        return new OrchestrationCatalogService(factory, tokenService, connectivity);
    }

    private sealed class FixedResponseHandler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        private readonly string _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
