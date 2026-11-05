using System.Security.Cryptography;
using System.Text;
using Helpdesk.Application.Resources;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Resources;

public sealed partial class DataManagementService
{
    public Task<DatasetDefinitionDto?> GetCreateResultAsync(string organizationId, Guid requestId, CancellationToken cancellationToken = default)
        => GetDatasetAsync(CreationDatasetId(organizationId.Trim(), requestId), cancellationToken);

    private static string CreationDatasetId(string organizationId, Guid requestId)
        => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"dataset-create\0{organizationId}\0{requestId:N}"))[..16]).ToString();

    private static void ValidateDefinition(string? name, DatasetSourceType sourceType,
        IReadOnlyList<DatasetColumnDto> columns, string? key, string? display, IReadOnlyList<string>? search)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(name))
            errors["Name"] = ["Enter a dataset name."];
        if (!Enum.IsDefined(sourceType)) errors["SourceType"] = ["Choose a supported dataset source."];
        if (columns.Count == 0) errors["Columns"] = ["Add at least one field."];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            if (string.IsNullOrWhiteSpace(column.Name)) errors[$"Columns[{index}].Name"] = ["Enter a field name."];
            else if (!names.Add(column.Name.Trim())) errors[$"Columns[{index}].Name"] = ["Field names must be unique, ignoring case."];
            if (!Enum.IsDefined(column.DataType)) errors[$"Columns[{index}].DataType"] = ["Choose Text, Number, Boolean or DateTime."];
        }
        if (!string.IsNullOrWhiteSpace(key) && !names.Contains(key.Trim())) errors["KeyColumn"] = ["Choose a key field in this schema."];
        if (!string.IsNullOrWhiteSpace(display) && !names.Contains(display.Trim())) errors["DisplayColumn"] = ["Choose a display field in this schema."];
        if (columns.Count(x => x.IsKey) > 1) errors["KeyColumn"] = ["Choose exactly one key field."];
        if (columns.Count(x => x.IsDisplay) > 1) errors["DisplayColumn"] = ["Choose exactly one display field."];
        if (search?.Any(value => string.IsNullOrWhiteSpace(value) || !names.Contains(value.Trim())) == true)
            errors["SearchColumns"] = ["Every searchable field must exist in this schema."];
        if (errors.Count != 0) throw new DatasetDefinitionValidationException(errors);
    }

    private static bool CreationMatches(DatasetDefinitionDto saved, CreateDatasetDefinitionDto draft,
        IReadOnlyList<DatasetColumnDto> columns)
        => saved.OrganizationId == draft.OrganizationId.Trim() && saved.Name == draft.Name.Trim() &&
           saved.SourceType == draft.SourceType && saved.IsActive == draft.IsActive &&
           saved.KeyColumn.Equals(ResolveKeyColumn(draft.KeyColumn, columns), StringComparison.OrdinalIgnoreCase) &&
           saved.DisplayColumn.Equals(ResolveDisplayColumn(draft.DisplayColumn, columns), StringComparison.OrdinalIgnoreCase) &&
           saved.SearchColumns.SequenceEqual(CreationSearchColumns(draft, columns), StringComparer.OrdinalIgnoreCase) &&
           saved.Columns.Select(x => (x.Name, x.DataType)).SequenceEqual(columns.Select(x => (x.Name, x.DataType)));

    private static List<string> CreationSearchColumns(CreateDatasetDefinitionDto draft, IReadOnlyList<DatasetColumnDto> columns)
        => draft.RequestId is not null
            ? draft.SearchColumns.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : ResolveSearchColumns(draft.SearchColumns, columns);

    private async Task<DatasetDefinitionDto?> FindCreationRetryAsync(CreateDatasetDefinitionDto dto,
        IReadOnlyList<DatasetColumnDto> columns, CancellationToken cancellationToken)
    {
        if (dto.RequestId is not { } requestId) return null;
        if (requestId == Guid.Empty) throw new DatasetDefinitionValidationException(new Dictionary<string, string[]> { ["RequestId"] = ["Use a nonempty creation request identifier."] });
        var saved = await GetCreateResultAsync(dto.OrganizationId, requestId, cancellationToken);
        if (saved is not null && !CreationMatches(saved, dto, columns))
            throw new DatasetWriteConflictException("This creation request already produced a different dataset. Reload its saved definition before starting a new draft.");
        return saved;
    }
}
