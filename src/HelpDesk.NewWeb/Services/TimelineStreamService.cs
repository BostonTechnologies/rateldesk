using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Helpdesk.Shared.DTOs.Worklog;

namespace HelpDesk.NewWeb.Services;

public enum TimelineConnectionState { Connecting, Live, Reconnecting, Paused }

/// <summary>Shares one authorized, request-scoped SSE connection between mounted consumers.</summary>
public sealed class TimelineStreamService(
    IHttpClientFactory httpClientFactory,
    ILogger<TimelineStreamService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private bool disposed;

    public async Task<IAsyncDisposable> SubscribeAsync(
        string ticketId,
        Func<TicketTimelineEventDto, Task>? onTimeline = null,
        string ticketType = "incidents",
        Func<Task>? onInvalidated = null,
        Func<TimelineConnectionState, Task>? onStateChanged = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketId);
        if (ticketType is not ("incidents" or "requests" or "changes" or "self-service/requests"))
            throw new ArgumentException("Unsupported ticket stream.", nameof(ticketType));
        var key = $"{ticketType}:{ticketId}";
        var subscriber = new Subscriber(onTimeline, onInvalidated, onStateChanged, ct);
        Connection connection;
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!connections.TryGetValue(key, out connection!))
            {
                connection = new Connection(httpClientFactory, logger, ticketId, ticketType);
                connections.Add(key, connection);
            }
            connection.Subscribers[subscriber.Id] = subscriber;
            connection.EnsureRunning();
        }
        finally { gate.Release(); }
        await subscriber.StateAsync(connection.State);
        return new Subscription(this, key, connection, subscriber);
    }

    private async ValueTask ReleaseAsync(string key, Connection connection, Subscriber subscriber)
    {
        subscriber.Active = false;
        var stop = false;
        await gate.WaitAsync();
        try
        {
            connection.Subscribers.TryRemove(subscriber.Id, out _);
            if (connections.TryGetValue(key, out var current) && ReferenceEquals(current, connection) && connection.Subscribers.IsEmpty)
            {
                connections.Remove(key);
                stop = true;
            }
        }
        finally { gate.Release(); }
        if (stop) await connection.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Connection[] active;
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            active = connections.Values.ToArray();
            connections.Clear();
        }
        finally { gate.Release(); }
        foreach (var connection in active) await connection.DisposeAsync();
    }

    private sealed class Subscription(TimelineStreamService owner, string key, Connection connection, Subscriber subscriber) : IAsyncDisposable
    {
        private int released;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref released, 1) == 0
            ? owner.ReleaseAsync(key, connection, subscriber) : ValueTask.CompletedTask;
    }

    private sealed class Subscriber(
        Func<TicketTimelineEventDto, Task>? timeline,
        Func<Task>? invalidated,
        Func<TimelineConnectionState, Task>? stateChanged,
        CancellationToken cancellation)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public volatile bool Active = true;
        public Task TimelineAsync(TicketTimelineEventDto value) => Active && !cancellation.IsCancellationRequested && timeline is not null ? timeline(value) : Task.CompletedTask;
        public Task InvalidatedAsync() => Active && !cancellation.IsCancellationRequested && invalidated is not null ? invalidated() : Task.CompletedTask;
        public Task StateAsync(TimelineConnectionState state) => Active && !cancellation.IsCancellationRequested && stateChanged is not null ? stateChanged(state) : Task.CompletedTask;
    }

    private sealed class Connection(
        IHttpClientFactory factory,
        ILogger logger,
        string ticketId,
        string ticketType) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        private readonly CancellationTokenSource lifetime = new();
        private Task? running;
        public ConcurrentDictionary<Guid, Subscriber> Subscribers { get; } = new();
        public TimelineConnectionState State { get; private set; } = TimelineConnectionState.Connecting;

        // Called under the owner's gate, including when a manual refresh resumes a paused stream.
        public void EnsureRunning()
        {
            if (running is null || running.IsCompleted) running = RunAsync(lifetime.Token);
        }

        private async Task SetStateAsync(TimelineConnectionState state)
        {
            State = state;
            foreach (var subscriber in Subscribers.Values) await DeliverAsync(() => subscriber.StateAsync(state));
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var failures = 0;
            await SetStateAsync(TimelineConnectionState.Connecting);
            while (!ct.IsCancellationRequested)
            {
                var connected = Stopwatch.StartNew();
                try
                {
                    await ReadConnectionAsync(ct);
                    if (ct.IsCancellationRequested) return;
                    throw new IOException("The timeline stream ended.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    logger.LogInformation("Timeline subscription is no longer authorized for {TicketId}", ticketId);
                    await SetStateAsync(TimelineConnectionState.Paused);
                    return;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
                {
                    logger.LogDebug(error, "Timeline disconnected for {TicketId}", ticketId);
                    if (connected.Elapsed >= TimeSpan.FromSeconds(30)) failures = 0;
                    if (++failures >= 6)
                    {
                        await SetStateAsync(TimelineConnectionState.Paused);
                        return;
                    }
                    await SetStateAsync(TimelineConnectionState.Reconnecting);
                    // Bounded exponential reconnect backoff; the browser never infers task outcomes.
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures - 1))), ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Timeline could not resume for {TicketId}", ticketId);
                    await SetStateAsync(TimelineConnectionState.Paused);
                    return;
                }
            }
        }

        private async Task ReadConnectionAsync(CancellationToken ct)
        {
            var client = factory.CreateClient("HelpdeskApiStreaming");
            using var message = new HttpRequestMessage(HttpMethod.Get, $"api/v1/{ticketType}/{Uri.EscapeDataString(ticketId)}/timeline/stream");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new IOException("The server did not open an event stream.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? eventName = null;
            var data = new StringBuilder();
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // The API sends a heartbeat every 15 seconds. A silent connection must reconnect
            // and resynchronize rather than leaving a false Live indicator indefinitely.
            idle.CancelAfter(TimeSpan.FromSeconds(45));
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(idle.Token) is { } line)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                if (line.Length == 0)
                {
                    await DispatchAsync(eventName, data.ToString());
                    eventName = null;
                    data.Clear();
                }
                else if (line.StartsWith(": connected", StringComparison.Ordinal))
                {
                    // Compatibility for incident/change endpoints. Request endpoints send ready
                    // only after server subscription/cursor setup, closing the snapshot/event gap.
                    if (ticketType is "incidents" or "changes") await SetStateAsync(TimelineConnectionState.Live);
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length + line.Length > 2 * 1024 * 1024) throw new IOException("Timeline frame exceeds the supported limit.");
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());
                }
            }
        }

        private async Task DispatchAsync(string? eventName, string data)
        {
            if (eventName == "ready")
            {
                await SetStateAsync(TimelineConnectionState.Live);
                return;
            }
            if (eventName == "request-state")
            {
                foreach (var subscriber in Subscribers.Values) await DeliverAsync(subscriber.InvalidatedAsync);
                return;
            }
            if (data.Length == 0 || eventName is not (null or "" or "timeline")) return;
            var value = JsonSerializer.Deserialize<TicketTimelineEventDto>(data, JsonOptions);
            if (value is null || !string.Equals(value.TicketId, ticketId, StringComparison.Ordinal)) return;
            foreach (var subscriber in Subscribers.Values)
            {
                await DeliverAsync(() => subscriber.TimelineAsync(value));
                await DeliverAsync(subscriber.InvalidatedAsync);
            }
        }

        private async Task DeliverAsync(Func<Task> handler)
        {
            try { await handler(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { logger.LogDebug("Timeline consumer cancelled for {TicketId}", ticketId); }
            catch (Exception error) { logger.LogWarning(error, "Timeline consumer could not refresh {TicketId}", ticketId); }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            if (running is not null) await running;
            lifetime.Dispose();
        }
    }
}
