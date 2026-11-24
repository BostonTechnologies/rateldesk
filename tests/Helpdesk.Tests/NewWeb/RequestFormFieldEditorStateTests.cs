extern alias NewWeb;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helpdesk.Shared.DTOs.RequestForm;
using NewWeb::HelpDesk.NewWeb.Services;

namespace Helpdesk.Tests.NewWeb;

public sealed class RequestFormFieldEditorStateTests
{
    [Theory]
    [InlineData("Text")]
    [InlineData("text")]
    [InlineData("helpdeskData")]
    public void DatasetBoundTextRoundTripPreservesBindingAndUsesSupportedSchemaType(string type)
    {
        var field = new FormField
        {
            Key = "device", Label = "Choose device", Type = type, Required = true, MultiSelect = true,
            DataBinding = new() { DatasetId = "devices", DisplayColumn = "displayName", SearchColumns = ["displayName", "serial"], Placeholder = "Find device", AllowFreeText = false, MinSearchLength = 2 }
        };
        var editor = RequestFormFieldEditorState.ForEditor(field);
        Assert.Equal(type, editor.Type);
        Assert.True(RequestFormFieldEditorState.SupportsDatasetBinding(editor));
        var saved = RequestFormFieldEditorState.ForPersistence(editor);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var json = JsonSerializer.Serialize(saved, options);
        Assert.Equal(FormFieldTypeDto.Text, JsonSerializer.Deserialize<FormFieldDefinitionDto>(json, options)!.Type);
        var reloaded = JsonSerializer.Deserialize<FormField>(json, options)!;
        Assert.Equal("device", reloaded.Key);
        Assert.Equal("Choose device", reloaded.Label);
        Assert.True(reloaded.Required);
        Assert.True(reloaded.MultiSelect);
        Assert.Contains("\"multiSelect\":true", json);
        Assert.Equal(JsonSerializer.Serialize(field.DataBinding, options), JsonSerializer.Serialize(reloaded.DataBinding, options));
        Assert.Equal(type, editor.Type); // Persistence cannot rewrite the live editor draft.
    }

    [Theory]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("predefined")]
    public void OtherFieldTypesDoNotAcquireDatasetBindingControls(string type) =>
        Assert.False(RequestFormFieldEditorState.SupportsDatasetBinding(new FormField { Type = type }));
    [Fact]
    public void Removing_binding_clears_selection_mode_and_rebinding_keeps_it_off()
    {
        var field = new FormField { Type = "helpdeskData", DataBinding = new() { DatasetId = "devices" }, MultiSelect = true };
        Assert.True(RequestFormFieldEditorState.SupportsMultiSelect(field, hasAvailableDataset: true));

        field.DataBinding = null;
        Assert.True(RequestFormFieldEditorState.ClearIncompatibleMultiSelect(field, hasAvailableDataset: false));
        Assert.False(field.MultiSelect);

        field.DataBinding = new() { DatasetId = "devices" };
        Assert.True(RequestFormFieldEditorState.SupportsMultiSelect(field, hasAvailableDataset: true));
        Assert.False(field.MultiSelect);
    }

    [Theory]
    [InlineData("predefined", true)]
    [InlineData("text", false)]
    [InlineData("textarea", false)]
    [InlineData("number", false)]
    [InlineData("boolean", false)]
    public void Unbound_field_eligibility_is_limited_to_predefined_lists(string type, bool expected)
    {
        var field = new FormField { Type = type };
        Assert.Equal(expected, RequestFormFieldEditorState.SupportsMultiSelect(field, hasAvailableDataset: false));
    }
}
