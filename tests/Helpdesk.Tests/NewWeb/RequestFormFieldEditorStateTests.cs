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
            Key = "device", Label = "Choose device", Type = type, Required = true,
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
        Assert.Equal(JsonSerializer.Serialize(field.DataBinding, options), JsonSerializer.Serialize(reloaded.DataBinding, options));
        Assert.Equal(type, editor.Type); // Persistence cannot rewrite the live editor draft.
    }

    [Theory]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("predefined")]
    public void OtherFieldTypesDoNotAcquireDatasetBindingControls(string type) =>
        Assert.False(RequestFormFieldEditorState.SupportsDatasetBinding(new FormField { Type = type }));
}
