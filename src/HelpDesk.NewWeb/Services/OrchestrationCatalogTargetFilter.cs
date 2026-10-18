using Helpdesk.Shared.DTOs.Orchestration;

namespace HelpDesk.NewWeb.Services;

public static class OrchestrationCatalogTargetFilter
{
    public static IReadOnlyList<OrchestrationCatalogRequestDefinitionDto> FilterForConnection(
        IEnumerable<OrchestrationCatalogRequestDefinitionDto> definitions,
        int? peerTenantId)
        => peerTenantId is null ? [] : definitions
            .Where(x => x.TenantId == peerTenantId && !string.IsNullOrWhiteSpace(x.RequestDefinitionId))
            .DistinctBy(x => x.RequestDefinitionId, StringComparer.Ordinal)
            .OrderBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(GetDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<OrchestrationCatalogRequestDefinitionDto> FilterForOrganization(
        IEnumerable<OrchestrationCatalogRequestDefinitionDto> definitions,
        int? organizationOrchestrationTenantId,
        string? organizationOrchestrationTenantName,
        string? selectedRequestDefinitionId = null)
    {
        if (organizationOrchestrationTenantId is null)
        {
            return [];
        }

        var source = definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.RequestDefinitionId))
            .ToList();

        var filtered = HasAnyTenantMetadata(source)
            ? source.Where(x => IsTenantMatch(x, organizationOrchestrationTenantId, organizationOrchestrationTenantName)
                || IsSelectedDefinition(x, selectedRequestDefinitionId))
            : source;

        return filtered
            .DistinctBy(x => x.RequestDefinitionId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(GetDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasAnyTenantMetadata(IEnumerable<OrchestrationCatalogRequestDefinitionDto> definitions)
        => definitions.Any(HasTenantMetadata);

    public static bool HasTenantMetadata(OrchestrationCatalogRequestDefinitionDto definition)
        => definition.TenantId.HasValue || !string.IsNullOrWhiteSpace(definition.TenantName);

    public static bool IsTenantMatch(
        OrchestrationCatalogRequestDefinitionDto definition,
        int? organizationOrchestrationTenantId,
        string? organizationOrchestrationTenantName)
    {
        if (organizationOrchestrationTenantId.HasValue && definition.TenantId == organizationOrchestrationTenantId.Value)
        {
            return true;
        }

        var definitionTenantName = NormalizeTenantName(definition.TenantName);
        var organizationTenantName = NormalizeTenantName(organizationOrchestrationTenantName);
        return definitionTenantName.Length > 0
            && string.Equals(definitionTenantName, organizationTenantName, StringComparison.Ordinal);
    }

    private static bool IsSelectedDefinition(OrchestrationCatalogRequestDefinitionDto definition, string? selectedRequestDefinitionId)
        => !string.IsNullOrWhiteSpace(selectedRequestDefinitionId)
            && string.Equals(definition.RequestDefinitionId, selectedRequestDefinitionId.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string GetDisplayName(OrchestrationCatalogRequestDefinitionDto definition)
        => FirstReadableName(definition.DisplayName, definition.OrchestrationJobDefinitionName, definition.RequestDefinitionName)
            ?? "External orchestration target";

    private static string? FirstReadableName(params string?[] values)
        => values
            .Select(value => value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !LooksLikeOpaqueIdentifier(value))
            ?? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool LooksLikeOpaqueIdentifier(string value)
        => value.Length >= 24 && value.All(c => char.IsDigit(c) || c is '-' || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F');

    private static string NormalizeTenantName(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
