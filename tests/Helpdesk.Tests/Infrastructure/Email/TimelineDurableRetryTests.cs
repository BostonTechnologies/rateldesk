using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class TimelineDurableRetryTests
{
    [Fact]
    public async Task Legacy_timeline_retry_queues_with_original_delivery_id_and_remains_pending()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var tenant = Substitute.For<ITenantContext>();
        tenant.IsHelpdeskAdmin.Returns(true);
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(connection, x => x.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations")).Options;
        await using var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        await db.Database.MigrateAsync();
        var ticket = new Incident { TrackingId = "INC-RETRY-1", Title = "A ticket" };
        var delivery = new TicketTimelineEvent
        {
            TicketId = ticket.Id, EventType = TimelineEventType.EmailDelivery,
            EmailStatus = EmailDeliveryStatus.Failed, EmailRecipient = "requester@example.test",
            MessageHtml = "<p>An update</p>"
        };
        db.Incidents.Add(ticket);
        db.EmailTemplates.Add(new EmailTemplate
        {
            Name = "TicketUpdated", Subject = "Update {{{TICKET_REF}}}", HtmlContent = "<p>Update</p>"
        });
        db.TicketTimelineEvents.Add(delivery);
        await db.SaveChangesAsync();

        var context = new IngressEffectContext();
        var email = Substitute.For<IEmailService, IDurableEmailService>();
        ((IDurableEmailService)email).QueuesDelivery.Returns(true);
        email.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>(),
            Arg.Any<IEnumerable<EmailAttachmentData>?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<bool>()).Returns(call =>
        {
            Assert.Equal(delivery.Id, context.TimelineDeliveryId);
            Assert.True(call.ArgAt<bool>(9));
            return Task.FromResult(true);
        });
        var renderer = Substitute.For<IEmailTemplateRenderer>();
        renderer.Render(Arg.Any<string>(), Arg.Any<EmailTemplateContext>()).Returns("<p>Rendered</p>");
        var layouts = Substitute.For<IEmailLayoutResolver>();
        layouts.ResolveAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EmailLayout?>(null));
        var branding = Substitute.For<ITenantBrandingResolver>();
        branding.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TenantBrandingResolved());
        var signer = Substitute.For<IPublicTicketLinkSigner>();
        signer.GenerateToken(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>())
            .Returns("signed-token");
        var events = Substitute.For<ITimelineEventBus>();
        var service = new TimelineService(db, email, renderer, layouts, branding, signer, events,
            new ConfigurationBuilder().Build(), effectContext: context);

        await service.RetryEmailAsync(delivery.Id, "admin", default);

        db.ChangeTracker.Clear();
        Assert.Equal(EmailDeliveryStatus.Pending,
            (await db.TicketTimelineEvents.SingleAsync(x => x.Id == delivery.Id)).EmailStatus);
        Assert.Equal(1, await db.TicketTimelineEvents.CountAsync());
        Assert.Null(context.TimelineDeliveryId);
        await email.Received(1).SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>(),
            ticket.Id, Arg.Any<IEnumerable<EmailAttachmentData>?>(), Arg.Any<string?>(), Arg.Any<string?>(), true);
    }
}
