using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

namespace HelpDesk.NewWeb.Services;

/// <summary>Shareable list state. Only known filters are emitted; legacy AI flags cannot leak into normal requests.</summary>
public sealed class TicketListState
{
    public string Search { get; set; } = "";
    public string? ValidationError { get; set; }
    public List<string> CustomerOrgIds { get; set; } = [];
    public List<string> ForIds { get; set; } = [];
    public List<string> States { get; set; } = [];
    public List<string> AssigneeIds { get; set; } = [];
    public List<string> Priorities { get; set; } = [];
    public bool Unassigned { get; set; }
    public string View { get; set; } = "active";
    public string TimeField { get; set; } = "updated";
    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string Sort { get; set; } = "updated";
    public bool Descending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public int ActiveCount => CustomerOrgIds.Count + ForIds.Count + States.Count + AssigneeIds.Count + Priorities.Count + (Unassigned ? 1 : 0) + (From.HasValue ? 1 : 0) + (To.HasValue ? 1 : 0);

    public static TicketListState Parse(string uri, bool changes)
    {
        var q = QueryHelpers.ParseQuery(new Uri(uri).Query);
        string One(string key, string fallback = "") => q.TryGetValue(key, out var value) ? value.FirstOrDefault() ?? fallback : fallback;
        List<string> Many(string key) => q.TryGetValue(key, out var value) ? value.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).Distinct().Take(20).ToList() : [];
        var state = new TicketListState {
            Search = One("search", One("q")), CustomerOrgIds = Many("customerOrgId"), ForIds = Many("forId"), States = Many("stateValues"),
            AssigneeIds = Many("assigneeId"), Priorities = Many("priority"), Unassigned = One("unassigned") == "true",
            View = One("view", One("historicOnly") == "true" ? "historic" : "active"),
            TimeField = One("timeField", "updated"), TimeZone = One("tz", "UTC"), Sort = One("sort", "updated"), Descending = One("descending", "true") != "false",
            Page = int.TryParse(One("page"), out var page) ? Math.Clamp(page, 1, 1_000_000) : 1,
            PageSize = int.TryParse(One("pageSize"), out var size) ? Math.Clamp(size, 1, 100) : 25,
            From = DateTimeOffset.TryParse(One("from"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ? from : null,
            To = DateTimeOffset.TryParse(One("to"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) ? to : null
        };
        foreach (var key in new[] { "from", "to" })
        {
            var value = One(key);
            if (value.Length > 0 && (!(value.EndsWith('Z') || (value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-')) ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                state.ValidationError = "Time boundaries require an ISO timestamp with UTC offset.";
        }
        if (state.From >= state.To) state.ValidationError = "From must be earlier than To.";
        if (state.States.Count == 0 && One(changes ? "lifecycleState" : "state") is { Length: > 0 } legacy) state.States.Add(legacy);
        if (state.States.Count > 0) state.View = "all";
        return state;
    }

    public string Query(bool api = false)
    {
        var parts = new List<string>();
        void Add(string key, object? value) { if (value is not null) parts.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture)!)); }
        if (Search.Length > 0) Add(api ? "q" : "search", Search);
        foreach (var id in CustomerOrgIds) Add("customerOrgId", id);
        foreach (var id in ForIds) Add("forId", id);
        foreach (var id in States) Add("stateValues", id);
        foreach (var id in AssigneeIds) Add("assigneeId", id);
        foreach (var id in Priorities) Add("priority", id);
        if (Unassigned) Add("unassigned", "true");
        Add("view", States.Count > 0 ? "all" : View);
        Add("timeField", TimeField);
        if (From.HasValue) Add("from", From.Value.UtcDateTime.ToString("O"));
        if (To.HasValue) Add("to", To.Value.UtcDateTime.ToString("O"));
        if (!api) Add("tz", TimeZone);
        Add("sort", Sort); Add("descending", Descending ? "true" : "false");
        Add("page", Page); Add("pageSize", PageSize);
        return string.Join('&', parts);
    }

    public static DateTimeOffset? LocalBoundary(string? value, string zoneId, bool exclusiveEnd)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParseExact(value, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new FormatException("Use YYYY-MM-DD or YYYY-MM-DD HH:mm.");
        if (exclusiveEnd && value.Length == 10) local = local.AddDays(1);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) throw new FormatException("That local time does not exist because the clocks change. Choose another time.");
        if (zone.IsAmbiguousTime(local)) throw new FormatException("That local time occurs twice because the clocks change. Use UTC or choose another time.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
