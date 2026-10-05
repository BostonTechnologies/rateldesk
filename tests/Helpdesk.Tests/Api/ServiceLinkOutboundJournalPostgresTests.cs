using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceLink;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Fact]
    public async Task Concurrent_postgres_resume_rolls_back_losing_journal_and_recovers_original_operation_after_response_loss()
    {
        var barrier = new OutboundJournalSaveBarrier();
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync();
        await using var local = await LocalAsync(postgres: true, saveInterceptor: barrier);
        var start = await StartAsync(local, peer);
        var callback = await peer.ApproveAsync(local.BaseUrl, start.AttemptId, BrowserState(start), SessionBinding);
        using (var review = await local.AdminAsync(HttpMethod.Post, "/api/v1/admin/service-links/callback", callback))
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using (var approval = await local.AdminAsync(HttpMethod.Post,
                   "/api/v1/admin/service-links/attempts/" + start.AttemptId + "/approve",
                   new ServiceLinkLocalApproveRequest(peer.GrantHash, SessionBinding)))
            Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        using (var exchange = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);

        long preparedRevision;
        await using (var prepared = local.Services.CreateAsyncScope())
        {
            var db = prepared.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
            Assert.Equal("prepared", attempt.LifecycleState);
            Assert.False(attempt.LocalPreparedAcknowledged);
            Assert.False(attempt.LocalInboundActive);
            Assert.False(attempt.LocalBusinessSenderEnabled);
            preparedRevision = attempt.Revision;
            Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync(x => x.Outbound));
        }

        // Both HTTP scopes reach the first journal save before either executes SQL.
        // The second save resumes after the first atomic commit, outside any DB lock.
        barrier.Arm();
        peer.LosePreparedAcknowledgementResponses = true;
        var responses = await Task.WhenAll(ResumeAsync(local, peer.LinkId), ResumeAsync(local, peer.LinkId));
        foreach (var response in responses)
        {
            using (response) Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        Assert.Equal(2, barrier.Candidates.Count);
        Assert.Equal(2, barrier.Candidates.Select(candidate => candidate.OperationId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(barrier.Candidates, candidate => Assert.Equal(preparedRevision, candidate.OriginalRevision));
        Assert.Equal(1, barrier.ConcurrencyFailures);

        string durableOperationId;
        string durableFingerprint;
        await using (var raced = local.Services.CreateAsyncScope())
        {
            var db = raced.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var journal = Assert.Single(await db.Set<ServiceLinkOperation>().AsNoTracking()
                .Where(x => x.Outbound && x.Kind == "ack-prepared").ToListAsync());
            durableOperationId = journal.OperationId;
            durableFingerprint = journal.RequestFingerprint;
            Assert.False(journal.Completed);
            Assert.NotNull(journal.ProtectedRequestJson);
            Assert.Contains(durableOperationId, barrier.Candidates.Select(candidate => candidate.OperationId));
            Assert.DoesNotContain(await db.Set<ServiceLinkOperation>().Select(x => x.OperationId).ToListAsync(),
                id => id == barrier.Candidates.Single(candidate => candidate.OperationId != durableOperationId).OperationId);
            var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
            Assert.Equal(preparedRevision + 1, attempt.Revision);
            Assert.False(attempt.LocalPreparedAcknowledged);
            Assert.False(attempt.LocalInboundActive);
            Assert.False(attempt.LocalBusinessSenderEnabled);
        }
        Assert.Equal(2, peer.PreparedAcknowledgementOperationIds.Length);
        Assert.All(peer.PreparedAcknowledgementOperationIds, id => Assert.Equal(durableOperationId, id));

        // A new process scope recovers the exact protected request after both responses were lost.
        peer.LosePreparedAcknowledgementResponses = false;
        await local.RestartAsync();
        using (var recovered = await ResumeAsync(local, peer.LinkId))
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await using var final = local.Services.CreateAsyncScope();
        var finalDb = final.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var completed = Assert.Single(await finalDb.Set<ServiceLinkOperation>().AsNoTracking()
            .Where(x => x.Outbound && x.Kind == "ack-prepared").ToListAsync());
        Assert.Equal(durableOperationId, completed.OperationId);
        Assert.Equal(durableFingerprint, completed.RequestFingerprint);
        Assert.True(completed.Completed);
        Assert.Null(completed.ProtectedRequestJson);
        Assert.Equal(3, peer.PreparedAcknowledgementOperationIds.Length);
        Assert.All(peer.PreparedAcknowledgementOperationIds, id => Assert.Equal(durableOperationId, id));
        var recoveredAttempt = await finalDb.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
        Assert.True(recoveredAttempt.LocalPreparedAcknowledged);
        Assert.False(recoveredAttempt.LocalInboundActive);
        Assert.False(recoveredAttempt.LocalBusinessSenderEnabled);
    }

    private sealed class OutboundJournalSaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource winnerSaved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<(string OperationId, long OriginalRevision)> candidates = new();
        private DbContext? firstContext;
        private int saves;
        private int armed;
        private int concurrencyFailures;
        public IReadOnlyCollection<(string OperationId, long OriginalRevision)> Candidates => candidates.ToArray();
        public int ConcurrencyFailures => Volatile.Read(ref concurrencyFailures);
        public void Arm() => Volatile.Write(ref armed, 1);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            if (Volatile.Read(ref armed) == 0 || context is null) return result;
            var operation = context.ChangeTracker.Entries<ServiceLinkOperation>().SingleOrDefault(entry =>
                entry.State == EntityState.Added && entry.Entity.Outbound && entry.Entity.Kind == "ack-prepared");
            if (operation is null) return result;
            var slot = Interlocked.Increment(ref saves);
            if (slot > 2) return result;
            var attempt = context.ChangeTracker.Entries<ServiceLinkAttempt>().Single();
            candidates.Enqueue((operation.Entity.OperationId, attempt.Property(entity => entity.Revision).OriginalValue));
            if (slot == 1)
            {
                firstContext = context;
                await bothReady.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            else
            {
                bothReady.TrySetResult();
                await winnerSaved.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(eventData.Context, firstContext)) winnerSaved.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref concurrencyFailures);
            return ValueTask.FromResult(result);
        }
    }
}
