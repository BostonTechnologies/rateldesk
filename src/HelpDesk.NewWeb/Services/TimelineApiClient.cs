using System.Net.Http.Json;
using System.Security.Claims;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.DTOs.Email;
using Helpdesk.Shared.Models;

namespace HelpDesk.NewWeb.Services;

public interface ITimelineApiClient
{
    Task<List<TicketTimelineEventDto>> GetTimelineAsync(string ticketId, string order = "desc", string ticketType = "incidents");
    Task RetryAsync(Guid id);
    Task RetryAllAsync();
    Task<MailboxOutgoingRetryPreview?> PreviewOutgoingRetryAsync(Guid id);
    Task RetryWithCurrentOutgoingAsync(Guid id, ConfirmMailboxOutgoingRetryRequest confirmation);
    Task<MailboxUncertainRetryPreview?> PreviewUncertainRetryAsync(Guid id);
    Task RetryConfirmedUndeliveredAsync(Guid id, ConfirmMailboxUndeliveredRetryRequest request);
    Task<MailboxRouteRetryPreview?> PreviewChangedRouteAsync(Guid id);
    Task RetryWithCurrentRouteAsync(Guid id, ConfirmMailboxRouteRetryRequest request);
    Task<int> GetPendingCountAsync();
    Task<List<TicketTimelineEventDto>> GetFailedAsync();
    Task<IReadOnlyList<PendingEmailOrganization>> SearchPendingOrganizationsAsync(string? search, string? id = null, CancellationToken ct = default);
    Task<PendingEmailPage> SearchPendingAsync(PendingEmailQuery query, CancellationToken ct = default);
    Task<PendingEmailActionResponse> ApplyPendingActionAsync(PendingEmailActionRequest request, CancellationToken ct = default);
}

public class TimelineApiClient(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor) : ITimelineApiClient
{
    private readonly HttpClient _client = httpClientFactory.CreateClient("HelpdeskApi");
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public async Task<List<TicketTimelineEventDto>> GetTimelineAsync(string ticketId, string order = "desc", string ticketType = "incidents")
    {
        var safeTicketType = string.IsNullOrWhiteSpace(ticketType) ? "incidents" : ticketType.Trim().ToLowerInvariant();
        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/{safeTicketType}/{ticketId}/timeline?order={order}");
        using var response = await _client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return new();

        return await response.Content.ReadFromJsonAsync<List<TicketTimelineEventDto>>() ?? new();
    }

    public async Task RetryAsync(Guid id)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Post, $"/api/v1/timeline/{id}/retry");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task RetryAllAsync()
    {
        using var request = CreateUserScopedRequest(HttpMethod.Post, "/api/v1/timeline/retry-all");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<MailboxOutgoingRetryPreview?> PreviewOutgoingRetryAsync(Guid id)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/timeline/{id}/outgoing-retry-preview");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MailboxOutgoingRetryPreview>();
    }

    public async Task RetryWithCurrentOutgoingAsync(Guid id, ConfirmMailboxOutgoingRetryRequest confirmation)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Post, $"/api/v1/timeline/{id}/retry-current-outgoing");
        request.Content = JsonContent.Create(confirmation);
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<MailboxUncertainRetryPreview?> PreviewUncertainRetryAsync(Guid id)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/timeline/{id}/uncertain-retry-preview");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MailboxUncertainRetryPreview>();
    }

    public async Task RetryConfirmedUndeliveredAsync(Guid id, ConfirmMailboxUndeliveredRetryRequest confirmation)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Post, $"/api/v1/timeline/{id}/retry-confirmed-undelivered");
        request.Content = JsonContent.Create(confirmation);
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<MailboxRouteRetryPreview?> PreviewChangedRouteAsync(Guid id)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/timeline/{id}/changed-route-preview");
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MailboxRouteRetryPreview>();
    }

    public async Task RetryWithCurrentRouteAsync(Guid id, ConfirmMailboxRouteRetryRequest confirmation)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Post, $"/api/v1/timeline/{id}/retry-current-route");
        request.Content = JsonContent.Create(confirmation);
        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> GetPendingCountAsync()
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, "/api/v1/timeline/pending-count");
        using var response = await _client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return 0;

        var value = await response.Content.ReadFromJsonAsync<int>();
        return value;
    }

    public async Task<List<TicketTimelineEventDto>> GetFailedAsync()
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, "/api/v1/timeline/failed");
        using var response = await _client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return new();

        return await response.Content.ReadFromJsonAsync<List<TicketTimelineEventDto>>() ?? new();
    }

    public async Task<IReadOnlyList<PendingEmailOrganization>> SearchPendingOrganizationsAsync(string? search, string? id = null, CancellationToken ct = default)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            "/api/v1/timeline/pending/organizations", new Dictionary<string, string?> { ["q"] = search, ["id"] = id, ["take"] = "20" }));
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<PendingEmailOrganization>>(ct) ?? [];
    }

    public async Task<PendingEmailPage> SearchPendingAsync(PendingEmailQuery query, CancellationToken ct = default)
    {
        var values = new Dictionary<string, string?>
        {
            ["search"] = query.Search, ["status"] = query.Status?.ToString(), ["organizationId"] = query.OrganizationId,
            ["sender"] = query.Sender, ["recipient"] = query.Recipient, ["reason"] = query.Reason,
            ["from"] = query.From?.ToUniversalTime().ToString("O"), ["to"] = query.To?.ToUniversalTime().ToString("O"),
            ["sort"] = query.Sort, ["descending"] = query.Descending.ToString(), ["page"] = query.Page.ToString(),
            ["pageSize"] = query.PageSize.ToString()
        };
        using var request = CreateUserScopedRequest(HttpMethod.Get,
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("/api/v1/timeline/pending", values));
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PendingEmailPage>(ct) ?? throw new HttpRequestException("Empty delivery response.");
    }

    public async Task<PendingEmailActionResponse> ApplyPendingActionAsync(PendingEmailActionRequest action, CancellationToken ct = default)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Post, "/api/v1/timeline/pending/actions");
        request.Content = JsonContent.Create(action);
        using var response = await _client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PendingEmailActionResponse>(ct) ?? throw new HttpRequestException("Empty action response.");
    }

    private HttpRequestMessage CreateUserScopedRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        var userId = ResolveCurrentUserId();
        if (!string.IsNullOrWhiteSpace(userId))
            request.Headers.TryAddWithoutValidation("X-Helpdesk-UserId", userId);

        return request;
    }

    private string? ResolveCurrentUserId()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null)
            return null;

        return user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue("preferred_username");
    }
}
