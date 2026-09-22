using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class MailboxLeaseStoreTests
{
    [Fact]
    public async Task Concurrent_replicas_acquire_exactly_one_token()
    {
        await using var fixture = await LeaseDatabase.CreateAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MailboxLeaseToken?> Acquire(string owner, TaskCompletionSource ready) => Task.Run(async () =>
        {
            await using var db = fixture.Open();
            ready.SetResult();
            await start.Task;
            return await new MailboxLeaseStore(db, fixture.Clock)
                .TryAcquireAsync(fixture.MailboxId, owner, TimeSpan.FromMinutes(1));
        });

        var first = Acquire("replica-a", firstReady);
        var second = Acquire("replica-b", secondReady);
        await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(TimeSpan.FromSeconds(10));
        start.SetResult();
        var results = await Task.WhenAll(first, second);
        var winner = Assert.Single(results.OfType<MailboxLeaseToken>());
        Assert.Equal(1, winner.Fence);
        await using var verify = fixture.Open();
        var persisted = await verify.Set<MailboxLease>().SingleAsync();
        Assert.Equal(winner.Owner, persisted.Owner);
        Assert.Equal(winner.Fence, persisted.Fence);
    }

    [Fact]
    public async Task Expiry_and_takeover_reject_stale_renew_release_and_business_fence()
    {
        await using var fixture = await LeaseDatabase.CreateAsync();
        await using var firstDb = fixture.Open();
        await using var secondDb = fixture.Open();
        var first = new MailboxLeaseStore(firstDb, fixture.Clock);
        var second = new MailboxLeaseStore(secondDb, fixture.Clock);
        var old = Assert.IsType<MailboxLeaseToken>(await first.TryAcquireAsync(fixture.MailboxId, "same-owner", TimeSpan.FromSeconds(10)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(await first.RenewAsync(old, TimeSpan.FromSeconds(30)));
        var current = Assert.IsType<MailboxLeaseToken>(await second.TryAcquireAsync(fixture.MailboxId, "same-owner", TimeSpan.FromSeconds(30)));
        Assert.Equal(old.Fence + 1, current.Fence);

        await first.ReleaseAsync(old);
        Assert.False(await first.RenewAsync(old, TimeSpan.FromSeconds(30)));
        await using (var transaction = await firstDb.Database.BeginTransactionAsync())
        {
            Assert.False(await first.FenceAsync(old));
            await transaction.RollbackAsync();
        }
        await using (var transaction = await secondDb.Database.BeginTransactionAsync())
        {
            Assert.True(await second.FenceAsync(current));
            await transaction.CommitAsync();
        }
        Assert.True(await second.RenewAsync(current, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Renewal_preserves_ownership_until_extended_expiry_and_release_advances_next_fence()
    {
        await using var fixture = await LeaseDatabase.CreateAsync();
        await using var db = fixture.Open();
        var store = new MailboxLeaseStore(db, fixture.Clock);
        var original = Assert.IsType<MailboxLeaseToken>(await store.TryAcquireAsync(fixture.MailboxId, "a", TimeSpan.FromSeconds(10)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await store.RenewAsync(original, TimeSpan.FromSeconds(20)));
        Assert.True(await store.RenewAsync(original, TimeSpan.FromSeconds(1)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(await store.TryAcquireAsync(fixture.MailboxId, "b", TimeSpan.FromSeconds(10)));

        await store.ReleaseAsync(original);
        var next = Assert.IsType<MailboxLeaseToken>(await store.TryAcquireAsync(fixture.MailboxId, "b", TimeSpan.FromSeconds(10)));
        Assert.Equal(original.Fence + 1, next.Fence);
        await store.ReleaseAsync(original);
        Assert.True(await store.RenewAsync(next, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Fencing_requires_transaction_and_rejects_expired_owner_without_takeover()
    {
        await using var fixture = await LeaseDatabase.CreateAsync();
        await using var db = fixture.Open();
        var store = new MailboxLeaseStore(db, fixture.Clock);
        var token = Assert.IsType<MailboxLeaseToken>(await store.TryAcquireAsync(fixture.MailboxId, "a", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FenceAsync(token));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.False(await store.FenceAsync(token));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Missing_configuration_does_not_create_a_lease()
    {
        await using var fixture = await LeaseDatabase.CreateAsync();
        await using var db = fixture.Open();
        var store = new MailboxLeaseStore(db, fixture.Clock);
        Assert.Null(await store.TryAcquireAsync(Guid.NewGuid(), "a", TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await db.Set<MailboxLease>().CountAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryAcquireAsync(fixture.MailboxId, "a", TimeSpan.Zero));
    }

    private sealed class LeaseDatabase : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"mailbox-lease-{Guid.NewGuid():N}.db");
        public Guid MailboxId { get; } = Guid.NewGuid();
        public ManualClock Clock { get; } = new();

        public static async Task<LeaseDatabase> CreateAsync()
        {
            var fixture = new LeaseDatabase();
            await using var db = fixture.Open();
            await db.Database.EnsureCreatedAsync();
            db.Set<MailboxLease>().Add(new MailboxLease { MailboxId = fixture.MailboxId });
            await db.SaveChangesAsync();
            return fixture;
        }

        public HelpdeskDbContext Open() => new LeaseTestContext(new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10 }.ToString())
            .Options);

        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return ValueTask.CompletedTask;
        }
    }

    // Exercise the real lease store and relational provider without unrelated application tables.
    private sealed class LeaseTestContext(DbContextOptions<HelpdeskDbContext> options)
        : HelpdeskDbContext(options, new AdminTenantContext(), new HttpContextAccessor())
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var type in modelBuilder.Model.GetEntityTypes().Select(x => x.ClrType).ToArray())
                modelBuilder.Ignore(type);
            modelBuilder.Entity<MailboxLease>().HasKey(x => x.MailboxId);
        }
    }

    private sealed class AdminTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long milliseconds = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref milliseconds));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref milliseconds, (long)duration.TotalMilliseconds);
    }
}
