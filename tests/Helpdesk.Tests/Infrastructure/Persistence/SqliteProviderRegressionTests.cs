using Helpdesk.API.Background;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class SqliteProviderRegressionTests
{
    [Fact]
    public async Task Chat_scope_migration_preserves_ticket_transcripts_and_provider_history_on_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = CreateDb(connection); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261029190000_AddMcpPolicyDefinitions");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AiAssistantChatConversations
            (Id, OrganizationId, TicketId, TicketType, State, LastSequence, LastActivityUtc, CreatedByUserId, IsInvestigation)
            VALUES ({id}, {"org"}, {"legacy-ticket"}, {"incidents"}, 0, 0, {DateTimeOffset.UtcNow}, {"operator"}, 0)
            """);
        await migrator.MigrateAsync(); await migrator.MigrateAsync();
        var conversation = await db.Set<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatConversation>().SingleAsync(x => x.Id == id);
        Assert.Equal("legacy-ticket", conversation.TicketId);
        Assert.Equal(Helpdesk.Shared.AiAssistant.Chat.ChatConversationScope.Ticket, conversation.Scope);
        Assert.Null(conversation.GuidanceSnapshot); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        db.Add(new Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatConversation { Scope = Helpdesk.Shared.AiAssistant.Chat.ChatConversationScope.Organization,
            OrganizationId = "org", Title = "Schema compatible", CreatedByUserId = "operator" });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatConversation>().CountAsync());
    }

    [Fact]
    public async Task Migration_preserves_existing_automation_binding_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260912220557_AddExternalIdentityDomainUserLink");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AutomationBindings (Id, OrganizationId, RequestFormId, TaskTemplateId, OrchestrationRequestDefinitionId, SyncState, Enabled, CreatedAtUtc, UpdatedAtUtc) VALUES ({"existing-binding"}, {"org"}, {"form"}, {Guid.NewGuid()}, {"definition"}, {0}, {false}, {"2026-09-13 12:00:00+02:00"}, {"2026-09-13 12:00:00+02:00"})");

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("existing-binding", (await db.AutomationBindings.OrderByUtc(db, x => x.UpdatedAtUtc).Take(1).SingleAsync()).Id);

        // Cover every new sortable mapping through a translated query on the real schema.
        Assert.Empty(await db.InboundEmailProcessingLogs.OrderByUtc(db, x => x.CreatedAtUtc).Take(1).ToListAsync());
        Assert.Empty(await db.DatasetIngestCredentials.OrderByUtc(db, x => x.CreatedAtUtc).Take(1).ToListAsync());
        Assert.Empty(await db.AiInvestigationWorklogEntries.OrderByUtc(db, x => x.OccurredUtc).Take(1).ToListAsync());
    }

    [Fact]
    public async Task Approval_timeout_job_executes_on_migrated_sqlite_and_only_expires_due_approvals()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var due = AddApproval(db, "due", now.AddHours(-1).ToOffset(TimeSpan.FromHours(2)));
        var future = AddApproval(db, "future", now.AddHours(1).ToOffset(TimeSpan.FromHours(-4)));
        var withoutDate = AddApproval(db, "no-date", null);
        await db.SaveChangesAsync();
        var service = new RequestTaskApprovalService(db, new RequestFormSchemaParser(),
            Substitute.For<ITicketNotificationService>(), Substitute.For<IPublicTicketLinkSigner>(),
            Substitute.For<ITimelineEventBus>(), NullLogger<RequestTaskApprovalService>.Instance);
        var job = new RequestTaskApprovalTimeoutHangfireJob(new RequestTaskApprovalTimeoutProcessor(service));

        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.Equal(RequestTaskApprovalStatus.Expired, (await db.RequestTaskApprovals.SingleAsync(x => x.Id == due)).Status);
        Assert.Equal(RequestTaskApprovalStatus.Pending, (await db.RequestTaskApprovals.SingleAsync(x => x.Id == future)).Status);
        Assert.Equal(RequestTaskApprovalStatus.Pending, (await db.RequestTaskApprovals.SingleAsync(x => x.Id == withoutDate)).Status);
        Assert.Equal("Cancelled", (await db.Requests.SingleAsync(x => x.Id == "request-due")).WorkflowStatus);
        Assert.Equal(RequestTaskStatus.Cancelled, (await db.RequestTasks.SingleAsync(x => x.Id == "task-due")).Status);
        Assert.Equal(RequestTaskStatus.PendingApproval, (await db.RequestTasks.SingleAsync(x => x.Id == "task-future")).Status);
    }

    [Fact]
    public async Task Sla_evaluation_job_persists_breach_state_on_migrated_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.MigrateAsync();
        var start = DateTimeOffset.UtcNow.AddHours(-10);
        db.Incidents.Add(new Incident { Id = "sla-ticket", TrackingId = "INC-SLA", Title = "Overdue" });
        db.TicketSlaStates.Add(new TicketSlaState
        {
            TicketId = "sla-ticket", StartedAt = start, ResponseDueAt = start.AddHours(1),
            ResolutionDueAt = start.AddHours(2), Status = SlaStatus.InProgress
        });
        await db.SaveChangesAsync();
        var job = new SlaEvaluationHangfireJob(new SlaEvaluationJob(
            new TicketSlaQueryRepository(db), new TicketSlaRepository(db), new SlaClockService(),
            Substitute.For<ISlaEscalationEvaluator>(), Options.Create(new SlaEvaluationJobSettings { BatchSize = 1 }),
            NullLogger<SlaEvaluationJob>.Instance));

        await job.RunAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var state = await db.TicketSlaStates.SingleAsync();
        Assert.True(state.ResponseBreached);
        Assert.True(state.ResolutionBreached);
        Assert.Equal(SlaStatus.Breached, state.Status);
    }

    private static string AddApproval(HelpdeskDbContext db, string name, DateTimeOffset? dueAt)
    {
        var request = new Request { Id = $"request-{name}", TrackingId = $"REQ-{name}", State = TicketState.PendingApproval };
        var task = new RequestTask
        {
            Id = $"task-{name}", TrackingId = $"TASK-{name}", Request = request,
            RequestId = request.Id, Type = RequestTaskType.Approval,
            Status = RequestTaskStatus.PendingApproval, DueAt = dueAt
        };
        var approval = new RequestTaskApproval
        {
            Request = request, RequestId = request.Id, RequestTask = task, RequestTaskId = task.Id,
            ApproverEmail = "approver@example.com", Status = RequestTaskApprovalStatus.Pending,
            TokenSentAtUtc = DateTimeOffset.UtcNow
        };
        db.RequestTaskApprovals.Add(approval);
        return approval.Id;
    }

    private static HelpdeskDbContext CreateDb(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"))
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options,
        new AdminTenantContext(), new HttpContextAccessor());

    private sealed class AdminTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
