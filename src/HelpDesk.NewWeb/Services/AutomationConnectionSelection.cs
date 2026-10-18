using System.Globalization;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Pairing;

namespace HelpDesk.NewWeb.Services;

public sealed record AutomationConnectionChoice(Guid ConnectionId, string Name, string PeerName, string OrganizationId, int PeerTenantId);

public static class AutomationConnectionSelection
{
    public static IReadOnlyList<AutomationConnectionChoice> ForOrganization(IEnumerable<PairingConnectionDto> connections, string? organizationId)
    {
        if (string.IsNullOrWhiteSpace(organizationId)) return [];
        return connections.Where(x => x.Status == "Connected" && x.Mapping is { RunAutomation: true } mapping
                && string.Equals(mapping.RatelDeskOrganizationId, organizationId, StringComparison.Ordinal)
                && mapping.Id == x.Id && mapping.PairId == x.PairId
                && Guid.TryParse(x.Id, out var id) && id != Guid.Empty
                && int.TryParse(mapping.NetRatelTenantId, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId) && tenantId > 0)
            .Select(x => new AutomationConnectionChoice(Guid.Parse(x.Id), x.Mapping!.Name, x.Peer.Name, organizationId,
                int.Parse(x.Mapping.NetRatelTenantId, CultureInfo.InvariantCulture)))
            .DistinctBy(x => x.ConnectionId)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static AutomationConnectionChoice? Resolve(IReadOnlyList<AutomationConnectionChoice> choices, Guid? storedConnectionId)
        => storedConnectionId is { } id ? choices.SingleOrDefault(x => x.ConnectionId == id)
            : choices.Count == 1 ? choices[0] : null;

    public static bool IsVerified(AutomationConnectionChoice? choice, OrchestrationConnectivitySettingsDto? settings)
        => choice is not null && settings is { Enabled: true, Source: "pairing" }
            && Guid.TryParse(settings.SourceKey, out var sourceId) && sourceId == choice.ConnectionId
            && string.Equals(settings.LocalTenantId, choice.OrganizationId, StringComparison.Ordinal)
            && int.TryParse(settings.PeerTenantId, NumberStyles.None, CultureInfo.InvariantCulture, out var peerTenantId)
            && peerTenantId == choice.PeerTenantId;

    public static string Query(string organizationId, Guid connectionId)
        => $"?organizationId={Uri.EscapeDataString(organizationId)}&connectionId={connectionId:D}";
}
