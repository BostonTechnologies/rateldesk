using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;

internal sealed class OpenAiChatWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OpenAiChatWorker> logger, CodexAppServerHost? codex = null) : BackgroundService
{
    private const int ConcurrencyLimit = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<Guid, Task>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var id in running.Where(work => work.Value.IsCompleted).Select(work => work.Key).ToArray())
                {
                    await running[id];
                    running.Remove(id);
                }
                var capacity = ConcurrencyLimit - running.Count;
                if (capacity == 0) continue;
                try
                {
                    var workIds = await FindWorkAsync(running.Keys.ToArray(), capacity, stoppingToken);
                    foreach (var id in workIds) running.Add(id, RunAsync(id, stoppingToken));
                }
                catch (NpgsqlException ex) when (ex.IsTransient && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("OpenAI chat work could not be loaded during a transient PostgreSQL failure.");
                    await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Stopping the OpenAI provider worker and releasing its observation leases.");
        }
        finally
        {
            await Task.WhenAll(running.Values);
        }
    }

    private async Task<Guid[]> FindWorkAsync(Guid[] running, int capacity, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        if (!db.Database.IsNpgsql()) return [];
        var now = clock.GetUtcNow();
        Guid? codexHostId = null;
        if (codex?.Configured == true)
        {
            try { codexHostId = await codex.CheckAsync(ct); }
            catch (Exception error) when (error is IOException or ChatConflictException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception || error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogDebug("This replica does not own a usable Codex runtime.");
            }
        }
        var conversations = db.Set<AiAssistantChatConversation>().AsNoTracking().Where(conversation =>
            conversation.ProviderKind == ChatProviderKind.OpenAiAgents ||
            conversation.ProviderKind == ChatProviderKind.CodexAppServer && codexHostId != null &&
            (from profile in db.Set<OpenAiChatProfile>()
             join connection in db.Set<CodexConnection>() on profile.CodexConnectionId equals connection.Id
             where profile.Id == conversation.ProviderProfileId && connection.HostId == codexHostId
             select connection.Id).Any());
        var due = await (from dispatch in db.Set<OpenAiChatDispatch>().AsNoTracking()
                         join conversation in conversations
                             on dispatch.ConversationId equals conversation.Id
                         where !running.Contains(conversation.Id) && conversation.State != ChatState.Archived &&
                               conversation.ActiveMessageId == dispatch.MessageId &&
                               dispatch.State != OpenAiDispatchState.Completed && dispatch.State != OpenAiDispatchState.Rejected &&
                               (dispatch.NextObservationAtUtc == null || dispatch.NextObservationAtUtc <= now)
                         orderby dispatch.NextObservationAtUtc ?? dispatch.AdmittedAtUtc
                         select conversation.Id).Take(capacity).ToArrayAsync(ct);
        if (due.Length == capacity) return due;
        var cleanup = await db.Set<OpenAiChatSessionGrant>().AsNoTracking().Where(grant =>
            !running.Contains(grant.ConversationId) && !due.Contains(grant.ConversationId) &&
            grant.RemoteDeletedAtUtc == null && conversations.Any(row => row.Id == grant.ConversationId) &&
            (grant.CleanupRequestedAtUtc != null || grant.ExpiresAtUtc <= now) &&
            (grant.CleanupNextAttemptAtUtc == null || grant.CleanupNextAttemptAtUtc <= now))
            .OrderBy(grant => grant.ExpiresAtUtc).Select(grant => grant.ConversationId).Take(capacity - due.Length).ToArrayAsync(ct);
        return [.. due, .. cleanup];
    }

    private async Task RunAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            if (db.Database.GetDbConnection() is not NpgsqlConnection connection) return;
            var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false };
            await using var lease = connection.CloneWith(builder.ConnectionString);
            await lease.OpenAsync(ct);
            await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@conversation, 437))", lease);
            claim.Parameters.AddWithValue("conversation", id.ToString("D"));
            if (await claim.ExecuteScalarAsync(ct) is not true) return;
            var run = scope.ServiceProvider.GetRequiredService<OpenAiChatRun>();
            await run.ProcessAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI observation failed for {ConversationId}: {FailureType}", id, ex.GetType().Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("OpenAI observation for {ConversationId} paused during shutdown.", id);
        }
    }
}
