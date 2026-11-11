extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Helpdesk.Shared.DTOs;
using NewWeb::HelpDesk.NewWeb.Services;

namespace Helpdesk.Tests.NewWeb;

public sealed class AdminDirectoryDataSourceTests
{
    [Fact]
    public async Task Superseded_search_cannot_replace_the_newest_result_even_if_the_handler_completes_late()
    {
        using var handler = new ControlledHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        using var source = new AdminDirectoryDataSource<string>();
        var oldLoad = source.LoadAsync(client, "directory?search=old", CancellationToken.None);
        var oldRequest = await handler.Pending.Reader.ReadAsync();
        var newLoad = source.LoadAsync(client, "directory?search=new", CancellationToken.None);
        var newRequest = await handler.Pending.Reader.ReadAsync();
        newRequest.SetResult(Response("new result", 133));
        var current = await newLoad;
        Assert.Equal("new result", Assert.Single(current.Items));
        oldRequest.SetResult(Response("stale result", 1000));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldLoad);
        Assert.Equal(133, source.TotalCount);
        Assert.Null(source.Error);
    }

    [Fact]
    public async Task Refresh_failure_retains_the_committed_rows_and_exposes_a_safe_recoverable_error()
    {
        using var handler = new ControlledHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        using var source = new AdminDirectoryDataSource<string>();
        var first = source.LoadAsync(client, "directory", CancellationToken.None);
        (await handler.Pending.Reader.ReadAsync()).SetResult(Response("retained", 120));
        await first;
        var refresh = source.LoadAsync(client, "directory", CancellationToken.None);
        (await handler.Pending.Reader.ReadAsync()).SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("<html>private upstream exception</html>") });
        var result = await refresh;
        Assert.Equal("retained", Assert.Single(result.Items));
        Assert.Equal(120, result.TotalItems);
        Assert.Contains("HTTP 503", source.Error);
        Assert.DoesNotContain("private", source.Error);
        Assert.True(source.HasLoaded);
        Assert.False(source.IsLoading);
    }

    [Fact]
    public async Task Upstream_timeout_is_a_recoverable_directory_error()
    {
        using var handler = new ControlledHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        using var source = new AdminDirectoryDataSource<string>();
        var request = source.LoadAsync(client, "directory", CancellationToken.None);
        (await handler.Pending.Reader.ReadAsync()).SetException(new TaskCanceledException("Synthetic upstream timeout"));
        var data = await request;
        Assert.Empty(data.Items);
        Assert.Contains("timed out", source.Error);
        Assert.False(source.IsLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resilience_pipeline_failures_offer_a_safe_retry_without_leaking_exceptions(bool circuitOpen)
    {
        using var handler = new ControlledHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        using var source = new AdminDirectoryDataSource<string>();
        var request = source.LoadAsync(client, "directory", CancellationToken.None);
        (await handler.Pending.Reader.ReadAsync()).SetException(circuitOpen
            ? new BrokenCircuitException("private upstream circuit details")
            : new TimeoutRejectedException("private timeout details"));
        var data = await request;
        Assert.Empty(data.Items);
        Assert.NotNull(source.Error); Assert.Contains("refresh", source.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", source.Error); Assert.False(source.IsLoading);
    }

    private static HttpResponseMessage Response(string value, int total) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new PagedResponse<string> { Page = 0, PageSize = 25, TotalCount = total, Items = [value] }) };
    private sealed class ControlledHandler : HttpMessageHandler
    {
        public Channel<TaskCompletionSource<HttpResponseMessage>> Pending { get; } = Channel.CreateUnbounded<TaskCompletionSource<HttpResponseMessage>>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Intentionally ignore cancellation to model an upstream response already in flight.
            var result = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Pending.Writer.TryWrite(result)) throw new InvalidOperationException("Test request channel closed.");
            return result.Task;
        }
    }
}
