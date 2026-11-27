using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Tests.Fixtures;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Helpdesk.Application.Events;
using Helpdesk.Application.Services.Changes;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Sla;
using Helpdesk.Infrastructure.Changes;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Shared.Enums;
using Helpdesk.Tests.Infrastructure.Email;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Changes;
using Helpdesk.API.Middleware;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed partial class AuthenticatedChangeApprovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Designated_change_approvers_are_actor_bound_and_replays_do_not_duplicate_committed_effects(bool postgres)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        using var owner = fixture.Client("owner-a");
        using var other = fixture.Client("other-owner");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/changes/change/approval/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/api/v1/changes/change/approval/review", new { actorId = "owner-a" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);

        using var second = fixture.Client("owner-b");
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2);
        await fixture.Timeline.DidNotReceive().PublishAsync(Arg.Any<TicketTimelineEventDto>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_change_review_requires_the_designated_actor_and_current_queue_approval_permission(bool postgres)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        await fixture.WithDbAsync(async db =>
        {
            db.SupportGroups.AddRange(
                new SupportGroup { Id = "vendor", Name = "App Vendor", OwningOrganizationId = "org-a" },
                new SupportGroup { Id = "wintel", Name = "Wintel", OwningOrganizationId = "org-a" });
            await db.SaveChangesAsync();
        });
        const string route = "/api/v1/changes/change/approval";
        var write = new TicketQueuePermissionGrant(HelpdeskPermissions.ChangeWrite, "org-a", "vendor", "org-a");
        var approve = new TicketQueuePermissionGrant(HelpdeskPermissions.ChangeApprove, "org-a", "vendor", "org-a");
        using var owner = fixture.Client("owner-a", approve);
        using var writer = fixture.Client("owner-a", write);
        using var other = fixture.Client("other-owner", approve);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(route)).StatusCode);
        await SetGroupAsync("vendor");
        Assert.Equal(HttpStatusCode.NotFound, (await writer.PostAsync(route + "/review", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync(route + "/review", null)).StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
        await SetGroupAsync("wintel");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync(route + "/review", null)).StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);

        Task SetGroupAsync(string groupId) => fixture.WithDbAsync(async db =>
        {
            var change = await db.Changes.SingleAsync();
            change.AssignedSupportGroupId = groupId;
            await db.SaveChangesAsync();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_and_authenticated_change_reviews_share_one_serialized_transition(bool postgres)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        using var owner = fixture.Client("owner-a");
        using var signed = fixture.Client("owner-b");
        var grant = await fixture.GrantAsync("owner-b");
        var responses = await Task.WhenAll(
            owner.PostAsync("/api/v1/changes/change/approval/review", null),
            PublicLinkFixture.SendAsync(signed, grant, "change/decision", new(true)));
        Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        if (responses[0].StatusCode == HttpStatusCode.Conflict)
        {
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/changes/change/approval/")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        }
        if (responses[1].StatusCode == HttpStatusCode.Conflict)
        {
            Assert.Equal(HttpStatusCode.OK, (await signed.GetAsync("/api/v1/changes/change/approval/")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PublicLinkFixture.SendAsync(signed, grant, "change/decision", new(true))).StatusCode);
        }
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 1);
        Assert.Equal(HttpStatusCode.OK, (await PublicLinkFixture.SendAsync(signed, grant, "change/decision", new(true))).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Commit_failure_rolls_back_change_review_audit_and_timeline_publication(bool postgres, bool signed)
    {
        await using var fixture = await Fixture.StartAsync(postgres, failCommit: true);
        using var client = fixture.Client("owner-a");
        var grant = signed ? await fixture.GrantAsync("owner-a") : null;
        var response = signed
            ? await PublicLinkFixture.SendAsync(client, grant!, "change/decision", new(true))
            : await client.PostAsync("/api/v1/changes/change/approval/review", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await fixture.AssertStoredAsync(0, ChangeLifecycleState.PendingApproval, 0, 0);
        await fixture.Timeline.DidNotReceive().PublishAsync(Arg.Any<TicketTimelineEventDto>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lifecycle_and_final_approval_share_one_fence_and_cannot_overwrite_the_winner(bool postgres, bool approvalWins)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        using var firstApprover = fixture.Client("owner-a");
        using var finalApprover = fixture.Client("owner-b");
        using var manager = fixture.Client("owner-a");
        Assert.Equal(HttpStatusCode.OK, (await firstApprover.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        fixture.Commits.HoldNext = true;
        var winner = Task.Run(() => approvalWins
            ? finalApprover.PostAsync("/api/v1/changes/change/approval/review", null)
            : manager.PostAsJsonAsync("/api/v1/changes/change/lifecycle", new { LifecycleState = ChangeLifecycleState.Draft }));
        Task<HttpResponseMessage> loser;
        try
        {
            await fixture.Commits.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
            Assert.Empty(fixture.Timeline.ReceivedCalls());
            Assert.Empty(fixture.DomainEvents.ReceivedCalls());
            fixture.Commits.ObserveCompetitor = true;
            loser = Task.Run(() => approvalWins
                ? manager.PostAsJsonAsync("/api/v1/changes/change/lifecycle", new { LifecycleState = ChangeLifecycleState.Draft })
                : finalApprover.PostAsync("/api/v1/changes/change/approval/review", null));
            await (postgres ? fixture.Commits.CompetingRead.Task : fixture.Commits.CompetingStart.Task)
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            fixture.Commits.Release.TrySetResult();
        }
        Assert.Equal(HttpStatusCode.OK, (await winner).StatusCode);
        using var losingResponse = await loser;
        Assert.Contains(losingResponse.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.BadRequest });
        await fixture.AssertStoredAsync(approvalWins ? 2 : 0,
            approvalWins ? ChangeLifecycleState.ApprovedForImplementation : ChangeLifecycleState.Draft,
            approvalWins ? 3 : 2, approvalWins ? 2 : 1);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_detail_update_cannot_restore_a_stale_lifecycle_over_a_concurrent_final_approval(bool postgres)
    {
        await using var fixture = await Fixture.StartAsync(postgres, recipients: true);
        using var first = fixture.Client("owner-a");
        using var last = fixture.Client("owner-b");
        using var editor = fixture.Client("owner-a");
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        fixture.Commits.HoldNext = true;
        var approval = Task.Run(() => last.PostAsync("/api/v1/changes/change/approval/review", null));
        Task<HttpResponseMessage> edit;
        try
        {
            await fixture.Commits.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
            fixture.Commits.ObserveCompetitor = true;
            edit = Task.Run(() => editor.PutAsJsonAsync("/api/v1/changes/change", new UpdateChangeDto()));
            await (postgres ? fixture.Commits.CompetingRead.Task : fixture.Commits.CompetingStart.Task)
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            fixture.Commits.Release.TrySetResult();
        }
        Assert.Equal(HttpStatusCode.OK, (await approval).StatusCode);
        Assert.Contains((await edit).StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2, emails: 1);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_approval_queues_each_recipient_once_even_after_replay(bool postgres)
    {
        await using var fixture = await Fixture.StartAsync(postgres, recipients: true, extraCc: true);
        using var first = fixture.Client("owner-a");
        using var last = fixture.Client("owner-b");
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await last.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2, emails: 2);
        var effects = await fixture.ReadEmailEffectsAsync();
        Assert.Equal(new[] { "cc@example.test", "owner-a@example.test" }, effects
            .SelectMany(effect => MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload).Recipients)
            .Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(effects, effect =>
        {
            Assert.Equal(MailboxEffectState.Pending, effect.State);
            var email = MailboxOutboxStore.Deserialize<IngressEmailEffect>(effect.Payload);
            Assert.Empty(email.Cc);
            Assert.Equal("change", email.TicketId);
            Assert.Equal("org-a", email.OrganizationId);
            Assert.NotNull(email.MailboxId);
        });
        Assert.Equal(HttpStatusCode.OK, (await last.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2, emails: 2);
        Assert.Equal(effects.Select(effect => effect.Id).Order(), (await fixture.ReadEmailEffectsAsync()).Select(effect => effect.Id).Order());
        Assert.Empty(fixture.Timeline.ReceivedCalls());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_committed_approval_keeps_actual_smtp_rejection_visible_without_resending_on_review_replay(bool postgres)
    {
        await using var smtp = new SmtpMailboxSenderTests.SmtpFixture(authStatus: 535);
        await using var fixture = await Fixture.StartAsync(postgres, recipients: true, smtpPort: smtp.Port);
        using var first = fixture.Client("owner-a");
        using var last = fixture.Client("owner-b");
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        fixture.Commits.HoldNext = true;
        var final = Task.Run(() => last.PostAsync("/api/v1/changes/change/approval/review", null));
        try
        {
            await fixture.Commits.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
            Assert.Empty(smtp.Commands);
            Assert.Empty(fixture.Timeline.ReceivedCalls());
        }
        finally
        {
            fixture.Commits.Release.TrySetResult();
        }
        Assert.Equal(HttpStatusCode.OK, (await final).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2, emails: 1);
        Assert.Empty(smtp.Commands);
        var queued = Assert.Single(await fixture.ReadEmailEffectsAsync());
        Assert.Equal(MailboxEffectState.Pending, queued.State);
        await fixture.DispatchAsync();
        await smtp.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = Assert.Single(await fixture.ReadEmailEffectsAsync());
        Assert.Equal(queued.Id, rejected.Id);
        Assert.Equal(MailboxEffectState.NeedsReview, rejected.State);
        Assert.Equal("SmtpAuthenticationFailed", rejected.LastErrorCode);
        Assert.Equal(1, rejected.Attempts);
        Assert.Contains(smtp.Commands, command => command.StartsWith("AUTH", StringComparison.Ordinal));
        Assert.DoesNotContain(smtp.Commands, command => command.StartsWith("MAIL FROM", StringComparison.Ordinal));
        Assert.Empty(smtp.Message);
        await fixture.AssertFailedDeliveryAsync(rejected.DeliveryEventId!.Value);
        Assert.Equal(HttpStatusCode.OK, (await last.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        Assert.Equal(1, Assert.Single(await fixture.ReadEmailEffectsAsync()).Attempts);
        await fixture.AssertApprovalStateAsync(2, ChangeLifecycleState.ApprovedForImplementation);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Final_approval_commit_failure_does_not_release_recipient_notifications(bool postgres, bool signed)
    {
        await using var smtp = new SmtpMailboxSenderTests.SmtpFixture(authStatus: 535);
        await using var fixture = await Fixture.StartAsync(postgres, recipients: true, smtpPort: smtp.Port);
        using var first = fixture.Client("owner-a");
        using var last = fixture.Client("owner-b");
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        var grant = signed ? await fixture.GrantAsync("owner-b") : null;
        fixture.Commits.Fail = true;
        using var failed = signed
            ? await PublicLinkFixture.SendAsync(last, grant!, "change/decision", new(true))
            : await last.PostAsync("/api/v1/changes/change/approval/review", null);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
        Assert.Empty(smtp.Commands);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
        fixture.Commits.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await last.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2, emails: 1);
        Assert.Empty(smtp.Commands);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_missing_or_failed_notification_template_rolls_back_final_approval_until_repaired(bool postgres, bool throws)
    {
        await using var fixture = await Fixture.StartAsync(postgres, recipients: true);
        using var first = fixture.Client("owner-a");
        using var last = fixture.Client("owner-b");
        Assert.Equal(HttpStatusCode.OK, (await first.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        if (throws) fixture.Notifications.FailRender = true;
        else await fixture.SetApprovedTemplateAsync(false);
        Assert.Equal(HttpStatusCode.InternalServerError, (await last.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
        fixture.Notifications.FailRender = false;
        if (!throws) await fixture.SetApprovedTemplateAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await last.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        await fixture.AssertStoredAsync(2, ChangeLifecycleState.ApprovedForImplementation, 3, 2, emails: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_commit_failure_preserves_approval_decisions_without_publishing_reset(bool postgres)
    {
        await using var fixture = await Fixture.StartAsync(postgres);
        using var manager = fixture.Client("owner-a");
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync("/api/v1/changes/change/approval/review", null)).StatusCode);
        fixture.Commits.Fail = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await manager.PostAsJsonAsync("/api/v1/changes/change/lifecycle",
            new { LifecycleState = ChangeLifecycleState.Draft })).StatusCode);
        await fixture.AssertStoredAsync(1, ChangeLifecycleState.PendingApproval, 1, 1);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
        Assert.Empty(fixture.DomainEvents.ReceivedCalls());
        fixture.Commits.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/v1/changes/change/lifecycle",
            new { LifecycleState = ChangeLifecycleState.Draft })).StatusCode);
        await fixture.AssertStoredAsync(0, ChangeLifecycleState.Draft, 2, 1);
        Assert.Empty(fixture.Timeline.ReceivedCalls());
    }

    private sealed class Fixture(WebApplication app, PostgreSqlContainer? container, string? sqlitePath,
        ITimelineEventBus timeline, IDomainEventPublisher domainEvents, CommitControl commits, NotificationControl notifications) : IAsyncDisposable
    {
        public ITimelineEventBus Timeline { get; } = timeline;
        public IDomainEventPublisher DomainEvents { get; } = domainEvents;
        public CommitControl Commits { get; } = commits;
        public NotificationControl Notifications { get; } = notifications;
        public HttpClient Client(string actor, TicketQueuePermissionGrant? grant = null)
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", actor);
            if (grant is not null) client.DefaultRequestHeaders.Add("X-Test-Queue-Grant", grant.ToString());
            return client;
        }

        public async Task<PublicLinkExchangeResponse> GrantAsync(string actor)
        {
            var fail = Commits.Fail;
            Commits.Fail = false;
            try
            {
                var launch = await PublicLinkFixture.IssueAsync(app.Services, "change", actor + "@example.test", PublicLinkPurpose.ChangeApproval);
                return await PublicLinkFixture.ExchangeAsync(app.Services, launch);
            }
            finally { Commits.Fail = fail; }
        }

        public async Task WithDbAsync(Func<HelpdeskDbContext, Task> action)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>());
        }

        public static async Task<Fixture> StartAsync(bool postgres, bool failCommit = false,
            bool recipients = false, bool extraCc = false, int smtpPort = 9)
        {
            PostgreSqlContainer? container = null;
            string? sqlitePath = null;
            if (postgres) { container = new PostgreSqlBuilder("postgres:16").Build(); await container.StartAsync(); }
            else sqlitePath = Path.Combine(Path.GetTempPath(), $"change-approvals-{Guid.NewGuid():N}.db");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["EmailSending:AllowedPrivateHosts:0"] = "localhost", ["PublicWebAppUrl"] = "https://desk.example.test" });
            builder.Services.AddHttpContextAccessor();
            var tenant = Substitute.For<ITenantContext>();
            tenant.IsHelpdeskAdmin.Returns(true);
            builder.Services.AddSingleton(tenant);
            var commits = new CommitControl();
            builder.Services.AddDbContext<HelpdeskDbContext>(options =>
            {
                if (container is not null) options.UseNpgsql(container.GetConnectionString());
                else options.UseSqlite($"Data Source={sqlitePath};Pooling=False;Default Timeout=15");
                options.AddInterceptors(commits, new ChangeReadObserver(commits));
            });
            builder.Services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
            builder.Services.AddSingleton(Substitute.For<ITicketSlaInitializer>());
            var references = Substitute.For<Helpdesk.Application.Services.Tickets.ITicketRefGeneratorService>();
            references.NextReferenceAsync(Arg.Any<string>()).Returns(call => $"CHG-{Guid.NewGuid():N}");
            builder.Services.AddSingleton(references);
            PublicLinkFixture.AddServices(builder);
            var timeline = Substitute.For<ITimelineEventBus>();
            var domainEvents = Substitute.For<IDomainEventPublisher>();
            builder.Services.AddSingleton(timeline);
            builder.Services.AddSingleton(domainEvents);
            builder.Services.AddSingleton(Substitute.For<ICorrelationContext>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaCompletionService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaService>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaRepository>());
            builder.Services.AddSingleton(Substitute.For<ISlaClockService>());
            builder.Services.AddSingleton(Substitute.For<ISlaEscalationEvaluator>());
            builder.Services.AddSingleton<IChangeTemplateService, ChangeTemplateService>();
            var notificationControl = new NotificationControl();
            var renderer = Substitute.For<IEmailTemplateRenderer>();
            renderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(call =>
                notificationControl.FailRender ? throw new InvalidOperationException("Synthetic template failure.") : call.ArgAt<string>(0));
            builder.Services.AddSingleton(renderer);
            builder.Services.AddSingleton(Substitute.For<IEmailLayoutResolver>());
            var branding = Substitute.For<ITenantBrandingResolver>();
            branding.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TenantBrandingResolved { ReplyTo = "support@example.test" });
            builder.Services.AddSingleton(branding);
            var protection = new EphemeralDataProtectionProvider();
            var outgoingSecrets = new MailboxOutgoingCredentialProtector(protection);
            builder.Services.AddSingleton(outgoingSecrets);
            builder.Services.AddSingleton(new MailboxCredentialProtector(protection));
            builder.Services.AddSingleton<MailboxDestinationPolicy>();
            builder.Services.AddSingleton<Helpdesk.Application.WorkLogs.IHtmlToPlainTextConverter, HtmlToPlainTextConverter>();
            builder.Services.AddScoped<IIngressEffectContext, IngressEffectContext>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<MailboxOutboxStore>();
            builder.Services.AddScoped<MailboxSenderResolver>();
            builder.Services.AddScoped<SmtpMailboxSender>();
            builder.Services.AddScoped<GraphMailboxSender>();
            builder.Services.AddScoped<MailboxEmailService>();
            builder.Services.AddScoped<IEmailService>(provider => provider.GetRequiredService<MailboxEmailService>());
            builder.Services.AddScoped<ITicketNotificationService, TicketNotificationService>();
            builder.Services.AddScoped<ICurrentUserAccessService, ClaimsAccess>();
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                foreach (var policy in new[] { "ChangeAccess", "ChangeManager", "HelpdeskAdmin" })
                    options.AddPolicy(policy, policyBuilder => policyBuilder.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
            });
            var app = builder.Build();
            app.UseExceptionHandler(handler => handler.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return context.Response.WriteAsync("Synthetic persistence or notification failure.");
            }));
            app.UseAuthentication();
            app.UseMiddleware<UserAccessClaimsMiddleware>();
            app.UseAuthorization();
            app.MapChangeEndpoints();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync();
                await TicketIdentityStore.CreateAsync(db);
                db.Organizations.Add(new Organization { Id = "org-a", Name = "Approval fixture" });
                db.Changes.Add(new Change { Id = "change", TrackingId = "CHG-OWNER", OrganizationId = "org-a",
                    Title = "Designated owner approval", LifecycleState = ChangeLifecycleState.PendingApproval,
                    ApproverUserIds = ["owner-a", "owner-b"], RequestedForUserId = recipients ? "owner-a" : null,
                    ImplementorUserId = recipients ? "owner-a" : null,
                    CcRecipients = extraCc ? ["OWNER-A@example.test", "cc@example.test", "CC@example.test"] : recipients ? ["OWNER-A@example.test"] : [] });
                foreach (var actor in new[] { "owner-a", "owner-b" })
                {
                    db.Users.Add(new User { Id = actor, Name = actor, Email = actor + "@example.test", OrganizationId = "org-a" });
                    db.ChangeApprovals.Add(new ChangeApproval { Id = "approval-" + actor, ChangeId = "change", ApproverId = actor,
                        ApproverName = actor, ApproverEmail = actor + "@example.test" });
                }
                foreach (var name in new[] { "ChangeApproved", "ChangeSubmitted", "ChangeApprovalRequired", "ChangeImplementationInProgress", "ChangeImplemented" })
                    db.EmailTemplates.Add(Template(name));
                var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), Scope = MailboxScope.Global,
                    MailboxAddress = "support@example.test", SourceKey = "approval-test-mailbox", Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                var outgoing = new MailboxOutgoingSettings { MailboxId = mailbox.Id, Enabled = true, Transport = MailboxOutgoingTransport.Smtp,
                    SmtpHost = "localhost", SmtpPort = smtpPort, SmtpTlsMode = MailboxTlsMode.TlsOnConnect, SmtpUsername = mailbox.MailboxAddress };
                outgoing.ProtectedSmtpPassword = outgoingSecrets.Protect(outgoing, "synthetic-password");
                db.EmailInboxSettings.Add(mailbox);
                db.Set<MailboxOutgoingSettings>().Add(outgoing);
                await db.SaveChangesAsync();
            }
            commits.Fail = failCommit;
            await app.StartAsync();
            return new Fixture(app, container, sqlitePath, timeline, domainEvents, commits, notificationControl);
        }

        public async Task AssertStoredAsync(int reviewed, ChangeLifecycleState state, int effects, int audits, int emails = 0)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(reviewed, await db.ChangeApprovals.CountAsync(item => item.Status == ChangeApprovalStatus.Reviewed));
            Assert.Equal(state, (await db.Changes.SingleAsync()).LifecycleState);
            Assert.Equal(effects + emails, await db.TicketTimelineEvents.CountAsync());
            Assert.Equal(effects, await db.Set<MailboxOutboxEffect>().CountAsync(item => item.Kind == MailboxEffectKind.Timeline));
            Assert.Equal(emails, await db.Set<MailboxOutboxEffect>().CountAsync(item => item.Kind == MailboxEffectKind.Email));
            Assert.Equal(audits, await db.ActivityLogs.CountAsync());
        }

        public async Task AssertApprovalStateAsync(int reviewed, ChangeLifecycleState state)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(reviewed, await db.ChangeApprovals.CountAsync(item => item.Status == ChangeApprovalStatus.Reviewed));
            Assert.Equal(state, (await db.Changes.SingleAsync()).LifecycleState);
        }

        public async Task<MailboxOutboxEffect[]> ReadEmailEffectsAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<MailboxOutboxEffect>()
                .AsNoTracking().Where(item => item.Kind == MailboxEffectKind.Email).ToArrayAsync();
        }

        public async Task AssertFailedDeliveryAsync(Guid deliveryId)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var delivery = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()
                .TicketTimelineEvents.AsNoTracking().SingleAsync(item => item.Id == deliveryId);
            Assert.Equal(EmailDeliveryStatus.Failed, delivery.EmailStatus);
            Assert.NotEqual("Email sent.", delivery.MessageText);
        }

        public async Task DispatchAsync()
        {
            using var dispatcher = new MailboxOutboxDispatcher(app.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MailboxOutboxDispatcher>.Instance);
            await dispatcher.DispatchBatchAsync();
        }

        public async Task SetApprovedTemplateAsync(bool present)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            if (present) db.EmailTemplates.Add(Template("ChangeApproved"));
            else await db.EmailTemplates.Where(template => template.Name == "ChangeApproved").ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }

        private static EmailTemplate Template(string name) => new() { Name = name, Subject = name + " {{TicketRef}}", HtmlContent = "<p>Review outcome for CHG-OWNER.</p>" };

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            if (container is not null) await container.DisposeAsync();
            if (sqlitePath is not null) foreach (var suffix in new[] { "", "-shm", "-wal" }) File.Delete(sqlitePath + suffix);
        }
    }

    private sealed class NotificationControl
    {
        public bool FailRender { get; set; }
    }

    private sealed class CommitControl : DbTransactionInterceptor
    {
        public bool Fail { get; set; }
        public bool HoldNext { get; set; }
        public bool ObserveCompetitor { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompetingStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompetingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int held;

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (ObserveCompetitor) CompetingStart.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Synthetic commit failure.");
            if (HoldNext && Interlocked.Exchange(ref held, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ChangeReadObserver(CommitControl commits) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (commits.ObserveCompetitor && eventData.Context?.Database.CurrentTransaction is not null)
                commits.CompetingRead.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ClaimsAccess : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult(CurrentUserAccessProfile.FromClaims(user));
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers.Authorization.ToString().Split(' ').Last();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor), new(ClaimTypes.Name, actor),
                new(ClaimTypes.Email, actor + "@example.test"), new("allowed_organization_id", "org-a") };
            if (Request.Headers["X-Test-Queue-Grant"].FirstOrDefault() is { } grant)
            {
                claims.Add(new("ticket_workforce_scope", "restricted"));
                claims.Add(new("ticket_queue_permission", grant));
            }
            else claims.Add(new(ClaimTypes.Role, "HelpdeskAdmin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }
}
