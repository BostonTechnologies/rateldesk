extern alias NewWeb;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Helpdesk.Shared.DTOs.Resources;
using NSubstitute;
using Polly.Timeout;
using DatasetHttpClients = NewWeb::HelpDesk.NewWeb.Services.HelpdeskCommandHttpClients;
using TokenHandler = NewWeb::HelpDesk.NewWeb.Services.TokenAuthorizationHandler;
using TokenService = NewWeb::HelpDesk.NewWeb.Services.ITokenService;
using DatasetService = NewWeb::HelpDesk.NewWeb.Services.ResourceDatasetService;

namespace Helpdesk.Tests.NewWeb;

public sealed class ResourceDatasetServiceWorkflowTests
{
    [Theory]
    [InlineData("key")]
    [InlineData("dataset")]
    [InlineData("graph")]
    public async Task Registered_mutation_transport_preserves_browser_authority_without_replaying_transient_responses(string operation)
    {
        using var handler = new ResponseHandler(request =>
        {
            Assert.Equal("fixture-api-token", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.BadGateway, "provider-secret response body");
        });
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddServiceDefaults();
        var tokens = Substitute.For<TokenService>();
        tokens.GetValidAccessTokenAsync().Returns(Task.FromResult<string?>("fixture-api-token"));
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        builder.Services.AddTransient<TokenHandler>();
        DatasetHttpClients.AddHelpdeskCommandsClient(builder.Services, new Uri("https://fixture.example.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = builder.Services.BuildServiceProvider();
        var service = new DatasetService(provider.GetRequiredService<IHttpClientFactory>());
        if (operation == "key") Assert.Null(await service.CreateCredentialAsync("dataset", new() { Name = "Named explicit key" }));
        else if (operation == "dataset") Assert.True((await service.CreateWithResultAsync(new() { RequestId = Guid.NewGuid(), OrganizationId = "org", Name = "Explicit dataset" })).IsUncertain);
        else await Assert.ThrowsAsync<HttpRequestException>(() => service.SaveAndImportGraphAsync(new() { RequestId = Guid.NewGuid(), Connection = new() { OrganizationId = "org" } }));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Creation_timeout_remains_uncertain_and_does_not_automatically_resubmit()
    {
        using var handler = new ResponseHandler(_ => throw new TimeoutRejectedException("provider-secret must not be displayed"));
        using var client = Client(handler);
        var service = Service(client);
        var result = await service.CreateWithResultAsync(new() { RequestId = Guid.NewGuid(), OrganizationId = "org", Name = "Synthetic dataset" });
        Assert.True(result.IsUncertain);
        Assert.Null(result.Dataset);
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain("provider-secret", result.Message);
    }

    [Theory]
    [InlineData("{\"Id\":")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":\" \",\"organizationId\":\"org\"}")]
    [InlineData("{\"id\":\"dataset\",\"organizationId\":\"different-org\"}")]
    public async Task Incomplete_creation_response_is_uncertain_and_keeps_the_same_request_reconcilable(string body)
    {
        using var handler = new ResponseHandler(_ => Json(HttpStatusCode.Created, body));
        using var client = Client(handler);
        var result = await Service(client).CreateWithResultAsync(new() { RequestId = Guid.NewGuid(), OrganizationId = "org", Name = "Retained draft" });
        Assert.True(result.IsUncertain);
        Assert.Null(result.Dataset);
        Assert.Equal(1, handler.Requests);
        Assert.Contains("reconcile", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creation_validation_preserves_field_errors_without_marking_a_known_rejection_uncertain()
    {
        using var handler = new ResponseHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"title":"Correct dataset fields","errors":{"Name":["Already exists"],"Columns[1].Name":["Duplicate field"]}}"""));
        using var client = Client(handler);
        var result = await Service(client).CreateWithResultAsync(new() { OrganizationId = "org", Name = "Repeated" });
        Assert.False(result.IsUncertain);
        Assert.Equal("Already exists", Assert.Single(result.Errors["Name"]));
        Assert.Equal("Duplicate field", Assert.Single(result.Errors["Columns[1].Name"]));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("import")]
    [InlineData("latest-status")]
    [InlineData("request-status")]
    [InlineData("reconcile")]
    public async Task Graph_timeouts_have_nonsecret_actionable_feedback_and_never_silently_retry(string operation)
    {
        using var handler = new ResponseHandler(_ => throw new TimeoutRejectedException("provider-secret must not be displayed"));
        using var client = Client(handler);
        var service = Service(client);
        var requestId = Guid.NewGuid();
        Func<Task> call = operation switch
        {
            "test" => () => service.TestGraphConnectionAsync(new() { OrganizationId = "org" }),
            "import" => () => service.SaveAndImportGraphAsync(new() { RequestId = requestId, Connection = new() { OrganizationId = "org" } }),
            "latest-status" => () => service.GetGraphImportStatusAsync("org"),
            "request-status" => () => service.GetGraphImportRequestStatusAsync("org", requestId),
            _ => () => service.ReconcileInterruptedGraphImportAsync("org", requestId)
        };
        var error = await Assert.ThrowsAsync<HttpRequestException>(call);
        Assert.Contains("reload recorded results", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-secret", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("graph-settings-read")]
    [InlineData("graph-settings-save")]
    [InlineData("key-create")]
    [InlineData("key-metadata")]
    public async Task Existing_wizard_operations_normalize_timeouts_without_exposing_provider_text(string operation)
    {
        using var handler = new ResponseHandler(_ => throw new TimeoutRejectedException("provider-secret must not be displayed"));
        using var client = Client(handler);
        var service = Service(client);
        Func<Task> call = operation switch
        {
            "graph-settings-read" => () => service.GetGraphSettingsAsync("org"),
            "graph-settings-save" => () => service.SaveGraphSettingsAsync(new() { OrganizationId = "org" }),
            "key-create" => () => service.CreateCredentialAsync("dataset", new() { Name = "Retained named key draft" }),
            _ => () => service.GetCredentialsAsync("dataset")
        };
        var error = await Assert.ThrowsAsync<HttpRequestException>(call);
        Assert.DoesNotContain("provider-secret", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Explicit_caller_cancellation_is_preserved()
    {
        using var handler = new ResponseHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(client).TestGraphConnectionAsync(new() { OrganizationId = "org" }, cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_graph_settings_read_failure_is_not_reported_as_an_unconfigured_connection(HttpStatusCode status)
    {
        using var handler = new ResponseHandler(_ => Json(status, "{}"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(client).GetGraphSettingsAsync("org"));
    }

    [Fact]
    public async Task Missing_settings_and_missing_exact_request_receipts_are_distinct_successful_reads()
    {
        using var handler = new ResponseHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("import-status", StringComparison.Ordinal))
                Assert.Contains("requestId=", request.RequestUri.Query);
            return Json(HttpStatusCode.NotFound, "{}");
        });
        using var client = Client(handler);
        var service = Service(client);
        Assert.Null(await service.GetGraphSettingsAsync("org"));
        Assert.Null(await service.GetGraphImportRequestStatusAsync("org", Guid.NewGuid()));
        Assert.Null(await service.GetCreateResultAsync("org", Guid.NewGuid()));
        Assert.Equal(3, handler.Requests);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false) { BaseAddress = new("https://fixture.example.test/") };
    private static DatasetService Service(HttpClient client)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return new(factory);
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }
}
