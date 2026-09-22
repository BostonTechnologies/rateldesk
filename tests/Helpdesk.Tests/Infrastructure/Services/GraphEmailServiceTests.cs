using Helpdesk.Application.Events;
using Helpdesk.Application.Timeline;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.DTOs.Worklog;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Users.Item.SendMail;
using NSubstitute;
using Xunit;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Helpdesk.Tests.Infrastructure.Services;

public class GraphEmailServiceTests
{
    private const string MailboxAddress = "helpdesk@example.com";

    [Fact]
    public async Task SendEmailAsync_SelfOnly_DoesNotCallGraph()
    {
        var graphCallCount = 0;
        var service = CreateService((_, _, _) =>
        {
            graphCallCount++;
            return Task.CompletedTask;
        });

        var result = await service.SendEmailAsync(
            [" HELPDESK@EXAMPLE.COM "],
            "Loop notification",
            "<p>Do not send</p>",
            [MailboxAddress],
            suppressTimeline: true);

        Assert.False(result);
        Assert.Equal(0, graphCallCount);
    }

    [Fact]
    public async Task SendEmailAsync_MixedRecipients_RemovesSelfAndDeliversExternalRecipients()
    {
        string? senderMailbox = null;
        SendMailPostRequestBody? capturedRequest = null;
        var service = CreateService((mailbox, request, _) =>
        {
            senderMailbox = mailbox;
            capturedRequest = request;
            return Task.CompletedTask;
        });

        var result = await service.SendEmailAsync(
            [MailboxAddress, " customer@example.com ", "CUSTOMER@example.com"],
            "Ticket update",
            "<p>External delivery</p>",
            [" HELPDESK@EXAMPLE.COM ", "copy@example.com"],
            suppressTimeline: true);

        Assert.True(result);
        Assert.Equal(MailboxAddress, senderMailbox);
        Assert.NotNull(capturedRequest?.Message);
        var toRecipient = Assert.Single(capturedRequest.Message.ToRecipients!);
        var ccRecipient = Assert.Single(capturedRequest.Message.CcRecipients!);
        Assert.Equal("customer@example.com", toRecipient.EmailAddress?.Address);
        Assert.Equal("copy@example.com", ccRecipient.EmailAddress?.Address);
    }

    [Fact]
    public async Task Dedicated_inbound_source_changes_reply_to_but_preserves_pinned_outbound_sender()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new HelpdeskDbContext(new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options,
            Substitute.For<ITenantContext>(), new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "tenant-a", Name = "Tenant A" });
        db.Customers.Add(new Customer { Id = "requester", Email = "customer@example.test", Name = "Requester", OrganizationId = "tenant-a" });
        db.Incidents.Add(new Incident { Id = "ticket-a", OrganizationId = "tenant-a", CustomerId = "requester", TrackingId = "INC-123" });
        db.EmailInboxSettings.Add(new EmailInboxSettings { Id = Guid.NewGuid(), Scope = MailboxScope.Organization,
            OrganizationId = "tenant-a", MailboxAddress = "tenant-inbox@example.test", Enabled = true, SourceKey = "dedicated" });
        db.Set<MailboxMigrationState>().Add(new() { Completed = true, OutboundMailboxAddress = "pinned-sender@example.test" });
        await db.SaveChangesAsync();
        string? sender = null;
        SendMailPostRequestBody? captured = null;
        var service = CreateService((mailbox, request, _) => { sender = mailbox; captured = request; return Task.CompletedTask; }, db);
        Assert.True(await service.SendEmailAsync(["customer@example.test"], "Update", "<p>Hello</p>", ticketId: "ticket-a", suppressTimeline: true));
        Assert.Equal("pinned-sender@example.test", sender);
        Assert.Equal("tenant-inbox@example.test", Assert.Single(captured!.Message!.ReplyTo!).EmailAddress!.Address);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendEmailAsync(["customer@example.test"], "Update", "Body",
            ticketId: "ticket-a", replyTo: "wrong-global@example.test", suppressTimeline: true));
    }

    private static GraphEmailService CreateService(
        Func<string, SendMailPostRequestBody, CancellationToken, Task> sendMailAsync, HelpdeskDbContext? db = null)
    {
        var domainEvents = Substitute.For<IDomainEventPublisher>();
        domainEvents
            .PublishAsync(Arg.Any<DomainEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var emailSettingsProvider = Substitute.For<IEmailSettingsProvider>();
        emailSettingsProvider
            .GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<EmailInboxSettings> { new() { Id = Guid.NewGuid(), Enabled = true, BackgroundSyncEnabled = true, MailboxAddress = "must-not-send-as-dedicated@example.test" } }));

        return new GraphEmailService(
            Options.Create(new ExchangeEmailOptions
            {
                Enabled = true,
                TenantId = Guid.NewGuid().ToString(),
                ClientId = Guid.NewGuid().ToString(),
                ClientSecret = "test-secret",
                MailboxAddress = MailboxAddress
            }),
            Substitute.For<ILogger<GraphEmailService>>(),
            domainEvents,
            Substitute.For<ICorrelationContext>(),
            Substitute.For<IRepository<TicketTimelineEvent>>(),
            Substitute.For<ITimelineEventBus>(),
            emailSettingsProvider,
            sendMailAsync, db);
    }
}
