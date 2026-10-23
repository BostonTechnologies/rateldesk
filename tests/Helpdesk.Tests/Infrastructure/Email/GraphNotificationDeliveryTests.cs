using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Events;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class GraphNotificationDeliveryTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Welcome_notification_distinguishes_durable_acceptance_from_delivery(bool durable, bool accepted)
    {
        var email = Substitute.For<IEmailService, IDurableEmailService>();
        ((IDurableEmailService)email).QueuesDelivery.Returns(durable);
        email.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<EmailAttachmentData>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(accepted);
        var templates = Substitute.For<IRepository<EmailTemplate>>();
        templates.GetAllAsync().Returns([new EmailTemplate { Name = "WelcomeNewUser", Subject = "Welcome", HtmlContent = "<p>Welcome</p>" }]);
        var branding = Substitute.For<ITenantBrandingResolver>();
        branding.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new TenantBrandingResolved());
        var events = Substitute.For<IDomainEventPublisher>();
        var processor = new GraphEmailProcessor(Substitute.For<IRequestSender>(), NullLogger<GraphEmailProcessor>.Instance,
            Substitute.For<ITenantProvisioningService>(), Substitute.For<ITicketNotificationService>(),
            Substitute.For<IRepository<BlockedEntity>>(), email, templates, Substitute.For<IRepository<Incident>>(),
            Substitute.For<IRepository<Ticket>>(), Substitute.For<IRepository<TicketTimelineEvent>>(),
            Substitute.For<ITicketAttachmentService>(), Substitute.For<IInboundInlineImageResolver>(),
            Substitute.For<IEmailTemplateRenderer>(), Substitute.For<IEmailLayoutResolver>(), branding, events);

        Assert.Equal(accepted, await processor.SendNotificationAsync("WelcomeNewUser", "customer@example.test", tenantId: "synthetic-tenant"));

        if (durable && accepted)
        {
            await events.Received(1).PublishAsync(Arg.Is<EmailQueuedEvent>(e => e.Recipient == "customer@example.test" &&
                e.TemplateName == "WelcomeNewUser" && e.TenantId == "synthetic-tenant"), Arg.Any<CancellationToken>());
            await events.DidNotReceive().PublishAsync(Arg.Any<EmailSentEvent>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await events.Received(1).PublishAsync(Arg.Is<EmailSentEvent>(e => e.Success == accepted), Arg.Any<CancellationToken>());
            await events.DidNotReceive().PublishAsync(Arg.Any<EmailQueuedEvent>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Queued_domain_notification_reports_information_without_delivery_success()
    {
        var notifications = Substitute.For<INotificationService>();
        var publisher = new NotificationDomainEventPublisher(notifications, NullLogger<NotificationDomainEventPublisher>.Instance);
        await publisher.PublishAsync(new EmailQueuedEvent("customer@example.test", "WelcomeNewUser", "synthetic-tenant", null, "synthetic-correlation"), default);
        await notifications.Received(1).CreateNotificationAsync(Arg.Is<CreateNotificationRequest>(request =>
            request.Title == "EmailQueued" && request.Severity == NotificationSeverity.Info &&
            request.TenantId == "synthetic-tenant" && !request.Message.Contains("EmailSent") && !request.Message.Contains("success")),
            Arg.Any<CancellationToken>());
    }
}
