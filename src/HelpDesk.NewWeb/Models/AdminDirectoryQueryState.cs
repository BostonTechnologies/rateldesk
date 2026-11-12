using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using MudBlazor;

namespace HelpDesk.NewWeb.Models;

/// <summary>URL-owned table state; browser history can restore the complete query.</summary>
public sealed class AdminDirectoryQueryState(params string[] filterNames)
{
    private string? appliedUri;
    private readonly Dictionary<string, string?> filters = new(StringComparer.OrdinalIgnoreCase);
    public string? Search { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 25;
    public string Sort { get; set; } = "name";
    public bool Descending { get; set; }
    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || filters.Values.Any(value => !string.IsNullOrEmpty(value));
    public string? Get(string name) => filters.GetValueOrDefault(name);
    public void Set(string name, string? value) { filters[name] = string.IsNullOrWhiteSpace(value) ? null : value; Page = 0; }
    public void Clear() { Search = null; filters.Clear(); Page = 0; }

    public bool Read(string uri)
    {
        if (uri == appliedUri) return false;
        appliedUri = uri;
        var values = QueryHelpers.ParseQuery(new Uri(uri).Query);
        Search = values.GetValueOrDefault("search").FirstOrDefault();
        Page = int.TryParse(values.GetValueOrDefault("page").FirstOrDefault(), out var page) ? Math.Max(0, page) : 0;
        PageSize = int.TryParse(values.GetValueOrDefault("pageSize").FirstOrDefault(), out var size) && size is 25 or 50 or 100 ? size : 25;
        Sort = values.GetValueOrDefault("sort").FirstOrDefault() ?? "name";
        Descending = bool.TryParse(values.GetValueOrDefault("descending").FirstOrDefault(), out var descending) && descending;
        foreach (var name in filterNames) filters[name] = values.GetValueOrDefault(name).FirstOrDefault();
        return true;
    }

    public string ApiUrl(string route, TableState state)
    {
        Page = state.Page;
        PageSize = state.PageSize is 25 or 50 or 100 ? state.PageSize : 25;
        Sort = state.SortLabel ?? Sort;
        if (state.SortDirection != SortDirection.None) Descending = state.SortDirection == SortDirection.Descending;
        return QueryHelpers.AddQueryString(route, Values(includeDefaults: true));
    }

    public void Remember(NavigationManager navigation)
    {
        appliedUri = navigation.GetUriWithQueryParameters(Values(includeDefaults: false).ToDictionary(row => row.Key, row => (object?)row.Value));
        if (appliedUri != navigation.Uri) navigation.NavigateTo(appliedUri, replace: true);
    }

    public SortDirection Direction(string column) => Sort != column ? SortDirection.None : Descending ? SortDirection.Descending : SortDirection.Ascending;
    private Dictionary<string, string?> Values(bool includeDefaults)
    {
        var values = new Dictionary<string, string?>
        {
            ["search"] = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
            ["page"] = includeDefaults || Page != 0 ? Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            ["pageSize"] = includeDefaults || PageSize != 25 ? PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            ["sort"] = includeDefaults || Sort != "name" ? Sort : null,
            ["descending"] = includeDefaults || Descending ? Descending.ToString().ToLowerInvariant() : null
        };
        foreach (var name in filterNames) values[name] = Get(name);
        return values;
    }
}
