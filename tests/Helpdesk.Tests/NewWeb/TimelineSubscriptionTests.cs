extern alias NewWeb;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Helpdesk.Shared.DTOs.Worklog;
using Microsoft.Extensions.Logging.Abstractions;
using Streams = NewWeb::HelpDesk.NewWeb.Services.TimelineStreamService;
using State = NewWeb::HelpDesk.NewWeb.Services.TimelineConnectionState;

namespace Helpdesk.Tests.NewWeb;

public sealed class TimelineSubscriptionTests
{
    [Fact]
    public async Task Timeline_and_progress_share_one_stream_and_disposing_activity_preserves_parent_updates()
    {
        var factory = new Factory();
        await using var streams = new Streams(factory, NullLogger<Streams>.Instance);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeline = new ConcurrentQueue<TicketTimelineEventDto>();
        await using var parent = await streams.SubscribeAsync("request-one", ticketType: "requests",
            onInvalidated: () => { changed.TrySetResult(true); return Task.CompletedTask; },
            onStateChanged: state => { if (state == State.Live) ready.TrySetResult(true); return Task.CompletedTask; });
        var activity = await streams.SubscribeAsync("request-one", item => { timeline.Enqueue(item); return Task.CompletedTask; }, "requests");
        factory.Body.Frame("event: ready\ndata: {}\n\n");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.Requests);
        await activity.DisposeAsync();
        factory.Body.Frame("event: request-state\ndata: {\"ticketId\":\"request-one\"}\n\n");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.Requests);
        Assert.Empty(timeline);
    }

    [Fact]
    public async Task Another_ticket_frame_cannot_reach_a_subscription_and_duplicate_updates_remain_invalidation_signals()
    {
        var factory = new Factory();
        await using var streams = new Streams(factory, NullLogger<Streams>.Instance);
        var delivered = new ConcurrentQueue<TicketTimelineEventDto>();
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await streams.SubscribeAsync("request-one", item =>
        {
            delivered.Enqueue(item);
            if (delivered.Count == 2) second.TrySetResult(true);
            return Task.CompletedTask;
        }, "requests");
        factory.Body.Frame("event: ready\ndata: {}\n\n");
        factory.Body.Frame("event: timeline\ndata: {\"id\":\"00000000-0000-0000-0000-000000000001\",\"ticketId\":\"request-two\",\"messageText\":\"foreign\"}\n\n");
        foreach (var value in new[] { "current", "older" })
            factory.Body.Frame($"event: timeline\ndata: {{\"id\":\"00000000-0000-0000-0000-000000000002\",\"ticketId\":\"request-one\",\"messageText\":\"{value}\"}}\n\n");
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["current", "older"], delivered.Select(x => x.MessageText));
        // Consumers refetch canonical state, so delivery order never acts as a state revision.
        Assert.All(delivered, item => Assert.Equal("request-one", item.TicketId));
    }

    [Fact]
    public async Task Authorization_denial_pauses_updates_without_an_automatic_retry_loop()
    {
        var factory = new Factory { Denied = true };
        await using var streams = new Streams(factory, NullLogger<Streams>.Instance);
        var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = await streams.SubscribeAsync("request-one", ticketType: "requests",
            onStateChanged: state => { if (state == State.Paused) paused.TrySetResult(true); return Task.CompletedTask; });
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.Requests);
    }

    private sealed class Factory : HttpMessageHandler, IHttpClientFactory
    {
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public bool Denied { get; set; }
        public ProducerStream Body { get; } = new();
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://loopback.test/"), Timeout = Timeout.InfiniteTimeSpan };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            var response = new HttpResponseMessage(Denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StreamContent(Body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
    private sealed class ProducerStream : Stream
    {
        private readonly Channel<byte[]> data = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> remaining;
        public void Frame(string frame) => Assert.True(data.Writer.TryWrite(Encoding.UTF8.GetBytes(frame)));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (remaining.IsEmpty)
            {
                if (!await data.Reader.WaitToReadAsync(ct)) return 0;
                remaining = await data.Reader.ReadAsync(ct);
            }
            var count = Math.Min(buffer.Length, remaining.Length);
            remaining[..count].CopyTo(buffer); remaining = remaining[count..]; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) data.Writer.TryComplete(); base.Dispose(disposing); }
    }
}
