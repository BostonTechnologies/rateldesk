using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class AutomationBindingDriftServiceTests
{
    [Fact]
    public async Task PreviewAsync_ShowsAddedInOrchestration_WhenTaskPayloadMappingsAreMissing()
    {
        await using var db = CreateDb();
        var requestForm = CreateRequestForm(
            fields:
            [
                new FormField { Key = "path1", Label = "path1", Type = "text", Required = true },
                new FormField { Key = "path2", Label = "path2", Type = "text", Required = true }
            ],
            payloadMapping: null);

        var binding = new AutomationBinding
        {
            Id = "binding-1",
            OrganizationId = "org-1",
            RequestFormId = requestForm.Id,
            TaskTemplateId = TaskTemplateId,
            OrchestrationRequestDefinitionId = "orchestration-req-1",
            Enabled = true
        };

        db.RequestForms.Add(requestForm);
        db.AutomationBindings.Add(binding);
        await db.SaveChangesAsync();

        var sut = new AutomationBindingDriftService(
            db,
            new TestTenantContext(),
            new StubOrchestrationCatalogService([
                new OrchestrationCatalogRequestDefinitionDto
                {
                    RequestDefinitionId = "orchestration-req-1",
                    RequestDefinitionName = "LS Folder",
                    Inputs =
                    [
                        new OrchestrationCatalogInputDefinitionDto { Key = "path1", Label = "path1", Type = "text", Required = true, Order = 0 },
                        new OrchestrationCatalogInputDefinitionDto { Key = "path2", Label = "path2", Type = "text", Required = true, Order = 1 }
                    ]
                }
            ]),
            new RequestFormSchemaParser());

        var preview = await sut.PreviewAsync(binding.Id);

        Assert.Equal(2, preview.Differences.Count);
        Assert.All(preview.Differences, diff => Assert.Equal("AddedInOrchestration", diff.ChangeType));
        Assert.Contains(preview.Differences, diff => diff.Key == "path1");
        Assert.Contains(preview.Differences, diff => diff.Key == "path2");
    }

    [Fact]
    public async Task RefreshAsync_MarksBindingInSync_WhenSavedRequestFormMatchesOrchestrationInputs()
    {
        await using var db = CreateDb();
        var requestForm = CreateRequestForm(
            fields:
            [
                new FormField { Key = "path1", Label = "path1", Type = "text", Required = true },
                new FormField { Key = "path2", Label = "path2", Type = "text", Required = true }
            ],
            payloadMapping: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["path1"] = "path1",
                ["path2"] = "path2"
            });

        var binding = new AutomationBinding
        {
            Id = "binding-2",
            OrganizationId = "org-1",
            RequestFormId = requestForm.Id,
            TaskTemplateId = TaskTemplateId,
            OrchestrationRequestDefinitionId = "orchestration-req-1",
            Enabled = true,
            SyncState = AutomationBindingSyncState.Drifted,
            LastSyncHash = "stale-hash"
        };

        db.RequestForms.Add(requestForm);
        db.AutomationBindings.Add(binding);
        await db.SaveChangesAsync();

        var sut = new AutomationBindingDriftService(
            db,
            new TestTenantContext(),
            new StubOrchestrationCatalogService([
                new OrchestrationCatalogRequestDefinitionDto
                {
                    RequestDefinitionId = "orchestration-req-1",
                    RequestDefinitionName = "LS Folder",
                    Inputs =
                    [
                        new OrchestrationCatalogInputDefinitionDto { Key = "path1", Label = "path1", Type = "text", Required = true, Order = 0 },
                        new OrchestrationCatalogInputDefinitionDto { Key = "path2", Label = "path2", Type = "text", Required = true, Order = 1 }
                    ]
                }
            ]),
            new RequestFormSchemaParser());

        var refreshed = await sut.RefreshAsync(requestForm.Id);

        var updated = Assert.Single(refreshed);
        Assert.Equal(AutomationBindingSyncState.InSync, updated.SyncState);
        Assert.NotEqual("stale-hash", updated.LastSyncHash);
    }

    [Fact]
    public async Task RefreshAsync_TreatsHelpdeskDataAsText_WhenComparingOrchestrationInputs()
    {
        await using var db = CreateDb();
        var requestForm = CreateRequestForm(
            fields:
            [
                new FormField { Key = "location", Label = "Location", Type = "helpdeskData", Required = true }
            ],
            payloadMapping: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["locationKey"] = "location"
            });

        var binding = new AutomationBinding
        {
            Id = "binding-3",
            OrganizationId = "org-1",
            RequestFormId = requestForm.Id,
            TaskTemplateId = TaskTemplateId,
            OrchestrationRequestDefinitionId = "orchestration-req-1",
            Enabled = true,
            SyncState = AutomationBindingSyncState.Drifted
        };

        db.RequestForms.Add(requestForm);
        db.AutomationBindings.Add(binding);
        await db.SaveChangesAsync();

        var sut = new AutomationBindingDriftService(
            db,
            new TestTenantContext(),
            new StubOrchestrationCatalogService([
                new OrchestrationCatalogRequestDefinitionDto
                {
                    RequestDefinitionId = "orchestration-req-1",
                    RequestDefinitionName = "Location Job",
                    Inputs =
                    [
                        new OrchestrationCatalogInputDefinitionDto { Key = "locationKey", Label = "Location", Type = "text", Required = true, Order = 0 }
                    ]
                }
            ]),
            new RequestFormSchemaParser());

        var refreshed = await sut.RefreshAsync(requestForm.Id);

        var updated = Assert.Single(refreshed);
        Assert.Equal(AutomationBindingSyncState.InSync, updated.SyncState);
    }

    [Fact]
    public async Task RefreshAsync_MarksSharedOrchestrationJobBindingsInSync_WhenSameParamMapsToDifferentFields()
    {
        await using var db = CreateDb();
        var firstTaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondTaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var requestForm = CreateRequestForm(
            fields:
            [
                new FormField { Key = "path1", Label = "path1", Type = "text", Required = true },
                new FormField { Key = "path2", Label = "path2", Type = "text", Required = true }
            ],
            tasks:
            [
                CreateTask(firstTaskId, "step1", 1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Path"] = "path1"
                }),
                CreateTask(secondTaskId, "step2", 2, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Path"] = "path2"
                })
            ]);

        db.RequestForms.Add(requestForm);
        db.AutomationBindings.AddRange(
            CreateBinding("binding-step1", requestForm.Id, firstTaskId),
            CreateBinding("binding-step2", requestForm.Id, secondTaskId));
        await db.SaveChangesAsync();

        var sut = new AutomationBindingDriftService(
            db,
            new TestTenantContext(),
            new StubOrchestrationCatalogService([
                new OrchestrationCatalogRequestDefinitionDto
                {
                    RequestDefinitionId = "orchestration-req-shared",
                    RequestDefinitionName = "Get-Childitem pathparam",
                    Inputs =
                    [
                        new OrchestrationCatalogInputDefinitionDto { Key = "Path", Label = "path1", Type = "text", Required = true, Order = 0 }
                    ]
                }
            ]),
            new RequestFormSchemaParser());

        var refreshed = await sut.RefreshAsync(requestForm.Id);

        Assert.Equal(2, refreshed.Count);
        Assert.All(refreshed, binding => Assert.Equal(AutomationBindingSyncState.InSync, binding.SyncState));
    }

    private static readonly Guid TaskTemplateId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static HelpdeskDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        var db = new HelpdeskDbContext(options, new TestTenantContext(), new HttpContextAccessor());
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = "org-1", Name = "Synthetic organization" });
        db.SaveChanges();
        return db;
    }

    private static RequestForm CreateRequestForm(IReadOnlyList<FormField> fields, Dictionary<string, string>? payloadMapping)
    {
        var schema = new
        {
            title = "Request",
            fields,
            tasks = new[]
            {
                new
                {
                    id = TaskTemplateId.ToString("D"),
                    name = "Automation Task",
                    type = "automation",
                    order = 1,
                    payloadMapping
                }
            }
        };

        return new RequestForm
        {
            Id = "form-1",
            Title = "Request",
            ServiceId = "service-1",
            OrganizationId = "org-1",
            JsonSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema))
        };
    }

    private static RequestForm CreateRequestForm(IReadOnlyList<FormField> fields, IReadOnlyList<RequestTaskTemplateModel> tasks)
    {
        var schema = new
        {
            title = "Request",
            fields,
            tasks = tasks.Select(task => new
            {
                id = task.Id.ToString("D"),
                name = task.Name,
                type = task.Type,
                order = task.Order,
                payloadMapping = task.PayloadMapping
            })
        };

        return new RequestForm
        {
            Id = "form-1",
            Title = "Request",
            ServiceId = "service-1",
            OrganizationId = "org-1",
            JsonSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema))
        };
    }

    private static RequestTaskTemplateModel CreateTask(
        Guid id,
        string name,
        int order,
        Dictionary<string, string>? payloadMapping)
        => new()
        {
            Id = id,
            Name = name,
            Type = "automation",
            Order = order,
            PayloadMapping = payloadMapping
        };

    private static AutomationBinding CreateBinding(string id, string requestFormId, Guid taskTemplateId)
        => new()
        {
            Id = id,
            OrganizationId = "org-1",
            RequestFormId = requestFormId,
            TaskTemplateId = taskTemplateId,
            OrchestrationRequestDefinitionId = "orchestration-req-shared",
            OrchestrationRequestDefinitionName = "Get-Childitem pathparam",
            OrchestrationJobDefinitionId = "orchestration-job-shared",
            OrchestrationJobDefinitionName = "Get-Childitem pathparam",
            Enabled = true,
            SyncState = AutomationBindingSyncState.Drifted
        };

    private sealed class StubOrchestrationCatalogService(IReadOnlyList<OrchestrationCatalogRequestDefinitionDto> requestDefinitions) : IOrchestrationCatalogService
    {
        public Task<IReadOnlyList<OrchestrationCatalogJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OrchestrationCatalogJobDto>>([]);

        public Task<IReadOnlyList<OrchestrationCatalogTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OrchestrationCatalogTenantDto>>([]);

        public Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(requestDefinitions);

        public Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(string? organizationId, Guid? connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("org-1", organizationId);
            Assert.Null(connectionId);
            return Task.FromResult(requestDefinitions);
        }

        public Task<OrchestrationCatalogRequestDefinitionDto> CreateRequestDefinitionAsync(CreateOrchestrationCatalogRequestDefinitionDto request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OrchestrationCatalogRequestDefinitionDto> SyncRequestDefinitionInputsAsync(string requestDefinitionId, IReadOnlyList<OrchestrationCatalogInputDefinitionDto> inputs, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
