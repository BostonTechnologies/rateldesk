using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Resources;
using Helpdesk.Application.Services.AI;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Resources;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure.Resources;

public sealed class OperatorDatasetConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_operator_and_ingestion_batches_preserve_one_case_insensitive_key_and_atomic_rows(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        var firstRead = new HeldRowRead();
        var secondStart = new ObservedWriterStart();
        await using var firstDb = fixture.Context(firstRead);
        await using var secondDb = fixture.Context(secondStart);
        using var firstCache = new MemoryCache(new MemoryCacheOptions());
        using var secondCache = new MemoryCache(new MemoryCacheOptions());
        var first = fixture.Service(firstDb, firstCache);
        var second = fixture.Service(secondDb, secondCache);
        var firstWrite = first.UpsertRowsAsOperatorAsync("dataset", "org-a", false, Batch("Asset", "first"));
        await firstRead.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var secondWrite = Task.Run(() => second.UpsertRowsAsync("dataset", fixture.IngestKey, Batch("asset", "second")));
        try
        {
            await secondStart.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            firstRead.Release.TrySetResult();
        }

        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => firstWrite), Record.ExceptionAsync(() => secondWrite));
        Assert.Contains(outcomes, outcome => outcome is null);
        Assert.All(outcomes.Where(outcome => outcome is not null), outcome => Assert.IsType<DatasetWriteConflictException>(outcome));

        await using var readback = fixture.Context();
        var rows = await readback.DatasetRows.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.ExternalKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rows, row => Assert.Equal("org-a", row.OrganizationId));
        Assert.Single(rows, row => string.Equals(row.ExternalKey, "asset", StringComparison.OrdinalIgnoreCase));
        Assert.Single(rows.Select(row => row.DataJson.RootElement.GetProperty("batch").GetString()).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_case_collisions_require_explicit_complete_replacement_and_do_not_poison_later_saves(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using (var seed = fixture.Context())
        {
            seed.DatasetRows.AddRange(Stored("Asset", "old-upper"), Stored("asset", "old-lower"), Stored("unrelated", "retained"));
            await seed.SaveChangesAsync();
        }
        await using (var db = fixture.Context())
        using (var cache = new MemoryCache(new MemoryCacheOptions()))
        {
            var service = fixture.Service(db, cache);
            await Assert.ThrowsAsync<DatasetWriteConflictException>(() =>
                service.UpsertRowsAsOperatorAsync("dataset", "org-a", false, Batch("ASSET", "rejected")));
            // A background caller can persist its failure record after a rejected batch.
            (await db.DatasetDefinitions.SingleAsync()).Name = "Failure was observed";
            await db.SaveChangesAsync();
        }
        await using (var unchanged = fixture.Context())
        {
            var original = await unchanged.DatasetRows.AsNoTracking().ToListAsync();
            Assert.Equal(3, original.Count);
            Assert.DoesNotContain(original, row => row.DataJson.RootElement.GetProperty("batch").GetString() == "rejected");
        }
        await using (var db = fixture.Context())
        using (var cache = new MemoryCache(new MemoryCacheOptions()))
        {
            await fixture.Service(db, cache).UpsertRowsAsOperatorAsync("dataset", "org-a", false,
                new UpsertDatasetRowsDto { DeleteMissing = true, Rows = [Row("asset", "reviewed")] });
        }
        await using var readback = fixture.Context();
        var repaired = await readback.DatasetRows.AsNoTracking().SingleAsync();
        Assert.Equal("reviewed", repaired.DataJson.RootElement.GetProperty("batch").GetString());
        Assert.Equal("asset", repaired.ExternalKey, ignoreCase: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_ingestion_keys_are_rejected_before_any_existing_row_changes(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        await service.UpsertRowsAsync("dataset", fixture.IngestKey, Batch("Asset", "original"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertRowsAsync("dataset", fixture.IngestKey,
            new UpsertDatasetRowsDto { DeleteMissing = true, Rows = [Row("Asset", "changed"), Row(" ASSET ", "duplicate")] }));
        await using var readback = fixture.Context();
        var rows = await readback.DatasetRows.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("original", row.DataJson.RootElement.GetProperty("batch").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_batch_cannot_be_replayed_by_a_background_failure_status_save(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using (var seed = fixture.Context())
        using (var cache = new MemoryCache(new MemoryCacheOptions()))
            await fixture.Service(seed, cache).UpsertRowsAsOperatorAsync("dataset", "org-a", false, Batch("Asset", "original"));

        await using (var db = fixture.Context(new FailOneRowSave()))
        using (var cache = new MemoryCache(new MemoryCacheOptions()))
        {
            var service = fixture.Service(db, cache);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertRowsAsOperatorAsync("dataset", "org-a", false,
                new UpsertDatasetRowsDto { DeleteMissing = true, Rows = [Row("Asset", "rejected"), Row("new", "rejected")] }));
            (await db.DatasetDefinitions.SingleAsync()).Name = "Failure recorded";
            await db.SaveChangesAsync();
        }

        await using var readback = fixture.Context();
        Assert.Equal("Failure recorded", (await readback.DatasetDefinitions.SingleAsync()).Name);
        var rows = await readback.DatasetRows.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("original", row.DataJson.RootElement.GetProperty("batch").GetString()));
        Assert.DoesNotContain(rows, row => row.ExternalKey == "new");
        Assert.Contains(rows, row => row.ExternalKey == "companion");
    }

    private static UpsertDatasetRowsDto Batch(string key, string batch) => new() { Rows = [Row(key, batch), Row("companion", batch)] };
    private static UpsertDatasetRowDto Row(string key, string batch) => new() { ExternalKey = key, Data = new JsonObject { ["batch"] = batch } };
    private static DatasetRow Stored(string key, string batch) => new()
    {
        DatasetId = "dataset", OrganizationId = "org-a", ExternalKey = key,
        DataJson = JsonDocument.Parse(JsonSerializer.Serialize(new { batch }))
    };

    private sealed class HeldRowRead : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"DatasetRows\"", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ObservedWriterStart : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailOneRowSave : SaveChangesInterceptor
    {
        private bool failed;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!failed && eventData.Context!.ChangeTracker.Entries<DatasetRow>().Any(entry =>
                    entry.State is Microsoft.EntityFrameworkCore.EntityState.Added or Microsoft.EntityFrameworkCore.EntityState.Modified or Microsoft.EntityFrameworkCore.EntityState.Deleted))
            {
                failed = true;
                throw new InvalidOperationException("Synthetic database write failure.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture(string connectionString, PostgreSqlContainer? container, string? sqlitePath) : IAsyncDisposable
    {
        public string IngestKey { get; private set; } = "";

        public HelpdeskDbContext Context(params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>();
            if (container is null) options.UseSqlite(connectionString);
            else options.UseNpgsql(connectionString);
            options.AddInterceptors(interceptors);
            return new HelpdeskDbContext(options.Options, new OwnerTenantContext(), new HttpContextAccessor());
        }

        public DataManagementService Service(HelpdeskDbContext db, IMemoryCache cache) => new(db, cache,
            Substitute.For<ISecretProtector>(), Substitute.For<IHttpClientFactory>(), NullLogger<DataManagementService>.Instance);

        public static async Task<Fixture> CreateAsync(bool postgres)
        {
            PostgreSqlContainer? container = null;
            string? sqlitePath = null;
            string connectionString;
            if (postgres)
            {
                container = new PostgreSqlBuilder("postgres:16").Build();
                await container.StartAsync();
                connectionString = container.GetConnectionString();
            }
            else
            {
                sqlitePath = Path.Combine(Path.GetTempPath(), $"dataset-concurrency-{Guid.NewGuid():N}.db");
                connectionString = $"Data Source={sqlitePath};Pooling=False;Default Timeout=15";
            }
            var fixture = new Fixture(connectionString, container, sqlitePath);
            await using var db = fixture.Context();
            await db.Database.EnsureCreatedAsync();
            db.DatasetDefinitions.Add(new DatasetDefinition { Id = "dataset", OrganizationId = "org-a", Name = "Concurrency fixture", Slug = "concurrency-fixture", SourceType = DatasetSourceType.Custom });
            await db.SaveChangesAsync();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            fixture.IngestKey = (await fixture.Service(db, cache).CreateCredentialAsync("dataset", new CreateDatasetIngestCredentialDto { Name = "Synthetic ingestion writer" })).PlainTextKey;
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            if (container is not null) await container.DisposeAsync();
            if (sqlitePath is not null)
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);
        }
    }

    private sealed class OwnerTenantContext : ITenantContext
    {
        public string? TenantId => "org-a";
        public string? UserId => "operator-concurrency-fixture";
        public bool IsHelpdeskAdmin => true;
    }
}
