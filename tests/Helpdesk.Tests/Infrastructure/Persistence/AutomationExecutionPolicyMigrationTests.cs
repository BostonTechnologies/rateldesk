using System.Text.Json;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class AutomationExecutionPolicyMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_backfills_exact_template_policy_and_preserves_unmatched_legacy_tasks(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        // Seed representative rows with the current model, then remove only the
        // new columns to establish the immediately preceding deployed schema.
        await using (var seed = fixture.Open())
        {
            await seed.Database.MigrateAsync();
            var template = Guid.NewGuid().ToString();
            seed.RequestForms.Add(new RequestForm { Id = "policy-form", JsonSchema = JsonDocument.Parse(
                JsonSerializer.Serialize(new { tasks = new object?[] { new { id = template, autoStart = false }, null, "legacy" } })) });
            seed.RequestForms.Add(new RequestForm { Id = "null-tasks-form", JsonSchema = JsonDocument.Parse("{\"tasks\":null}") });
            seed.Requests.Add(new Request { Id = "policy-parent", RequestFormId = "policy-form", TrackingId = "REQ-POLICY" });
            seed.Requests.Add(new Request { Id = "null-tasks-parent", RequestFormId = "null-tasks-form", TrackingId = "REQ-LEGACY" });
            seed.RequestTasks.AddRange(
                new RequestTask { Id = "policy-task", RequestId = "policy-parent", TemplateId = template, TrackingId = "TSK-POLICY" },
                new RequestTask { Id = "legacy-task", RequestId = "policy-parent", TemplateId = Guid.NewGuid().ToString(), TrackingId = "TSK-LEGACY" },
                new RequestTask { Id = "null-tasks-task", RequestId = "null-tasks-parent", TemplateId = Guid.NewGuid().ToString(), TrackingId = "TSK-NULL" });
            await seed.SaveChangesAsync();
            var migrations = seed.Database.GetMigrations().ToArray();
            var index = Array.FindIndex(migrations, x => x.EndsWith("_PersistAutomationExecutionPolicy", StringComparison.Ordinal));
            Assert.True(index > 0);
            await seed.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        }
        await using var upgraded = fixture.Open();
        await upgraded.Database.MigrateAsync();
        await upgraded.Database.MigrateAsync();
        Assert.False((await upgraded.RequestTasks.SingleAsync(x => x.Id == "policy-task")).AutoStart);
        Assert.True((await upgraded.RequestTasks.SingleAsync(x => x.Id == "legacy-task")).AutoStart);
        Assert.True((await upgraded.RequestTasks.SingleAsync(x => x.Id == "null-tasks-task")).AutoStart);
        Assert.Equal(3, await upgraded.RequestTasks.CountAsync());
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
    }
}
