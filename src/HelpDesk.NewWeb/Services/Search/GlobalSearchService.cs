using System.Diagnostics;
using Helpdesk.Shared.DTOs.AiOps;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using System.Runtime.CompilerServices;

namespace HelpDesk.NewWeb.Services.Search;

public sealed class GlobalSearchService : IGlobalSearchService
{
    private const int ResultLimit = 5;
    private const int FetchLimit = ResultLimit + 1;
    private static readonly TimeSpan DefaultSourceTimeout = TimeSpan.FromMilliseconds(1200);

    private static readonly SectionDefinition[] Sections =
    [
        new("self-service", "Self service requests", Icons.Material.Filled.Storefront, "/self-service", SearchAudience.Authenticated),
        new("incidents", "Incidents", Icons.Material.Filled.ListAlt, "/incidents", SearchAudience.Workspace),
        new("requests", "Requests", Icons.Material.Filled.List, "/requests", SearchAudience.Workspace),
        new("changes", "Changes", Icons.Material.Filled.ChangeCircle, "/changes", SearchAudience.Workspace),
        new("tasks", "Tasks", Icons.Material.Filled.TaskAlt, "/tasks", SearchAudience.Workspace),
        new("investigations", "AI investigations", Icons.Material.Filled.Psychology, "/ai/ops", SearchAudience.Workspace),
        new("team", "Team", Icons.Material.Filled.Group, "/admin/users", SearchAudience.Admin),
        new("customers", "Customers", Icons.Material.Filled.People, "/admin/customers", SearchAudience.Admin),
        new("organizations", "Organizations", Icons.Material.Filled.Apartment, "/admin/organizations", SearchAudience.Admin)
    ];

    private readonly IHttpClientFactory _clientFactory;
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly ILogger<GlobalSearchService> _logger;
    private readonly TimeSpan _sourceTimeout;

    public GlobalSearchService(
        IHttpClientFactory clientFactory,
        AuthenticationStateProvider authenticationStateProvider,
        ILogger<GlobalSearchService> logger)
        : this(clientFactory, authenticationStateProvider, logger, DefaultSourceTimeout)
    {
    }

    public GlobalSearchService(
        IHttpClientFactory clientFactory,
        AuthenticationStateProvider authenticationStateProvider,
        ILogger<GlobalSearchService> logger,
        TimeSpan sourceTimeout)
    {
        _clientFactory = clientFactory;
        _authenticationStateProvider = authenticationStateProvider;
        _logger = logger;
        _sourceTimeout = sourceTimeout;
    }

    public async Task<IReadOnlyList<GlobalSearchGroup>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        var user = (await _authenticationStateProvider.GetAuthenticationStateAsync()).User;
        var term = query?.Trim();
        var sections = GetOrderedSections(user, term).ToList();

        if (string.IsNullOrWhiteSpace(term))
        {
            return sections.Select(s => new GlobalSearchGroup(s.Key, s.Title, s.Icon, s.Href, [])).ToList();
        }

        var lookup = new Dictionary<string, GlobalSearchGroup>(StringComparer.OrdinalIgnoreCase);
        await foreach (var group in SearchIncrementalAsync(term, cancellationToken).ConfigureAwait(false))
        {
            lookup[group.Key] = group;
        }

        return sections.Select(section => lookup.TryGetValue(section.Key, out var group) ? group : EmptyGroup(section)).ToList();
    }

    public async IAsyncEnumerable<GlobalSearchGroup> SearchIncrementalAsync(
        string? query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var user = (await _authenticationStateProvider.GetAuthenticationStateAsync()).User;
        var term = query?.Trim();
        var sections = GetOrderedSections(user, term).ToList();

        if (string.IsNullOrWhiteSpace(term))
        {
            foreach (var section in sections)
            {
                yield return EmptyGroup(section);
            }

            yield break;
        }

        var overall = Stopwatch.StartNew();
        var queryHash = QueryHash(term);
        _logger.LogInformation(
            "Global search started. QueryLength={QueryLength} QueryHash={QueryHash} SectionCount={SectionCount} TimeoutMs={TimeoutMs}",
            term.Length,
            queryHash,
            sections.Count,
            _sourceTimeout.TotalMilliseconds);

        foreach (var section in sections)
        {
            yield return LoadingGroup(section);
        }

        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var api = _clientFactory.CreateClient("HelpdeskApi");
        var tasks = sections
            .Select(section => SearchSectionWithTimeoutAsync(api, section, term, streamCancellation.Token))
            .ToList();
        var resultCount = 0;

        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);
                var group = await completed.ConfigureAwait(false);
                resultCount += group.Results.Count;
                yield return group;
            }
        }
        finally
        {
            await streamCancellation.CancelAsync().ConfigureAwait(false);
        }

        overall.Stop();
        _logger.LogInformation(
            "Global search completed. QueryLength={QueryLength} QueryHash={QueryHash} SectionCount={SectionCount} ResultCount={ResultCount} ElapsedMs={ElapsedMs}",
            term.Length,
            queryHash,
            sections.Count,
            resultCount,
            overall.ElapsedMilliseconds);
    }

    private async Task<GlobalSearchGroup> SearchSectionWithTimeoutAsync(
        HttpClient api,
        SectionDefinition section,
        string term,
        CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(_sourceTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        try
        {
            _logger.LogInformation(
                "Global search source started. Section={Section} QueryLength={QueryLength} QueryHash={QueryHash} TimeoutMs={TimeoutMs}",
                section.Key,
                term.Length,
                QueryHash(term),
                _sourceTimeout.TotalMilliseconds);

            return await SearchSectionAsync(api, section, term, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Global search source timed out. Section={Section} QueryLength={QueryLength} QueryHash={QueryHash} TimeoutMs={TimeoutMs} TimedOut={TimedOut} ElapsedMs={ElapsedMs}",
                section.Key,
                term.Length,
                QueryHash(term),
                _sourceTimeout.TotalMilliseconds,
                true,
                elapsed.ElapsedMilliseconds);

            return TimeoutGroup(section, term);
        }
    }

    private async Task<GlobalSearchGroup> SearchSectionAsync(HttpClient api, SectionDefinition section, string term, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var group = section.Key switch
            {
                "investigations" => await SearchInvestigationsAsync(api, section, term, token),
                "self-service" => await SearchPagedAsync<ServiceItemDto>(
                    api,
                    section,
                    $"api/v1/service-items/search?q={Escape(term)}&pageSize={FetchLimit}&includeTotal=false",
                    term,
                    ToSelfServiceResult,
                    token),
                "incidents" => await SearchPagedAsync<IncidentDto>(
                    api,
                    section,
                    $"api/v1/incidents?page=1&pageSize={FetchLimit}&activeOnly=true&includeTotal=false&summaryOnly=true&q={Escape(term)}",
                    term,
                    ToIncidentResult,
                    token),
                "requests" => await SearchPagedAsync<RequestDto>(
                    api,
                    section,
                    $"api/v1/requests?page=1&pageSize={FetchLimit}&activeOnly=true&includeTotal=false&summaryOnly=true&q={Escape(term)}",
                    term,
                    ToRequestResult,
                    token),
                "changes" => await SearchPagedAsync<ChangeDto>(
                    api,
                    section,
                    $"api/v1/changes?page=1&pageSize={FetchLimit}&activeOnly=true&includeTotal=false&summaryOnly=true&q={Escape(term)}",
                    term,
                    ToChangeResult,
                    token),
                "tasks" => await SearchPagedAsync<RequestTaskListItemDto>(
                    api,
                    section,
                    $"api/v1/request-tasks?page=1&pageSize={FetchLimit}&assignedToMe=false&includeTotal=false&q={Escape(term)}",
                    term,
                    ToTaskResult,
                    token),
                "team" => await SearchPagedAsync<UserDto>(
                    api,
                    section,
                    $"api/v1/global-search/users?q={Escape(term)}&pageSize={FetchLimit}",
                    term,
                    ToUserResult,
                    token),
                "customers" => await SearchPagedAsync<CustomerDto>(
                    api,
                    section,
                    $"api/v1/global-search/customers?q={Escape(term)}&pageSize={FetchLimit}",
                    term,
                    ToCustomerResult,
                    token),
                "organizations" => await SearchPagedAsync<OrganizationDto>(
                    api,
                    section,
                    $"api/v1/global-search/organizations?q={Escape(term)}&pageSize={FetchLimit}",
                    term,
                    ToOrganizationResult,
                    token),
                _ => EmptyGroup(section)
            };

            LogSectionTiming(section, term, group, elapsed.Elapsed);
            return group;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Global search source {Section} failed.", section.Key);
            return EmptyGroup(section);
        }
    }

    private static async Task<GlobalSearchGroup> SearchInvestigationsAsync(HttpClient api, SectionDefinition section, string term, CancellationToken ct)
    {
        var rows = await api.GetFromJsonAsync<List<AiInvestigationDto>>($"api/v1/ai/ops/investigations?q={Escape(term)}", ct) ?? [];
        return new(section.Key, section.Title, section.Icon, section.Href,
            rows.Take(ResultLimit).Select(x => new GlobalSearchResult(section.Title, x.Title, "Saved investigation", null, $"/ai/ops?id={x.Id}", section.Icon)).ToList(),
            rows.Count > ResultLimit ? $"/ai/ops?q={Escape(term)}" : null, rows.Count);
    }

    private static async Task<GlobalSearchGroup> SearchPagedAsync<T>(
        HttpClient api,
        SectionDefinition section,
        string url,
        string term,
        Func<T, GlobalSearchResult> map,
        CancellationToken token)
    {
        var response = await api.GetFromJsonAsync<PagedResponse<T>>(url, token) ?? new();
        var results = response.Items.Take(ResultLimit).Select(map).ToList();
        var total = Math.Max(response.TotalCount, response.Items.Count);
        var continueHref = total > ResultLimit ? BuildContinueHref(section, term) : null;
        return new GlobalSearchGroup(section.Key, section.Title, section.Icon, section.Href, results, continueHref, total);
    }

    private static IEnumerable<SectionDefinition> GetAllowedSections(ClaimsPrincipal user)
    {
        var isAdmin = HasRole(user, "HelpdeskAdmin");

        foreach (var section in Sections)
        {
            if (section.Audience == SearchAudience.Admin && !isAdmin)
            {
                continue;
            }

            if (section.Audience == SearchAudience.Workspace && !CanSearchSection(user, section.Key))
            {
                continue;
            }

            yield return section;
        }
    }

    private static IEnumerable<SectionDefinition> GetOrderedSections(ClaimsPrincipal user, string? term)
    {
        var isAdmin = HasRole(user, "HelpdeskAdmin");
        var hasWorkspaceAccess = HasAnyRole(user,
            "Incident.User", "Incident.Read", "Incident.Write",
            "Incident.Manager",
            "Request.User", "Request.Read", "Request.Write",
            "Request.Manager",
            "Change.User", "Change.Read", "Change.Write",
            "Change.Manager",
            "Technician");
        return GetAllowedSections(user)
            .Select((section, index) => new { Section = section, Rank = SectionRank(section, term, isAdmin || hasWorkspaceAccess), Index = index })
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .Select(x => x.Section);
    }

    private static int SectionRank(SectionDefinition section, string? term, bool workspaceUser)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return 0;
        }

        var normalized = term.Trim();
        if (StartsWithAny(normalized, "INC-"))
        {
            return section.Key == "incidents" ? 0 : section.Audience == SearchAudience.Workspace ? 1 : 2;
        }

        if (StartsWithAny(normalized, "REQ-", "REQUEST-"))
        {
            return section.Key == "requests" ? 0 : section.Audience == SearchAudience.Workspace ? 1 : 2;
        }

        if (StartsWithAny(normalized, "CHG-", "CHANGE-"))
        {
            return section.Key == "changes" ? 0 : section.Audience == SearchAudience.Workspace ? 1 : 2;
        }

        if (LooksLikePersonOrOrganization(normalized))
        {
            return section.Audience == SearchAudience.Admin ? 0 : section.Key == "self-service" ? 1 : 2;
        }

        if (workspaceUser)
        {
            return section.Audience == SearchAudience.Workspace ? 0 : section.Key == "self-service" ? 1 : 2;
        }

        return section.Key == "self-service" ? 0 : 1;
    }

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikePersonOrOrganization(string value) =>
        value.Contains('@', StringComparison.Ordinal) ||
        value.Contains('.', StringComparison.Ordinal) ||
        value.Contains(' ', StringComparison.Ordinal);

    private static bool HasRole(ClaimsPrincipal user, string role) =>
        user.IsInRole(role) ||
        user.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "roles") &&
            string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));

    private static bool HasAnyRole(ClaimsPrincipal user, params string[] roles) =>
        roles.Any(role => HasRole(user, role));

    private static bool CanSearchSection(ClaimsPrincipal user, string sectionKey) =>
        HasRole(user, "HelpdeskAdmin") ||
        sectionKey switch
        {
            "incidents" => HasAnyRole(user, "Incident.User", "Incident.Read", "Incident.Write", "Incident.Manager", "Technician"),
            "requests" => HasAnyRole(user, "Request.User", "Request.Read", "Request.Write", "Request.Manager", "Technician"),
            "changes" => HasAnyRole(user, "Change.User", "Change.Read", "Change.Write", "Change.Manager", "Technician"),
            "investigations" => HasAnyRole(user, "Incident.Write", "Incident.Manager", "Request.Write", "Request.Manager", "Change.Write", "Change.Manager", "Technician"),
            "tasks" => HasAnyRole(user, "Request.Read", "Request.Write", "Request.Manager", "Technician"),
            _ => false
        };

    private static GlobalSearchResult ToSelfServiceResult(ServiceItemDto item) => new(
        "Self service requests",
        item.Name,
        item.ItemType == ServiceItemType.RequestForm ? "Request form" : "Service",
        item.Description,
        item.ItemType == ServiceItemType.RequestForm ? $"/self-service/request/{item.Id}" : $"/self-service/{item.Id}",
        item.ItemType == ServiceItemType.RequestForm ? Icons.Material.Filled.DynamicForm : Icons.Material.Filled.Storefront);

    private static GlobalSearchResult ToIncidentResult(IncidentDto incident) => new(
        "Incidents",
        FirstNonEmpty(incident.TrackingId, incident.Subject) ?? "Incident",
        incident.Subject,
        BuildTicketMeta(incident.State.ToString(), incident.CustomerOrgName, FirstNonEmpty(incident.CustomerName, incident.CustomerEmail, incident.RequesterEmail)),
        $"/incidents/{incident.Id}",
        Icons.Material.Filled.ListAlt);

    private static GlobalSearchResult ToRequestResult(RequestDto request) => new(
        "Requests",
        FirstNonEmpty(request.TrackingId, request.Title) ?? "Request",
        request.Title,
        BuildTicketMeta(request.State.ToString(), request.CustomerOrgName, FirstNonEmpty(request.CustomerName, request.CustomerEmail)),
        $"/requests/{request.Id}",
        Icons.Material.Filled.List);

    private static GlobalSearchResult ToChangeResult(ChangeDto change) => new(
        "Changes",
        FirstNonEmpty(change.TrackingId, change.Title) ?? "Change",
        change.Title,
        BuildTicketMeta(FormatChangeLifecycle(change.LifecycleState), FirstNonEmpty(change.ChangeType, change.CustomerOrgName), FirstNonEmpty(change.CustomerName, change.CustomerEmail)),
        $"/changes/{change.Id}",
        Icons.Material.Filled.ChangeCircle);

    private static GlobalSearchResult ToTaskResult(RequestTaskListItemDto task) => new(
        "Tasks",
        task.Name,
        FirstNonEmpty(task.RequestTrackingId, task.RequestTitle),
        $"{task.Status} · {task.Type}",
        $"/requests/{task.RequestId}",
        Icons.Material.Filled.TaskAlt);

    private static GlobalSearchResult ToUserResult(UserDto user) => new(
        "Team",
        user.Name,
        user.Email,
        user.Role,
        $"/admin/users?search={Escape(FirstNonEmpty(user.Name, user.Email) ?? user.Id)}",
        Icons.Material.Filled.Group);

    private static GlobalSearchResult ToCustomerResult(CustomerDto customer) => new(
        "Customers",
        customer.Name,
        customer.Email,
        customer.OrganizationName,
        $"/admin/customers?search={Escape(FirstNonEmpty(customer.Name, customer.Email) ?? customer.Id)}",
        Icons.Material.Filled.People);

    private static GlobalSearchResult ToOrganizationResult(OrganizationDto organization) => new(
        "Organizations",
        organization.Name,
        organization.DnsName,
        organization.ContactInfo,
        $"/admin/organizations?search={Escape(organization.Name)}",
        Icons.Material.Filled.Apartment);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string BuildTicketMeta(params string?[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string FormatChangeLifecycle(ChangeLifecycleState state) => state switch
    {
        ChangeLifecycleState.Draft => "Draft",
        ChangeLifecycleState.Submitted => "Submitted",
        ChangeLifecycleState.PendingApproval => "Pending Approval",
        ChangeLifecycleState.ApprovedForImplementation => "Approved For Implementation",
        ChangeLifecycleState.ImplementationInProgress => "Implementation In Progress",
        ChangeLifecycleState.ImplementedSuccess => "Implemented - Success",
        ChangeLifecycleState.ImplementedBackedOut => "Implemented - Backed Out",
        _ => state.ToString()
    };

    private void LogSectionTiming(SectionDefinition section, string term, GlobalSearchGroup group, TimeSpan elapsed)
    {
        const int slowThresholdMs = 1000;
        var level = elapsed.TotalMilliseconds >= slowThresholdMs ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(
            level,
            "Global search source completed. Section={Section} QueryLength={QueryLength} QueryHash={QueryHash} ResultCount={ResultCount} Continue={Continue} TimedOut={TimedOut} ElapsedMs={ElapsedMs}",
            section.Key,
            term.Length,
            QueryHash(term),
            group.Results.Count,
            group.ContinueHref is not null,
            false,
            elapsed.TotalMilliseconds);
    }

    private static string QueryHash(string term)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(term.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 6);
    }

    private static string BuildContinueHref(SectionDefinition section, string term) =>
        section.Key switch
        {
            "incidents" => $"/incidents?search={Escape(term)}&activeOnly=true",
            "requests" => $"/requests?search={Escape(term)}&activeOnly=true",
            "changes" => $"/changes?search={Escape(term)}&activeOnly=true",
            "tasks" => $"/tasks?search={Escape(term)}",
            "self-service" => $"/self-service?search={Escape(term)}",
            _ => $"{section.Href}?search={Escape(term)}"
        };

    private static GlobalSearchGroup EmptyGroup(SectionDefinition section) =>
        new(section.Key, section.Title, section.Icon, section.Href, []);

    private static GlobalSearchGroup LoadingGroup(SectionDefinition section) =>
        new(section.Key, section.Title, section.Icon, section.Href, [], IsLoading: true);

    private static GlobalSearchGroup TimeoutGroup(SectionDefinition section, string term) =>
        new(section.Key, section.Title, section.Icon, section.Href, [], BuildContinueHref(section, term), TimedOut: true);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed record SectionDefinition(string Key, string Title, string Icon, string Href, SearchAudience Audience);

    private enum SearchAudience
    {
        Authenticated,
        Workspace,
        Admin
    }
}
