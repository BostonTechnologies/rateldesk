using System.Net;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class OrchestrationInternalClientTests
{
    [Fact]
    public async Task Successful_empty_response_is_not_invented_as_a_submission()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        await Assert.ThrowsAsync<OrchestrationAcknowledgementException>(() => client.IngestAsync(Settings(), Request()));
    }

    [Fact]
    public async Task Successful_html_health_response_is_not_treated_as_provider_health()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>public home page</html>")
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        var result = await client.HealthAsync(Settings());

        Assert.False(result.Success);
        Assert.Contains("contract", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_error_text_is_bounded_and_redacted()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"message":"client_secret=secret authorization: Bearer provider-token"}""")
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        var result = await client.HealthAsync(Settings());

        Assert.False(result.Success);
        Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-token", result.Message, StringComparison.Ordinal);
        Assert.Contains("redacted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetRatel_wire_names_are_used_and_real_acknowledgement_is_required()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                requestId = "41",
                runId = "99",
                executionId = "99",
                status = "Accepted"
            }))
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        var result = await client.IngestAsync(Settings(), Request());

        Assert.Equal("99", result.ExecutionId);
        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("NetRatelJobDefinitionId", body, StringComparison.Ordinal);
        Assert.Contains("NetRatelRequestDefinitionId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientSecret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Accepted", OrchestrationSubmissionDisposition.Admitted, OrchestrationExecutionOutcome.Processing)]
    [InlineData("Processing", OrchestrationSubmissionDisposition.Admitted, OrchestrationExecutionOutcome.Processing)]
    [InlineData("Completed", OrchestrationSubmissionDisposition.Existing, OrchestrationExecutionOutcome.Completed)]
    [InlineData("Failed", OrchestrationSubmissionDisposition.Existing, OrchestrationExecutionOutcome.Failed)]
    [InlineData("Cancelled", OrchestrationSubmissionDisposition.Existing, OrchestrationExecutionOutcome.Cancelled)]
    [InlineData("already_exists", OrchestrationSubmissionDisposition.Existing, OrchestrationExecutionOutcome.Processing)]
    public async Task Provider_status_is_classified_without_losing_a_remote_identity(
        string status,
        OrchestrationSubmissionDisposition disposition,
        OrchestrationExecutionOutcome outcome)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                executionId = "99",
                status
            }))
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        var result = await client.IngestAsync(Settings(), Request());

        Assert.Equal("99", result.ExecutionId);
        Assert.Equal(disposition, result.Disposition);
        Assert.Equal(outcome, result.Outcome);
    }

    [Fact]
    public async Task Unknown_status_with_a_remote_identity_is_durable_unknown_not_a_rejection()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"executionId":"99","status":"provider-specific-state"}""")
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        var result = await client.IngestAsync(Settings(), Request());

        Assert.Equal("99", result.ExecutionId);
        Assert.Equal(OrchestrationSubmissionDisposition.Unknown, result.Disposition);
        Assert.Equal(OrchestrationExecutionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task Explicit_http_rejection_is_not_reported_as_uncertain()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"request definition rejected"}""")
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        await Assert.ThrowsAsync<OrchestrationSubmissionRejectedException>(() => client.IngestAsync(Settings(), Request()));
    }

    [Fact]
    public async Task Oversized_success_response_is_submission_uncertain_after_send()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 1024 * 1024 + 1))
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);

        var exception = await Assert.ThrowsAsync<OrchestrationSubmissionUncertainException>(() => client.IngestAsync(Settings(), Request()));

        Assert.Contains("too large", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_after_send_is_submission_uncertain()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(sent);
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);
        using var cancellation = new CancellationTokenSource();

        var submission = client.IngestAsync(Settings(), Request(), cancellation.Token);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAsync<OrchestrationSubmissionUncertainException>(() => submission);

        Assert.Contains("cancelled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_before_send_remains_cancellation()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"executionId":"99","status":"Accepted"}""")
        });
        var clientFactory = Substitute.For<IHttpClientFactory>();
        clientFactory.CreateClient("OrchestrationInternalApi").Returns(new HttpClient(handler));
        var tokenService = Substitute.For<IOrchestrationTokenService>();
        tokenService.GetAccessTokenAsync(Arg.Any<OrchestrationResolvedSettings>(), Arg.Any<CancellationToken>()).Returns("token");
        var client = new OrchestrationInternalClient(clientFactory, tokenService, NullLogger<OrchestrationInternalClient>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.IngestAsync(Settings(), Request(), cancellation.Token));
        Assert.Empty(handler.RequestBodies);
    }

    private static OrchestrationResolvedSettings Settings() => new()
    {
        Enabled = true,
        BaseUrl = "https://netratel.example.test",
        TokenEndpoint = "https://netratel.example.test/connect/token",
        Scope = "netratel.api",
        ClientId = "client",
        ClientSecret = "secret",
        IngestPath = "/internal/ingest"
    };

    private static OrchestrationIngestRequest Request() => new()
    {
        RequestId = "request-1",
        RequestTaskId = "task-1",
        CorrelationId = "corr-1",
        OrchestrationRequestDefinitionId = "request-definition-1",
        OrchestrationJobDefinitionId = "job-definition-1",
        JobName = "job",
        PayloadJson = "{}"
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder = responder;
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(request);
        }
    }

    private sealed class BlockingHandler(TaskCompletionSource sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.TrySetResult();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(static state =>
                ((TaskCompletionSource)state!).TrySetCanceled(), cancelled);
            await cancelled.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
