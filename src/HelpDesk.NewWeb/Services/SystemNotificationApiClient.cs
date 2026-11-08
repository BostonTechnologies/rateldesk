using System.Net.Http.Json;
using System.Security.Claims;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Notification;

namespace HelpDesk.NewWeb.Services;

public interface ISystemNotificationApiClient
{
    Task<PagedResponse<NotificationDto>> GetPagedNotificationsAsync(
        int page = 1,
        int pageSize = 10,
        string? searchTerm = null,
        NotificationSeverity? severity = null,
        string? source = null,
        string? category = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default);
    Task<List<NotificationDto>> GetNotificationsAsync(
        int page = 1,
        int pageSize = 10,
        string? searchTerm = null,
        NotificationSeverity? severity = null,
        string? source = null,
        string? category = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null);
    Task<List<NotificationDto>> GetUnreadNotificationsAsync(int page = 1, int pageSize = 10);
    Task<List<NotificationDto>> GetUnreadErrorNotificationsAsync(int take = 20);
    Task<List<NotificationDto>> GetTimelineAsync(
        string? reference = null,
        string? correlationId = null,
        string? tenantId = null,
        int take = 200);
    Task<NotificationDto?> GetByIdAsync(Guid id);
    Task<NotificationSummaryDto> GetSummaryAsync();
    Task<NotificationSummaryDto> GetErrorSummaryAsync();
    Task MarkReadAsync(Guid id);
    Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids);
    Task<int> PurgeAllAsync(CancellationToken ct = default);
}

public class SystemNotificationApiClient(IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor) : ISystemNotificationApiClient
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("HelpdeskApi");
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public async Task<PagedResponse<NotificationDto>> GetPagedNotificationsAsync(
        int page = 1,
        int pageSize = 10,
        string? searchTerm = null,
        NotificationSeverity? severity = null,
        string? source = null,
        string? category = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? sortBy = null,
        string? sortDir = null,
        CancellationToken ct = default)
    {
        var queryParts = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };

        if (!string.IsNullOrWhiteSpace(searchTerm))
            queryParts.Add($"search={Uri.EscapeDataString(searchTerm)}");
        if (severity.HasValue)
            queryParts.Add($"severity={Uri.EscapeDataString(severity.Value.ToString())}");
        if (!string.IsNullOrWhiteSpace(source))
            queryParts.Add($"source={Uri.EscapeDataString(source)}");
        if (!string.IsNullOrWhiteSpace(category))
            queryParts.Add($"category={Uri.EscapeDataString(category)}");
        if (from.HasValue)
            queryParts.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to.HasValue)
            queryParts.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(sortBy))
            queryParts.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
        if (!string.IsNullOrWhiteSpace(sortDir))
            queryParts.Add($"sortDir={Uri.EscapeDataString(sortDir)}");

        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/notifications?{string.Join("&", queryParts)}");
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PagedResponse<NotificationDto>>(cancellationToken: ct)
               ?? new PagedResponse<NotificationDto> { Page = page, PageSize = pageSize };
    }

    public async Task<List<NotificationDto>> GetNotificationsAsync(
        int page = 1,
        int pageSize = 10,
        string? searchTerm = null,
        NotificationSeverity? severity = null,
        string? source = null,
        string? category = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var queryParts = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };

        if (!string.IsNullOrWhiteSpace(searchTerm))
            queryParts.Add($"search={Uri.EscapeDataString(searchTerm)}");
        if (severity.HasValue)
            queryParts.Add($"severity={Uri.EscapeDataString(severity.Value.ToString())}");
        if (!string.IsNullOrWhiteSpace(source))
            queryParts.Add($"source={Uri.EscapeDataString(source)}");
        if (!string.IsNullOrWhiteSpace(category))
            queryParts.Add($"category={Uri.EscapeDataString(category)}");
        if (from.HasValue)
            queryParts.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to.HasValue)
            queryParts.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");

        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/notifications?{string.Join("&", queryParts)}");
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return new List<NotificationDto>();

        var paged = await response.Content.ReadFromJsonAsync<PagedResponse<NotificationDto>>();
        return paged?.Items ?? new List<NotificationDto>();
    }

    public async Task<List<NotificationDto>> GetUnreadNotificationsAsync(int page = 1, int pageSize = 10)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/notifications/unread?page={page}&pageSize={pageSize}");

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return new();

        return await response.Content
            .ReadFromJsonAsync<List<NotificationDto>>()
            ?? new();
    }

    public async Task<List<NotificationDto>> GetUnreadErrorNotificationsAsync(int take = 20)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/notifications/unread-errors?take={take}");

        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return new();

        return await response.Content
            .ReadFromJsonAsync<List<NotificationDto>>()
            ?? new();
    }

    public async Task<List<NotificationDto>> GetTimelineAsync(
        string? reference = null,
        string? correlationId = null,
        string? tenantId = null,
        int take = 200)
    {
        var queryParts = new List<string> { $"take={take}" };
        if (!string.IsNullOrWhiteSpace(reference))
            queryParts.Add($"reference={Uri.EscapeDataString(reference)}");
        if (!string.IsNullOrWhiteSpace(correlationId))
            queryParts.Add($"correlationId={Uri.EscapeDataString(correlationId)}");
        if (!string.IsNullOrWhiteSpace(tenantId))
            queryParts.Add($"tenantId={Uri.EscapeDataString(tenantId)}");

        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/notifications/timeline?{string.Join("&", queryParts)}");
        using var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            return new();

        return await response.Content
            .ReadFromJsonAsync<List<NotificationDto>>()
            ?? new();
    }

    public async Task<NotificationDto?> GetByIdAsync(Guid id)
    {
        if (id == Guid.Empty)
            return null;

        using var request = CreateUserScopedRequest(HttpMethod.Get, $"/api/v1/notifications/{id}");
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<NotificationDto>();
    }

    public async Task<NotificationSummaryDto> GetSummaryAsync()
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, "/api/v1/notifications/summary");
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<NotificationSummaryDto>() ?? new NotificationSummaryDto();
    }

    public async Task<NotificationSummaryDto> GetErrorSummaryAsync()
    {
        using var request = CreateUserScopedRequest(HttpMethod.Get, "/api/v1/notifications/error-summary");
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return new NotificationSummaryDto();

        return await response.Content.ReadFromJsonAsync<NotificationSummaryDto>() ?? new NotificationSummaryDto();
    }

    public async Task MarkReadAsync(Guid id)
    {
        if (id == Guid.Empty)
            return;

        using var request = CreateUserScopedRequest(HttpMethod.Post, $"/api/v1/notifications/{id}/read");
        request.Content = JsonContent.Create(new MarkNotificationReadRequest { NotificationId = id });

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids)
    {
        var idList = ids?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList() ?? new List<Guid>();

        if (idList.Count == 0)
            return 0;

        using var request = CreateUserScopedRequest(HttpMethod.Post, "/api/v1/notifications/mark-read");
        request.Content = JsonContent.Create(new BulkMarkNotificationReadRequest { Ids = idList });

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<BulkMarkReadResult>();
        return result?.Updated ?? 0;
    }

    public async Task<int> PurgeAllAsync(CancellationToken ct = default)
    {
        using var request = CreateUserScopedRequest(HttpMethod.Delete, "/api/v1/notifications/purge");
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<PurgeNotificationsResult>(cancellationToken: ct);
        return result?.Deleted ?? 0;
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

    private sealed class PurgeNotificationsResult
    {
        public int Deleted { get; set; }
    }
}
