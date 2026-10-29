using System.Text.Json;
using Helpdesk.Infrastructure.Migrations;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure.AiAssistant;

public sealed class ChatMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Theory]
    [InlineData("original")]
    [InlineData("snapshot")]
    [InlineData("retained-netclaw")]
    public async Task ExistingDevMigrationBaselineUpgradesWithoutChangingWebhookData(string predecessorIndex)
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");
        var migrator = db.GetService<IMigrator>();
        const string previous = "20260724141540_AddOperatorAssistanceRequestToAiInvestigation";
        await migrator.MigrateAsync(previous);
        var webhook = new AiAssistantWebhookConfiguration { Id = Guid.NewGuid(), OrganizationId = "migration-test", Name = "Preserve webhook" };
        db.Add(webhook);
        await db.SaveChangesAsync();
        var original = JsonSerializer.Serialize(await db.Set<AiAssistantWebhookConfiguration>().AsNoTracking().SingleAsync(x => x.Id == webhook.Id));
        await migrator.MigrateAsync("20260909063810_AddAiAssistantSignalRChat");
        if (predecessorIndex == "snapshot")
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER INDEX "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId"
                RENAME TO "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~";
                """);
        }
        else if (predecessorIndex == "retained-netclaw")
        {
            await db.Database.ExecuteSqlRawAsync("""
                ALTER INDEX "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId"
                RENAME TO "IX_NetclawChatConversations_OrganizationId_TicketType_TicketId";
                """);
        }
        var legacyConversationId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AiAssistantChatConversations"
                ("Id", "OrganizationId", "TicketId", "TicketType", "AiAssistantSessionId", "State", "LastSequence", "ActiveMessageId", "LastActivityUtc", "CreatedByUserId")
            VALUES
                ({legacyConversationId}, {"migration-test"}, {"legacy-ticket"}, {"incidents"}, {"beta3-session"}, {(int)ChatState.Idle}, 0, NULL, {DateTimeOffset.UtcNow}, {"operator"});
            """);
        await migrator.MigrateAsync("20261027133754_AddAiOpsAndMcpPolicies");
        if (predecessorIndex == "retained-netclaw")
        {
            // Exercise a database that has already completed the beta.14 chain.
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_NetclawChatConversations_OrganizationId_TicketType_TicketId'").SingleAsync());
        }
        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // Normal repository EF migration path is repeatable.
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(original, JsonSerializer.Serialize(await db.Set<AiAssistantWebhookConfiguration>().AsNoTracking().SingleAsync(x => x.Id == webhook.Id)));
        var upgradedConversation = await db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == legacyConversationId);
        Assert.Equal("beta3-session", upgradedConversation.AiAssistantSessionId);
        Assert.Null(upgradedConversation.ProviderProfileFingerprint);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_NetclawChatConversations_OrganizationId_TicketType_TicketId'").SingleAsync());
        db.AddRange(
            new AiAssistantChatConversation { Id = Guid.NewGuid(), OrganizationId = "migration-test", TicketId = "legacy-ticket", TicketType = "incidents", CreatedByUserId = "operator", IsInvestigation = true },
            new AiAssistantChatConversation { Id = Guid.NewGuid(), OrganizationId = "migration-test", TicketId = "legacy-ticket", TicketType = "incidents", CreatedByUserId = "operator", IsInvestigation = true });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<AiAssistantChatConversation>().CountAsync(x => x.TicketId == "legacy-ticket" && x.IsInvestigation));
        db.Add(new AiAssistantChatConversation { Id = Guid.NewGuid(), OrganizationId = "migration-test", TicketId = "legacy-ticket", TicketType = "incidents", CreatedByUserId = "operator" });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var unique = Assert.IsType<Npgsql.PostgresException>(duplicate.InnerException);
        Assert.Equal("23505", unique.SqlState);
        Assert.Equal("IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~", unique.ConstraintName);
        db.ChangeTracker.Clear();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'AiAssistantChat%'").ToListAsync();
        Assert.Equal(3, tables.Count);
        var retiredTables = await db.Database.SqlQueryRaw<string>("SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('AiProviders', 'KnowledgeBaseArticles', 'KnowledgeEmbeddings', 'OrganizationAiKbSettings')").ToListAsync();
        Assert.Empty(retiredTables);
    }

    [Fact]
    public void ChatMigrationOnlyCreatesItsOwnTablesAndIndexes()
    {
        var migration = new AddAiAssistantSignalRChat();
        Assert.All(migration.UpOperations, operation =>
        {
            var table = operation switch
            {
                CreateTableOperation created => created.Name,
                CreateIndexOperation index => index.Table,
                _ => throw new InvalidOperationException($"Unexpected migration operation: {operation.GetType().Name}")
            };
            Assert.StartsWith("AiAssistantChat", table);
        });
    }
}
