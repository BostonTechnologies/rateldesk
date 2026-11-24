using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class AutomationBindingServiceTests
{
    [Fact]
    public async Task Self_service_runtime_reads_only_bindings_for_the_authorized_submission_organization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
        var tenant = new TestTenantContext();
        await using var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();

        db.RequestForms.AddRange(
            new RequestForm { Id = "form-1", OrganizationId = "org-1", Title = "Home form" },
            new RequestForm { Id = "form-2", OrganizationId = "org-2", Title = "Secondary form" });
        var templateId = Guid.NewGuid();
        var home = CreateBinding("home-binding", templateId, DateTimeOffset.UtcNow);
        var secondary = CreateBinding("secondary-binding", templateId, DateTimeOffset.UtcNow);
        secondary.OrganizationId = "org-2";
        secondary.RequestFormId = "form-2";
        secondary.SystemConnectionId = Guid.NewGuid();
        db.AutomationBindings.AddRange(home, secondary);
        await db.SaveChangesAsync();
        var service = new AutomationBindingService(db, tenant, new RequestFormSchemaParser());

        Assert.Null(await service.GetByTaskTemplateAsync(secondary.RequestFormId, templateId));
        using (db.BindSelfServiceOrganization(secondary.OrganizationId))
        {
            var resolved = await service.GetByTaskTemplateAsync(secondary.RequestFormId, templateId);
            Assert.NotNull(resolved);
            Assert.Equal(secondary.SystemConnectionId, resolved.SystemConnectionId);
            Assert.Equal(secondary.OrganizationId, resolved.OrganizationId);
            Assert.Null(await service.GetByTaskTemplateAsync(home.RequestFormId, templateId));
        }
        Assert.Equal(home.Id, (await service.GetByTaskTemplateAsync(home.RequestFormId, templateId))?.Id);
        Assert.Null(await service.GetByTaskTemplateAsync(secondary.RequestFormId, templateId));
    }

    [Theory]
    [InlineData(false, "org-1", 2)]
    [InlineData(true, "different-home-org", 2)]
    [InlineData(false, "foreign-org", 0)]
    public async Task Binding_reads_and_saves_honor_instance_administrators_and_preserve_tenant_isolation(bool admin, string homeOrganization, int count)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(connection)
            .Options;
        var tenant = new TestTenantContext(admin, homeOrganization);
        await using var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();

        db.RequestForms.Add(new RequestForm
        {
            Id = "form-1",
            OrganizationId = "org-1",
            Title = "Request form"
        });
        db.AutomationBindings.AddRange(
            CreateBinding(
                "binding-new",
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                DateTimeOffset.Parse("2026-01-02T00:00:00+00:00")),
            CreateBinding(
                "binding-old",
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                DateTimeOffset.Parse("2026-01-01T00:00:00+00:00")));
        await db.SaveChangesAsync();

        var service = new AutomationBindingService(db, tenant, new RequestFormSchemaParser());

        var bindings = await service.ListAsync("form-1");

        Assert.Equal(count, bindings.Count);
        var read = await service.GetAsync("binding-old");
        var updated = await service.UpdateAsync("binding-old", new UpdateAutomationBindingDto { Enabled = false });
        if (count == 2)
        {
            Assert.Equal(["binding-old", "binding-new"], bindings.Select(binding => binding.Id));
            Assert.NotNull(read);
            Assert.False(updated!.Enabled);
        }
        else
        {
            Assert.Null(read);
            Assert.Null(updated);
            Assert.True((await db.AutomationBindings.AsNoTracking().SingleAsync(x => x.Id == "binding-old")).Enabled);
        }
    }

    [Fact]
    public async Task Binding_import_and_sync_keep_multi_select_field_key_options_and_existing_text_parameter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
        var tenant = new TestTenantContext();
        await using var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();
        var templateId = Guid.NewGuid();
        var form = new RequestForm
        {
            Id = "form-1", OrganizationId = "org-1", Title = "Groups",
            JsonSchema = JsonDocument.Parse($$$"""
                {"fields":[{"key":"selected_groups","type":"predefined","label":"Groups","multiSelect":true,"predefinedOptions":["A","B"]}],
                 "tasks":[{"id":"{{{templateId}}}","type":"automation","name":"Count choices","payloadMapping":{"groupList":"selected_groups"}}]}
                """)
        };
        db.RequestForms.Add(form);
        var binding = CreateBinding("binding", templateId, DateTimeOffset.UtcNow);
        db.AutomationBindings.Add(binding);
        await db.SaveChangesAsync();
        var catalog = Substitute.For<IOrchestrationCatalogService>();
        var input = new OrchestrationCatalogInputDefinitionDto { Key = "groupList", Type = "string", Required = true, DefaultValue = "[]" };
        var remote = new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = binding.OrchestrationRequestDefinitionId, Inputs = [input] };
        catalog.ListRequestDefinitionsAsync("org-1", null, Arg.Any<CancellationToken>()).Returns([remote]);
        catalog.SyncRequestDefinitionInputsAsync(remote.RequestDefinitionId,
            Arg.Any<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(), "org-1", null, Arg.Any<CancellationToken>()).Returns(remote);
        var parser = new RequestFormSchemaParser();
        var importer = new AutomationBindingImportService(db, tenant, parser, catalog);
        var sync = new AutomationBindingSchemaSyncService(db, tenant, parser, catalog, Substitute.For<ICorrelationContext>());

        await importer.ImportAsync(binding.Id);
        await sync.SyncAsync(binding.Id);

        var saved = parser.Parse(form.JsonSchema);
        var field = Assert.Single(saved.Fields);
        Assert.True(field.MultiSelect);
        Assert.Equal("selected_groups", field.Key);
        Assert.Equal("predefined", field.Type);
        Assert.Equal(["A", "B"], field.PredefinedOptions);
        Assert.Equal("selected_groups", Assert.Single(saved.Tasks).PayloadMapping!["groupList"]);
        await catalog.Received(1).SyncRequestDefinitionInputsAsync(remote.RequestDefinitionId,
            Arg.Is<IReadOnlyList<OrchestrationCatalogInputDefinitionDto>>(inputs =>
                inputs.Count == 1 && inputs[0].Type == "string" && inputs[0].Required && inputs[0].DefaultValue == "[]"),
            "org-1", null, Arg.Any<CancellationToken>());

        input.Type = "select";
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(binding.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAsync(binding.Id));
    }

    private static AutomationBinding CreateBinding(
        string id,
        Guid taskTemplateId,
        DateTimeOffset createdAtUtc) => new()
    {
        Id = id,
        OrganizationId = "org-1",
        RequestFormId = "form-1",
        TaskTemplateId = taskTemplateId,
        OrchestrationRequestDefinitionId = "orchestration-request",
        CreatedAtUtc = createdAtUtc,
        UpdatedAtUtc = createdAtUtc
    };

    private sealed class TestTenantContext(bool admin = false, string organizationId = "org-1") : ITenantContext
    {
        public string? TenantId => organizationId;
        public string? UserId => "user-1";
        public bool IsHelpdeskAdmin => admin;
    }
}
