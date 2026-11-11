using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Shared.DTOs;
using MudBlazor;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace HelpDesk.NewWeb.Services;

/// <summary>Keeps committed directory results during refresh and rejects superseded responses.</summary>
public sealed class AdminDirectoryDataSource<T> : IDisposable
{
    private CancellationTokenSource? request;
    private long revision;
    public string? Error { get; private set; }
    public bool HasLoaded { get; private set; }
    public bool IsLoading { get; private set; }
    public int Page { get; private set; }
    public int TotalCount { get; private set; }
    private TableData<T> lastData = new() { Items = [], TotalItems = 0 };

    public async Task<TableData<T>> LoadAsync(HttpClient api, string url, CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref revision);
        request?.Cancel();
        request?.Dispose();
        request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = request.Token;
        Error = null;
        IsLoading = true;
        try
        {
            var response = await api.GetFromJsonAsync<PagedResponse<T>>(url, token)
                ?? throw new JsonException("Missing directory response.");
            if (response.Items is null || response.TotalCount < 0 || response.Page < 0) throw new JsonException("Invalid directory response.");
            token.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref revision)) throw new OperationCanceledException(token);
            Page = response.Page;
            TotalCount = response.TotalCount;
            HasLoaded = true;
            lastData = new TableData<T> { Items = response.Items, TotalItems = response.TotalCount };
            return lastData;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || version != Volatile.Read(ref revision))
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Error = "The directory request timed out. Refresh to try again.";
            return lastData;
        }
        catch (BrokenCircuitException)
        {
            Error = "The directory service is temporarily unavailable. Wait briefly, then refresh to retry.";
            return lastData;
        }
        catch (TimeoutRejectedException)
        {
            Error = "The directory request timed out. Refresh to try again.";
            return lastData;
        }
        catch (HttpRequestException ex)
        {
            if (token.IsCancellationRequested || version != Volatile.Read(ref revision)) throw new OperationCanceledException(token);
            if (version == Volatile.Read(ref revision))
                Error = ex.StatusCode.HasValue
                    ? $"The directory could not be loaded (HTTP {(int)ex.StatusCode}). Refresh to try again."
                    : "The directory could not be reached. Refresh to try again.";
            return lastData;
        }
        catch (JsonException)
        {
            if (token.IsCancellationRequested || version != Volatile.Read(ref revision)) throw new OperationCanceledException(token);
            if (version == Volatile.Read(ref revision)) Error = "The directory returned an invalid response. Refresh to try again.";
            return lastData;
        }
        finally
        {
            if (version == Volatile.Read(ref revision)) IsLoading = false;
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref revision);
        request?.Cancel();
        request?.Dispose();
    }
}
