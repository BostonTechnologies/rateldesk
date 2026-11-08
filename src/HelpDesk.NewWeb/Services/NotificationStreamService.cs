using System.Text;
using System.Text.Json;
using Helpdesk.Shared.DTOs.Notification;
using Microsoft.Extensions.Logging;

namespace HelpDesk.NewWeb.Services;

public sealed class NotificationStreamService : IAsyncDisposable
{
    public event Action<bool>? ConnectionChanged;
    public bool IsConnected { get; private set; }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    private const int InitialReconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NotificationStreamService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;

    private CancellationTokenSource? _cts;
    private Task? _runningTask;

    public NotificationStreamService(
        IHttpClientFactory httpClientFactory,
        ILogger<NotificationStreamService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task StartAsync(
        Func<NotificationDto, Task> handler,
        string? tenantId = null,
        string? category = "DomainEvent",
        CancellationToken ct = default)
    {
        if (_disposed)
            return;

        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_runningTask is not null && !_runningTask.IsCompleted)
                return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runningTask = RunStreamLoopAsync(handler, tenantId, category, _cts.Token);
        }
        finally
        {
            try { _gate.Release(); } catch { }
        }
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;

        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await StopCoreAsync();
        }
        finally
        {
            try { _gate.Release(); } catch { }
        }
    }

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        var task = _runningTask;

        _cts = null;
        _runningTask = null;

        try { cts?.Cancel(); } catch { }

        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Notification stream stop ignored exception");
            }
        }

        try { cts?.Dispose(); } catch { }
    }

    private async Task RunStreamLoopAsync(
        Func<NotificationDto, Task> handler,
        string? tenantId,
        string? category,
        CancellationToken ct)
    {
        var retryCount = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndStreamAsync(handler, tenantId, category, ct);
                retryCount = 0;
                await Task.Delay(InitialReconnectDelayMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Notification SSE disconnected; reconnecting");
                retryCount++;
                var delayMs = Math.Min(
                    MaxReconnectDelayMs,
                    (int)(InitialReconnectDelayMs * Math.Pow(2, retryCount)));

                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ConnectAndStreamAsync(
        Func<NotificationDto, Task> handler,
        string? tenantId,
        string? category,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("HelpdeskApiStreaming");
        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenantId))
            queryParts.Add($"tenantId={Uri.EscapeDataString(tenantId)}");
        if (!string.IsNullOrWhiteSpace(category))
            queryParts.Add($"category={Uri.EscapeDataString(category)}");

        var endpoint = "/api/v1/notifications/stream";
        if (queryParts.Count > 0)
            endpoint = $"{endpoint}?{string.Join("&", queryParts)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        SetConnected(true);
        _logger.LogInformation("Notification stream connected");

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? eventName = null;
            var dataBuilder = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break;

                if (line.Length == 0)
                {
                    await DispatchFrameAsync(eventName, dataBuilder, handler);
                    eventName = null;
                    dataBuilder.Clear();
                    continue;
                }

                if (line.StartsWith(':'))
                    continue;

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    eventName = line[6..].Trim();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (dataBuilder.Length > 0)
                        dataBuilder.Append('\n');

                    dataBuilder.Append(line[5..].Trim());
                }
            }
        }
        finally
        {
            SetConnected(false);
            _logger.LogInformation("Notification stream disconnected");
        }
    }

    private static async Task DispatchFrameAsync(
        string? eventName,
        StringBuilder dataBuilder,
        Func<NotificationDto, Task> handler)
    {
        if (dataBuilder.Length == 0)
            return;

        if (!string.IsNullOrWhiteSpace(eventName) &&
            !string.Equals(eventName, "notification", StringComparison.OrdinalIgnoreCase))
            return;

        var dto = JsonSerializer.Deserialize<NotificationDto>(dataBuilder.ToString(), JsonOptions);
        if (dto is null)
            return;

        await handler(dto);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            await StopCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NotificationStreamService DisposeAsync ignored exception");
        }

        try
        {
            _gate.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
