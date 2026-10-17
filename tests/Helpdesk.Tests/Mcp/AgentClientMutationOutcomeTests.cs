using System.Net;
using Helpdesk.AgentClient;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class AgentClientMutationOutcomeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_write_response_keeps_a_trace_without_replaying_or_exposing_transport_details(bool headersReceived)
    {
        var calls = 0;
        string? sentCorrelation = null;
        var client = Client(request =>
        {
            calls++;
            sentCorrelation = request.Headers.GetValues("X-Correlation-Id").Single();
            if (!headersReceived) throw new HttpRequestException("private-transport-detail");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new InterruptedStream())
            };
            response.Headers.Add("X-Correlation-Id", "corr-committed-write");
            return response;
        });

        var failure = await Assert.ThrowsAsync<AgentClientRemoteException>(() => client.SendAsync(HttpMethod.Post, "/api/v1/request-tasks/synthetic-task/complete"));

        Assert.Equal("api_connection_lost", failure.Code);
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal(headersReceived ? "corr-committed-write" : sentCorrelation, failure.CorrelationId);
        Assert.Matches("^corr-[a-zA-Z0-9-]+$", sentCorrelation!);
        Assert.Equal(1, calls);
        Assert.Null(failure.ResponseBody);
        Assert.DoesNotContain("private-transport-detail", failure.ToString());
    }

    [Fact]
    public async Task Invalid_success_body_retains_correlation_and_is_a_bounded_remote_failure()
    {
        var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("private-invalid-response{") };
            response.Headers.Add("X-Correlation-Id", "corr-serialized-write");
            return response;
        });

        var failure = await Assert.ThrowsAsync<AgentClientRemoteException>(() => client.SendAsync(HttpMethod.Post, "/api/v1/request-tasks/synthetic-task/start"));

        Assert.Equal("api_response_invalid", failure.Code);
        Assert.Equal("corr-serialized-write", failure.CorrelationId);
        Assert.Null(failure.ResponseBody);
        Assert.DoesNotContain("private-invalid-response", failure.ToString());
    }

    [Fact]
    public async Task Write_timeout_before_headers_keeps_its_outbound_correlation()
    {
        string? sentCorrelation = null;
        var client = Client(request =>
        {
            sentCorrelation = request.Headers.GetValues("X-Correlation-Id").Single();
            throw new TaskCanceledException();
        });

        var failure = await Assert.ThrowsAsync<AgentClientRemoteException>(() => client.SendAsync(HttpMethod.Post, "/api/v1/request-tasks/synthetic-task/fail"));

        Assert.Equal("api_request_timeout", failure.Code);
        Assert.Equal(504, failure.StatusCode);
        Assert.Equal(sentCorrelation, failure.CorrelationId);
        Assert.NotNull(sentCorrelation);
    }

    [Fact]
    public async Task Bounded_body_failure_retains_the_received_correlation()
    {
        var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) };
            response.Headers.Add("X-Correlation-Id", "corr-large-response");
            return response;
        });

        var failure = await Assert.ThrowsAsync<AgentClientRemoteException>(() => client.SendAsync(HttpMethod.Post, "/api/v1/request-tasks/synthetic-task/complete"));

        Assert.Equal("api_response_too_large", failure.Code);
        Assert.Equal("corr-large-response", failure.CorrelationId);
        Assert.Null(failure.ResponseBody);
    }

    private static HelpdeskAgentClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://api.example",
            CredentialMode = "integration",
            IntegrationCredential = "rdk_synthetic_secret"
        }, () => new Handler(responder));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class InterruptedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("private-transport-detail"));
    }
}
