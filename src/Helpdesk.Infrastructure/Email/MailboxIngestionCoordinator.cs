using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Email;

public sealed class MailboxIngestionCoordinator(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<MailboxIngestionCoordinator> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    private const int MaximumReceiptAttempts = 5;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan pollTimeout = ReadBoundedDuration(configuration,
        "EmailIngestion:PollTimeout", TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(100));
    private readonly TimeSpan acknowledgmentTimeout = ReadBoundedDuration(configuration,
        "EmailIngestion:AcknowledgmentTimeout", TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(25));
    private readonly TimeSpan acknowledgmentPhaseBudget = ReadBoundedDuration(configuration,
        "EmailIngestion:AcknowledgmentPhaseBudget", TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(25));
    private readonly TimeSpan historicalPhaseBudget = ReadBoundedDuration(configuration,
        "EmailIngestion:HistoricalPhaseBudget", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(25));
    private readonly TimeSpan fetchPhaseBudget = ReadBoundedDuration(configuration,
        "EmailIngestion:FetchPhaseBudget", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(70));
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly Dictionary<Guid, DateTimeOffset> due = [];
    private readonly Dictionary<Guid, Runner> running = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning("Mailbox reconciliation failed ({FailureType}).", ex.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Mailbox reconciliation stopping.");
        }
        finally
        {
            await StopRunnersAsync();
        }
    }

    internal async Task StopRunnersAsync()
    {
        foreach (var runner in running.Values) runner.Cancellation.Cancel();
        await Task.WhenAll(running.Values.Select(x => x.Task));
        foreach (var runner in running.Values) runner.Cancellation.Dispose();
        running.Clear();
    }

    internal async Task<IReadOnlyDictionary<Guid, Task>> ReconcileAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var worker = await scope.ServiceProvider.GetRequiredService<MailboxWorkerPolicy>().GetStatusAsync(timeout.Token);
        if (worker.InstanceRunning)
            await scope.ServiceProvider.GetRequiredService<MailboxWorkerPolicy>().RecordHeartbeatAsync(timeout.Token);
        var mailboxes = worker.InstanceRunning
            ? await db.EmailInboxSettings.AsNoTracking()
                .Where(x => !x.Archived && x.Enabled && x.BackgroundSyncEnabled).ToListAsync(timeout.Token)
            : [];
        var requested = await db.Set<MailboxIngestionState>().AsNoTracking()
            .Where(x => x.SyncRequestedVersion > x.SyncCompletedVersion)
            .Select(x => x.MailboxId).ToListAsync(timeout.Token);
        foreach (var id in requested) due.Remove(id);
        var configured = mailboxes.ToDictionary(x => x.Id);
        foreach (var (id, runner) in running.ToArray())
        {
            var changed = !configured.TryGetValue(id, out var mailbox) || mailbox.Version != runner.Version;
            if (changed)
            {
                runner.Cancellation.Cancel();
                due.Remove(id);
            }
            if (!runner.Task.IsCompleted) continue;
            await runner.Task;
            runner.Cancellation.Dispose();
            running.Remove(id);
        }
        foreach (var id in due.Keys.Where(x => !configured.ContainsKey(x)).ToArray()) due.Remove(id);
        var now = clock.GetUtcNow();
        foreach (var mailbox in mailboxes.Where(x => !running.ContainsKey(x.Id))
            .OrderBy(x => due.GetValueOrDefault(x.Id, DateTimeOffset.MinValue)))
        {
            if (running.Count >= 4) break;
            if (due.TryGetValue(mailbox.Id, out var next) && next > now) continue;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            due[mailbox.Id] = now.AddSeconds(mailbox.PollIntervalSeconds);
            // SQLite's async database calls can perform synchronous lock waits. Keep a
            // runner's synchronous prefix off the independent reconciliation loop.
            var task = Task.Run(() => RunMailboxAsync(mailbox, cancellation.Token), CancellationToken.None);
            running.Add(mailbox.Id, new Runner(mailbox.Version, cancellation, task));
        }
        return running.ToDictionary(x => x.Key, x => x.Value.Task);
    }

    private async Task RunMailboxAsync(EmailInboxSettings mailbox, CancellationToken ct)
    {
        try { await PollAsync(mailbox, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("Mailbox {MailboxId} runner cancelled for reconciliation.", mailbox.Id);
        }
        catch (Exception error)
        {
            logger.LogWarning("Mailbox {MailboxId} runner failed ({FailureType}).", mailbox.Id, error.GetType().Name);
        }
    }

    internal async Task PollAsync(EmailInboxSettings mailbox, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<HelpdeskDbContext>();
        var leases = services.GetRequiredService<MailboxLeaseStore>();
        var lease = await leases.TryAcquireAsync(mailbox.Id, owner, TimeSpan.FromMinutes(2), ct);
        if (lease is null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(pollTimeout);
        var workToken = timeout.Token;
        long syncCommandVersion = 0;
        try
        {
            var adapter = services.GetServices<IInboundMailboxAdapter>().Single(x => x.Provider == mailbox.Provider);
            var state = await db.Set<MailboxIngestionState>().SingleAsync(x => x.MailboxId == mailbox.Id, workToken);
            state.LastAttemptUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
            state.CurrentStage = "Recovering receipts";
            await db.SaveChangesAsync(workToken);
            syncCommandVersion = state.SyncRequestedVersion > state.SyncCompletedVersion ? state.SyncRequestedVersion : 0;
            var known = (await db.Set<InboundMessageReceipt>().Where(x => x.MailboxId == mailbox.Id && x.SourceKey == mailbox.SourceKey)
                .Select(x => x.TransportKey).Take(100001).ToListAsync(workToken)).ToHashSet(StringComparer.Ordinal);
            if (known.Count > 100000) throw new InvalidOperationException("ReceiptCapacityExceeded");
            await ProcessPendingAsync(db, mailbox, lease, workToken);
            state.CurrentStage = "Historical import";
            await db.SaveChangesAsync(workToken);
            await RecoverHistoricalImportsAsync(mailbox, lease, adapter, workToken);

            string? fetchError = null;
            var fetchAttempted = syncCommandVersion > 0 || state.NextRetryUnixMilliseconds is null ||
                state.NextRetryUnixMilliseconds <= clock.GetUtcNow().ToUnixTimeMilliseconds();
            var pendingCount = await db.Set<InboundMessageReceipt>().CountAsync(x => x.MailboxId == mailbox.Id &&
                (x.Outcome == InboundReceiptOutcome.Pending || x.Outcome == InboundReceiptOutcome.RetryableFailure), workToken);
            if (fetchAttempted && pendingCount >= 100) fetchError = "PendingReceiptCapacity";
            if (fetchAttempted && pendingCount < 100)
            {
                state.CurrentStage = "Fetching source";
                await db.SaveChangesAsync(workToken);
                using var fetchTimeout = CancellationTokenSource.CreateLinkedTokenSource(workToken);
                fetchTimeout.CancelAfter(fetchPhaseBudget);
                try
                {
                    var batch = await adapter.FetchAsync(mailbox, state, known, fetchTimeout.Token);
                    fetchTimeout.Token.ThrowIfCancellationRequested();
                    fetchTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await using var capture = await db.Database.BeginTransactionAsync(workToken);
                    await FenceAsync(db, leases, lease, mailbox, workToken);
                    var protector = services.GetRequiredService<MailboxCredentialProtector>();
                    foreach (var source in batch.Messages)
                    {
                        if (!known.Add(source.Key)) continue;
                        db.Set<InboundMessageReceipt>().Add(new()
                        {
                            MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey, TransportKey = source.Key,
                            ConfigurationVersion = mailbox.Version, InternetMessageId = source.Message?.InternetMessageId,
                            Outcome = source.Ignore ? InboundReceiptOutcome.Ignored : source.HoldReason is not null ? InboundReceiptOutcome.NeedsReview : InboundReceiptOutcome.Pending,
                            Reason = source.HoldReason, Acknowledged = source.Ignore,
                            AcknowledgmentStatus = source.Ignore ? InboundAcknowledgmentStatus.NotRequired : InboundAcknowledgmentStatus.Pending,
                            AcknowledgmentTargetFingerprint = AcknowledgmentTarget(mailbox),
                            ProtectedEnvelope = source.Message is null ? string.Empty : protector.Protect(mailbox.Id, JsonSerializer.Serialize(source.Message)),
                            CreatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds(), UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds()
                        });
                    }
                    state.Cursor = batch.NextCursor;
                    if (!state.Initialized && batch.InitializationComplete)
                        state.BaselineCompletedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
                    state.Initialized |= batch.InitializationComplete;
                    await db.SaveChangesAsync(workToken);
                    await capture.CommitAsync(workToken);
                }
                catch (OperationCanceledException) when (workToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (fetchTimeout.IsCancellationRequested)
                {
                    fetchError = "FetchTimeout";
                    logger.LogWarning("Mailbox {MailboxId} enumeration timed out; durable recovery continued.", mailbox.Id);
                }
                catch (MailboxFenceLostException) { throw; }
                catch (Exception error)
                {
                    fetchError = error.GetType().Name;
                    logger.LogWarning("Mailbox {MailboxId} enumeration failed ({FailureType}); durable recovery continued.",
                        mailbox.Id, fetchError);
                }
            }
            await ProcessPendingAsync(db, mailbox, lease, workToken);
            state.CurrentStage = "Acknowledging";
            await db.SaveChangesAsync(workToken);
            await RecoverAcknowledgmentsAsync(mailbox, lease, adapter, workToken);
            if (fetchAttempted) await UpdateDiagnosticsAsync(mailbox, lease, fetchError, syncCommandVersion, workToken);
            else
            {
                state.CurrentStage = "Retry scheduled";
                await db.SaveChangesAsync(workToken);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (MailboxFenceLostException)
        {
            logger.LogDebug("Mailbox {MailboxId} runner lost ownership or configuration.", mailbox.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Mailbox {MailboxId} ingestion paused ({FailureType}).", mailbox.Id, ex.GetType().Name);
            using var diagnosticTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            diagnosticTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await UpdateDiagnosticsAsync(mailbox, lease, ex.GetType().Name, syncCommandVersion, diagnosticTimeout.Token);
        }
        finally
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await leases.ReleaseAsync(lease, releaseTimeout.Token); }
            catch (Exception error)
            {
                logger.LogWarning("Mailbox {MailboxId} lease release failed ({FailureType}); lease expiry provides recovery.", mailbox.Id, error.GetType().Name);
            }
        }
    }

    private async Task RecoverHistoricalImportsAsync(EmailInboxSettings mailbox, MailboxLeaseToken lease,
        IInboundMailboxAdapter adapter, CancellationToken ct)
    {
        if (adapter is not IHistoricalMailboxAdapter historical) return;
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phase.CancelAfter(historicalPhaseBudget);
        var token = phase.Token;
        IReadOnlyList<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            ids = await db.Set<InboundMessageReceipt>().AsNoTracking().Where(x => x.MailboxId == mailbox.Id &&
                    x.SourceKey == mailbox.SourceKey && x.Outcome == InboundReceiptOutcome.Ignored &&
                    x.Reason == "InitialBaselineSkipped" && x.HistoricalImportRequestId != null &&
                    x.HistoricalImportCompletedUnixMilliseconds == null)
                .OrderBy(x => x.HistoricalImportRequestedUnixMilliseconds).Take(10).Select(x => x.Id).ToListAsync(token);
        }
        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                var receipt = await db.Set<InboundMessageReceipt>().SingleAsync(x => x.Id == id, token);
                InboundSourceMessage source;
                try { source = await historical.FetchHistoricalAsync(mailbox, receipt.TransportKey, token); }
                catch (InboundSourceMissingException)
                {
                    source = new(receipt.TransportKey, null, HoldReason: "ImportSourceMissing");
                }
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var leases = scope.ServiceProvider.GetRequiredService<MailboxLeaseStore>();
                await FenceAsync(db, leases, lease, mailbox, token);
                if (receipt.Outcome != InboundReceiptOutcome.Ignored || receipt.Reason != "InitialBaselineSkipped" ||
                    receipt.HistoricalImportRequestId is null || receipt.HistoricalImportCompletedUnixMilliseconds is not null)
                {
                    await transaction.RollbackAsync(token);
                    continue;
                }
                receipt.ProtectedEnvelope = source.Message is null ? string.Empty :
                    scope.ServiceProvider.GetRequiredService<MailboxCredentialProtector>()
                        .Protect(mailbox.Id, JsonSerializer.Serialize(source.Message));
                receipt.InternetMessageId = source.Message?.InternetMessageId;
                receipt.Outcome = source.Message is not null ? InboundReceiptOutcome.Pending : InboundReceiptOutcome.NeedsReview;
                receipt.Reason = source.Message is not null ? null : source.HoldReason ?? "ImportSourceMissing";
                receipt.Acknowledged = false;
                receipt.AcknowledgmentStatus = InboundAcknowledgmentStatus.Pending;
                receipt.AcknowledgmentTargetFingerprint = AcknowledgmentTarget(mailbox);
                receipt.ConfigurationVersion = mailbox.Version;
                receipt.Attempts = 0;
                receipt.HistoricalImportCompletedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
                receipt.HistoricalImportErrorCode = receipt.Reason;
                receipt.UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && token.IsCancellationRequested) { break; }
            catch (MailboxFenceLostException) { throw; }
            catch (Exception error)
            {
                logger.LogWarning("Mailbox {MailboxId} historical receipt {ReceiptId} delayed ({FailureType}).",
                    mailbox.Id, id, error.GetType().Name);
            }
        }
    }

    private async Task ProcessPendingAsync(HelpdeskDbContext db, EmailInboxSettings mailbox,
        MailboxLeaseToken lease, CancellationToken ct)
    {
        var receipts = await db.Set<InboundMessageReceipt>().AsNoTracking().Where(x => x.MailboxId == mailbox.Id &&
            x.SourceKey == mailbox.SourceKey &&
            (x.Outcome == InboundReceiptOutcome.Pending || x.Outcome == InboundReceiptOutcome.RetryableFailure))
            .OrderBy(x => x.UpdatedUnixMilliseconds).Take(mailbox.BatchSize).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in receipts) await ProcessAsync(mailbox, lease, id, ct);
    }

    private async Task RecoverAcknowledgmentsAsync(EmailInboxSettings mailbox, MailboxLeaseToken lease,
        IInboundMailboxAdapter adapter, CancellationToken ct)
    {
        IReadOnlyList<Guid> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            ids = await db.Set<InboundMessageReceipt>().AsNoTracking().Where(x => x.MailboxId == mailbox.Id &&
                    x.SourceKey == mailbox.SourceKey && !x.Acknowledged &&
                    (x.Outcome == InboundReceiptOutcome.Succeeded || x.Outcome == InboundReceiptOutcome.Ignored) &&
                    x.AcknowledgmentStatus != InboundAcknowledgmentStatus.NeedsReview &&
                    (x.AcknowledgmentNextRetryUnixMilliseconds == null || x.AcknowledgmentNextRetryUnixMilliseconds <= now) &&
                    (x.AcknowledgmentStatus != InboundAcknowledgmentStatus.InFlight ||
                     x.AcknowledgmentClaimExpiresUnixMilliseconds <= now))
                .OrderBy(x => x.AcknowledgmentNextRetryUnixMilliseconds).ThenBy(x => x.UpdatedUnixMilliseconds)
                .Take(mailbox.BatchSize).Select(x => x.Id).ToListAsync(ct);
        }

        using var phaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        phaseTimeout.CancelAfter(acknowledgmentPhaseBudget);
        var phaseToken = phaseTimeout.Token;
        foreach (var id in ids)
        {
            AcknowledgmentWork? work;
            try
            {
                work = await TryClaimAcknowledgmentAsync(mailbox, lease, id, phaseToken);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && phaseToken.IsCancellationRequested)
            {
                break;
            }
            if (work is null) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(phaseToken);
                timeout.CancelAfter(acknowledgmentTimeout);
                await adapter.AcknowledgeAsync(work.Mailbox, work.TransportKey, timeout.Token);
                await CompleteAcknowledgmentAsync(work, ct);
            }
            catch (InboundSourceMissingException)
            {
                await CompleteMissingAcknowledgmentAsync(work, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Mailbox {MailboxId} acknowledgment for receipt {ReceiptId} timed out.",
                    mailbox.Id, id);
                await ReleaseAcknowledgmentAsync(work, "AcknowledgmentTimeout", ct);
                if (phaseToken.IsCancellationRequested) break;
            }
            catch (Exception error)
            {
                logger.LogWarning("Mailbox {MailboxId} acknowledgment for receipt {ReceiptId} failed ({FailureType}).",
                    mailbox.Id, id, error.GetType().Name);
                await ReleaseAcknowledgmentAsync(work, error.GetType().Name, ct);
            }
        }
    }

    private static TimeSpan ReadBoundedDuration(IConfiguration configuration, string key,
        TimeSpan fallback, TimeSpan minimum, TimeSpan maximum)
    {
        if (configuration[key] is null) return fallback;
        if (!TimeSpan.TryParse(configuration[key], System.Globalization.CultureInfo.InvariantCulture, out var duration) ||
            duration < minimum || duration > maximum)
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        return duration;
    }

    private async Task<AcknowledgmentWork?> TryClaimAcknowledgmentAsync(EmailInboxSettings mailbox,
        MailboxLeaseToken lease, Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FenceAsync(db, scope.ServiceProvider.GetRequiredService<MailboxLeaseStore>(), lease, mailbox, ct);
        var receipt = await db.Set<InboundMessageReceipt>().SingleOrDefaultAsync(x => x.Id == id &&
            x.MailboxId == mailbox.Id && x.SourceKey == mailbox.SourceKey && !x.Acknowledged, ct);
        if (receipt is null || receipt.Outcome is not (InboundReceiptOutcome.Succeeded or InboundReceiptOutcome.Ignored))
            return null;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (receipt.AcknowledgmentNextRetryUnixMilliseconds > now ||
            receipt.AcknowledgmentStatus == InboundAcknowledgmentStatus.InFlight &&
            receipt.AcknowledgmentClaimExpiresUnixMilliseconds > now) return null;
        var target = AcknowledgmentTarget(mailbox);
        if (receipt.AcknowledgmentTargetFingerprint.Length > 0 && receipt.AcknowledgmentTargetFingerprint != target)
        {
            receipt.AcknowledgmentStatus = InboundAcknowledgmentStatus.NeedsReview;
            receipt.AcknowledgmentErrorCode = "AcknowledgmentConfigurationChanged";
            receipt.Reason ??= "AcknowledgmentConfigurationChanged";
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        var claimId = Guid.NewGuid();
        receipt.AcknowledgmentTargetFingerprint = target;
        receipt.AcknowledgmentStatus = InboundAcknowledgmentStatus.InFlight;
        receipt.AcknowledgmentClaimId = claimId;
        receipt.AcknowledgmentClaimExpiresUnixMilliseconds = clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds();
        receipt.AcknowledgmentAttempts++;
        receipt.AcknowledgmentErrorCode = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new AcknowledgmentWork(receipt.Id, claimId, receipt.TransportKey, mailbox);
    }

    private async Task CompleteAcknowledgmentAsync(AcknowledgmentWork work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await db.Set<InboundMessageReceipt>().Where(x => x.Id == work.ReceiptId && !x.Acknowledged &&
                x.AcknowledgmentStatus == InboundAcknowledgmentStatus.InFlight &&
                x.AcknowledgmentClaimId == work.ClaimId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Acknowledged, true)
                .SetProperty(x => x.AcknowledgmentStatus, InboundAcknowledgmentStatus.Succeeded)
                .SetProperty(x => x.AcknowledgmentClaimId, (Guid?)null)
                .SetProperty(x => x.AcknowledgmentClaimExpiresUnixMilliseconds, (long?)null)
                .SetProperty(x => x.AcknowledgmentNextRetryUnixMilliseconds, (long?)null)
                .SetProperty(x => x.AcknowledgmentErrorCode, (string?)null), ct);
    }

    private async Task ReleaseAcknowledgmentAsync(AcknowledgmentWork work, string errorCode, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await db.Set<InboundMessageReceipt>().Where(x => x.Id == work.ReceiptId && !x.Acknowledged &&
                x.AcknowledgmentStatus == InboundAcknowledgmentStatus.InFlight &&
                x.AcknowledgmentClaimId == work.ClaimId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.AcknowledgmentStatus, InboundAcknowledgmentStatus.Pending)
                .SetProperty(x => x.AcknowledgmentClaimId, (Guid?)null)
                .SetProperty(x => x.AcknowledgmentClaimExpiresUnixMilliseconds, (long?)null)
                .SetProperty(x => x.AcknowledgmentNextRetryUnixMilliseconds,
                    clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds())
                .SetProperty(x => x.AcknowledgmentErrorCode, errorCode), ct);
    }

    private async Task CompleteMissingAcknowledgmentAsync(AcknowledgmentWork work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await db.Set<InboundMessageReceipt>().Where(x => x.Id == work.ReceiptId && !x.Acknowledged &&
                x.AcknowledgmentStatus == InboundAcknowledgmentStatus.InFlight &&
                x.AcknowledgmentClaimId == work.ClaimId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Acknowledged, true)
                .SetProperty(x => x.AcknowledgmentStatus, InboundAcknowledgmentStatus.NotRequired)
                .SetProperty(x => x.AcknowledgmentClaimId, (Guid?)null)
                .SetProperty(x => x.AcknowledgmentClaimExpiresUnixMilliseconds, (long?)null)
                .SetProperty(x => x.AcknowledgmentNextRetryUnixMilliseconds, (long?)null)
                .SetProperty(x => x.AcknowledgmentErrorCode, "SourceMissingDuringAcknowledgment")
                .SetProperty(x => x.Reason, x => x.Reason ?? "SourceMissingDuringAcknowledgment"), ct);
    }

    private static string AcknowledgmentTarget(EmailInboxSettings mailbox)
    {
        var value = $"{mailbox.SourceKey}\n{mailbox.MarkReadAfterSuccess}\n{mailbox.ProcessedFolder?.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    internal async Task ProcessAsync(EmailInboxSettings mailbox, MailboxLeaseToken lease, Guid id, CancellationToken ct)
    {
        // Persist the attempt before business work. A crash after claim still consumes an
        // attempt, and no user/ticket changes escape the following business transaction.
        var attempt = await BeginAttemptAsync(mailbox, lease, id, ct);
        if (attempt is null) return;
        IReadOnlyList<InboundEmailProcessingLog> audit = [];
        string? organizationId = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<HelpdeskDbContext>();
            db.BeginIngressRoutingScope();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await FenceAsync(db, services.GetRequiredService<MailboxLeaseStore>(), lease, mailbox, ct);
            var receipt = await db.Set<InboundMessageReceipt>().SingleAsync(x => x.Id == id, ct);
            if (receipt.Attempts != attempt || receipt.Outcome is not (InboundReceiptOutcome.Pending or InboundReceiptOutcome.RetryableFailure)) return;
            var effectContext = services.GetRequiredService<IIngressEffectContext>();
            using var effects = effectContext.Begin(receipt.Id);
            try
            {
                var message = JsonSerializer.Deserialize<InboundEmailContext>(services.GetRequiredService<MailboxCredentialProtector>()
                    .Unprotect(mailbox, receipt.ProtectedEnvelope)) ?? throw new InvalidDataException("Invalid captured message.");
                message = message with { MailboxId = mailbox.Id, SourceMessageKey = $"ingress:{receipt.Id:D}" };
                var router = services.GetRequiredService<InboundTenantRouter>();
                var isDeliveryFailure = InboundTicketProcessor.IsDeliveryFailureMessage(message);
                var route = await router.ResolveAsync(mailbox, message, ct, isDeliveryFailure);
                var canEvaluateForwardedRoute = route.ForwardedCandidate?.OrganizationId is not null &&
                    route.Reason is "RequesterOwnershipConflict" or "TenantUsesDedicatedMailbox";
                organizationId = canEvaluateForwardedRoute
                    ? route.ForwardedCandidate!.OrganizationId
                    : route.OrganizationId;
                effectContext.OrganizationId = organizationId;
                receipt.OrganizationId = organizationId;
                if (route.Reason is null && organizationId is not null)
                    db.RestrictIngressToOrganization(organizationId);
                receipt.Reason = null;
                if (route.Reason is not null && !canEvaluateForwardedRoute)
                    throw new InboundReceiptHoldException(route.Reason);
                if (EmailAddressGuard.IsSameAddress(message.FromEmail, mailbox.MailboxAddress) || isDeliveryFailure)
                {
                    receipt.Outcome = InboundReceiptOutcome.Ignored;
                }
                else if (await FindLegacyReplayAsync(db, mailbox, message, route, ct) is { } historicalTicketId)
                {
                    receipt.Outcome = InboundReceiptOutcome.Succeeded;
                    receipt.TicketId = historicalTicketId;
                    receipt.Reason = "LegacyReplayAdopted";
                }
                else
                {
                    // Evaluate rules before default-path provisioning. Holds cannot create
                    // requester/organization rows, and failed actions roll everything back.
                    message = message with
                    {
                        MailboxTenantId = route.OrganizationId,
                        ForwardedRequesterEmail = route.ForwardedCandidate?.Email,
                        ForwardedRequesterName = route.ForwardedCandidate?.Name,
                        ForwardedRequesterTenantId = route.ForwardedCandidate?.OrganizationId
                    };
                    var result = await services.GetRequiredService<IInboundEmailRuleProcessor>().ProcessAsync(message, ct);
                    Ticket? ticket = result.Ticket;
                    if (result.Handled && ticket is null)
                        throw new InboundReceiptHoldException(result.HoldReason ?? "RuleRequiresReview");
                    if (!result.Handled)
                    {
                        if (route.Reason is not null)
                            throw new InboundReceiptHoldException(route.Reason);
                        var customer = await router.GetRequesterAsync(route, ct);
                        db.RestrictIngressToOrganization(customer.OrganizationId);
                        message = message with { MailboxTenantId = customer.OrganizationId };
                        receipt.OrganizationId = customer.OrganizationId;
                        effectContext.OrganizationId = customer.OrganizationId;
                        ticket = await services.GetRequiredService<InboundTicketProcessor>().ProcessTicketEmailAsync(message,
                            message.Attachments, customer, ct, message.SourceMessageKey, route.ReferencedTicketId);
                    }
                    receipt.Outcome = InboundReceiptOutcome.Succeeded;
                    receipt.TicketId = ticket?.Id;
                    receipt.OrganizationId = ticket?.OrganizationId ?? receipt.OrganizationId;
                }
                await services.GetRequiredService<MailboxOutboxStore>().FlushAsync(ct);
                receipt.ProtectedEnvelope = string.Empty;
                receipt.UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                audit = db.ChangeTracker.Entries<InboundEmailProcessingLog>().Select(x => x.Entity).ToArray();
                throw; // Transaction disposal rolls back all business changes before recording outcome.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (MailboxFenceLostException) { throw; }
        catch (Exception error)
        {
            var held = error is InboundReceiptHoldException || attempt >= MaximumReceiptAttempts;
            var reason = error is InboundReceiptHoldException hold ? hold.Reason : error.GetType().Name;
            await RecordFailureAsync(mailbox, lease, id, attempt.Value, held, reason, organizationId, audit, ct);
        }
    }

    private static async Task<string?> FindLegacyReplayAsync(HelpdeskDbContext db, EmailInboxSettings mailbox,
        InboundEmailContext message, InboundRoute route, CancellationToken ct)
    {
        if (!mailbox.LegacySource || route.OrganizationId is null) return null;
        var ids = InboundTenantRouter.MessageIdCandidates(message.InternetMessageId);
        if (ids.Length == 0) return null;
        // An old RFC marker can adopt one native delivery. A later distinct delivery
        // reusing that RFC identity must not be suppressed forever.
        if (await db.Set<InboundMessageReceipt>().AnyAsync(x => x.MailboxId == mailbox.Id &&
            x.SourceKey == mailbox.SourceKey && x.OrganizationId == route.OrganizationId &&
            x.Outcome == InboundReceiptOutcome.Succeeded && x.Reason == "LegacyReplayAdopted" &&
            x.InternetMessageId != null && ids.Contains(x.InternetMessageId), ct)) return null;
        var ruleTickets = db.InboundEmailProcessingLogs.Where(x =>
            x.Status == InboundEmailProcessingStatus.Succeeded && ids.Contains(x.MessageId) &&
            (x.MailboxId == mailbox.Id || x.MailboxId == null && x.MailboxKey == "default") && x.TicketId != null)
            .Select(x => x.TicketId!);
        var replyTickets = db.TicketTimelineEvents.Where(x => x.EventType == TimelineEventType.CustomerReply &&
            ids.Contains(x.CreatedByUserId)).Select(x => x.TicketId);
        var matches = await db.Tickets.AsNoTracking().Where(x => x.OrganizationId == route.OrganizationId &&
            (ruleTickets.Contains(x.Id) || replyTickets.Contains(x.Id))).Select(x => x.Id).Take(2).ToListAsync(ct);
        if (matches.Count > 1) throw new InboundReceiptHoldException("AmbiguousLegacyReplay");
        return matches.SingleOrDefault();
    }

    private async Task<int?> BeginAttemptAsync(EmailInboxSettings mailbox, MailboxLeaseToken lease, Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FenceAsync(db, scope.ServiceProvider.GetRequiredService<MailboxLeaseStore>(), lease, mailbox, ct);
        var receipt = await db.Set<InboundMessageReceipt>().SingleAsync(x => x.Id == id && x.MailboxId == mailbox.Id && x.SourceKey == mailbox.SourceKey, ct);
        if (receipt.Outcome is not (InboundReceiptOutcome.Pending or InboundReceiptOutcome.RetryableFailure)) return null;
        if (receipt.Attempts >= MaximumReceiptAttempts)
        {
            receipt.Outcome = InboundReceiptOutcome.NeedsReview;
            receipt.Reason = "RetryLimitReached";
        }
        else
        {
            receipt.Attempts++;
            receipt.Outcome = InboundReceiptOutcome.Pending;
        }
        receipt.UpdatedUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return receipt.Outcome == InboundReceiptOutcome.NeedsReview ? null : receipt.Attempts;
    }

    private async Task RecordFailureAsync(EmailInboxSettings mailbox, MailboxLeaseToken lease, Guid id, int attempt,
        bool held, string reason, string? organizationId, IReadOnlyList<InboundEmailProcessingLog> audit, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await FenceAsync(db, scope.ServiceProvider.GetRequiredService<MailboxLeaseStore>(), lease, mailbox, ct);
        var changed = await db.Set<InboundMessageReceipt>().Where(x => x.Id == id && x.Attempts == attempt &&
            (x.Outcome == InboundReceiptOutcome.Pending || x.Outcome == InboundReceiptOutcome.RetryableFailure))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Outcome, held ? InboundReceiptOutcome.NeedsReview : InboundReceiptOutcome.RetryableFailure)
                .SetProperty(x => x.Reason, reason).SetProperty(x => x.OrganizationId, organizationId)
                .SetProperty(x => x.UpdatedUnixMilliseconds, clock.GetUtcNow().ToUnixTimeMilliseconds()), ct);
        if (changed != 1) return;
        foreach (var entry in audit)
        {
            // Audit survives a hold, but rolled-back actions must never count as successful
            // idempotency markers and their nonexistent ticket IDs must not be exposed.
            if (entry.Status == InboundEmailProcessingStatus.Succeeded)
                entry.Status = InboundEmailProcessingStatus.Failed;
            entry.TicketId = null;
            entry.Error ??= reason;
            db.InboundEmailProcessingLogs.Add(entry);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task UpdateDiagnosticsAsync(EmailInboxSettings mailbox, MailboxLeaseToken lease, string? error,
        long syncCommandVersion, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await TryFenceAsync(db, scope.ServiceProvider.GetRequiredService<MailboxLeaseStore>(), lease, mailbox, ct)) return;
        var state = await db.Set<MailboxIngestionState>().SingleAsync(x => x.MailboxId == mailbox.Id, ct);
        state.ErrorCode = error;
        state.CurrentStage = error is null ? "Waiting for next poll" : "Retry scheduled";
        state.NextRetryUnixMilliseconds = error is null ? null : clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds();
        if (error is null) state.LastSyncUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (syncCommandVersion > 0 && state.SyncRequestedVersion == syncCommandVersion)
        {
            state.SyncCompletedVersion = syncCommandVersion;
            state.LastSyncCommandUnixMilliseconds = clock.GetUtcNow().ToUnixTimeMilliseconds();
            state.LastSyncCommandErrorCode = error;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<bool> TryFenceAsync(HelpdeskDbContext db, MailboxLeaseStore leases,
        MailboxLeaseToken token, EmailInboxSettings mailbox, CancellationToken ct) =>
        await leases.FenceAsync(token, ct) && await db.EmailInboxSettings
            .Where(x => x.Id == mailbox.Id && x.Version == mailbox.Version && x.Enabled && x.BackgroundSyncEnabled && !x.Archived)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Version, x => x.Version), ct) == 1;

    private static async Task FenceAsync(HelpdeskDbContext db, MailboxLeaseStore leases, MailboxLeaseToken token,
        EmailInboxSettings mailbox, CancellationToken ct)
    {
        if (!await TryFenceAsync(db, leases, token, mailbox, ct)) throw new MailboxFenceLostException();
    }

    private sealed record Runner(long Version, CancellationTokenSource Cancellation, Task Task);
    private sealed record AcknowledgmentWork(Guid ReceiptId, Guid ClaimId, string TransportKey, EmailInboxSettings Mailbox);
    private sealed class MailboxFenceLostException : Exception { }
    private sealed class InboundReceiptHoldException(string reason) : Exception(reason)
    {
        public string Reason { get; } = reason;
    }
}
