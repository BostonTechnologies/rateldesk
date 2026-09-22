using Helpdesk.Application.Incidents;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Application.Services;

public class InboundEmailActionExecutorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_AuthorizedForward_CreatesNewUnassignedIncidentForOriginalRequester(bool withInlineImage, bool existingRequester)
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "tenant-1", Name = "Tenant", DnsName = "example.com" });
        db.Users.Add(new User { Id = "tech-1", Email = "tech@support.local", Name = "Tech", Role = HelpdeskRoleBundles.Technical, OrganizationId = "tenant-1" });
        LinkLegacyForwarder(db, "tenant-1");
        if (existingRequester)
            db.Customers.Add(new Customer { Id = "customer-1", Email = "jane@example.com", Name = "Jane", OrganizationId = "tenant-1" });
        await db.SaveChangesAsync();

        var sender = Substitute.For<IRequestSender>();
        var created = new Incident { Id = "inc-1", TrackingId = "INC-1", State = TicketState.New };
        CreateIncidentCommand? command = null;
        sender.Send(Arg.Do<CreateIncidentCommand>(x => command = x), Arg.Any<CancellationToken>()).Returns(created);

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.UpdateAsync(Arg.Any<Incident>()).Returns(call => call.Arg<Incident>());
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(call => call.Arg<TicketTimelineEvent>());

        var storage = Substitute.For<IInlineImageStorageService>();
        storage.SaveIncidentInlineImageAsync("inc-1", "image001.png", Arg.Any<byte[]>())
            .Returns("/api/incidents/inc-1/images/image001-hash.png?token=test");
        var attachmentService = Substitute.For<ITicketAttachmentService>();
        var context = Context() with
        {
            HtmlBody = "<p>Forwarder wrapper</p><img src='cid:wrapper'>",
            Attachments = withInlineImage
                ? [new("image001.png", "image/png", "body", [1, 2]), new("download.txt", "text/plain", "unreferenced", [3])]
                : []
        };
        var forwarded = withInlineImage ? Forwarded() with { OriginalBodyHtml = "<p>Original body</p><img src='cid:body'>" } : Forwarded();

        var executor = new InboundEmailActionExecutor(
            db,
            sender,
            incidentRepo,
            timelineRepo,
            NullLogger<InboundEmailActionExecutor>.Instance,
            new InboundInlineImageResolver(storage, NullLogger<InboundInlineImageResolver>.Instance),
            attachmentService);

        var result = await executor.ExecuteAsync(
            Rule("tenant-1"),
            new InboundEmailRuleActionConfig(InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender),
            context,
            forwarded,
            CancellationToken.None);

        if (withInlineImage)
        {
            Assert.Equal("<p>Original body</p><img src='/api/incidents/inc-1/images/image001-hash.png?token=test'>", created.OriginalEmailHtml);
            Assert.Equal(created.OriginalEmailHtml, created.Description);
            await attachmentService.Received(1).SaveAsync("inc-1",
                Arg.Is<IEnumerable<Helpdesk.Shared.DTOs.Attachment.AttachmentUpload>>(uploads => uploads.Single().FileName == "download.txt"),
                null, Arg.Any<CancellationToken>());
        }

        Assert.True(result.Handled);
        Assert.True(result.StopDefaultProcessing);
        Assert.Equal("jane@example.com", command!.RequesterEmail);
        var requester = Assert.Single(await db.Customers.Where(x => x.Email == "jane@example.com").ToListAsync());
        Assert.Equal("tenant-1", requester.OrganizationId);
        Assert.Equal("Jane", requester.Name);
        Assert.Equal(requester.Id, command.CustomerId);
        if (existingRequester) Assert.Equal("customer-1", command.CustomerId);
        Assert.Equal("tenant-1", command.OrganizationId);
        Assert.Null(command.AssignedToId);
        await incidentRepo.Received(1).UpdateAsync(Arg.Is<Incident>(x => x.State == TicketState.New && x.AssignedToId == null && x.EmailFrom == "jane@example.com"));
        await timelineRepo.Received(1).CreateAsync(Arg.Is<TicketTimelineEvent>(x => x.EventType == TimelineEventType.InternalNote && x.MessageText!.Contains("tech@support.local")));
        Assert.Contains(db.InboundEmailProcessingLogs, x => x.Status == InboundEmailProcessingStatus.Succeeded && x.TicketId == "inc-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_Forward_cannot_reassign_foreign_or_blocked_requester(bool blocked)
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "tenant-1", Name = "Tenant", DnsName = "example.com" });
        db.Users.Add(new User { Id = "tech-1", Email = "tech@support.local", Role = HelpdeskRoleBundles.Technical, OrganizationId = "tenant-1" });
        LinkLegacyForwarder(db, "tenant-1");
        db.Customers.Add(new Customer { Id = "requester", Email = "jane@example.com", OrganizationId = blocked ? "tenant-1" : "foreign", State = blocked ? Helpdesk.Shared.Models.EntityState.Blocked : Helpdesk.Shared.Models.EntityState.Enabled });
        await db.SaveChangesAsync();
        var sender = Substitute.For<IRequestSender>();
        var result = await CreateExecutor(db, sender).ExecuteAsync(Rule("tenant-1"), Action(), Context(), Forwarded());
        Assert.True(result.Handled);
        Assert.Null(result.Ticket);
        await sender.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        var requester = await db.Customers.SingleAsync(x => x.Id == "requester");
        Assert.Equal(blocked ? "tenant-1" : "foreign", requester.OrganizationId);
        Assert.Equal(blocked ? Helpdesk.Shared.Models.EntityState.Blocked : Helpdesk.Shared.Models.EntityState.Enabled, requester.State);
    }

    [Fact]
    public async Task ExecuteAsync_UnauthorizedSender_DoesNotCreateIncident()
    {
        await using var db = CreateDb();
        db.Users.Add(new User { Id = "user-1", Email = "tech@support.local", Name = "Not support", Role = "User", OrganizationId = "tenant-1" });
        await db.SaveChangesAsync();
        var sender = Substitute.For<IRequestSender>();
        var executor = CreateExecutor(db, sender);

        var result = await executor.ExecuteAsync(Rule("tenant-1"), Action(), Context(), Forwarded());

        Assert.True(result.Handled);
        Assert.True(result.StopDefaultProcessing);
        Assert.Equal("ForwarderUnauthorized", result.HoldReason);
        await sender.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        Assert.Contains(db.InboundEmailProcessingLogs, x => x.Status == InboundEmailProcessingStatus.UnauthorizedSender);
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousTenant_DoesNotCreateIncident()
    {
        await using var db = CreateDb();
        db.Users.Add(new User { Id = "tech-1", Email = "tech@support.local", Name = "Tech", Role = HelpdeskRoleBundles.Technical, OrganizationId = "msp" });
        LinkLegacyForwarder(db, "msp");
        db.Organizations.AddRange(
            new Organization { Id = "tenant-1", Name = "Tenant 1", ItSupportOrganizationId = "msp" },
            new Organization { Id = "tenant-2", Name = "Tenant 2", ItSupportOrganizationId = "msp" });
        await db.SaveChangesAsync();
        var sender = Substitute.For<IRequestSender>();
        var executor = CreateExecutor(db, sender);

        var result = await executor.ExecuteAsync(Rule(null), Action(), Context(), Forwarded());

        Assert.True(result.Handled);
        await sender.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        Assert.Contains(db.InboundEmailProcessingLogs, x => x.Status == InboundEmailProcessingStatus.TenantResolutionAmbiguous);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateSucceededLog_DoesNotCreateSecondIncident()
    {
        await using var db = CreateDb();
        db.Users.Add(new User { Id = "tech-1", Email = "tech@support.local", Name = "Tech", Role = HelpdeskRoleBundles.Technical, OrganizationId = "tenant-1" });
        LinkLegacyForwarder(db, "tenant-1");
        db.InboundEmailProcessingLogs.Add(new InboundEmailProcessingLog
        {
            MessageId = "message-1",
            MailboxKey = "default",
            RuleId = "rule-1",
            ActionKey = InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender.ToString(),
            Status = InboundEmailProcessingStatus.Succeeded,
            Matched = true,
            TicketId = "inc-1"
        });
        await db.SaveChangesAsync();
        var sender = Substitute.For<IRequestSender>();
        var executor = CreateExecutor(db, sender);

        var result = await executor.ExecuteAsync(Rule("tenant-1"), Action(), Context(), Forwarded());

        Assert.True(result.Handled);
        await sender.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ParserFailure_DoesNotCreateIncidentForForwarder()
    {
        await using var db = CreateDb();
        db.Users.Add(new User { Id = "tech-1", Email = "tech@support.local", Name = "Tech", Role = HelpdeskRoleBundles.Technical, OrganizationId = "tenant-1" });
        LinkLegacyForwarder(db, "tenant-1");
        await db.SaveChangesAsync();
        var sender = Substitute.For<IRequestSender>();
        var executor = CreateExecutor(db, sender);
        var failedParse = new ForwardedEmailParseResult(ForwardedEmailParseStatus.MissingOriginalSender, null, null, null, null, null, null, null, 0.2);

        await executor.ExecuteAsync(Rule("tenant-1"), Action(), Context(), failedParse);

        await sender.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        Assert.Contains(db.InboundEmailProcessingLogs, x => x.Status == InboundEmailProcessingStatus.ParserFailed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_Local_forwarder_cannot_use_revoked_or_other_tenant_write_grant(bool grantInOtherTenant)
    {
        await using var db = CreateDb();
        await using var identityDb = new RatelDeskIdentityDbContext(new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Organizations.AddRange(new Organization { Id = "tenant-1", Name = "Target" }, new Organization { Id = "other", Name = "Other" });
        db.Users.Add(new User { Id = "local", Email = "tech@support.local", Name = "Local", Role = "Technician", OrganizationId = "tenant-1" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "local", OrganizationId = grantInOtherTenant ? "other" : "tenant-1", RoleKey = ScopedRoleCatalog.IncidentWriter });
        identityDb.Users.Add(new ApplicationUser { Id = "local", UserName = "tech@support.local", Email = "tech@support.local", IsEnabled = true });
        await db.SaveChangesAsync();
        await identityDb.SaveChangesAsync();
        if (!grantInOtherTenant)
        {
            db.ScopedRoleAssignments.RemoveRange(db.ScopedRoleAssignments);
            await db.SaveChangesAsync();
        }
        var sender = Substitute.For<IRequestSender>();
        await CreateExecutor(db, sender, identityDb).ExecuteAsync(Rule("tenant-1"), Action(), Context(), Forwarded());
        await sender.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(db.InboundEmailProcessingLogs, log => log.Status == InboundEmailProcessingStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_Legacy_role_without_verified_external_link_does_not_authorize_forwarding()
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "tenant-1", Name = "Target" });
        db.Users.Add(new User { Id = "unlinked", Email = "tech@support.local", Name = "Unlinked", Role = "Technician", OrganizationId = "tenant-1" });
        await db.SaveChangesAsync();
        var sender = Substitute.For<IRequestSender>();
        await CreateExecutor(db, sender).ExecuteAsync(Rule("tenant-1"), Action(), Context(), Forwarded());
        await sender.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        Assert.Contains(db.InboundEmailProcessingLogs, log => log.Status == InboundEmailProcessingStatus.UnauthorizedSender);
    }

    private static void LinkLegacyForwarder(HelpdeskDbContext db, string organizationId)
    {
        if (!db.Organizations.Local.Any(organization => organization.Id == organizationId))
            db.Organizations.Add(new Organization { Id = organizationId, Name = organizationId });
        db.Customers.Add(new Customer { Id = "forwarder-contact", Name = "Forwarder", OrganizationId = organizationId });
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = "forwarder-contact", DomainUserId = "tech-1", OidcIssuer = "https://issuer.example", OidcSubject = "forwarder"
        });
    }

    private static InboundEmailActionExecutor CreateExecutor(HelpdeskDbContext db, IRequestSender sender, RatelDeskIdentityDbContext? identityDb = null)
    {
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        return new InboundEmailActionExecutor(
            db,
            sender,
            incidentRepo,
            timelineRepo,
            NullLogger<InboundEmailActionExecutor>.Instance,
            new InboundInlineImageResolver(Substitute.For<IInlineImageStorageService>(), NullLogger<InboundInlineImageResolver>.Instance),
            Substitute.For<ITicketAttachmentService>(), identityDb);
    }

    private static HelpdeskDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
    }

    private static InboundEmailRule Rule(string? tenantId) => new()
    {
        Id = "rule-1",
        ScopeType = tenantId is null ? InboundEmailRuleScopeType.Global : InboundEmailRuleScopeType.Tenant,
        TenantId = tenantId,
        StopProcessing = true,
        Name = "Rule"
    };

    private static InboundEmailRuleActionConfig Action() =>
        new(InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender);

    private static InboundEmailContext Context() => new(
        "message-1",
        "graph-1",
        null,
        null,
        "support@example.com",
        "tech@support.local",
        "Tech",
        [],
        [],
        "Fwd: Printer",
        "<p>Forwarded</p>",
        "Forwarded",
        DateTimeOffset.UtcNow,
        new Dictionary<string, string>(),
        []);

    private static ForwardedEmailParseResult Forwarded() => new(
        ForwardedEmailParseStatus.Parsed,
        "jane@example.com",
        "Jane",
        "support@example.com",
        DateTimeOffset.UtcNow,
        "Printer down",
        "<p>The printer is offline.</p>",
        "The printer is offline.",
        0.95);
}
