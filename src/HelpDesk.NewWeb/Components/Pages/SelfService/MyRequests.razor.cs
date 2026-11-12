using System.Globalization;
using System.Net.Http.Json;
using HelpDesk.NewWeb.Components.Layout;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace HelpDesk.NewWeb.Components.Pages.SelfService;

public partial class MyRequests : IDisposable
{
    [Inject] private IHttpClientFactory HttpClientFactory { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [CascadingParameter] private SelfServiceNavigationState? SectionNavigation { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? QuerySearch { get; set; }
    [SupplyParameterFromQuery(Name = "state")] public int? QueryState { get; set; }
    [SupplyParameterFromQuery(Name = "release")] public int? QueryRelease { get; set; }
    [SupplyParameterFromQuery(Name = "createdFrom")] public DateTime? QueryCreatedFrom { get; set; }
    [SupplyParameterFromQuery(Name = "createdThrough")] public DateTime? QueryCreatedThrough { get; set; }
    [SupplyParameterFromQuery(Name = "updatedFrom")] public DateTime? QueryUpdatedFrom { get; set; }
    [SupplyParameterFromQuery(Name = "updatedThrough")] public DateTime? QueryUpdatedThrough { get; set; }
    [SupplyParameterFromQuery(Name = "sort")] public string? QuerySort { get; set; }
    [SupplyParameterFromQuery(Name = "descending")] public bool? QueryDescending { get; set; }
    [SupplyParameterFromQuery(Name = "page")] public int? QueryPage { get; set; }
    [SupplyParameterFromQuery(Name = "pageSize")] public int? QueryPageSize { get; set; }

    private MudTable<MyRequestListItemDto>? _table;
    private RequestQuery _query = new();
    private string? _search;
    private int? _state, _release;
    private DateTime? _createdFrom, _createdThrough, _updatedFrom, _updatedThrough;
    private int _currentPage, _pageSize = 25, _generation;
    private bool _loading, _hasLoaded;
    private string? _loadError;
    private CancellationTokenSource? _debounceCts, _loadCts;
    private TableData<MyRequestListItemDto> _lastData = new() { Items = [], TotalItems = 0 };
    private bool HasFilters => !string.IsNullOrWhiteSpace(_search) || _state.HasValue || _release.HasValue ||
        _createdFrom.HasValue || _createdThrough.HasValue || _updatedFrom.HasValue || _updatedThrough.HasValue;
    private int ActiveDateCount => new[] { _createdFrom, _createdThrough, _updatedFrom, _updatedThrough }.Count(x => x.HasValue);
    private string DateFilterLabel => ActiveDateCount > 0 ? $"Date filters ({ActiveDateCount} active)" : "Date filters";
    private string ResultsSummary => _loading ? (_hasLoaded ? "Refreshing requests…" : "Loading requests…")
        : _loadError is not null ? (_hasLoaded ? $"Showing the last loaded result ({_lastData.TotalItems} requests)." : "Requests unavailable")
        : $"{_lastData.TotalItems} matching requests";

    protected override void OnParametersSet()
    {
        var next = new RequestQuery(QuerySearch?.Trim(), ValidState(QueryState), ValidRelease(QueryRelease),
            QueryCreatedFrom?.Date, QueryCreatedThrough?.Date, QueryUpdatedFrom?.Date, QueryUpdatedThrough?.Date,
            ValidSort(QuerySort), QueryDescending ?? true, Math.Max(1, QueryPage ?? 1), QueryPageSize is 50 or 100 ? QueryPageSize.Value : 25);
        if (_query == next && _table is not null) return;
        _query = next;
        _search = next.Search;
        _state = next.State;
        _release = next.Release;
        _createdFrom = next.CreatedFrom;
        _createdThrough = next.CreatedThrough;
        _updatedFrom = next.UpdatedFrom;
        _updatedThrough = next.UpdatedThrough;
        _currentPage = next.Page - 1;
        _pageSize = next.PageSize;
        _generation++;
        _loadCts?.Cancel();
        if (_table is not null) _ = InvokeAsync(_table.ReloadServerData);
    }

    private async Task<TableData<MyRequestListItemDto>> LoadDataAsync(TableState state, CancellationToken token)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = _loadCts.Token;
        var generation = ++_generation;
        var query = _query;
        _loading = true;
        _loadError = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            var url = BuildApiUrl(query);
            var response = await HttpClientFactory.CreateClient("HelpdeskApi")
                .GetFromJsonAsync<PagedResponse<MyRequestListItemDto>>(url, ct)
                ?? throw new HttpRequestException("Missing request page.");
            ct.ThrowIfCancellationRequested();
            if (generation != _generation) throw new OperationCanceledException(ct);
            _lastData = new() { Items = response.Items, TotalItems = response.TotalCount };
            _hasLoaded = true;
            if (response.Page != query.Page)
            {
                _currentPage = Math.Max(0, response.Page - 1);
                _query = query with { Page = _currentPage + 1 };
                Persist(_query);
            }
            return _lastData;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || generation != _generation) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or
            OperationCanceledException or TimeoutRejectedException or BrokenCircuitException)
        {
            if (generation == _generation)
                _loadError = "Requests could not be loaded. Check your filters and retry. Your current filters have been kept.";
            return _lastData;
        }
        finally
        {
            if (generation == _generation)
            {
                _loading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private async Task SearchChangedAsync(string? value)
    {
        _search = value;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(300, token);
            token.ThrowIfCancellationRequested();
            FiltersChanged();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
    }

    private void FiltersChanged()
    {
        _debounceCts?.Cancel();
        Persist(_query with { Search = _search?.Trim(), State = _state, Release = _release,
            CreatedFrom = _createdFrom?.Date, CreatedThrough = _createdThrough?.Date,
            UpdatedFrom = _updatedFrom?.Date, UpdatedThrough = _updatedThrough?.Date, Page = 1 });
    }

    private void ClearFilters()
    {
        _search = null;
        _state = _release = null;
        _createdFrom = _createdThrough = _updatedFrom = _updatedThrough = null;
        FiltersChanged();
    }

    private void PageChanged(int page)
    {
        _currentPage = page;
        _query = _query with { Page = page + 1 };
        Persist(_query);
    }

    private void PageSizeChanged(int size)
    {
        _pageSize = size;
        _currentPage = 0;
        _query = _query with { PageSize = size, Page = 1 };
        Persist(_query);
    }

    private async Task ToggleSortAsync(string sort)
    {
        _currentPage = 0;
        _query = _query with { Sort = sort, Descending = _query.Sort == sort && !_query.Descending, Page = 1 };
        Persist(_query);
        await RefreshAsync();
    }

    private string AriaSortFor(string sort) => _query.Sort != sort ? "none" : _query.Descending ? "descending" : "ascending";
    private string SortIconFor(string sort) => _query.Sort != sort ? Icons.Material.Filled.UnfoldMore
        : _query.Descending ? Icons.Material.Filled.ArrowDownward : Icons.Material.Filled.ArrowUpward;
    private Task RefreshAsync() => _table?.ReloadServerData() ?? Task.CompletedTask;

    private void Persist(RequestQuery query) => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
    {
        ["q"] = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
        ["state"] = query.State, ["release"] = query.Release,
        ["createdFrom"] = query.CreatedFrom, ["createdThrough"] = query.CreatedThrough,
        ["updatedFrom"] = query.UpdatedFrom, ["updatedThrough"] = query.UpdatedThrough,
        ["sort"] = query.Sort == "created" ? null : query.Sort,
        ["descending"] = query.Descending ? null : false,
        ["page"] = query.Page == 1 ? null : query.Page,
        ["pageSize"] = query.PageSize == 25 ? null : query.PageSize
    }), replace: true);

    private static string BuildApiUrl(RequestQuery query)
    {
        var args = new Dictionary<string, string?>
        {
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture),
            ["q"] = query.Search, ["state"] = query.State?.ToString(CultureInfo.InvariantCulture),
            ["releaseStage"] = query.Release?.ToString(CultureInfo.InvariantCulture),
            ["createdFromUtc"] = query.CreatedFrom?.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture),
            ["createdBeforeUtc"] = ExclusiveDate(query.CreatedThrough),
            ["updatedFromUtc"] = query.UpdatedFrom?.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture),
            ["updatedBeforeUtc"] = ExclusiveDate(query.UpdatedThrough),
            ["sortBy"] = query.Sort, ["descending"] = query.Descending.ToString().ToLowerInvariant()
        };
        return "api/v1/self-service/requests?" + string.Join("&", args.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value!)}"));
    }

    private static string? ExclusiveDate(DateTime? date) => date is null ? null
        : (date.Value.Date < DateTime.MaxValue.Date ? date.Value.Date.AddDays(1) : DateTime.MaxValue)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static int? ValidState(int? value) => value.HasValue && Enum.IsDefined((TicketState)value.Value) ? value : null;
    private static int? ValidRelease(int? value) => value.HasValue && Enum.IsDefined((RequestFormReleaseStatus)value.Value) ? value : null;
    private static string ValidSort(string? value) => value?.ToLowerInvariant() switch
    {
        "trackingid" => "trackingId", "title" => "title", "state" => "state", "updated" => "updated", _ => "created"
    };
    private static string StatusText(TicketState state) => state switch
    {
        TicketState.WaitingReply => "Waiting for reply", TicketState.Replied => "Customer replied",
        TicketState.InProgress => "In progress", TicketState.PendingApproval => "Pending approval",
        TicketState.OnHold => "On hold", _ => state.ToString()
    };

    public void Dispose()
    {
        _generation++;
        _debounceCts?.Cancel(); _debounceCts?.Dispose();
        _loadCts?.Cancel(); _loadCts?.Dispose();
    }

    private sealed record RequestQuery(string? Search = null, int? State = null, int? Release = null,
        DateTime? CreatedFrom = null, DateTime? CreatedThrough = null, DateTime? UpdatedFrom = null, DateTime? UpdatedThrough = null,
        string Sort = "created", bool Descending = true, int Page = 1, int PageSize = 25);
}
