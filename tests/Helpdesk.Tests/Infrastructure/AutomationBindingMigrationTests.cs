using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure;

public sealed class AutomationBindingMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    private HelpdeskDbContext Context() => new(new DbContextOptionsBuilder<HelpdeskDbContext>()
        .UseNpgsql(postgres.GetConnectionString())
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options,
        Substitute.For<ITenantContext>(), new HttpContextAccessor());

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PublishedPredecessorUpgradesCurrentLegacyAndPartialShapesWithoutChangingRows(int legacyColumns)
    {
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261029181000_RemoveLegacyNetclawConversationIndex");
        var binding = new AutomationBinding
        {
            OrganizationId = "migration-test", RequestFormId = "migration-form", TaskTemplateId = Guid.NewGuid(),
            OrchestrationRequestDefinitionId = "request-42", OrchestrationRequestDefinitionName = "Original request",
            OrchestrationJobDefinitionId = "job-17", OrchestrationJobDefinitionName = "Original job",
            LastSyncHash = "original-hash", LastCorrelationId = "original-correlation"
        };
        db.Add(binding);
        await db.SaveChangesAsync();
        var original = JsonSerializer.Serialize(await db.Set<AutomationBinding>().IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == binding.Id));
        var suffixes = new[] { "JobDefinitionId", "JobDefinitionName", "RequestDefinitionId", "RequestDefinitionName" };
        foreach (var suffix in suffixes.Take(legacyColumns))
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"AutomationBindings\" RENAME COLUMN \"Orchestration{suffix}\" TO \"Sto{suffix}\";");

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var actual = await db.Set<AutomationBinding>().IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == binding.Id);
        Assert.Equal(original, JsonSerializer.Serialize(actual));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='AutomationBindings' AND column_name LIKE 'Sto%'").SingleAsync());
        actual.OrchestrationJobDefinitionName = "Updated after upgrade";
        db.Update(actual);
        await db.SaveChangesAsync();
        Assert.Equal("Updated after upgrade", await db.Set<AutomationBinding>().IgnoreQueryFilters().Where(x => x.Id == binding.Id).Select(x => x.OrchestrationJobDefinitionName).SingleAsync());
    }

    [Fact]
    public async Task AmbiguousDuplicateColumnsStopMigrationWithoutLosingEitherValue()
    {
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261029181000_RemoveLegacyNetclawConversationIndex");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AutomationBindings\" ADD COLUMN \"StoJobDefinitionId\" varchar(256);");
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync());
        Assert.Contains("contains both StoJobDefinitionId and OrchestrationJobDefinitionId", exception.MessageText);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='AutomationBindings' AND column_name IN ('StoJobDefinitionId','OrchestrationJobDefinitionId')").SingleAsync());
        Assert.Contains("20261030000000_NormalizeLegacyAutomationBindingColumns", await db.Database.GetPendingMigrationsAsync());
    }
}
