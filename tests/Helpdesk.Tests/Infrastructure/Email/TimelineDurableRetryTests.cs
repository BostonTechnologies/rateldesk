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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_retry_reuses_history_and_only_completes_synchronous_delivery(bool durable)
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
        ((IDurableEmailService)email).QueuesDelivery.Returns(durable);
        email.SendEmailAsync(Arg.Any<EmailSendRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.Equal(durable ? delivery.Id : (Guid?)null, context.TimelineDeliveryId);
            Assert.Equal(delivery.Id, call.Arg<EmailSendRequest>().TimelineDeliveryId);
            Assert.True(call.Arg<EmailSendRequest>().SuppressTimeline);
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
        Assert.Equal(durable ? EmailDeliveryStatus.Pending : EmailDeliveryStatus.Delivered,
            (await db.TicketTimelineEvents.SingleAsync(x => x.Id == delivery.Id)).EmailStatus);
        Assert.Equal(1, await db.TicketTimelineEvents.CountAsync());
        Assert.Null(context.TimelineDeliveryId);
        await email.Received(1).SendEmailAsync(Arg.Is<EmailSendRequest>(request =>
            request.TicketId == ticket.Id && request.TimelineDeliveryId == delivery.Id && request.SuppressTimeline), Arg.Any<CancellationToken>());
    }
}
