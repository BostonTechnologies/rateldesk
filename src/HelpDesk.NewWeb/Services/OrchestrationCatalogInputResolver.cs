using Helpdesk.Shared.DTOs.Orchestration;

namespace HelpDesk.NewWeb.Services;

public static class OrchestrationCatalogInputResolver
{
    public static IReadOnlyList<OrchestrationCatalogInputDefinitionDto> ResolveInputs(
        OrchestrationCatalogRequestDefinitionDto? requestDefinition,
        IEnumerable<OrchestrationCatalogJobDto> jobs)
    {
        if (requestDefinition is null)
        {
            return [];
        }

        if (requestDefinition.Inputs.Count > 0)
        {
            return requestDefinition.Inputs;
        }

        return FindJob(requestDefinition, jobs)?.Inputs ?? [];
    }

    public static OrchestrationCatalogJobDto? FindJob(
        OrchestrationCatalogRequestDefinitionDto requestDefinition,
        IEnumerable<OrchestrationCatalogJobDto> jobs)
    {
        var source = jobs.ToList();
        if (!string.IsNullOrWhiteSpace(requestDefinition.OrchestrationJobDefinitionId))
        {
            var byId = source.FirstOrDefault(x =>
                string.Equals(x.Id, requestDefinition.OrchestrationJobDefinitionId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        var requestJobName = NormalizeName(FirstReadableName(
            requestDefinition.OrchestrationJobDefinitionName,
            requestDefinition.DisplayName,
            requestDefinition.RequestDefinitionName));
        var requestFolder = NormalizePath(requestDefinition.FolderPath);

        return source.FirstOrDefault(job =>
            IsNameMatch(requestJobName, NormalizeName(FirstReadableName(job.DisplayName, job.Name)))
            && (string.IsNullOrWhiteSpace(requestFolder) || NormalizePath(job.FolderPath) == requestFolder))
            ?? source.FirstOrDefault(job =>
                IsNameMatch(requestJobName, NormalizeName(FirstReadableName(job.DisplayName, job.Name))));
    }

    public static string JoinTargetLabel(string? folderPath, string displayName)
    {
        var folder = NormalizePath(folderPath);
        var name = NormalizePath(displayName);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.IsNullOrWhiteSpace(name) ? "External orchestration target" : name;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return folder;
        }

        return name.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{folder}/{name}";
    }

    public static bool IsOpaqueIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length >= 24
            && trimmed.All(c => char.IsDigit(c) || c is '-' || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F');
    }

    public static string? FirstReadableName(params string?[] values)
        => values
            .Select(value => value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !IsOpaqueIdentifier(value))
            ?? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    public static string ReadinessLabel(OrchestrationAutomationTargetDto target) => target.Readiness switch
    {
        "incomplete" => "Incomplete job: add executable content in NetRatel",
        "requires-input" => "Requires input before execution",
        "worker-check-at-run" => "Worker eligibility is checked when the job runs",
        _ => "Execution readiness is unavailable"
    };

    private static string NormalizePath(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('/');

    private static string NormalizeName(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Trim().Where(c => !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());

    private static bool IsNameMatch(string left, string right)
        => left.Length > 0
            && right.Length > 0
            && (string.Equals(left, right, StringComparison.Ordinal)
                || left.EndsWith(right, StringComparison.Ordinal)
                || right.EndsWith(left, StringComparison.Ordinal));
}
