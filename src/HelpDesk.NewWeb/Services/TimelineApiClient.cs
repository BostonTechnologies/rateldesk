using System.Net.Http.Json;
using System.Security.Claims;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Models;

namespace HelpDesk.NewWeb.Services;

public interface ITimelineApiClient
{
    Task<List<TicketTimelineEventDto>> GetTimelineAsync(string ticketId, string order = "desc", string ticketType = "incidents");
    Task RetryAsync(Guid id);
    Task RetryAllAsync();
    Task<MailboxOutgoingRetryPreview?> PreviewOutgoingRetryAsync(Guid id);
    Task RetryWithCurrentOutgoingAsync(Guid id, long expectedOutgoingVersion);
    Task<int> GetPendingCountAsync();
    Task<List<TicketTimelineEventDto>> GetFailedAsync();
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

    public async Task RetryWithCurrentOutgoingAsync(Guid id, long expectedOutgoingVersion)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Post, $"/api/v1/timeline/{id}/retry-current-outgoing");
        request.Content = JsonContent.Create(new ConfirmMailboxOutgoingRetryRequest(expectedOutgoingVersion, true));
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
