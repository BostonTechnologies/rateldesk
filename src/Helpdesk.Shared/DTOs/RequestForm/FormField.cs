using System.Text.Json.Serialization;

namespace Helpdesk.Shared.DTOs.RequestForm;

public class FormField
{
    public string Label { get; set; } = string.Empty;
    public string? Key { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool Required { get; set; }
    [JsonPropertyName("multiSelect")]
    public bool MultiSelect { get; set; }
    public FormFieldDataBinding? DataBinding { get; set; }
    public List<string> PredefinedOptions { get; set; } = new();
}

public class FormFieldDataBinding
{
    public string DatasetId { get; set; } = string.Empty;
    public string? DisplayColumn { get; set; }
    public List<string> SearchColumns { get; set; } = new();
    public string? Placeholder { get; set; }
    public bool AllowFreeText { get; set; }
    public int MinSearchLength { get; set; } = 1;
}
