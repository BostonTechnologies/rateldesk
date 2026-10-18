using System.Text.Json;
using Helpdesk.Application.Events;
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

public sealed class AutomationBindingSchemaSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_UsesMappedInputsFromSiblingSteps_WhenSharingOrchestrationRequestDefinition()
    {
        await using var db = CreateDb();
        var requestForm = CreateRequestForm(
            fields:
            [
                new FormField { Key = "step1", Label = "step1", Type = "text", Required = true },
                new FormField { Key = "step2", Label = "step2", Type = "text", Required = true }
            ],
            tasks:
            [
                CreateTask(TaskOneId, "step1", 1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Path"] = "step1"
                }),
                CreateTask(TaskTwoId, "step2", 2, payloadMapping: null)
            ]);

        db.RequestForms.Add(requestForm);
        db.AutomationBindings.AddRange(
            CreateBinding("binding-1", requestForm.Id, TaskOneId),
            CreateBinding("binding-2", requestForm.Id, TaskTwoId));
        await db.SaveChangesAsync();

        var catalog = new CapturingOrchestrationCatalogService();
        var sut = CreateSut(db, catalog);

        await sut.SyncAsync("binding-2");

        Assert.Equal(1, catalog.SyncCallCount);
        Assert.Equal(SharedOrchestrationRequestDefinitionId, catalog.LastRequestDefinitionId);
        var input = Assert.Single(catalog.LastInputs!);
        Assert.Equal("Path", input.Key);
        Assert.Equal("step1", input.Label);
    }

    [Fact]
    public async Task SyncAsync_DoesNotSendEmptyInputList_WhenSharedOrchestrationRequestDefinitionHasNoMappings()
    {
        await using var db = CreateDb();
        var requestForm = CreateRequestForm(
            fields:
            [
                new FormField { Key = "step1", Label = "step1", Type = "text", Required = true },
                new FormField { Key = "step2", Label = "step2", Type = "text", Required = true }
            ],
            tasks:
            [
                CreateTask(TaskOneId, "step1", 1, payloadMapping: null),
                CreateTask(TaskTwoId, "step2", 2, payloadMapping: null)
            ]);

        db.RequestForms.Add(requestForm);
        db.AutomationBindings.AddRange(
            CreateBinding("binding-1", requestForm.Id, TaskOneId),
            CreateBinding("binding-2", requestForm.Id, TaskTwoId));
        await db.SaveChangesAsync();

        var catalog = new CapturingOrchestrationCatalogService();
        var sut = CreateSut(db, catalog);

        var result = await sut.SyncAsync("binding-2");

        Assert.Equal(0, catalog.SyncCallCount);
        Assert.Equal(AutomationBindingSyncState.InSync, result.SyncState);

        var binding = await db.AutomationBindings.SingleAsync(x => x.Id == "binding-2");
        Assert.Equal(AutomationBindingSyncState.InSync, binding.SyncState);
        Assert.NotNull(binding.LastSyncHash);
        Assert.NotNull(binding.LastSyncedAtUtc);
    }

    private static readonly Guid TaskOneId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string SharedOrchestrationRequestDefinitionId = "orchestration-req-shared";

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

    private static AutomationBindingSchemaSyncService CreateSut(
        HelpdeskDbContext db,
        IOrchestrationCatalogService catalog)
        => new(
            db,
            new TestTenantContext(),
            new RequestFormSchemaParser(),
            catalog,
            new TestCorrelationContext());

    private static RequestForm CreateRequestForm(
        IReadOnlyList<FormField> fields,
        IReadOnlyList<RequestTaskTemplateModel> tasks)
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
                autoStart = task.AutoStart,
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
            AutoStart = true,
            PayloadMapping = payloadMapping
        };

    private static AutomationBinding CreateBinding(string id, string requestFormId, Guid taskTemplateId)
        => new()
        {
            Id = id,
            OrganizationId = "org-1",
            RequestFormId = requestFormId,
            TaskTemplateId = taskTemplateId,
            OrchestrationRequestDefinitionId = SharedOrchestrationRequestDefinitionId,
            OrchestrationRequestDefinitionName = "Shared Job",
            OrchestrationJobDefinitionId = "orchestration-job-shared",
            OrchestrationJobDefinitionName = "Shared Job",
            Enabled = true,
            SyncState = AutomationBindingSyncState.Drifted
        };

    private sealed class CapturingOrchestrationCatalogService : IOrchestrationCatalogService
    {
        public int SyncCallCount { get; private set; }
        public string? LastRequestDefinitionId { get; private set; }
        public IReadOnlyList<OrchestrationCatalogInputDefinitionDto>? LastInputs { get; private set; }

        public Task<IReadOnlyList<OrchestrationCatalogJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OrchestrationCatalogJobDto>>([]);

        public Task<IReadOnlyList<OrchestrationCatalogTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OrchestrationCatalogTenantDto>>([]);

        public Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>>([]);

        public Task<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>> ListRequestDefinitionsAsync(string? organizationId, Guid? connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("org-1", organizationId);
            Assert.Null(connectionId);
            return Task.FromResult<IReadOnlyList<OrchestrationCatalogRequestDefinitionDto>>([new() { RequestDefinitionId = SharedOrchestrationRequestDefinitionId }]);
        }

        public Task<OrchestrationCatalogRequestDefinitionDto> CreateRequestDefinitionAsync(CreateOrchestrationCatalogRequestDefinitionDto request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OrchestrationCatalogRequestDefinitionDto> SyncRequestDefinitionInputsAsync(
            string requestDefinitionId,
            IReadOnlyList<OrchestrationCatalogInputDefinitionDto> inputs,
            string? organizationId,
            Guid? connectionId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("org-1", organizationId);
            Assert.Null(connectionId);
            return SyncRequestDefinitionInputsAsync(requestDefinitionId, inputs, cancellationToken);
        }

        public Task<OrchestrationCatalogRequestDefinitionDto> SyncRequestDefinitionInputsAsync(
            string requestDefinitionId,
            IReadOnlyList<OrchestrationCatalogInputDefinitionDto> inputs,
            CancellationToken cancellationToken = default)
        {
            SyncCallCount++;
            LastRequestDefinitionId = requestDefinitionId;
            LastInputs = inputs;
            return Task.FromResult(new OrchestrationCatalogRequestDefinitionDto
            {
                RequestDefinitionId = requestDefinitionId,
                RequestDefinitionName = "Shared Job",
                OrchestrationJobDefinitionId = "orchestration-job-shared",
                OrchestrationJobDefinitionName = "Shared Job",
                Inputs = inputs
            });
        }
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string GetCorrelationId() => "corr-schema-sync-tests";
    }
}
