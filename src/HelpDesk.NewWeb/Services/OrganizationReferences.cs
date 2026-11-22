using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Organization;
using Microsoft.AspNetCore.WebUtilities;

namespace HelpDesk.NewWeb.Services;

internal static class OrganizationReferences
{
    // Read labels only for references present on the current surface.
    internal static async Task<List<OrganizationDto>> LoadAsync(HttpClient api, IEnumerable<string?> ids, string endpoint = "api/v1/admin/directories/organizations/search")
    {
        var organizations = new List<OrganizationDto>();
        foreach (var chunk in ids.OfType<string>().Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).Chunk(25))
        {
            var url = QueryHelpers.AddQueryString(endpoint, "limit", chunk.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var id in chunk) url = QueryHelpers.AddQueryString(url, "ids", id);
            var page = await api.GetFromJsonAsync<CreationLookupResult<OrganizationDto>>(url);
            if (page is not null) organizations.AddRange(page.Items);
        }
        return organizations;
    }
}
