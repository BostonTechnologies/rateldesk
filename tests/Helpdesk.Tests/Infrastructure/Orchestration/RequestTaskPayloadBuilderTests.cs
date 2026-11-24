using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class RequestTaskPayloadBuilderTests
{
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "step1", 1, "path1")]
    [InlineData("22222222-2222-2222-2222-222222222222", "step2", 2, "path2")]
    public async Task BuildAsync_MapsPerStepHelpdeskField_ToSharedOrchestrationJobParam(
        string templateId,
        string taskName,
        int order,
        string sourceFieldKey)
    {
        var requestRepo = new InMemoryRepository<Request>();
        var formRepo = new InMemoryRepository<RequestForm>();
        var organizationRepo = new InMemoryRepository<Organization>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var parser = new RequestFormSchemaParser();
        var payloadContract = new AutomationBindingPayloadContractService(parser, bindings);
        var builder = new RequestTaskPayloadBuilder(
            requestRepo,
            formRepo,
            organizationRepo,
            parser,
            bindings,
            payloadContract);

        var templateGuid = Guid.Parse(templateId);
        await requestRepo.CreateAsync(new Request
        {
            Id = "request-1",
            RequestFormId = "form-1",
            PayloadJson = """{"path1":"c:\\","path2":"c:\\"}""",
            TrackingId = "REQ-123"
        });
        await formRepo.CreateAsync(CreateRequestForm());

        bindings.GetByTaskTemplateAsync("form-1", templateGuid, Arg.Any<CancellationToken>())
            .Returns(new AutomationBindingDto
            {
                Id = $"binding-{order}",
                RequestFormId = "form-1",
                TaskTemplateId = templateGuid,
                OrchestrationRequestDefinitionId = "14",
                OrchestrationRequestDefinitionName = "Get-Childitem pathparam",
                OrchestrationJobDefinitionId = "14",
                OrchestrationJobDefinitionName = "Get-Childitem pathparam",
                Enabled = true,
                SyncState = AutomationBindingSyncState.InSync
            });

        var task = new RequestTask
        {
            Id = $"task-{order}",
            RequestId = "request-1",
            TemplateId = templateId,
            Name = taskName,
            Title = taskName,
            Order = order,
            Type = RequestTaskType.Automation
        };

        var result = await builder.BuildAsync(task, "corr-test");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Get-Childitem pathparam", result.JobName);

        var root = JsonNode.Parse(result.PayloadJson)!.AsObject();
        Assert.Equal("c:\\", root["input"]!["Path"]!.GetValue<string>());
        Assert.Equal("request-1", root["meta"]!["requestId"]!.GetValue<string>());
        Assert.Equal($"task-{order}", root["meta"]!["taskId"]!.GetValue<string>());
        Assert.Equal($"binding-{order}", root["meta"]!["automationBindingId"]!.GetValue<string>());
        Assert.Equal(sourceFieldKey, parser.Parse(CreateRequestForm().JsonSchema).Tasks.Single(x => x.Id == templateGuid).PayloadMapping!["Path"]);
    }

    [Fact]
    public async Task Retained_dispatch_uses_its_original_bytes_after_form_edits()
    {
        var requests = new InMemoryRepository<Request>();
        var forms = new InMemoryRepository<RequestForm>();
        var bindings = Substitute.For<IAutomationBindingService>();
        var inputs = Substitute.For<IAutomationBindingPayloadContractService>();
        var parser = new RequestFormSchemaParser();
        var templateId = Guid.NewGuid();
        await requests.CreateAsync(new Request { Id = "request-1", RequestFormId = "form-1", PayloadJson = """{"groups":["Fleet"]}""" });
        await forms.CreateAsync(new RequestForm { Id = "form-1" });
        bindings.GetByTaskTemplateAsync("form-1", templateId, Arg.Any<CancellationToken>())
            .Returns(new AutomationBindingDto { Enabled = true, Id = "binding", OrchestrationRequestDefinitionId = "14" });
        var builder = new RequestTaskPayloadBuilder(requests, forms, new InMemoryRepository<Organization>(), parser, bindings, inputs);
        const string retained = """{ "meta": {}, "input": {"groupList":"[\"Fleet\"]"} }""";

        var result = await builder.BuildAsync(new RequestTask
        {
            Id = "task-1", RequestId = "request-1", TemplateId = templateId.ToString(), AutomationDispatchPayloadJson = retained
        }, "corr-test");

        Assert.True(result.Success, result.Error);
        Assert.Equal(retained, result.PayloadJson);
        Assert.Empty(inputs.ReceivedCalls());
    }

    private static RequestForm CreateRequestForm()
    {
        var schema = new
        {
            title = "Test power shell param - steps (2)",
            fields = new[]
            {
                new FormField { Key = "path1", Label = "path1", Type = "text", Required = true },
                new FormField { Key = "path2", Label = "path2", Type = "text", Required = true }
            },
            tasks = new[]
            {
                new
                {
                    id = "11111111-1111-1111-1111-111111111111",
                    name = "step1",
                    type = "automation",
                    order = 1,
                    autoStart = true,
                    payloadMapping = new Dictionary<string, string> { ["Path"] = "path1" }
                },
                new
                {
                    id = "22222222-2222-2222-2222-222222222222",
                    name = "step2",
                    type = "automation",
                    order = 2,
                    autoStart = true,
                    payloadMapping = new Dictionary<string, string> { ["Path"] = "path2" }
                }
            }
        };

        return new RequestForm
        {
            Id = "form-1",
            Title = "Test power shell param - steps (2)",
            JsonSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema))
        };
    }
}
