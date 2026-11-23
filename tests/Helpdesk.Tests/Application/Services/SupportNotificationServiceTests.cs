using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Application.Services;

public sealed class SupportNotificationServiceTests
{
    [Fact]
    public async Task NotifyTicketCreatedUnassigned_EmailFailure_MarksDeliveryFailed()
    {
        var fixture = CreateFixture();
        fixture.Email.SendEmailAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<IEnumerable<EmailAttachmentData>?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns(false);

        await fixture.Service.NotifyTicketCreatedUnassignedAsync(new Incident
        {
            Id = "ticket-1",
            TrackingId = "INC-1",
            Title = "Printer",
            OrganizationId = "org-1"
        });

        var delivery = Assert.Single(await fixture.Deliveries.GetAllAsync());
        Assert.Equal(SupportNotificationDeliveryStatus.Failed, delivery.Status);
        Assert.NotNull(delivery.FailedUtc);
    }

    [Fact]
    public async Task NotifyTicketCreatedUnassigned_ExistingDeliverySkipsSend()
    {
        var fixture = CreateFixture();
        await fixture.Deliveries.CreateAsync(new SupportNotificationDelivery
        {
            Id = "delivery-1",
            DeduplicationKey = "support:ticket-created-unassigned:ticket-1:user:user-1",
            TicketId = "ticket-1",
            EventType = SupportNotificationEventType.TicketCreatedUnassigned,
            RecipientUserId = "user-1",
            RecipientEmail = "agent@example.test",
            Status = SupportNotificationDeliveryStatus.Sent
        });

        await fixture.Service.NotifyTicketCreatedUnassignedAsync(new Incident
        {
            Id = "ticket-1",
            TrackingId = "INC-1",
            Title = "Printer",
            OrganizationId = "org-1"
        });

        await fixture.Email.DidNotReceiveWithAnyArgs().SendEmailAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<IEnumerable<EmailAttachmentData>?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task NotifyTicketAssigned_SendsOnlyWhenAssignmentChanges()
    {
        var fixture = CreateFixture(SupportNotificationEventType.TicketAssigned);
        fixture.Email.SendEmailAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<IEnumerable<EmailAttachmentData>?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns(true);

        var ticket = new Incident
        {
            Id = "ticket-1",
            TrackingId = "INC-1",
            Title = "Printer",
            OrganizationId = "org-1"
        };

        await fixture.Service.NotifyTicketAssignedAsync(ticket, "user-1", "user-1");
        Assert.Empty(await fixture.Deliveries.GetAllAsync());

        await fixture.Service.NotifyTicketAssignedAsync(ticket, null, "user-1");
        var delivery = Assert.Single(await fixture.Deliveries.GetAllAsync());
        Assert.Equal(SupportNotificationDeliveryStatus.Sent, delivery.Status);
    }

    private static Fixture CreateFixture(
        SupportNotificationEventType eventType = SupportNotificationEventType.TicketCreatedUnassigned)
    {
        var resolver = Substitute.For<ISupportNotificationRecipientResolver>();
        resolver.ResolveTicketEventRecipientsAsync(
                Arg.Any<Ticket>(),
                SupportNotificationEventType.TicketCreatedUnassigned,
                SupportNotificationChannel.Email,
                Arg.Any<CancellationToken>())
            .Returns([new SupportNotificationRecipient("user-1", "Agent", "agent@example.test")]);
        resolver.ResolveAssignmentRecipientsAsync(
                Arg.Any<Ticket>(),
                "user-1",
                SupportNotificationChannel.Email,
                Arg.Any<CancellationToken>())
            .Returns([new SupportNotificationRecipient("user-1", "Agent", "agent@example.test")]);

        var deliveries = new InMemoryRepository<SupportNotificationDelivery>();
        var template = new EmailTemplate
        {
            Name = eventType == SupportNotificationEventType.TicketAssigned
                ? "SupportTicketAssigned"
                : "SupportTicketCreatedUnassigned",
            Subject = "Subject {{{TICKET_REF}}}",
            HtmlContent = "Body {{{TICKET_REF}}}"
        };
        var templates = Substitute.For<IRepository<EmailTemplate>>();
        templates.GetAllAsync().Returns([template]);

        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        layoutResolver.ResolveAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((EmailLayout?)null);
        var brandingResolver = Substitute.For<ITenantBrandingResolver>();
        brandingResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TenantBrandingResolved());
        var signer = Substitute.For<IPublicLinkIssuer>();
        signer.IssueAsync(Arg.Any<Ticket>(), Arg.Any<string>(), Arg.Any<Helpdesk.Shared.DTOs.PublicAccess.PublicLinkPurpose>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("https://desk.example.test/public/link#synthetic-fixture");
        var email = Substitute.For<IEmailService>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicWebAppUrl"] = "https://public.example"
            })
            .Build();

        var service = new SupportNotificationService(
            resolver,
            deliveries,
            templates,
            layoutResolver,
            brandingResolver,
            new EmailTemplateRenderer(new TemplateEngine()),
            email,
            signer,
            configuration,
            NullLogger<SupportNotificationService>.Instance);

        return new Fixture(service, deliveries, email);
    }

    private sealed record Fixture(
        SupportNotificationService Service,
        InMemoryRepository<SupportNotificationDelivery> Deliveries,
        IEmailService Email);
}
