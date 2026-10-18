using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Helpdesk.Tests.Application.WorkLogs;

public class CreateWorkLogCommandHandlerTests
{
    [Fact]
    public async Task Handle_SendsTicketUpdatedEmail()
    {
        var workLogRepo = Substitute.For<IRepository<WorkLog>>();
        workLogRepo.CreateAsync(Arg.Any<WorkLog>()).Returns(ci => ci.Arg<WorkLog>());

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAsync("t1").Returns(new Incident { Id = "t1", CustomerId = "c1", CcRecipients = new List<string> { "cc1@b.com", "cc2@b.com" } });
        incidentRepo.UpdateAsync(Arg.Any<Incident>()).Returns(ci => ci.Arg<Incident>());

        var activityRepo = Substitute.For<IRepository<ActivityLog>>();
        var notificationService = Substitute.For<INotificationService>();

        var emailService = Substitute.For<IEmailService>();
        emailService.SendEmailAsync(Arg.Any<EmailSendRequest>(), Arg.Any<CancellationToken>()).Returns(true);

        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(new[]
        {
            new EmailTemplate { Name = "TicketUpdated", Subject = "sub {{{TICKET_REF}}}", HtmlContent = "body" }
        });

        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAsync("t1").Returns(new Incident { Id = "t1", TrackingId = "INC-1", CcRecipients = new List<string> { "cc1@b.com", "cc2@b.com" } });

        var customerRepo = Substitute.For<IRepository<Customer>>();
        customerRepo.GetAsync("c1").Returns(new Customer { Id = "c1", Email = "a@b.com", Name = "Cust" });
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(ci => ci.Arg<TicketTimelineEvent>());
        var timelineEventBus = Substitute.For<ITimelineEventBus>();
        var sanitizer = Substitute.For<IHtmlSanitizerService>();
        sanitizer.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var imageStorage = Substitute.For<IWorklogImageStorageService>();
        imageStorage.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        var plainTextConverter = Substitute.For<IHtmlToPlainTextConverter>();
        plainTextConverter.Convert(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var templateRenderer = Substitute.For<IEmailTemplateRenderer>();
        templateRenderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(ci => ci.Arg<string>());
        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        var tenantBrandingResolver = Substitute.For<ITenantBrandingResolver>();
        tenantBrandingResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TenantBrandingResolved());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PublicWebAppUrl"] = "https://app" })
            .Build();
        var publicTicketLinkSigner = Substitute.For<IPublicTicketLinkSigner>();
        publicTicketLinkSigner
            .GenerateToken(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>())
            .Returns("signed-token");

        var handler = new CreateWorkLogCommandHandler(
            workLogRepo,
            incidentRepo,
            activityRepo,
            notificationService,
            emailService,
            templateRepo,
            ticketRepo,
            customerRepo,
            timelineRepo,
            timelineEventBus,
            sanitizer,
            imageStorage,
            plainTextConverter,
            templateRenderer,
            layoutResolver,
            tenantBrandingResolver,
            publicTicketLinkSigner,
            config);

        var command = new CreateWorkLogCommand("t1", 1, "note", "tech", "Tech User");
        await handler.Handle(command, CancellationToken.None);

        await incidentRepo.Received(1).UpdateAsync(Arg.Is<Incident>(i =>
            i.State == TicketState.Replied &&
            i.LastReplierName == "Tech User" &&
            i.UpdatedAt.HasValue));

        await emailService.Received(1)
            .SendEmailAsync(
                Arg.Is<EmailSendRequest>(email => email.Recipients.Contains("a@b.com") &&
                    email.Subject.Contains("INC-1") && email.Cc.Contains("cc1@b.com") && email.Cc.Contains("cc2@b.com") &&
                    email.TicketId == "t1" && email.SuppressTimeline && email.TimelineDeliveryId != null && email.WorkLogId != null),
                Arg.Any<CancellationToken>());

        await timelineRepo.Received(1).UpdateAsync(Arg.Is<TicketTimelineEvent>(e =>
            e.TicketId == "t1" &&
            e.EventType == TimelineEventType.EmailDelivery &&
            e.EmailStatus == EmailDeliveryStatus.Delivered &&
            e.EmailRecipient == "a@b.com" &&
            e.MessageText != null &&
            e.MessageText.Contains("successfully sent")));
    }

    [Fact]
    public async Task Handle_DoesNotSendCustomerEmail_WhenTicketEmailIsExcluded()
    {
        var workLogRepo = Substitute.For<IRepository<WorkLog>>();
        workLogRepo.CreateAsync(Arg.Any<WorkLog>()).Returns(ci => ci.Arg<WorkLog>());

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAsync("t1").Returns(new Incident { Id = "t1", CustomerId = "c1" });
        incidentRepo.UpdateAsync(Arg.Any<Incident>()).Returns(ci => ci.Arg<Incident>());

        var activityRepo = Substitute.For<IRepository<ActivityLog>>();
        var notificationService = Substitute.For<INotificationService>();
        var emailService = Substitute.For<IEmailService>();
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Array.Empty<EmailTemplate>());

        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAsync("t1").Returns(new Incident
        {
            Id = "t1",
            TrackingId = "INC-1",
            EmailExclusionReason = TicketEmailExclusionReason.MarketingSpam
        });

        var customerRepo = Substitute.For<IRepository<Customer>>();
        customerRepo.GetAsync("c1").Returns(new Customer { Id = "c1", Email = "vendor@example.com", Name = "Vendor" });
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(ci => ci.Arg<TicketTimelineEvent>());
        var timelineEventBus = Substitute.For<ITimelineEventBus>();
        var sanitizer = Substitute.For<IHtmlSanitizerService>();
        sanitizer.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var imageStorage = Substitute.For<IWorklogImageStorageService>();
        imageStorage.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        var plainTextConverter = Substitute.For<IHtmlToPlainTextConverter>();
        plainTextConverter.Convert(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var templateRenderer = Substitute.For<IEmailTemplateRenderer>();
        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        var tenantBrandingResolver = Substitute.For<ITenantBrandingResolver>();
        var config = new ConfigurationBuilder().Build();
        var publicTicketLinkSigner = Substitute.For<IPublicTicketLinkSigner>();

        var handler = new CreateWorkLogCommandHandler(
            workLogRepo,
            incidentRepo,
            activityRepo,
            notificationService,
            emailService,
            templateRepo,
            ticketRepo,
            customerRepo,
            timelineRepo,
            timelineEventBus,
            sanitizer,
            imageStorage,
            plainTextConverter,
            templateRenderer,
            layoutResolver,
            tenantBrandingResolver,
            publicTicketLinkSigner,
            config);

        await handler.Handle(new CreateWorkLogCommand("t1", 1, "note", "tech", "Tech User"), CancellationToken.None);

        await emailService.DidNotReceive().SendEmailAsync(Arg.Any<EmailSendRequest>(), Arg.Any<CancellationToken>());
        await notificationService.DidNotReceive().NotifyUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await workLogRepo.Received(1).CreateAsync(Arg.Any<WorkLog>());
    }

    [Fact]
    public async Task Handle_SendsTicketUpdatedEmail_ToCcRecipient_WhenNoCustomerOrRequesterEmail()
    {
        var workLogRepo = Substitute.For<IRepository<WorkLog>>();
        workLogRepo.CreateAsync(Arg.Any<WorkLog>()).Returns(ci => ci.Arg<WorkLog>());

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAsync("t1").Returns(new Incident { Id = "t1", CcRecipients = new List<string> { "cc@b.com" } });
        incidentRepo.UpdateAsync(Arg.Any<Incident>()).Returns(ci => ci.Arg<Incident>());

        var activityRepo = Substitute.For<IRepository<ActivityLog>>();
        var notificationService = Substitute.For<INotificationService>();

        var emailService = Substitute.For<IEmailService>();
        emailService.SendEmailAsync(Arg.Any<EmailSendRequest>(), Arg.Any<CancellationToken>()).Returns(true);

        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(new[]
        {
            new EmailTemplate { Name = "TicketUpdated", Subject = "sub {{{TICKET_REF}}}", HtmlContent = "body" }
        });

        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAsync("t1").Returns(new Incident { Id = "t1", TrackingId = "INC-1", CcRecipients = new List<string> { "cc@b.com" } });

        var customerRepo = Substitute.For<IRepository<Customer>>();
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(ci => ci.Arg<TicketTimelineEvent>());
        timelineRepo.UpdateAsync(Arg.Any<TicketTimelineEvent>()).Returns(ci => ci.Arg<TicketTimelineEvent>());
        var timelineEventBus = Substitute.For<ITimelineEventBus>();
        var sanitizer = Substitute.For<IHtmlSanitizerService>();
        sanitizer.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var imageStorage = Substitute.For<IWorklogImageStorageService>();
        imageStorage.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        var plainTextConverter = Substitute.For<IHtmlToPlainTextConverter>();
        plainTextConverter.Convert(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var templateRenderer = Substitute.For<IEmailTemplateRenderer>();
        templateRenderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(ci => ci.Arg<string>());
        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        var tenantBrandingResolver = Substitute.For<ITenantBrandingResolver>();
        tenantBrandingResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TenantBrandingResolved());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PublicWebAppUrl"] = "https://app" })
            .Build();
        var publicTicketLinkSigner = Substitute.For<IPublicTicketLinkSigner>();
        publicTicketLinkSigner
            .GenerateToken(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>())
            .Returns("signed-token");

        var handler = new CreateWorkLogCommandHandler(
            workLogRepo,
            incidentRepo,
            activityRepo,
            notificationService,
            emailService,
            templateRepo,
            ticketRepo,
            customerRepo,
            timelineRepo,
            timelineEventBus,
            sanitizer,
            imageStorage,
            plainTextConverter,
            templateRenderer,
            layoutResolver,
            tenantBrandingResolver,
            publicTicketLinkSigner,
            config);

        await handler.Handle(new CreateWorkLogCommand("t1", 1, "note", "tech", "Tech User"), CancellationToken.None);

        await emailService.Received(1)
            .SendEmailAsync(
                Arg.Is<EmailSendRequest>(email => email.Recipients.Contains("cc@b.com") &&
                    email.Subject.Contains("INC-1") && email.Cc.Length == 0 && email.TicketId == "t1" && email.SuppressTimeline),
                Arg.Any<CancellationToken>());

        await timelineRepo.Received(1).UpdateAsync(Arg.Is<TicketTimelineEvent>(e =>
            e.TicketId == "t1" &&
            e.EventType == TimelineEventType.EmailDelivery &&
            e.EmailStatus == EmailDeliveryStatus.Delivered &&
            e.EmailRecipient == "cc@b.com"));
    }

    [Fact]
    public async Task Handle_LogsFailedEmailDelivery_WhenNoRecipientsExist()
    {
        var workLogRepo = Substitute.For<IRepository<WorkLog>>();
        workLogRepo.CreateAsync(Arg.Any<WorkLog>()).Returns(ci => ci.Arg<WorkLog>());

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAsync("t1").Returns(new Incident { Id = "t1" });
        incidentRepo.UpdateAsync(Arg.Any<Incident>()).Returns(ci => ci.Arg<Incident>());

        var activityRepo = Substitute.For<IRepository<ActivityLog>>();
        var notificationService = Substitute.For<INotificationService>();
        var emailService = Substitute.For<IEmailService>();

        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(new[]
        {
            new EmailTemplate { Name = "TicketUpdated", Subject = "sub {{{TICKET_REF}}}", HtmlContent = "body" }
        });

        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAsync("t1").Returns(new Incident { Id = "t1", TrackingId = "INC-1" });

        var customerRepo = Substitute.For<IRepository<Customer>>();
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(ci => ci.Arg<TicketTimelineEvent>());
        var timelineEventBus = Substitute.For<ITimelineEventBus>();
        var sanitizer = Substitute.For<IHtmlSanitizerService>();
        sanitizer.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var imageStorage = Substitute.For<IWorklogImageStorageService>();
        imageStorage.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        var plainTextConverter = Substitute.For<IHtmlToPlainTextConverter>();
        plainTextConverter.Convert(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var templateRenderer = Substitute.For<IEmailTemplateRenderer>();
        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        var tenantBrandingResolver = Substitute.For<ITenantBrandingResolver>();
        var config = new ConfigurationBuilder().Build();
        var publicTicketLinkSigner = Substitute.For<IPublicTicketLinkSigner>();

        var handler = new CreateWorkLogCommandHandler(
            workLogRepo,
            incidentRepo,
            activityRepo,
            notificationService,
            emailService,
            templateRepo,
            ticketRepo,
            customerRepo,
            timelineRepo,
            timelineEventBus,
            sanitizer,
            imageStorage,
            plainTextConverter,
            templateRenderer,
            layoutResolver,
            tenantBrandingResolver,
            publicTicketLinkSigner,
            config);

        await handler.Handle(new CreateWorkLogCommand("t1", 1, "note", "tech", "Tech User"), CancellationToken.None);

        await emailService.DidNotReceive().SendEmailAsync(Arg.Any<EmailSendRequest>(), Arg.Any<CancellationToken>());

        await timelineRepo.Received(1).CreateAsync(Arg.Is<TicketTimelineEvent>(e =>
            e.TicketId == "t1" &&
            e.EventType == TimelineEventType.EmailDelivery &&
            e.EmailStatus == EmailDeliveryStatus.Failed &&
            e.EmailRecipient == "(none)" &&
            e.MessageText != null &&
            e.MessageText.Contains("no requester")));
    }

    [Fact]
    public async Task Handle_DoesNotSendEmail_AndMarksTimeline_WhenInternalNote()
    {
        var workLogRepo = Substitute.For<IRepository<WorkLog>>();
        workLogRepo.CreateAsync(Arg.Any<WorkLog>()).Returns(ci => ci.Arg<WorkLog>());

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAsync("t1").Returns(new Incident { Id = "t1", CustomerId = "c1" });
        incidentRepo.UpdateAsync(Arg.Any<Incident>()).Returns(ci => ci.Arg<Incident>());

        var activityRepo = Substitute.For<IRepository<ActivityLog>>();
        var notificationService = Substitute.For<INotificationService>();

        var emailService = Substitute.For<IEmailService>();
        emailService.SendEmailAsync(Arg.Any<EmailSendRequest>(), Arg.Any<CancellationToken>()).Returns(true);

        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(new[]
        {
            new EmailTemplate { Name = "TicketUpdated", Subject = "sub {{{TICKET_REF}}}", HtmlContent = "body" }
        });

        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAsync("t1").Returns(new Incident { Id = "t1", TrackingId = "INC-1" });

        var customerRepo = Substitute.For<IRepository<Customer>>();
        customerRepo.GetAsync("c1").Returns(new Customer { Id = "c1", Email = "a@b.com", Name = "Cust" });
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(ci => ci.Arg<TicketTimelineEvent>());
        var timelineEventBus = Substitute.For<ITimelineEventBus>();
        var sanitizer = Substitute.For<IHtmlSanitizerService>();
        sanitizer.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var imageStorage = Substitute.For<IWorklogImageStorageService>();
        imageStorage.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        var plainTextConverter = Substitute.For<IHtmlToPlainTextConverter>();
        plainTextConverter.Convert(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var templateRenderer = Substitute.For<IEmailTemplateRenderer>();
        templateRenderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(ci => ci.Arg<string>());
        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        var tenantBrandingResolver = Substitute.For<ITenantBrandingResolver>();
        tenantBrandingResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TenantBrandingResolved());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PublicWebAppUrl"] = "https://app" })
            .Build();
        var publicTicketLinkSigner = Substitute.For<IPublicTicketLinkSigner>();
        publicTicketLinkSigner
            .GenerateToken(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>())
            .Returns("signed-token");

        var handler = new CreateWorkLogCommandHandler(
            workLogRepo,
            incidentRepo,
            activityRepo,
            notificationService,
            emailService,
            templateRepo,
            ticketRepo,
            customerRepo,
            timelineRepo,
            timelineEventBus,
            sanitizer,
            imageStorage,
            plainTextConverter,
            templateRenderer,
            layoutResolver,
            tenantBrandingResolver,
            publicTicketLinkSigner,
            config);

        var command = new CreateWorkLogCommand("t1", 1, "note", "tech", "Tech User", IsInternalNote: true);
        await handler.Handle(command, CancellationToken.None);

        await emailService.DidNotReceive().SendEmailAsync(Arg.Any<EmailSendRequest>(), Arg.Any<CancellationToken>());
        await notificationService.DidNotReceive().NotifyUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await workLogRepo.Received(1).CreateAsync(Arg.Is<WorkLog>(x => x.IsInternalNote));
        await timelineRepo.Received(1).CreateAsync(Arg.Is<TicketTimelineEvent>(x => x.EventType == TimelineEventType.InternalNote));
    }

    [Fact]
    public async Task Handle_PausesSla_AfterWorklogCreated()
    {
        var workLogRepo = Substitute.For<IRepository<WorkLog>>();
        workLogRepo.CreateAsync(Arg.Any<WorkLog>()).Returns(ci => ci.Arg<WorkLog>());

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAsync("t1").Returns(new Incident { Id = "t1", CustomerId = "c1" });
        incidentRepo.UpdateAsync(Arg.Any<Incident>()).Returns(ci => ci.Arg<Incident>());

        var activityRepo = Substitute.For<IRepository<ActivityLog>>();
        var notificationService = Substitute.For<INotificationService>();
        var emailService = Substitute.For<IEmailService>();
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Array.Empty<EmailTemplate>());
        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        var customerRepo = Substitute.For<IRepository<Customer>>();
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(ci => ci.Arg<TicketTimelineEvent>());
        var timelineEventBus = Substitute.For<ITimelineEventBus>();
        var sanitizer = Substitute.For<IHtmlSanitizerService>();
        sanitizer.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var imageStorage = Substitute.For<IWorklogImageStorageService>();
        imageStorage.ExtractAndStoreImagesAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(1));
        var plainTextConverter = Substitute.For<IHtmlToPlainTextConverter>();
        plainTextConverter.Convert(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var templateRenderer = Substitute.For<IEmailTemplateRenderer>();
        templateRenderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns(ci => ci.Arg<string>());
        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        var tenantBrandingResolver = Substitute.For<ITenantBrandingResolver>();
        tenantBrandingResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TenantBrandingResolved());
        var config = new ConfigurationBuilder().Build();
        var publicTicketLinkSigner = Substitute.For<IPublicTicketLinkSigner>();
        var ticketSlaService = Substitute.For<ITicketSlaService>();

        var handler = new CreateWorkLogCommandHandler(
            workLogRepo,
            incidentRepo,
            activityRepo,
            notificationService,
            emailService,
            templateRepo,
            ticketRepo,
            customerRepo,
            timelineRepo,
            timelineEventBus,
            sanitizer,
            imageStorage,
            plainTextConverter,
            templateRenderer,
            layoutResolver,
            tenantBrandingResolver,
            publicTicketLinkSigner,
            config,
            ticketSlaService);

        await handler.Handle(new CreateWorkLogCommand("t1", 1, "note", "tech", "Tech User"), CancellationToken.None);

        await ticketSlaService.Received(1).PauseAsync("t1", "tech", "AgentResponded");
    }
}
