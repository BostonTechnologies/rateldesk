using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Tickets;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Shared.DTOs.PublicAccess;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Application.Services;

public sealed class TicketNotificationServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approval_notification_targets_decision_link_to_the_designated_approver_only(bool change)
    {
        var templates = CreateTemplateRepository(new EmailTemplate
        {
            Name = change ? "ChangeApprovalRequired" : "RequestApprovalRequired",
            Subject = "Review requested",
            HtmlContent = "{{{TICKET_LINK}}} {{{CHANGE_APPROVAL_LINK}}} {{{REQUEST_APPROVAL_LINK}}} {{{REQUEST_REJECT_LINK}}}"
        });
        var mail = Substitute.For<IEmailService>();
        var deliveries = new List<(string Recipient, string Body, IEnumerable<string>? Cc)>();
        mail.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IEnumerable<EmailAttachmentData>?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<bool>())
            .Returns(call =>
            {
                deliveries.Add((call.ArgAt<IEnumerable<string>>(0).Single(), call.ArgAt<string>(2), call.ArgAt<IEnumerable<string>?>(3)));
                return true;
            });
        var links = Substitute.For<IPublicLinkIssuer>();
        links.IssueAsync(Arg.Any<Ticket>(), Arg.Any<string>(), Arg.Any<PublicLinkPurpose>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(call => "https://public.example/public/link#" + call.ArgAt<PublicLinkPurpose>(2));
        var service = CreateService(templates, mail, links);
        const string approverEmail = "approver@example.test";
        var cc = new[] { "copy@example.test", "COPY@example.test", approverEmail };
        var sent = change
            ? await service.SendChangeApprovalRequiredAsync(new Change { Id = "test", TrackingId = "CHG-123" },
                new ChangeApproval { ApproverEmail = approverEmail }, "Example", "Requester", "Implementer", "Approver", cc)
            : await service.SendRequestApprovalRequiredAsync(new Request { Id = "test", TrackingId = "REQ-123" },
                new RequestTask { Id = "task" }, new RequestTaskApproval { ApproverEmail = approverEmail }, "Approver", "", cc);

        Assert.True(sent);
        Assert.Equal(2, deliveries.Count);
        var decision = change ? "#ChangeApproval" : "#RequestApproval";
        Assert.Equal(approverEmail, deliveries[0].Recipient);
        Assert.Contains(decision, deliveries[0].Body);
        Assert.Equal("copy@example.test", deliveries[1].Recipient);
        Assert.Contains(change ? "#ChangeView" : "#Ticket", deliveries[1].Body);
        Assert.DoesNotContain(decision, deliveries[1].Body);
        Assert.All(deliveries, delivery => Assert.Null(delivery.Cc));
    }

    [Fact]
    public async Task SendNewTicketConfirmation_Renders_Request_Type_Reference_And_Signed_Link()
    {
        var templates = CreateTemplateRepository(new EmailTemplate
        {
            Name = "NewTicketConfirmation",
            Subject = "Your {{{TICKET_TYPE}}} Has Been Created {{{TICKET_REF}}}",
            HtmlContent = "Hello {{{USER_NAME}}}: {{{TICKET_TYPE}}} {{{TICKET_REF}}} {{{TICKET_LINK}}}"
        });

        var emailService = Substitute.For<IEmailService>();
        string? subject = null;
        string? body = null;
        emailService.SendEmailAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Do<string>(x => subject = x),
                Arg.Do<string>(x => body = x),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<IEnumerable<EmailAttachmentData>?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns(true);

        var service = CreateService(templates, emailService);

        var sent = await service.SendNewTicketConfirmationAsync(
            new Request
            {
                Id = "request-1",
                TrackingId = "REQ-123",
                OrganizationId = "org-1"
            },
            "customer@example.com",
            "Customer");

        Assert.True(sent);
        Assert.Equal("Your Request Has Been Created REQ-123", subject);
        Assert.Contains("Request REQ-123", body);
        Assert.Contains("https://public.example/public/link#fixture-REQ-123", body);
    }

    [Fact]
    public async Task SendSelfServiceRequestCreated_Renders_Service_Request_Details_And_Link()
    {
        var templates = CreateTemplateRepository(new EmailTemplate
        {
            Name = "SelfServiceRequestCreated",
            Subject = "Created {{{TICKET_REF}}} {{{SERVICE_NAME}}}",
            HtmlContent = "{{{SERVICE_NAME}}} {{{REQUEST_TITLE}}} {{{REQUEST_DESCRIPTION}}} {{{TICKET_LINK}}}"
        });
        var emailService = Substitute.For<IEmailService>();
        string? subject = null;
        string? body = null;
        CaptureEmail(emailService, x => subject = x, x => body = x);
        var service = CreateService(templates, emailService);

        var sent = await service.SendSelfServiceRequestCreatedAsync(
            new Request
            {
                Id = "request-1",
                TrackingId = "REQ-123",
                Title = "Disk capacity report",
                Description = "Collect disk usage",
                OrganizationId = "org-1"
            },
            new RequestForm
            {
                Title = "Disk capacity report",
                Description = "Disk capacity report"
            },
            "customer@example.com",
            "Customer");

        Assert.True(sent);
        Assert.Equal("Created REQ-123 Disk capacity report", subject);
        Assert.Contains("Disk capacity report", body);
        Assert.Contains("Collect disk usage", body);
        Assert.Contains("https://public.example/public/link#fixture-REQ-123", body);
    }

    [Fact]
    public async Task SendSelfServiceRequestFailed_Renders_Incident_Reference_And_FailureReason()
    {
        var templates = CreateTemplateRepository(new EmailTemplate
        {
            Name = "SelfServiceRequestFailed",
            Subject = "Failed {{{TICKET_REF}}} {{{INCIDENT_REF}}}",
            HtmlContent = "{{{FAILURE_REASON}}} {{{INCIDENT_REF}}} {{{INCIDENT_LINK}}}"
        });
        var emailService = Substitute.For<IEmailService>();
        string? subject = null;
        string? body = null;
        CaptureEmail(emailService, x => subject = x, x => body = x);
        var service = CreateService(templates, emailService);

        var sent = await service.SendSelfServiceRequestFailedAsync(
            new Request { Id = "request-1", TrackingId = "REQ-123", Title = "Linux Disk Report" },
            new Incident { Id = "incident-1", TrackingId = "INC-999" },
            "External orchestration run failed",
            "customer@example.com",
            "Customer");

        Assert.True(sent);
        Assert.Equal("Failed REQ-123 INC-999", subject);
        Assert.Contains("External orchestration run failed", body);
        Assert.Contains("INC-999", body);
        Assert.Contains("https://public.example/public/link#fixture-INC-999", body);
    }

    [Fact]
    public async Task SendTicketResolved_Uses_Incident_Template_For_Incidents()
    {
        var templates = CreateTemplateRepository(new EmailTemplate
        {
            Name = "IncidentResolved",
            Subject = "Resolved {{{TICKET_REF}}}",
            HtmlContent = "{{{REQUEST_TITLE}}} {{{RESOLVED_AT}}}"
        });
        var emailService = Substitute.For<IEmailService>();
        string? subject = null;
        string? body = null;
        CaptureEmail(emailService, x => subject = x, x => body = x);
        var service = CreateService(templates, emailService);

        var sent = await service.SendTicketResolvedAsync(
            new Incident
            {
                Id = "incident-1",
                TrackingId = "INC-123",
                Title = "Printer down",
                ClosedAt = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero)
            },
            "customer@example.com",
            "Customer");

        Assert.True(sent);
        Assert.Equal("Resolved INC-123", subject);
        Assert.Contains("Printer down", body);
        Assert.Contains("2026-05-15 12:00 UTC", body);
    }

    [Fact]
    public async Task SendTicketResolved_Skips_Email_When_Ticket_Is_Excluded()
    {
        var templates = CreateTemplateRepository(new EmailTemplate
        {
            Name = "IncidentResolved",
            Subject = "Resolved {{{TICKET_REF}}}",
            HtmlContent = "{{{REQUEST_TITLE}}}"
        });
        var emailService = Substitute.For<IEmailService>();
        var service = CreateService(templates, emailService);

        var sent = await service.SendTicketResolvedAsync(
            new Incident
            {
                Id = "incident-1",
                TrackingId = "INC-SPAM",
                Title = "Marketing email",
                EmailExclusionReason = TicketEmailExclusionReason.MarketingSpam
            },
            "vendor@example.com",
            "Vendor");

        Assert.False(sent);
        await emailService.DidNotReceiveWithAnyArgs().SendEmailAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData("", "customer@example.com")]
    [InlineData("INC-123", "")]
    public async Task SendNewTicketConfirmation_Skips_Malformed_Public_Link(string trackingId, string recipient)
    {
        var templates = CreateTemplateRepository(new EmailTemplate
        {
            Name = "NewTicketConfirmation",
            Subject = "Subject",
            HtmlContent = "Body"
        });
        var emailService = Substitute.For<IEmailService>();
        var service = CreateService(templates, emailService);

        var sent = await service.SendNewTicketConfirmationAsync(
            new Incident { Id = "incident-1", TrackingId = trackingId },
            recipient,
            "Customer");

        Assert.False(sent);
        await emailService.DidNotReceiveWithAnyArgs().SendEmailAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    private static TicketNotificationService CreateService(
        IRepository<EmailTemplate> templates,
        IEmailService emailService, IPublicLinkIssuer? publicLinks = null)
    {
        var layoutResolver = Substitute.For<IEmailLayoutResolver>();
        layoutResolver.ResolveAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((EmailLayout?)null);
        var brandingResolver = Substitute.For<ITenantBrandingResolver>();
        brandingResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TenantBrandingResolved
            {
                BrandName = "RatelDesk",
                FromName = "Helpdesk"
            });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicWebAppUrl"] = "https://public.example"
            })
            .Build();

        return new TicketNotificationService(
            templates,
            layoutResolver,
            brandingResolver,
            emailService,
            new EmailTemplateRenderer(new TemplateEngine()),
            publicLinks ?? new TestPublicLinkIssuer(),
            config,
            NullLogger<TicketNotificationService>.Instance);
    }

    private static IRepository<EmailTemplate> CreateTemplateRepository(params EmailTemplate[] templates)
    {
        var repository = Substitute.For<IRepository<EmailTemplate>>();
        repository.GetAllAsync().Returns(templates);
        return repository;
    }

    private static void CaptureEmail(
        IEmailService emailService,
        Action<string> subject,
        Action<string> body)
    {
        emailService.SendEmailAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Do<string>(x => subject(x)),
                Arg.Do<string>(x => body(x)),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<IEnumerable<EmailAttachmentData>?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<bool>())
            .Returns(true);
    }

    private sealed class TestPublicLinkIssuer : IPublicLinkIssuer
    {
        public Task<string> IssueAsync(Ticket ticket, string recipient, Helpdesk.Shared.DTOs.PublicAccess.PublicLinkPurpose purpose,
            string? applicationUrl, CancellationToken cancellationToken, string? taskId = null) =>
            Task.FromResult($"https://public.example/public/link#fixture-{ticket.TrackingId}");
    }
}
