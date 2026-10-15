using System.Globalization;
using System.Linq.Expressions;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace Helpdesk.API.Endpoints.Ticketing;

/// <summary>Validated additive list contract. Predicates remain SQL expressions on both providers.</summary>
internal sealed class TicketListQuery
{
    public string[] CustomerOrgIds { get; private set; } = [];
    public string[] ForIds { get; private set; } = [];
    public string[] AssigneeIds { get; private set; } = [];
    public int[] States { get; private set; } = [];
    public TicketPriority[] Priorities { get; private set; } = [];
    public bool Unassigned { get; private set; }
    public string TimeField { get; private set; } = "updated";
    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }
    public string Sort { get; private set; } = "updated";
    public bool Descending { get; private set; } = true;
    public bool ExplicitStates { get; private set; }

    public static bool TryParse(IQueryCollection query, bool changes, out TicketListQuery result, out string? error)
    {
        result = new(); error = null;
        try
        {
            result.CustomerOrgIds = Values(query["customerOrgId"]);
            result.ForIds = Values(query["forId"]);
            result.AssigneeIds = Values(query["assigneeId"]);
            result.States = Values(query["stateValues"]).Select(value => changes ? (int)ParseEnum<ChangeLifecycleState>(value) : (int)ParseEnum<TicketState>(value)).ToArray();
            result.Priorities = Values(query["priority"]).Select(ParseEnum<TicketPriority>).ToArray();
            result.Unassigned = Boolean(query["unassigned"], false);
            result.TimeField = query["timeField"].FirstOrDefault() ?? "updated";
            result.Sort = query["sort"].FirstOrDefault() ?? "updated";
            result.Descending = Boolean(query["descending"], true);
            result.From = Boundary(query["from"]);
            result.To = Boundary(query["to"]);
            if (result.From >= result.To) throw new FormatException("From must be earlier than To.");
            if (result.TimeField is not ("updated" or "created")) throw new FormatException("Unknown time field.");
            if (result.Sort is not ("updated" or "created" or "trackingId" or "subject" or "priority" or "state")) throw new FormatException("Unknown sort field.");
            var view = query["view"].FirstOrDefault();
            if (view is not (null or "" or "all" or "active" or "historic")) throw new FormatException("Unknown ticket view.");
            result.ExplicitStates = result.States.Length > 0 || view == "all";
            if (query["q"].ToString().Length > 200) throw new FormatException("Search is limited to 200 characters.");
            return true;
        }
        catch (FormatException ex) { error = ex.Message; return false; }
    }

    private static string[] Values(StringValues values)
    {
        if (values.Count > 20 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200))
            throw new FormatException("Each filter accepts at most 20 nonempty values of 200 characters.");
        return values.Select(value => value!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
    }
    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new FormatException("Unknown state or priority.");
    private static bool Boolean(StringValues value, bool fallback) => StringValues.IsNullOrEmpty(value) ? fallback :
        bool.TryParse(value.ToString(), out var parsed) ? parsed : throw new FormatException("Invalid boolean filter.");
    private static DateTime? Boundary(StringValues value)
    {
        if (StringValues.IsNullOrEmpty(value)) return null;
        var text = value.ToString();
        // Require an offset; server timezone must never change a shared query's meaning.
        if (!(text.EndsWith('Z') || (text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-')) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new FormatException("Time boundaries require an ISO timestamp with UTC offset.");
        return parsed.UtcDateTime;
    }

    public IQueryable<T> Apply<T>(IQueryable<T> query, bool changes) where T : Ticket
    {
        if (CustomerOrgIds.Length > 0) query = query.Where(ticket => CustomerOrgIds.Contains(ticket.OrganizationId!));
        if (ForIds.Length > 0) query = changes
            ? query.Where(ticket => ForIds.Contains(EF.Property<string>(ticket, nameof(Change.RequestedForUserId))))
            : query.Where(ticket => ForIds.Contains(ticket.CustomerId!));
        if (AssigneeIds.Length > 0 || Unassigned) query = changes
            ? query.Where(ticket => AssigneeIds.Contains(EF.Property<string>(ticket, nameof(Change.ImplementorUserId))) || (Unassigned && EF.Property<string?>(ticket, nameof(Change.ImplementorUserId)) == null))
            : query.Where(ticket => AssigneeIds.Contains(ticket.AssignedToId!) || (Unassigned && ticket.AssignedToId == null));
        if (States.Length > 0) query = changes
            ? query.Where(ticket => States.Contains((int)(EF.Property<ChangeLifecycleState?>(ticket, nameof(Change.LifecycleState)) ?? ChangeLifecycleState.Draft)))
            : query.Where(ticket => States.Contains((int)ticket.State));
        if (Priorities.Length > 0) query = query.Where(ticket => Priorities.Contains(ticket.Priority));
        if (From is { } from) query = TimeField == "created" ? query.Where(ticket => ticket.CreatedAt >= from) : query.Where(ticket => (ticket.UpdatedAt ?? ticket.CreatedAt) >= from);
        if (To is { } to) query = TimeField == "created" ? query.Where(ticket => ticket.CreatedAt < to) : query.Where(ticket => (ticket.UpdatedAt ?? ticket.CreatedAt) < to);
        return query;
    }

    public IOrderedQueryable<T> Order<T>(IQueryable<T> query, string ticketProperty)
    {
        var item = Expression.Parameter(typeof(T), "row");
        var ticket = Expression.Property(item, ticketProperty);
        Expression key = Sort switch
        {
            "created" => Expression.Property(ticket, nameof(Ticket.CreatedAt)),
            "trackingId" => Expression.Property(ticket, nameof(Ticket.TrackingId)),
            "subject" => Expression.Property(ticket, nameof(Ticket.Title)),
            "priority" => Expression.Property(ticket, nameof(Ticket.Priority)),
            "state" => Expression.Property(ticket, ticket.Type == typeof(Change) ? nameof(Change.LifecycleState) : nameof(Ticket.State)),
            _ => Expression.Coalesce(Expression.Property(ticket, nameof(Ticket.UpdatedAt)), Expression.Property(ticket, nameof(Ticket.CreatedAt)))
        };
        var ordered = Expression.Call(typeof(Queryable), Descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy), [typeof(T), key.Type], query.Expression, Expression.Quote(Expression.Lambda(key, item)));
        // Stable tie breaker prevents rows moving between pages when timestamps are equal.
        var id = Expression.Property(ticket, nameof(Ticket.Id));
        return (IOrderedQueryable<T>)query.Provider.CreateQuery<T>(Expression.Call(typeof(Queryable), nameof(Queryable.ThenBy), [typeof(T), typeof(string)], ordered, Expression.Quote(Expression.Lambda(id, item))));
    }

    public static IQueryable<T> Authorized<T>(IQueryable<T> query, CurrentUserAccessProfile access, string module) where T : Ticket
    {
        if (access.IsHelpdeskAdmin) return query;
        var prefix = char.ToUpperInvariant(module[0]) + module[1..];
        var read = access.OrganizationIdsFor(prefix + ".Read").Concat(access.OrganizationIdsFor(prefix + ".Write")).Concat(access.OrganizationIdsFor(prefix + ".Manager")).Distinct().ToArray();
        var own = access.OrganizationIdsFor(prefix + ".User").ToArray();
        var hasCustomer = !string.IsNullOrWhiteSpace(access.CustomerId);
        return query.Where(ticket => read.Contains(ticket.OrganizationId!) || (own.Contains(ticket.OrganizationId!) && hasCustomer && ticket.CustomerId == access.CustomerId));
    }
}
