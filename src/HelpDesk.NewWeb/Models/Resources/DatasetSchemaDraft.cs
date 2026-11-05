using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;

namespace HelpDesk.NewWeb.Models.Resources;

/// <summary>Transient editor state. Credentials never belong in a dataset draft.</summary>
public sealed class DatasetSchemaDraft
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public List<DatasetColumnDto> Columns { get; set; } =
    [
        new() { Name = "id", IsKey = true, SortOrder = 0 },
        new() { Name = "name", IsDisplay = true, IsSearchable = true, SortOrder = 1 }
    ];

    public static Dictionary<string, string> ValidateColumns(IReadOnlyList<DatasetColumnDto> columns)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (columns.Count == 0) errors["Columns"] = "Add at least one field.";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var name = column.Name?.Trim() ?? string.Empty;
            if (name.Length == 0) errors[$"Columns[{index}].Name"] = "Enter a field name.";
            else if (!names.Add(name)) errors[$"Columns[{index}].Name"] = "Field names must be unique, ignoring case.";
            if (!Enum.IsDefined(column.DataType)) errors[$"Columns[{index}].DataType"] = "Choose Text, Number, Boolean or DateTime.";
        }
        if (columns.Count(x => x.IsKey) != 1) errors["KeyColumn"] = "Choose exactly one key field.";
        if (columns.Count(x => x.IsDisplay) != 1) errors["DisplayColumn"] = "Choose exactly one display field.";
        return errors;
    }

    public CreateDatasetDefinitionDto ToCreateRequest(string organizationId) => new()
    {
        OrganizationId = organizationId,
        Name = Name.Trim(),
        SourceType = DatasetSourceType.Custom,
        KeyColumn = Columns.Single(x => x.IsKey).Name.Trim(),
        DisplayColumn = Columns.Single(x => x.IsDisplay).Name.Trim(),
        SearchColumns = Columns.Where(x => x.IsSearchable).Select(x => x.Name.Trim()).ToList(),
        IsActive = IsActive,
        Columns = Columns.Select((column, index) => new DatasetColumnDto
        {
            Name = column.Name.Trim(), DataType = column.DataType,
            IsKey = column.IsKey, IsDisplay = column.IsDisplay,
            IsSearchable = column.IsSearchable, SortOrder = index
        }).ToList()
    };
}
