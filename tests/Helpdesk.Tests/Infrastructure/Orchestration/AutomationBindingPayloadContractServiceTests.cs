using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class AutomationBindingPayloadContractServiceTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task Missing_or_disabled_target_blocks_automatic_work_but_preserves_manual_work(
        bool hasDisabledBinding, bool autoStart, bool expected)
    {
        var parser = new RequestFormSchemaParser();
        var form = CreateRequestForm(MultiSelectSchema.Replace("\"type\":\"automation\"", $"\"type\":\"automation\",\"autoStart\":{autoStart.ToString().ToLowerInvariant()}"));
        var task = Assert.Single(parser.Parse(form.JsonSchema).Tasks);
        var bindings = Substitute.For<IAutomationBindingService>();
        IReadOnlyList<AutomationBindingDto> saved = hasDisabledBinding
            ? [new() { TaskTemplateId = task.Id, Enabled = false }]
            : [];
        bindings.ListAsync(form.Id, Arg.Any<CancellationToken>()).Returns(saved);
        var service = new AutomationBindingPayloadContractService(parser, bindings);

        var validation = await service.ValidateBoundRequestPayloadAsync(form, "{}");

        Assert.Equal(expected, validation.Success);
        if (!expected)
            Assert.Contains(hasDisabledBinding ? "disabled NetRatel binding" : "no NetRatel binding", Assert.Single(validation.Errors));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multi_select_maps_exactly_one_json_text_parameter(bool single)
    {
        var parser = new RequestFormSchemaParser();
        var service = new AutomationBindingPayloadContractService(parser, Substitute.For<IAutomationBindingService>());
        var form = CreateRequestForm(MultiSelectSchema);
        var template = Assert.Single(parser.Parse(form.JsonSchema).Tasks);
        string[] selections = single ? ["CN=Fleet Admin,OU=Groups,DC=example,DC=org"]
            : ["CN=Fleet Admin,OU=Groups,DC=example,DC=org", """Warehouse "quoted" C:\Shared — Ångström"""];
        var payload = JsonSerializer.Serialize(new { groups = selections, _boundLabels = new { groups = new[] { "Label only" } } });

        var result = service.BuildTaskInput(form, template, payload);

        Assert.True(result.Success, result.Error);
        Assert.Single(result.InputNode);
        var wire = result.InputNode["groupList"]!.GetValue<string>();
        Assert.Equal(JsonSerializer.Serialize(selections), wire);
        Assert.Equal(selections, JsonSerializer.Deserialize<string[]>(wire));
    }

    [Fact]
    public void Later_form_edits_preserve_the_shape_of_historical_scalar_and_array_submissions()
    {
        var parser = new RequestFormSchemaParser();
        var service = new AutomationBindingPayloadContractService(parser, Substitute.For<IAutomationBindingService>());
        var multi = CreateRequestForm(MultiSelectSchema);
        var single = CreateRequestForm(MultiSelectSchema.Replace("multiSelect\":true", "multiSelect\":false", StringComparison.Ordinal));
        var template = Assert.Single(parser.Parse(multi.JsonSchema).Tasks);

        var legacy = service.BuildTaskInput(multi, template, """{"groups":"CN=Historical,DC=example"}""");
        var newer = service.BuildTaskInput(single, template, """{"groups":["CN=Recorded,DC=example"]}""");

        Assert.Equal("CN=Historical,DC=example", legacy.InputNode["groupList"]!.GetValue<string>());
        Assert.Equal("""["CN=Recorded,DC=example"]""", newer.InputNode["groupList"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("text", false, null, true)]
    [InlineData("text", true, null, false)]
    [InlineData("text", true, "[]", true)]
    [InlineData("number", false, null, false)]
    [InlineData("boolean", false, null, false)]
    [InlineData("select", false, null, false)]
    public async Task Optional_empty_multi_select_omits_input_but_honors_the_bound_destination(
        string type, bool required, string? defaultValue, bool expected)
    {
        var parser = new RequestFormSchemaParser();
        var form = CreateRequestForm(MultiSelectSchema);
        var template = Assert.Single(parser.Parse(form.JsonSchema).Tasks);
        var bindings = Substitute.For<IAutomationBindingService>();
        bindings.ListAsync(form.Id, Arg.Any<CancellationToken>()).Returns([
            new() { TaskTemplateId = template.Id, Enabled = true, OrganizationId = "org", OrchestrationRequestDefinitionId = "14" }]);
        var catalog = Substitute.For<IOrchestrationCatalogService>();
        catalog.ListRequestDefinitionsAsync("org", null, Arg.Any<CancellationToken>()).Returns([
            new() { RequestDefinitionId = "14", Inputs = [new() { Key = "groupList", Type = type, Required = required, DefaultValue = defaultValue }] }]);
        var service = new AutomationBindingPayloadContractService(parser, bindings, catalog);

        var built = service.BuildTaskInput(form, template, """{"groups":[]}""");
        var validation = await service.ValidateBoundRequestPayloadAsync(form, """{"groups":[]}""");

        Assert.True(built.Success, built.Error);
        Assert.Empty(built.InputNode);
        Assert.Equal(expected, validation.Success);
    }

    private const string MultiSelectSchema = """
        {"fields":[{"key":"groups","label":"Groups","type":"text","multiSelect":true,"dataBinding":{"datasetId":"groups"}}],
        "tasks":[{"id":"11111111-1111-1111-1111-111111111111","type":"automation","name":"Process groups","payloadMapping":{"groupList":"groups"}}]}
        """;

    [Fact]
    public async Task ValidateBoundRequestPayloadAsync_Fails_WhenRequiredMappedFieldIsMissing()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        bindings.ListAsync("form-1", Arg.Any<CancellationToken>())
            .Returns([
                new AutomationBindingDto
                {
                    Id = "binding-1",
                    RequestFormId = "form-1",
                    TaskTemplateId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    OrchestrationRequestDefinitionId = "orchestration-req-1",
                    Enabled = true
                }
            ]);

        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                { "key": "user_email", "label": "User Email", "type": "text", "required": true }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Provision mailbox",
                  "type": "automation",
                  "payloadMapping": {
                    "userEmail": "user_email"
                  }
                }
              ]
            }
            """);

        var result = await service.ValidateBoundRequestPayloadAsync(form, "{}");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, x => x.Contains("user_email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildTaskInput_OmitsMissingOptionalFields_AndPreservesPresentValues()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                { "key": "user_email", "label": "User Email", "type": "text", "required": true },
                { "key": "department", "label": "Department", "type": "text", "required": false }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Provision mailbox",
                  "type": "automation",
                  "payloadMapping": {
                    "userEmail": "user_email",
                    "department": "department"
                  }
                }
              ]
            }
            """);

        var taskTemplate = parser.Parse(form.JsonSchema).Tasks.Single();

        var result = service.BuildTaskInput(form, taskTemplate, "{\"user_email\":\"person@example.com\"}");

        Assert.True(result.Success);
        Assert.Equal("person@example.com", result.InputNode["userEmail"]?.GetValue<string>());
        Assert.Null(result.InputNode["department"]);
    }

    [Fact]
    public void BuildTaskInput_Succeeds_ForGeneratedLabelKeys()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                { "label": "path1", "type": "text", "required": true },
                { "label": "path2", "type": "text", "required": true }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Run param job",
                  "type": "automation",
                  "payloadMapping": {
                    "path1": "path1",
                    "path2": "path2"
                  }
                }
              ]
            }
            """);

        var taskTemplate = parser.Parse(form.JsonSchema).Tasks.Single();

        var result = service.BuildTaskInput(form, taskTemplate, "{\"path1\":\"c:\\\\\",\"path2\":\"c:\\\\DeepStack\"}");

        Assert.True(result.Success);
        Assert.Equal("c:\\", result.InputNode["path1"]?.GetValue<string>());
        Assert.Equal("c:\\DeepStack", result.InputNode["path2"]?.GetValue<string>());
    }

    [Fact]
    public void BuildTaskInput_PreservesScalarDatasetBoundKeys()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                {
                  "key": "location",
                  "label": "Location",
                  "type": "text",
                  "required": true,
                  "dataBinding": {
                    "datasetId": "dataset-1",
                    "displayColumn": "name",
                    "searchColumns": ["name"]
                  }
                }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Provision by location",
                  "type": "automation",
                  "payloadMapping": {
                    "locationKey": "location"
                  }
                }
              ]
            }
            """);

        var taskTemplate = parser.Parse(form.JsonSchema).Tasks.Single();

        var result = service.BuildTaskInput(
            form,
            taskTemplate,
            "{\"location\":\"loc-123\",\"_boundLabels\":{\"location\":\"Cape Town\"}}");

        Assert.True(result.Success);
        Assert.Equal("loc-123", result.InputNode["locationKey"]?.GetValue<string>());
    }

    [Fact]
    public void BuildTaskInput_TreatsHelpdeskDataAsTextLikeMappedField()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                {
                  "key": "asset",
                  "label": "Asset",
                  "type": "helpdeskData",
                  "required": true,
                  "dataBinding": {
                    "datasetId": "dataset-1",
                    "displayColumn": "name",
                    "searchColumns": ["name"]
                  }
                }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Provision asset",
                  "type": "automation",
                  "payloadMapping": {
                    "assetId": "asset"
                  }
                }
              ]
            }
            """);

        var taskTemplate = parser.Parse(form.JsonSchema).Tasks.Single();

        var result = service.BuildTaskInput(form, taskTemplate, "{\"asset\":\"asset-123\"}");

        Assert.True(result.Success);
        Assert.Equal("asset-123", result.InputNode["assetId"]?.GetValue<string>());
    }

    [Fact]
    public void BuildTaskInput_TreatsPredefinedAsTextLikeMappedField()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                {
                  "key": "path1",
                  "label": "Path",
                  "type": "predefined",
                  "required": true,
                  "predefinedOptions": ["c:\\", "c:\\apps"]
                }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Run script",
                  "type": "automation",
                  "payloadMapping": {
                    "Path": "path1"
                  }
                }
              ]
            }
            """);

        var taskTemplate = parser.Parse(form.JsonSchema).Tasks.Single();

        var result = service.BuildTaskInput(form, taskTemplate, "{\"path1\":\"c:\\\\apps\"}");

        Assert.True(result.Success);
        Assert.Equal("c:\\apps", result.InputNode["Path"]?.GetValue<string>());
    }

    [Fact]
    public void BuildTaskInput_Fails_WhenRequiredPredefinedMappedFieldIsEmpty()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                {
                  "key": "path1",
                  "label": "Path",
                  "type": "predefined",
                  "required": true,
                  "predefinedOptions": ["c:\\", "c:\\apps"]
                }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Run script",
                  "type": "automation",
                  "payloadMapping": {
                    "Path": "path1"
                  }
                }
              ]
            }
            """);

        var taskTemplate = parser.Parse(form.JsonSchema).Tasks.Single();

        var result = service.BuildTaskInput(form, taskTemplate, "{\"path1\":\"\"}");

        Assert.False(result.Success);
        Assert.Contains("path1", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildTaskInput_Fails_WhenMappingsResolveNoOrchestrationInputs()
    {
        var parser = new RequestFormSchemaParser();
        var bindings = Substitute.For<IAutomationBindingService>();
        var service = new AutomationBindingPayloadContractService(parser, bindings);
        var form = CreateRequestForm(
            """
            {
              "fields": [
                { "key": "path", "label": "Path", "type": "text", "required": false }
              ],
              "tasks": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "name": "Run script",
                  "type": "automation",
                  "payloadMapping": {
                    "Path": "path"
                  }
                }
              ]
            }
            """);

        var taskTemplate = parser.Parse(form.JsonSchema).Tasks.Single();

        var result = service.BuildTaskInput(form, taskTemplate, "{}");

        Assert.False(result.Success);
        Assert.Contains("Run script", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static RequestForm CreateRequestForm(string schemaJson)
    {
        return new RequestForm
        {
            Id = "form-1",
            Title = "Automation Request",
            JsonSchema = JsonDocument.Parse(schemaJson)
        };
    }
}
