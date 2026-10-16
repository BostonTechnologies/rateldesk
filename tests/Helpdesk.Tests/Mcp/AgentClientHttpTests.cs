using System.Net;
using System.Text;
using Helpdesk.AgentClient;
using Xunit;

namespace Helpdesk.Tests.Mcp;

public sealed class AgentClientHttpTests
{
    [Fact]
    public async Task Health_uses_one_deadline_and_preserves_completed_checks()
    {
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new CancellationAwareHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri?.AbsolutePath == "/health/live")
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"Healthy\"}") };

                var cancelled = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
                return await cancelled.Task;
            }));

        var checks = await client.GetHealthAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, checks.Count);
        Assert.True(checks[0]!["success"]!.GetValue<bool>());
        Assert.Equal("ready", checks[1]!["name"]!.GetValue<string>());
        Assert.Equal("auth", checks[2]!["name"]!.GetValue<string>());
        Assert.All(checks.Skip(1), check =>
        {
            Assert.False(check!["success"]!.GetValue<bool>());
            Assert.Equal(504, check["statusCode"]!.GetValue<int>());
            Assert.Equal("health_check_timeout", check["error"]!.GetValue<string>());
        });
    }

    [Fact]
    public async Task Health_preserves_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new CancellationAwareHandler((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(token);
            }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetHealthAsync(cancellation.Token));
    }

    [Fact]
    public async Task Token_is_cached_until_expiry()
    {
        var calls = 0;
        var client = new HelpdeskAgentClient(new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null), () => new TestHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}") };
        }));

        Assert.Equal("token", await client.GetAccessTokenAsync());
        Assert.Equal("token", await client.GetAccessTokenAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Synchronously_completed_expired_refresh_is_not_reused()
    {
        var calls = 0;
        var client = new HelpdeskAgentClient(new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null), () => new TestHandler(_ =>
        {
            var token = $"token-{++calls}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"access_token\":\"{token}\",\"expires_in\":0}}") };
        }));

        Assert.Equal("token-1", await client.GetAccessTokenAsync());
        Assert.Equal("token-2", await client.GetAccessTokenAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Independent_clients_do_not_share_token_caches()
    {
        var devCalls = 0;
        var prodCalls = 0;
        var dev = new HelpdeskAgentClient(new AgentClientConfiguration("https://dev.example", "https://auth.example/token", "dev-client", "dev-agent", "dev-secret", "scope", null), () => new TestHandler(_ =>
        {
            devCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"dev-token\",\"expires_in\":3600}") };
        }));
        var prod = new HelpdeskAgentClient(new AgentClientConfiguration("https://prod.example", "https://auth.example/token", "prod-client", "prod-agent", "prod-secret", "scope", null), () => new TestHandler(_ =>
        {
            prodCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"prod-token\",\"expires_in\":3600}") };
        }));

        Assert.Equal("dev-token", await dev.GetAccessTokenAsync());
        Assert.Equal("prod-token", await prod.GetAccessTokenAsync());
        Assert.Equal("dev-token", await dev.GetAccessTokenAsync());
        Assert.Equal("prod-token", await prod.GetAccessTokenAsync());
        Assert.Equal(1, devCalls);
        Assert.Equal(1, prodCalls);
    }

    [Fact]
    public async Task Concurrent_token_requests_share_one_refresh()
    {
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new AsyncTestHandler(async _ =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}")
                };
            }));

        var first = client.GetAccessTokenAsync();
        await started.Task;
        var second = client.GetAccessTokenAsync();
        release.SetResult();

        Assert.Equal(["token", "token"], await Task.WhenAll(first, second));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancelling_a_token_waiter_does_not_cancel_the_shared_refresh()
    {
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new AsyncTestHandler(async _ =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}")
                };
            }));

        var first = client.GetAccessTokenAsync();
        await started.Task;
        using var cancelled = new CancellationTokenSource();
        var waiter = client.GetAccessTokenAsync(cancelled.Token);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();

        Assert.Equal("token", await first);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancelling_the_refresh_starter_cancels_the_active_token_http_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new CancellationAwareHandler(async (_, cancellationToken) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { cancelled.TrySetResult(); }
                throw new InvalidOperationException("The cancellable handler should not complete normally.");
            }));

        using var cancellation = new CancellationTokenSource();
        var starter = client.GetAccessTokenAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Unreachable_token_endpoint_returns_a_structured_timeout_failure()
    {
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new AsyncTestHandler(_ => Task.FromException<HttpResponseMessage>(new TaskCanceledException())));

        var exception = await Assert.ThrowsAsync<AgentClientRemoteException>(() => client.GetAccessTokenAsync());

        Assert.Equal("auth_token_timeout", exception.Code);
        Assert.Equal(504, exception.StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_api_request_cancels_the_active_api_http_request()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new CancellationAwareHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri?.Host == "auth.example")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"token\",\"expires_in\":3600}")
                    };
                }

                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { cancelled.TrySetResult(); }
                throw new InvalidOperationException("The cancellable handler should not complete normally.");
            }));

        // Cache the token before making the cancellable API request.
        await client.GetAccessTokenAsync();
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync("/api/v1/auth/ai-agent/status", cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Api_requests_use_the_agent_token_and_never_an_unrelated_inbound_token()
    {
        const string inboundMcpToken = "inbound-mcp-token";
        string? outboundAuthorization = null;
        var client = new HelpdeskAgentClient(
            new AgentClientConfiguration("https://api.example", "https://auth.example/token", "client", "agent", "secret", "scope", null),
            () => new TestHandler(request =>
            {
                if (request.RequestUri?.Host == "auth.example")
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"access_token\":\"outbound-agent-token\",\"expires_in\":3600}")
                    };

                outboundAuthorization = request.Headers.Authorization?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}")
                };
            }));

        await client.GetAsync("/api/v1/auth/ai-agent/status");

        Assert.Equal("Bearer outbound-agent-token", outboundAuthorization);
        Assert.DoesNotContain(inboundMcpToken, outboundAuthorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("corr-safe-123", "corr-safe-123")]
    [InlineData("unsafe private reference", null)]
    [InlineData("rdk_secret_as_reference", null)]
    public async Task Downstream_denial_preserves_only_bounded_safe_correlation_header(string header, string? expected)
    {
        var configuration = AgentClientConfiguration.Empty with { ApiBaseUrl = "https://api.example", CredentialMode = "integration", IntegrationCredential = "rdk_test_secret" };
        var client = new HelpdeskAgentClient(configuration, () => new TestHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("raw-private-payload") };
            response.Headers.TryAddWithoutValidation("X-Correlation-Id", header);
            return response;
        }));
        var denied = await Assert.ThrowsAsync<AgentClientRemoteException>(() => client.GetAsync("/api/v1/auth/me"));
        Assert.Equal(expected, denied.CorrelationId); Assert.Equal(403, denied.StatusCode); Assert.Null(denied.ResponseBody);
        Assert.DoesNotContain("raw-private-payload", denied.Message);
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }

    private sealed class AsyncTestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }

    private sealed class CancellationAwareHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
