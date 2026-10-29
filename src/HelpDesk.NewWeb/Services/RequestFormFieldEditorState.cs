using Helpdesk.Shared.DTOs.RequestForm;

namespace HelpDesk.NewWeb.Services;

public static class RequestFormFieldEditorState
{
    public static bool SupportsDatasetBinding(FormField field) =>
        string.Equals(field.Type, "text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(field.Type, "helpdeskData", StringComparison.OrdinalIgnoreCase);

    public static FormField ForEditor(FormField field)
    {
        // A binding augments a Text field; it does not change the schema type.
        field.PredefinedOptions ??= [];
        return field;
    }

    public static FormField ForPersistence(FormField field) => new()
    {
        Key = field.Key,
        Label = field.Label,
        Type = string.Equals(field.Type, "helpdeskData", StringComparison.OrdinalIgnoreCase) ? "text" : field.Type,
        Required = field.Required,
        DataBinding = field.DataBinding,
        PredefinedOptions = field.PredefinedOptions
    };
}
