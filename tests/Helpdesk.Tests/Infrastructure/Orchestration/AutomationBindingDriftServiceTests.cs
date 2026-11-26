using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Pairing;
using NSubstitute;
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
                new FormField { Key = "path1", Label = "Path", Type = "text", Required = true },
                new FormField { Key = "path2", Label = "Path", Type = "text", Required = true }
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
                        new OrchestrationCatalogInputDefinitionDto { Key = "Path", Label = "Path", Type = "text", Required = true, Order = 0 }
                    ]
                }
            ]),
            new RequestFormSchemaParser());

        var refreshed = await sut.RefreshAsync(requestForm.Id);

        Assert.Equal(2, refreshed.Count);
        Assert.All(refreshed, binding => Assert.Equal(AutomationBindingSyncState.InSync, binding.SyncState));
    }

    [Theory]
    [InlineData("text", "string", true, true, "Unchanged")]
    [InlineData("text", " STRING ", true, true, "Unchanged")]
    [InlineData("string", "text", false, false, "Unchanged")]
    [InlineData("helpdeskData", "string", true, true, "Unchanged")]
    [InlineData("predefined", "text", true, true, "Unchanged")]
    [InlineData("text", "string", false, true, "Modified")]
    [InlineData("text", "number", true, true, "Modified")]
    public async Task PreviewAndRefresh_NormalizeTextAliasesWithoutHidingSemanticChanges(
        string localType, string remoteType, bool localRequired, bool remoteRequired, string expectedChange)
    {
        await using var db = CreateDb();
        var form = CreateRequestForm(
            [new FormField { Key = "input", Label = "Input", Type = localType, Required = localRequired }],
            new Dictionary<string, string> { ["input"] = "input" });
        var binding = CreateBinding("text-alias-binding", form.Id, TaskTemplateId);
        binding.SyncState = AutomationBindingSyncState.Drifted;
        db.RequestForms.Add(form);
        db.AutomationBindings.Add(binding);
        await db.SaveChangesAsync();
        var sut = new AutomationBindingDriftService(
            db, new TestTenantContext(),
            new StubOrchestrationCatalogService([
                new OrchestrationCatalogRequestDefinitionDto
                {
                    RequestDefinitionId = binding.OrchestrationRequestDefinitionId,
                    RequestDefinitionName = "Text input job",
                    Inputs = [new OrchestrationCatalogInputDefinitionDto
                    {
                        Key = "input", Label = "Input", Type = remoteType, Required = remoteRequired, Order = 0
                    }]
                }
            ]), new RequestFormSchemaParser());

        var preview = await sut.PreviewAsync(binding.Id);
        Assert.Equal(expectedChange, Assert.Single(preview.Differences).ChangeType);
        var refreshed = Assert.Single(await sut.RefreshAsync(form.Id));
        Assert.Equal(expectedChange == "Unchanged"
            ? AutomationBindingSyncState.InSync
            : AutomationBindingSyncState.Drifted, refreshed.SyncState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_push_and_import_preserve_fields_and_require_a_fresh_snapshot(bool uncertainWrite)
    {
        await using var db = CreateDb();
        var form = CreateRequestForm(
            [
                new() { Key = "operation", Label = "Action", Type = "predefined", Required = true, PredefinedOptions = ["Add", "Remove"] },
                new() { Key = "groups_internal", Label = "Old groups", Type = "text", MultiSelect = true, DataBinding = new() { DatasetId = "synthetic-groups" } },
                new() { Key = "requester", Label = "User", Type = "text" },
                new() { Key = "untouched", Label = "Untouched", Type = "text" }
            ],
            new Dictionary<string, string> { ["Action"] = "operation", ["adgroups"] = "groups_internal", ["User"] = "requester", ["Untouched"] = "untouched" });
        var connection = new SystemConnection { Id = Guid.NewGuid(), State = "connected" };
        var binding = CreateBinding("review", form.Id, TaskTemplateId);
        binding.SystemConnectionId = connection.Id;
        db.AddRange(form, connection, binding);
        await db.SaveChangesAsync();

        var target = new OrchestrationCatalogRequestDefinitionDto
        {
            RequestDefinitionId = binding.OrchestrationRequestDefinitionId,
            OrchestrationJobDefinitionId = binding.OrchestrationJobDefinitionId,
            Inputs =
            [
                new() { Key = "Action", Label = "Action", Order = 1, Type = "Text" },
                new() { Key = "ADGroups", Label = "Groups", Order = 2, Type = "text", Required = true },
                new() { Key = "User", Label = "User", Order = 3, Type = "text", Required = true },
                new() { Key = "Untouched", Label = "Untouched", Order = 4, Type = "text", DefaultValue = "keep", HelpText = "owned by peer" }
            ]
        };
        var catalog = Substitute.For<IOrchestrationCatalogService>();
        catalog.ListRequestDefinitionsAsync(binding.OrganizationId, connection.Id, Arg.Any<CancellationToken>()).Returns([target]);
        catalog.ApplyReviewedInputsAsync(target.RequestDefinitionId, Arg.Any<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(),
            Arg.Any<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(), binding.OrganizationId, connection.Id, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Same(target.Inputs, call.ArgAt<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(1));
                target.Inputs = call.ArgAt<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(2);
                return uncertainWrite ? Task.FromException<OrchestrationCatalogRequestDefinitionDto>(new HttpRequestException())
                    : Task.FromResult(target);
            });
        var parser = new RequestFormSchemaParser();
        var sut = new AutomationBindingDriftService(db, new TestTenantContext(), catalog, parser);
        var preview = await sut.PreviewAsync(binding.Id);
        Assert.Equal(3, preview.Differences.Count(row => row.ChangeType == "Modified"));
        var initial = new ApplyAutomationInputReviewDto(preview.ReviewVersion!,
            [new("Action", AutomationInputDirection.Push, null, false),
             new("ADGroups", AutomationInputDirection.Import, "GROUPS_INTERNAL", false),
             new("User", AutomationInputDirection.LeaveUnresolved, null, false)]);

        var applied = await sut.ApplyReviewAsync(binding.Id, initial);

        Assert.False(applied.Synced);
        Assert.Equal(!uncertainWrite, applied.HelpdeskChanged);
        Assert.Equal(3, applied.Outcomes.Count);
        Assert.True(target.Inputs.Single(input => input.Key == "Action").Required);
        Assert.Equal("keep", target.Inputs.Single(input => input.Key == "Untouched").DefaultValue);
        Assert.Equal("owned by peer", target.Inputs.Single(input => input.Key == "Untouched").HelpText);
        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => sut.ApplyReviewAsync(binding.Id, initial));
        var rechecked = await sut.PreviewAsync(binding.Id);
        Assert.Equal(uncertainWrite ? 2 : 1, rechecked.Differences.Count(row => row.ChangeType != "Unchanged"));
        var final = new ApplyAutomationInputReviewDto(rechecked.ReviewVersion!, uncertainWrite
            ? [new("ADGroups", AutomationInputDirection.Import, "groups_internal", false), new("User", AutomationInputDirection.Import, "requester", false)]
            : [new("User", AutomationInputDirection.Import, "requester", false)]);
        Assert.True((await sut.ApplyReviewAsync(binding.Id, final)).Synced);
        var reopened = await sut.PreviewAsync(binding.Id);
        Assert.All(reopened.Differences, row => Assert.Equal("Unchanged", row.ChangeType));
        var persisted = parser.Parse((await db.RequestForms.AsNoTracking().SingleAsync()).JsonSchema);
        Assert.Equal(4, persisted.Fields.Count);
        Assert.Equal(["Add", "Remove"], persisted.Fields.Single(field => field.Key == "operation").PredefinedOptions);
        var groups = persisted.Fields.Single(field => field.Key == "groups_internal");
        Assert.Equal("Groups", groups.Label);
        Assert.True(groups.MultiSelect);
        Assert.Equal("synthetic-groups", groups.DataBinding!.DatasetId);
        Assert.Equal("groups_internal", Assert.Single(persisted.Tasks).PayloadMapping!["ADGroups"]);
    }

    [Theory]
    [InlineData(" ADGroups ", null, true)]
    [InlineData("ADGroups", null, false)]
    [InlineData("removed_key", "groups_internal", true)]
    [InlineData(null, "groups_internal", true)]
    public async Task Reviewed_push_saves_existing_dataset_field_mapping_without_importing(
        string? savedReference, string? selectedField, bool peerRequiresGroups)
    {
        await using var db = CreateDb();
        var mapping = new Dictionary<string, string> { ["Action"] = "operation", ["User"] = "directory_user" };
        if (savedReference is not null) mapping["ADGroups"] = savedReference;
        var form = CreateRequestForm(
            [
                new() { Key = "operation", Label = "Action", Type = "predefined", Required = true, PredefinedOptions = ["Add", "Remove"] },
                new() { Key = "directory_user", Label = "User", Type = "helpdeskData", DataBinding = new() { DatasetId = "synthetic-users" } },
                new() { Key = "groups_internal", Label = "AD Groups", Type = "helpdeskData", MultiSelect = true, DataBinding = new() { DatasetId = "synthetic-groups" } },
                new() { Key = "justification", Label = "Business justification", Type = "text" }
            ],
            mapping);
        var originalFields = form.JsonSchema.RootElement.GetProperty("fields").Clone();
        var connection = new SystemConnection { Id = Guid.NewGuid(), State = "connected" };
        var binding = CreateBinding("legacy-groups-review", form.Id, TaskTemplateId);
        binding.SystemConnectionId = connection.Id;
        binding.SyncState = AutomationBindingSyncState.ImportPending;
        db.AddRange(form, connection, binding);
        await db.SaveChangesAsync();

        var action = new OrchestrationCatalogInputDefinitionDto { Key = "Action", Label = "Action", Order = 1, Type = "text", Required = true };
        var user = new OrchestrationCatalogInputDefinitionDto { Key = "User", Label = "User", Order = 2, Type = "text", DefaultValue = "keep" };
        var groups = new OrchestrationCatalogInputDefinitionDto
        {
            Key = "ADGroups", Label = "AD Groups", Order = 3, Type = "text", Required = peerRequiresGroups, HelpText = "Keep peer metadata"
        };
        var target = new OrchestrationCatalogRequestDefinitionDto
        {
            RequestDefinitionId = binding.OrchestrationRequestDefinitionId,
            OrchestrationJobDefinitionId = binding.OrchestrationJobDefinitionId,
            Inputs = [action, user, groups]
        };
        var catalog = Substitute.For<IOrchestrationCatalogService>();
        catalog.ListRequestDefinitionsAsync(binding.OrganizationId, connection.Id, Arg.Any<CancellationToken>()).Returns([target]);
        catalog.ApplyReviewedInputsAsync(target.RequestDefinitionId, Arg.Any<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(),
            Arg.Any<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(), binding.OrganizationId, connection.Id, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Same(target.Inputs, call.ArgAt<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(1));
                target.Inputs = call.ArgAt<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(2);
                return Task.FromResult(target);
            });
        var parser = new RequestFormSchemaParser();
        var service = new AutomationBindingDriftService(db, new TestTenantContext(), catalog, parser);

        var preview = await service.PreviewAsync(binding.Id);
        var difference = Assert.Single(preview.Differences, row => row.ChangeType != "Unchanged");
        Assert.Equal("ADGroups", difference.Key);
        if (selectedField is null)
        {
            Assert.Equal("MappingNeedsUpdate", difference.ChangeType);
            Assert.Equal("groups_internal", difference.Helpdesk!.FieldKey);
            Assert.True(difference.Helpdesk.MultiSelect);
            Assert.True(difference.Helpdesk.UsesDataset);
        }
        var result = await service.ApplyReviewAsync(binding.Id, new(preview.ReviewVersion!,
            [new("ADGroups", AutomationInputDirection.Push, selectedField, false)]));

        Assert.True(result.Synced);
        Assert.True(result.HelpdeskChanged);
        Assert.Contains("Helpdesk mapping", Assert.Single(result.Outcomes).Message);
        Assert.Collection(target.Inputs, input => Assert.Same(action, input), input => Assert.Same(user, input), input =>
        {
            Assert.Equal("ADGroups", input.Key);
            Assert.Equal("text", input.Type);
            Assert.Equal(groups.HelpText, input.HelpText);
            Assert.False(input.Required);
        });
        var persisted = (await db.RequestForms.AsNoTracking().SingleAsync()).JsonSchema;
        Assert.True(JsonElement.DeepEquals(originalFields, persisted.RootElement.GetProperty("fields")));
        mapping["ADGroups"] = "groups_internal";
        var persistedMapping = Assert.Single(parser.Parse(persisted).Tasks).PayloadMapping!;
        Assert.Equal(mapping.OrderBy(pair => pair.Key), persistedMapping.OrderBy(pair => pair.Key));
        Assert.All((await service.PreviewAsync(binding.Id)).Differences, row => Assert.Equal("Unchanged", row.ChangeType));
        Assert.Equal(AutomationBindingSyncState.InSync, Assert.Single(await service.RefreshAsync(form.Id)).SyncState);
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
