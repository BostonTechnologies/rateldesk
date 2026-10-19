using System.Text.Json.Nodes;
using Helpdesk.Application.Services.AI;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Resources;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Resources;

public sealed class OperatorDatasetWriterTests
{
    [Fact]
    public async Task Scoped_operator_write_persists_rows_and_delete_missing_is_explicit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.UpsertRowsAsOperatorAsync("custom", "org-a", false,
            new UpsertDatasetRowsDto { Rows = [Row("one", "First"), Row("two", "Second")] });
        await fixture.Service.UpsertRowsAsOperatorAsync("custom", "org-a", false,
            new UpsertDatasetRowsDto { Rows = [Row("one", "Updated")] });

        await using (var readback = fixture.Readback())
        {
            var rows = await readback.DatasetRows.AsNoTracking().OrderBy(x => x.ExternalKey).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal("org-a", row.OrganizationId));
            Assert.Equal("Updated", rows[0].DataJson.RootElement.GetProperty("name").GetString());
            Assert.Equal("Second", rows[1].DataJson.RootElement.GetProperty("name").GetString());
        }

        await fixture.Service.UpsertRowsAsOperatorAsync("custom", "org-a", false,
            new UpsertDatasetRowsDto { DeleteMissing = true, Rows = [Row("one", "Retained")] });
        await using var finalReadback = fixture.Readback();
        var retained = await finalReadback.DatasetRows.AsNoTracking().SingleAsync();
        Assert.Equal("one", retained.ExternalKey);
        Assert.Equal("Retained", retained.DataJson.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Foreign_scope_and_built_in_source_denials_do_not_write_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var update = new UpsertDatasetRowsDto { Rows = [Row("one", "Unauthorized")] };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.UpsertRowsAsOperatorAsync("custom", "org-b", false, update));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Service.UpsertRowsAsOperatorAsync("built-in", "org-a", false, update));

        await using var readback = fixture.Readback();
        Assert.False(await readback.DatasetRows.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task Invalid_duplicate_batch_does_not_partially_edit_existing_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.UpsertRowsAsOperatorAsync("custom", "org-a", false,
            new UpsertDatasetRowsDto { Rows = [Row("one", "Original")] });

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.UpsertRowsAsOperatorAsync("custom", "org-a", false,
            new UpsertDatasetRowsDto { DeleteMissing = true, Rows = [Row("one", "Changed"), Row(" ONE ", "Duplicate")] }));

        await using var readback = fixture.Readback();
        var row = await readback.DatasetRows.AsNoTracking().SingleAsync();
        Assert.Equal("Original", row.DataJson.RootElement.GetProperty("name").GetString());
    }

    private static UpsertDatasetRowDto Row(string key, string name) => new()
    {
        ExternalKey = key,
        Data = new JsonObject { ["name"] = name }
    };

    private sealed class Fixture(SqliteConnection connection, DbContextOptions<HelpdeskDbContext> options,
        HelpdeskDbContext db, MemoryCache cache, DataManagementService service) : IAsyncDisposable
    {
        public DataManagementService Service { get; } = service;

        public HelpdeskDbContext Readback() => new(options, new OwnerTenantContext(), new HttpContextAccessor());

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
            var db = new HelpdeskDbContext(options, new OwnerTenantContext(), new HttpContextAccessor());
            await db.Database.EnsureCreatedAsync();
            db.DatasetDefinitions.AddRange(
                new DatasetDefinition { Id = "custom", OrganizationId = "org-a", Name = "Custom", Slug = "custom", SourceType = DatasetSourceType.Custom },
                new DatasetDefinition { Id = "built-in", OrganizationId = "org-a", Name = "Built in", Slug = "built-in", SourceType = DatasetSourceType.GraphUsers });
            await db.SaveChangesAsync();
            var cache = new MemoryCache(new MemoryCacheOptions());
            var service = new DataManagementService(db, cache, Substitute.For<ISecretProtector>(),
                Substitute.For<IHttpClientFactory>(), NullLogger<DataManagementService>.Instance);
            return new Fixture(connection, options, db, cache, service);
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            cache.Dispose();
            await connection.DisposeAsync();
        }
    }

    private sealed class OwnerTenantContext : ITenantContext
    {
        public string? TenantId => "org-a";
        public string? UserId => "operator-fixture";
        public bool IsHelpdeskAdmin => true;
    }
}
