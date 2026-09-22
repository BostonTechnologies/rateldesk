using System.Net;
using System.Net.Http.Json;
using Helpdesk.Shared.DTOs.EmailRules;

namespace HelpDesk.NewWeb.Services;

public interface IInboundEmailRuleAdminClient
{
    Task<IReadOnlyList<InboundEmailRuleDto>> GetRulesAsync(string? tenantId = null, Guid? mailboxId = null);
    Task<InboundEmailRuleDto?> GetRuleAsync(string id);
    Task<InboundEmailRuleDto?> CreateAsync(CreateInboundEmailRuleRequest request);
    Task<InboundEmailRuleDto?> UpdateAsync(string id, UpdateInboundEmailRuleRequest request);
    Task<InboundEmailRuleDto?> EnableAsync(string id);
    Task<InboundEmailRuleDto?> DisableAsync(string id);
    Task<IReadOnlyList<InboundEmailRuleDto>> ReorderAsync(IEnumerable<InboundEmailRulePriorityDto> rules);
    Task<IReadOnlyList<InboundEmailRuleAuditDto>> GetAuditAsync(string? messageId = null, string? ticketId = null, string? tenantId = null, Guid? mailboxId = null);
}

public sealed class InboundEmailRuleAdminClient(IHttpClientFactory httpClientFactory) : IInboundEmailRuleAdminClient
{
    private readonly HttpClient http = httpClientFactory.CreateClient("HelpdeskApi");

    public async Task<IReadOnlyList<InboundEmailRuleDto>> GetRulesAsync(string? tenantId = null, Guid? mailboxId = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query.Add($"tenantId={Uri.EscapeDataString(tenantId)}");
        }
        if (mailboxId.HasValue)
        {
            query.Add($"mailboxId={Uri.EscapeDataString(mailboxId.Value.ToString())}");
        }

        var url = "api/v1/inbound-email-rules";
        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        return await http.GetFromJsonAsync<List<InboundEmailRuleDto>>(url) ?? [];
    }

    public async Task<InboundEmailRuleDto?> GetRuleAsync(string id)
    {
        var response = await http.GetAsync($"api/v1/inbound-email-rules/{Uri.EscapeDataString(id)}");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadResultAsync<InboundEmailRuleDto>(response, "Load inbound email rule failed.");
    }

    public async Task<InboundEmailRuleDto?> CreateAsync(CreateInboundEmailRuleRequest request)
    {
        using var response = await http.PostAsJsonAsync("api/v1/inbound-email-rules", request);
        return await ReadResultAsync<InboundEmailRuleDto>(response, "Create inbound email rule failed.");
    }

    public async Task<InboundEmailRuleDto?> UpdateAsync(string id, UpdateInboundEmailRuleRequest request)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/inbound-email-rules/{Uri.EscapeDataString(id)}", request);
        return await ReadResultAsync<InboundEmailRuleDto>(response, "Update inbound email rule failed.");
    }

    public async Task<InboundEmailRuleDto?> EnableAsync(string id)
    {
        var response = await http.PostAsync($"api/v1/inbound-email-rules/{Uri.EscapeDataString(id)}/enable", null);
        return await ReadResultAsync<InboundEmailRuleDto>(response, "Enable inbound email rule failed.");
    }

    public async Task<InboundEmailRuleDto?> DisableAsync(string id)
    {
        var response = await http.PostAsync($"api/v1/inbound-email-rules/{Uri.EscapeDataString(id)}/disable", null);
        return await ReadResultAsync<InboundEmailRuleDto>(response, "Disable inbound email rule failed.");
    }

    public async Task<IReadOnlyList<InboundEmailRuleDto>> ReorderAsync(IEnumerable<InboundEmailRulePriorityDto> rules)
    {
        var response = await http.PostAsJsonAsync(
            "api/v1/inbound-email-rules/reorder",
            new ReorderInboundEmailRulesRequest(rules.ToList()));
        return await ReadResultAsync<List<InboundEmailRuleDto>>(response, "Reorder inbound email rules failed.") ?? [];
    }

    public async Task<IReadOnlyList<InboundEmailRuleAuditDto>> GetAuditAsync(string? messageId = null, string? ticketId = null, string? tenantId = null, Guid? mailboxId = null)
    {
        var query = new List<string>();
        if (mailboxId.HasValue) query.Add($"mailboxId={mailboxId.Value:D}");
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            query.Add($"messageId={Uri.EscapeDataString(messageId)}");
        }
        if (!string.IsNullOrWhiteSpace(ticketId))
        {
            query.Add($"ticketId={Uri.EscapeDataString(ticketId)}");
        }
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query.Add($"tenantId={Uri.EscapeDataString(tenantId)}");
        }

        var url = "api/v1/inbound-email-rules/audit";
        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        return await http.GetFromJsonAsync<List<InboundEmailRuleAuditDto>>(url) ?? [];
    }

    private static async Task<T?> ReadResultAsync<T>(HttpResponseMessage response, string fallback)
    {
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<T>();
        }

        throw new InvalidOperationException(await ReadErrorAsync(response, fallback));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, string fallback)
    {
        var body = await response.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body)
            ? $"{fallback} HTTP {(int)response.StatusCode}."
            : $"{fallback} HTTP {(int)response.StatusCode}: {body}";
    }
}
