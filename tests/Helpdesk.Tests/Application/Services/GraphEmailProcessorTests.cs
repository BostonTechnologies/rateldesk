using System.Collections.Generic;
using Helpdesk.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.AI;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Attachment;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using GraphAttachment = Microsoft.Graph.Models.Attachment;
using Microsoft.Graph.Models;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Application.Services;

public class GraphEmailProcessorTests
{
    private static GraphEmailProcessor CreateProcessor(
        IRequestSender requestSender,
        IRepository<Incident> incidentRepo,
        ITicketAttachmentService attachmentService,
        IEmailService emailService,
        IRepository<EmailTemplate> templateRepo,
        IRepository<Ticket>? ticketRepo = null,
        IRepository<TicketTimelineEvent>? timelineRepo = null,
        ITenantProvisioningService? tenantProvisioningService = null,
        ITicketNotificationService? ticketNotificationService = null,
        IInlineImageStorageService? inlineImageStorage = null)
    {
        if (timelineRepo is null)
        {
            timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
            timelineRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<TicketTimelineEvent>>(Array.Empty<TicketTimelineEvent>()));
            timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(call => call.Arg<TicketTimelineEvent>());
        }

        if (ticketRepo is null)
        {
            ticketRepo = Substitute.For<IRepository<Ticket>>();
            ticketRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>()));
            ticketRepo.GetAsync(Arg.Any<string>()).Returns((Ticket?)null);
            ticketRepo.UpdateAsync(Arg.Any<Ticket>()).Returns(call => call.Arg<Ticket>());
        }

        return new GraphEmailProcessor(
            requestSender,
            Substitute.For<ILogger<GraphEmailProcessor>>(),
            tenantProvisioningService ?? Substitute.For<ITenantProvisioningService>(),
            ticketNotificationService ?? Substitute.For<ITicketNotificationService>(),
            Substitute.For<IRepository<BlockedEntity>>(),
            emailService,
            templateRepo,
            incidentRepo,
            ticketRepo,
            timelineRepo,
            attachmentService,
            new InboundInlineImageResolver(inlineImageStorage ?? Substitute.For<IInlineImageStorageService>(), NullLogger<InboundInlineImageResolver>.Instance),
            Substitute.For<IEmailTemplateRenderer>(),
            Substitute.For<IEmailLayoutResolver>(),
            Substitute.For<ITenantBrandingResolver>());
    }

    [Fact]
    public async Task ProcessIncidentAsync_DuplicateNamesAndLaterReply_PreserveEachImagePayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "helpdesk-783-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new Helpdesk.Infrastructure.Storage.StorageOptions
            {
                RootPath = root,
                ImageSigningSecret = "test-image-signing-secret"
            });
            var storage = new Helpdesk.Infrastructure.Storage.InlineImageStorageService(Substitute.For<IHostEnvironment>(), options,
                new Helpdesk.Infrastructure.Storage.ImageLinkSigner(options, NullLogger<Helpdesk.Infrastructure.Storage.ImageLinkSigner>.Instance),
                NullLogger<Helpdesk.Infrastructure.Storage.InlineImageStorageService>.Instance);
            var incident = new Incident { OrganizationId = "org", Id = "inc-783", TrackingId = "INC-783" };
            var repo = Substitute.For<IRepository<Incident>>();
            repo.GetAllAsync().Returns(Array.Empty<Incident>());
            var sender = Substitute.For<IRequestSender>();
            sender.Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>()).Returns(incident);
            CreateWorkLogCommand? reply = null;
            sender.Send(Arg.Do<CreateWorkLogCommand>(command => reply = command), Arg.Any<CancellationToken>()).Returns(new WorkLog());
            var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
            templateRepo.GetAllAsync().Returns(Array.Empty<EmailTemplate>());
            var processor = CreateProcessor(sender, repo, Substitute.For<ITicketAttachmentService>(), Substitute.For<IEmailService>(), templateRepo, inlineImageStorage: storage);
            var customer = new Customer { Id = "customer", OrganizationId = "org", Email = "customer@example.test", Name = "Customer" };
            var message = new Message { Subject = "Screenshots", Body = new ItemBody { ContentType = BodyType.Html, Content = "<img src='cid:body'><img src='cid:signature'>" } };
            FileAttachment Image(string cid, byte content) => new() { Name = "image001.png", ContentId = cid, ContentBytes = [content], ContentType = "image/png" };
            await processor.ProcessIncidentAsync(message, [Image("signature", 2), Image("body", 1)], customer, CancellationToken.None);
            var originalHtml = incident.OriginalEmailHtml;
            repo.GetAllAsync().Returns(new[] { incident });
            message.Subject = "Re: INC-783";
            await processor.ProcessIncidentAsync(message, [Image("body", 3), Image("signature", 2)], customer, CancellationToken.None);
            Assert.Equal(originalHtml, incident.OriginalEmailHtml);
            Assert.NotNull(reply);
            Assert.NotNull(reply.Notes);
            async Task<byte[][]> ReadImages(string html)
            {
                var document = new HtmlAgilityPack.HtmlDocument();
                document.LoadHtml(html);
                var payloads = new List<byte[]>();
                foreach (var node in document.DocumentNode.Descendants("img"))
                {
                    var url = new Uri("https://test" + node.Attributes["src"].DeEntitizeValue);
                    payloads.Add(await File.ReadAllBytesAsync(Path.Combine(root, "incidents", incident.Id, "inline", Path.GetFileName(url.AbsolutePath))));
                }
                return payloads.ToArray();
            }
            var originalBytes = await ReadImages(originalHtml!);
            var replyBytes = await ReadImages(reply.Notes);
            Assert.Equal(new byte[] { 1 }, originalBytes[0]);
            Assert.Equal(new byte[] { 2 }, originalBytes[1]);
            Assert.Equal(new byte[] { 3 }, replyBytes[0]);
            Assert.Equal(new byte[] { 2 }, replyBytes[1]);
            Assert.Equal(3, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProcessIncidentAsync_CreatesNewIncident_WhenNoReference()
    {
        var mediator = Substitute.For<IRequestSender>();
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(Array.Empty<Incident>()));
        var created = new Incident { OrganizationId = "org1", Id = "1", TrackingId = "INC-NEW-1" };
        CreateIncidentCommand? capturedCmd = null;
        mediator.Send(Arg.Do<CreateIncidentCommand>(c => capturedCmd = c), Arg.Any<CancellationToken>()).Returns(created);
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var tenant = Substitute.For<ITenantContext>();
        var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        var attachmentService = new TicketAttachmentService(db, new Helpdesk.Infrastructure.Storage.TicketAttachmentFileStore(env, Microsoft.Extensions.Options.Options.Create(new Helpdesk.Infrastructure.Storage.StorageOptions { RootPath = Path.Combine(tempRoot, "storage") })));
        var emailService = Substitute.For<IEmailService>();
        emailService.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<EmailTemplate>>(new[]
        {
            new EmailTemplate { Name = "NewTicketConfirmation", Subject = "sub {{{TICKET_REF}}}", HtmlContent = "body" }
        }));
        var inlineImageStorage = Substitute.For<IInlineImageStorageService>();
        inlineImageStorage.SaveIncidentInlineImageAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>())
            .Returns(callInfo =>
                $"/api/incidents/{callInfo.ArgAt<string>(0)}/images/{callInfo.ArgAt<string>(1)}");

        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<TicketTimelineEvent>>(Array.Empty<TicketTimelineEvent>()));
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(call => call.Arg<TicketTimelineEvent>());

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            attachmentService,
            emailService,
            templateRepo,
            timelineRepo: timelineRepo,
            inlineImageStorage: inlineImageStorage);

        var message = new Message
        {
            Subject = "Help",
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = "<p>body</p><img src=\"cid:img-1\"/>"
            },
            CcRecipients = new List<Recipient>
            {
                new() { EmailAddress = new EmailAddress { Address = " Friend1@example.com " } },
                new() { EmailAddress = new EmailAddress { Address = "friend2@example.com" } },
                new() { EmailAddress = new EmailAddress { Address = "FRIEND1@example.com" } },
                new() { EmailAddress = new EmailAddress { Address = "a@b.com" } } // requester
            }
        };
        var customer = new Customer { Id = "cust1", OrganizationId = "org1", Email = "a@b.com", Name = "Cust" };
        var attachments = new List<GraphAttachment>
        {
            new FileAttachment
            {
                Name = "file.txt",
                ContentId = "unreferenced-download",
                ContentType = "text/plain",
                ContentBytes = System.Text.Encoding.UTF8.GetBytes("hi")
            },
            new FileAttachment
            {
                Name = "inline.png",
                ContentType = "image/png",
                ContentId = "<img-1>",
                ContentBytes = new byte[] { 1, 2, 3 }
            }
        };

        var result = await processor.ProcessIncidentAsync(message, attachments, customer, CancellationToken.None, "<msg-new@example.test>");

        Assert.Equal(created, result);
        await mediator.Received(1).Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        Assert.Equal("a@b.com", capturedCmd!.RequesterEmail);
        Assert.Contains("friend1@example.com", capturedCmd.CcRecipients!);
        Assert.Contains("friend2@example.com", capturedCmd.CcRecipients!);
        Assert.DoesNotContain("a@b.com", capturedCmd.CcRecipients!);
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
        Assert.Single(db.Attachments);
        var saved = db.Attachments.First();
        Assert.True(File.Exists(Path.Combine(tempRoot, "storage", "attachments", saved.FilePath)));
        Assert.Equal(TicketState.WaitingReply, created.State);
        Assert.NotNull(created.UpdatedAt);
        Assert.Equal(customer.Name, created.LastReplierName);
        Assert.Contains($"/api/incidents/{created.Id}/images/inline.png", created.OriginalEmailHtml);
        Assert.DoesNotContain("cid:img-1", created.OriginalEmailHtml);
        await incidentRepo.Received(1).UpdateAsync(created);
        await timelineRepo.Received(1).CreateAsync(Arg.Is<TicketTimelineEvent>(e =>
            e.TicketId == created.Id &&
            e.EventType == TimelineEventType.CustomerReply &&
            e.CreatedByUserId == "<msg-new@example.test>" &&
            e.MessageText == "Inbound email received."));
        await inlineImageStorage.Received(1).SaveIncidentInlineImageAsync(created.Id, "inline.png", Arg.Any<byte[]>());
    }

    [Fact]
    public async Task ProcessIncidentAsync_AddsWorkLog_WhenReferenceExists()
    {
        var existing = new Incident { OrganizationId = "org1", Id = "2", TrackingId = "INC-EXIST-123" };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));
        var mediator = Substitute.For<IRequestSender>();
        mediator.Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>()).Returns(new WorkLog());
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var tenant = Substitute.For<ITenantContext>();
        var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        var attachmentService = new TicketAttachmentService(db, new Helpdesk.Infrastructure.Storage.TicketAttachmentFileStore(env, Microsoft.Extensions.Options.Options.Create(new Helpdesk.Infrastructure.Storage.StorageOptions { RootPath = Path.Combine(tempRoot, "storage") })));
        var emailService = Substitute.For<IEmailService>();
        emailService.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<EmailTemplate>>(Array.Empty<EmailTemplate>()));
        var inlineImageStorage = Substitute.For<IInlineImageStorageService>();
        inlineImageStorage.SaveIncidentInlineImageAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<byte[]>())
            .Returns(callInfo =>
                $"/api/incidents/{callInfo.ArgAt<string>(0)}/images/{callInfo.ArgAt<string>(1)}");

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            attachmentService,
            emailService,
            templateRepo,
            inlineImageStorage: inlineImageStorage);

        var message = new Message
        {
            Subject = "Re: INC-EXIST-123",
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = "<html><head><style>.x{color:red;}</style></head><body><p>Hello team</p><img src=\"cid:img-2\"/><div>Thanks</div><blockquote>old thread</blockquote></body></html>"
            },
            From = new Recipient { EmailAddress = new EmailAddress { Address = "customer@example.com", Name = "Customer Name" } }
        };
        var customer = new Customer { Id = "cust1", OrganizationId = "org1", Email = "a@b.com", Name = "Cust" };
        var attachments = new List<GraphAttachment>
        {
            new FileAttachment
            {
                Name = "file.txt",
                ContentId = "unreferenced-download",
                ContentType = "text/plain",
                ContentBytes = System.Text.Encoding.UTF8.GetBytes("hi")
            },
            new FileAttachment
            {
                Name = "reply-inline.png",
                ContentType = "image/png",
                ContentId = "<img-2>",
                ContentBytes = new byte[] { 1, 2, 3 }
            }
        };

        var result = await processor.ProcessIncidentAsync(message, attachments, customer, CancellationToken.None);

        Assert.Equal(existing, result);
        await mediator.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(
            Arg.Is<CreateWorkLogCommand>(c =>
                c.NotifyCustomer == false &&
                c.EventType == TimelineEventType.CustomerReply &&
                c.EventCreatedByUserId == "customer@example.com" &&
                c.EventCreatedByUserName == "customer@example.com" &&
                c.Notes == $"<html><head><style>.x{{color:red;}}</style></head><body><p>Hello team</p><img src=\"/api/incidents/{existing.Id}/images/reply-inline.png\"/><div>Thanks</div><blockquote>old thread</blockquote></body></html>"),
            Arg.Any<CancellationToken>());
        Assert.Single(db.Attachments);
        var saved = db.Attachments.First();
        Assert.True(File.Exists(Path.Combine(tempRoot, "storage", "attachments", saved.FilePath)));
        Assert.Equal(TicketState.WaitingReply, existing.State);
        Assert.NotNull(existing.UpdatedAt);
        Assert.Equal(customer.Name, existing.LastReplierName);
        await incidentRepo.Received(1).UpdateAsync(existing);
        await inlineImageStorage.Received(1).SaveIncidentInlineImageAsync(existing.Id, "reply-inline.png", Arg.Any<byte[]>());
    }

    [Fact]
    public async Task ProcessIncidentAsync_IgnoresReferencedMarketingSpamTicket()
    {
        var existing = new Incident
        { OrganizationId = "org1",
            Id = "spam-incident",
            TrackingId = "INC-SPAM-123",
            State = TicketState.Resolved,
            EmailExclusionReason = TicketEmailExclusionReason.MarketingSpam
        };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));
        var mediator = Substitute.For<IRequestSender>();
        var attachmentService = Substitute.For<ITicketAttachmentService>();
        var emailService = Substitute.For<IEmailService>();
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<EmailTemplate>>(Array.Empty<EmailTemplate>()));
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<TicketTimelineEvent>>(Array.Empty<TicketTimelineEvent>()));
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(call => call.Arg<TicketTimelineEvent>());

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            attachmentService,
            emailService,
            templateRepo,
            timelineRepo: timelineRepo);

        var result = await processor.ProcessIncidentAsync(
            new Message
            {
                Subject = "Re: INC-SPAM-123",
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = "<p>please stop emailing us</p>"
                },
                From = new Recipient { EmailAddress = new EmailAddress { Address = "vendor@example.com", Name = "Vendor" } }
            },
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "vendor@example.com", Name = "Vendor" },
            CancellationToken.None,
            "<spam-reply@example.test>");

        Assert.Equal(existing, result);
        Assert.Equal(TicketState.Resolved, existing.State);
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
        await incidentRepo.DidNotReceive().UpdateAsync(Arg.Any<Incident>());
        await timelineRepo.Received(1).CreateAsync(Arg.Is<TicketTimelineEvent>(e =>
            e.TicketId == existing.Id &&
            e.EventType == TimelineEventType.CustomerReply &&
            e.CreatedByUserId == "<spam-reply@example.test>"));
    }

    [Fact]
    public async Task ProcessIncidentAsync_ReturnsExistingIncident_WhenInboundMessageAlreadyRecorded()
    {
        var existing = new Incident { OrganizationId = "org1", Id = "2", TrackingId = "INC-EXIST-123" };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));
        incidentRepo.GetAsync(existing.Id).Returns(existing);

        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<TicketTimelineEvent>>(new[]
        {
            new TicketTimelineEvent
            {
                TicketId = existing.Id,
                EventType = TimelineEventType.CustomerReply,
                CreatedByUserId = "<msg-duplicate@example.test>"
            }
        }));

        var mediator = Substitute.For<IRequestSender>();
        var attachmentService = Substitute.For<ITicketAttachmentService>();
        attachmentService.SaveAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<AttachmentUpload>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<AttachmentDto>>(Array.Empty<AttachmentDto>()));
        var emailService = Substitute.For<IEmailService>();
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<EmailTemplate>>(Array.Empty<EmailTemplate>()));

        var processor = CreateProcessor(mediator, incidentRepo, attachmentService, emailService, templateRepo, timelineRepo: timelineRepo);

        var result = await processor.ProcessIncidentAsync(
            new Message { Subject = "New inbound subject", Body = new ItemBody { Content = "body" } },
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "customer@example.test", Name = "Cust" },
            CancellationToken.None,
            "<msg-duplicate@example.test>");

        Assert.Equal(existing, result);
        await mediator.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        await timelineRepo.DidNotReceive().CreateAsync(Arg.Any<TicketTimelineEvent>());
    }

    [Fact]
    public async Task ProcessIncidentAsync_DoesNotAddDuplicateWorkLog_WhenMessageIdAlreadyProcessed()
    {
        var existing = new Incident { OrganizationId = "org1", Id = "2", TrackingId = "INC-EXIST-123" };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));

        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<TicketTimelineEvent>>(new[]
        {
            new TicketTimelineEvent
            {
                TicketId = existing.Id,
                EventType = TimelineEventType.CustomerReply,
                CreatedByUserId = "<msg-123@example.test>"
            }
        }));

        var mediator = Substitute.For<IRequestSender>();
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(tempRoot);
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var tenant = Substitute.For<ITenantContext>();
        var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
        var attachmentService = new TicketAttachmentService(db, new Helpdesk.Infrastructure.Storage.TicketAttachmentFileStore(env, Microsoft.Extensions.Options.Options.Create(new Helpdesk.Infrastructure.Storage.StorageOptions { RootPath = Path.Combine(tempRoot, "storage") })));
        var emailService = Substitute.For<IEmailService>();
        emailService.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<EmailTemplate>>(Array.Empty<EmailTemplate>()));

        var processor = CreateProcessor(mediator, incidentRepo, attachmentService, emailService, templateRepo, timelineRepo: timelineRepo);

        var message = new Message
        {
            Subject = "Re: INC-EXIST-123",
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = "<p>Follow up</p>"
            },
            From = new Recipient { EmailAddress = new EmailAddress { Address = "customer@example.com", Name = "Customer Name" } }
        };

        var customer = new Customer { Id = "cust1", OrganizationId = "org1", Email = "a@b.com", Name = "Cust" };

        _ = await processor.ProcessIncidentAsync(
            message,
            Enumerable.Empty<GraphAttachment>(),
            customer,
            CancellationToken.None,
            "<msg-123@example.test>");

        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_AddsWorkLog_WhenRequestReferenceExists()
    {
        var existing = new Helpdesk.Shared.Models.Request { OrganizationId = "org1", Id = "req-1", TrackingId = "REQ-Y2P-J6C-H3K" };
        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Ticket>>(new[] { existing }));
        ticketRepo.GetAsync(existing.Id).Returns(existing);
        ticketRepo.UpdateAsync(Arg.Any<Ticket>()).Returns(call => call.Arg<Ticket>());

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(Array.Empty<Incident>()));
        var mediator = Substitute.For<IRequestSender>();
        mediator.Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>()).Returns(new WorkLog());
        var attachmentService = Substitute.For<ITicketAttachmentService>();
        var emailService = Substitute.For<IEmailService>();
        var templateRepo = Substitute.For<IRepository<EmailTemplate>>();
        templateRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<EmailTemplate>>(Array.Empty<EmailTemplate>()));

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            attachmentService,
            emailService,
            templateRepo,
            ticketRepo: ticketRepo);

        var message = new Message
        {
            Subject = "Re: Your Request Has Been Created REQ-Y2P-J6C-H3K",
            Body = new ItemBody { ContentType = BodyType.Html, Content = "<p>request reply</p>" },
            From = new Recipient { EmailAddress = new EmailAddress { Address = "customer@example.com", Name = "Customer Name" } },
            CcRecipients = new List<Recipient>
            {
                new() { EmailAddress = new EmailAddress { Address = "watcher@example.com" } }
            }
        };
        var customer = new Customer { Id = "cust1", OrganizationId = "org1", Email = "customer@example.com", Name = "Cust" };

        var result = await processor.ProcessTicketEmailAsync(
            message,
            new GraphAttachment[]
            {
                new FileAttachment
                {
                    Name = "request-reply.txt",
                    ContentType = "text/plain",
                    ContentBytes = System.Text.Encoding.UTF8.GetBytes("attachment")
                }
            },
            customer,
            CancellationToken.None,
            "<msg-request-reply@example.test>");

        Assert.Equal(existing, result);
        await mediator.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(
            Arg.Is<CreateWorkLogCommand>(c =>
                c.TicketId == existing.Id &&
                c.NotifyCustomer == false &&
                c.EventType == TimelineEventType.CustomerReply &&
                c.EventCreatedByUserId == "<msg-request-reply@example.test>" &&
                c.EventCreatedByUserName == "customer@example.com" &&
                c.Notes == "<p>request reply</p>"),
            Arg.Any<CancellationToken>());
        Assert.Equal(TicketState.WaitingReply, existing.State);
        Assert.NotNull(existing.UpdatedAt);
        Assert.Equal(customer.Name, existing.LastReplierName);
        Assert.Equal(customer.Email, existing.RequesterEmail);
        Assert.Contains("watcher@example.com", existing.CcRecipients);
        await ticketRepo.Received(1).UpdateAsync(existing);
        await attachmentService.Received(1).SaveAsync(
            existing.Id,
            Arg.Is<IEnumerable<AttachmentUpload>>(uploads => uploads.Single().FileName == "request-reply.txt"),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_DoesNotAddDuplicateRequestWorkLog_WhenMessageIdAlreadyProcessed()
    {
        var existing = new Helpdesk.Shared.Models.Request { OrganizationId = "org1", Id = "req-1", TrackingId = "REQ-DUP-123" };
        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Ticket>>(new[] { existing }));
        ticketRepo.GetAsync(existing.Id).Returns(existing);

        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<TicketTimelineEvent>>(new[]
        {
            new TicketTimelineEvent
            {
                TicketId = existing.Id,
                EventType = TimelineEventType.CustomerReply,
                CreatedByUserId = "<msg-request-dup@example.test>"
            }
        }));

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(Array.Empty<Incident>()));
        var mediator = Substitute.For<IRequestSender>();
        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>(),
            ticketRepo: ticketRepo,
            timelineRepo: timelineRepo);

        _ = await processor.ProcessTicketEmailAsync(
            new Message
            {
                Subject = "Re: REQ-DUP-123",
                Body = new ItemBody { ContentType = BodyType.Html, Content = "<p>duplicate</p>" },
                From = new Recipient { EmailAddress = new EmailAddress { Address = "customer@example.com" } }
            },
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "customer@example.com", Name = "Cust" },
            CancellationToken.None,
            "<msg-request-dup@example.test>");

        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_CreatesIncident_WhenRequestReferenceIsUnknown()
    {
        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>()));
        ticketRepo.GetAsync(Arg.Any<string>()).Returns((Ticket?)null);

        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(Array.Empty<Incident>()));
        var created = new Incident { OrganizationId = "org1", Id = "inc-1", TrackingId = "INC-NEW-1" };
        var mediator = Substitute.For<IRequestSender>();
        mediator.Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>()).Returns(created);
        var timelineRepo = Substitute.For<IRepository<TicketTimelineEvent>>();
        timelineRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<TicketTimelineEvent>>(Array.Empty<TicketTimelineEvent>()));
        timelineRepo.CreateAsync(Arg.Any<TicketTimelineEvent>()).Returns(call => call.Arg<TicketTimelineEvent>());

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>(),
            ticketRepo: ticketRepo,
            timelineRepo: timelineRepo);

        var result = await processor.ProcessTicketEmailAsync(
            new Message { Subject = "Re: REQ-UNKNOWN-123", Body = new ItemBody { Content = "new issue" } },
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "customer@example.com", Name = "Cust" },
            CancellationToken.None,
            "<msg-unknown-request@example.test>");

        Assert.Equal(created, result);
        await mediator.Received(1).Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_MatchesRequestReference_CaseInsensitively()
    {
        var existing = new Helpdesk.Shared.Models.Request { OrganizationId = "org1", Id = "req-1", TrackingId = "REQ-LOW-123" };
        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Ticket>>(new[] { existing }));
        ticketRepo.GetAsync(existing.Id).Returns(existing);
        ticketRepo.UpdateAsync(Arg.Any<Ticket>()).Returns(call => call.Arg<Ticket>());

        var mediator = Substitute.For<IRequestSender>();
        mediator.Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>()).Returns(new WorkLog());
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(Array.Empty<Incident>()));
        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>(),
            ticketRepo: ticketRepo);

        _ = await processor.ProcessTicketEmailAsync(
            new Message
            {
                Subject = "re: req-low-123",
                Body = new ItemBody { ContentType = BodyType.Text, Content = "lowercase reference" },
                From = new Recipient { EmailAddress = new EmailAddress { Address = "customer@example.com" } }
            },
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "customer@example.com", Name = "Cust" },
            CancellationToken.None);

        await mediator.Received(1).Send(
            Arg.Is<CreateWorkLogCommand>(c => c.TicketId == existing.Id),
            Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_IgnoresDsnBounce_ForResolvedIncident()
    {
        var existing = new Incident { OrganizationId = "org1", Id = "inc-1", TrackingId = "INC-BOUNCE-1", State = TicketState.Resolved };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));
        var mediator = Substitute.For<IRequestSender>();
        var attachmentService = Substitute.For<ITicketAttachmentService>();

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            attachmentService,
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>());

        var result = await processor.ProcessTicketEmailAsync(
            DsnBounceMessage("Undeliverable: INC-BOUNCE-1", "postmaster@example.com", "INC-BOUNCE-1"),
            new[] { new FileAttachment { Name = "dsn.txt", ContentBytes = new byte[] { 1 } } },
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "postmaster@example.com", Name = "Postmaster" },
            CancellationToken.None,
            "<dsn-1@example.test>");

        Assert.Equal(existing, result);
        Assert.Equal(TicketState.Resolved, existing.State);
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        await incidentRepo.DidNotReceive().UpdateAsync(Arg.Any<Incident>());
        await attachmentService.DidNotReceive().SaveAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<AttachmentUpload>>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_IgnoresDsnBounce_ForOpenIncident()
    {
        var existing = new Incident { OrganizationId = "org1", Id = "inc-1", TrackingId = "INC-OPEN-1", State = TicketState.InProgress };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));
        var mediator = Substitute.For<IRequestSender>();

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>());

        var result = await processor.ProcessTicketEmailAsync(
            DsnBounceMessage("Delivery Status Notification (Failure) INC-OPEN-1", "mailer-daemon@example.com", "INC-OPEN-1"),
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "mailer-daemon@example.com", Name = "Mailer Daemon" },
            CancellationToken.None,
            "<dsn-open@example.test>");

        Assert.Equal(existing, result);
        Assert.Equal(TicketState.InProgress, existing.State);
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_IgnoresDsnBounce_ForRequestReference()
    {
        var existing = new Helpdesk.Shared.Models.Request { OrganizationId = "org1", Id = "req-1", TrackingId = "REQ-BOUNCE-1", State = TicketState.OnHold };
        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Ticket>>(new[] { existing }));
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(Array.Empty<Incident>()));
        var mediator = Substitute.For<IRequestSender>();

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>(),
            ticketRepo: ticketRepo);

        var result = await processor.ProcessTicketEmailAsync(
            DsnBounceMessage("Undeliverable: REQ-BOUNCE-1", "postmaster@example.com", "REQ-BOUNCE-1"),
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "postmaster@example.com", Name = "Postmaster" },
            CancellationToken.None,
            "<dsn-request@example.test>");

        Assert.Equal(existing, result);
        Assert.Equal(TicketState.OnHold, existing.State);
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
        await ticketRepo.DidNotReceive().UpdateAsync(Arg.Any<Ticket>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_DsnBounceWithoutReference_DoesNotCreateIncident()
    {
        var ticketRepo = Substitute.For<IRepository<Ticket>>();
        ticketRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Ticket>>(Array.Empty<Ticket>()));
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(Array.Empty<Incident>()));
        var mediator = Substitute.For<IRequestSender>();

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>(),
            ticketRepo: ticketRepo);

        var result = await processor.ProcessTicketEmailAsync(
            DsnBounceMessage("Delivery Status Notification (Failure)", "postmaster@example.com", null),
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "postmaster@example.com", Name = "Postmaster" },
            CancellationToken.None,
            "<dsn-no-ref@example.test>");

        Assert.Null(result);
        await mediator.DidNotReceive().Send(Arg.Any<CreateIncidentCommand>(), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_CustomerForwardedUndeliverableSubject_ProcessesNormally()
    {
        var existing = new Incident { OrganizationId = "org1", Id = "inc-1", TrackingId = "INC-CUSTOMER-1", State = TicketState.Resolved };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));
        var mediator = Substitute.For<IRequestSender>();
        mediator.Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>()).Returns(new WorkLog());

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>());

        var result = await processor.ProcessTicketEmailAsync(
            new Message
            {
                Subject = "FW: Undeliverable: INC-CUSTOMER-1",
                Body = new ItemBody
                {
                    ContentType = BodyType.Html,
                    Content = """
                              <p>Please help with this bounce.</p>
                              <pre>
                              Final-Recipient: rfc822; missing@example.com
                              Action: failed
                              Status: 5.1.1
                              Diagnostic-Code: smtp; 550 5.1.1 recipient not found
                              </pre>
                              """
                },
                From = new Recipient { EmailAddress = new EmailAddress { Address = "customer@example.com", Name = "Customer" } }
            },
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "customer@example.com", Name = "Customer" },
            CancellationToken.None,
            "<customer-forward@example.test>");

        Assert.Equal(existing, result);
        Assert.Equal(TicketState.WaitingReply, existing.State);
        await mediator.Received(1).Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_DsnBounceWithMultipleReferences_DoesNotMutateTickets()
    {
        var first = new Incident { OrganizationId = "org1", Id = "inc-1", TrackingId = "INC-MULTI-1", State = TicketState.Resolved };
        var second = new Incident { OrganizationId = "org1", Id = "inc-2", TrackingId = "INC-MULTI-2", State = TicketState.InProgress };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { first, second }));
        var mediator = Substitute.For<IRequestSender>();

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>());

        var result = await processor.ProcessTicketEmailAsync(
            DsnBounceMessage("Undeliverable: INC-MULTI-1", "postmaster@example.com", "INC-MULTI-2"),
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "postmaster@example.com", Name = "Postmaster" },
            CancellationToken.None,
            "<dsn-multi@example.test>");

        Assert.Null(result);
        Assert.Equal(TicketState.Resolved, first.State);
        Assert.Equal(TicketState.InProgress, second.State);
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
        await incidentRepo.DidNotReceive().UpdateAsync(Arg.Any<Incident>());
    }

    [Fact]
    public async Task ProcessTicketEmailAsync_DsnBounceDuplicateMessage_HasNoRepeatedSideEffects()
    {
        var existing = new Incident { OrganizationId = "org1", Id = "inc-1", TrackingId = "INC-DUP-DSN", State = TicketState.Resolved };
        var incidentRepo = Substitute.For<IRepository<Incident>>();
        incidentRepo.GetAllAsync().Returns(Task.FromResult<IEnumerable<Incident>>(new[] { existing }));
        var mediator = Substitute.For<IRequestSender>();

        var processor = CreateProcessor(
            mediator,
            incidentRepo,
            Substitute.For<ITicketAttachmentService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IRepository<EmailTemplate>>());
        var message = DsnBounceMessage("Undeliverable: INC-DUP-DSN", "postmaster@example.com", "INC-DUP-DSN");

        var first = await processor.ProcessTicketEmailAsync(
            message,
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "postmaster@example.com", Name = "Postmaster" },
            CancellationToken.None,
            "<dsn-duplicate@example.test>");
        var second = await processor.ProcessTicketEmailAsync(
            message,
            Enumerable.Empty<GraphAttachment>(),
            new Customer { Id = "cust1", OrganizationId = "org1", Email = "postmaster@example.com", Name = "Postmaster" },
            CancellationToken.None,
            "<dsn-duplicate@example.test>");

        Assert.Equal(existing, first);
        Assert.Equal(existing, second);
        Assert.Equal(TicketState.Resolved, existing.State);
        await mediator.DidNotReceive().Send(Arg.Any<CreateWorkLogCommand>(), Arg.Any<CancellationToken>());
        await incidentRepo.DidNotReceive().UpdateAsync(Arg.Any<Incident>());
    }

    [Theory]
    [InlineData("postmaster@example.com", "Re: normal update", "plain status text", false)]
    [InlineData("customer@example.com", "FW: Undeliverable: test", "Please help with this bounce", false)]
    [InlineData("postmaster@example.com", "Delivery Status Notification (Failure)", "plain failure notice", false)]
    [InlineData("postmaster@example.com", "Undeliverable: test", "plain failure notice", false)]
    [InlineData("noreply@vendor.com", "Undeliverable: workflow notice", "plain failure notice", false)]
    [InlineData("customer@example.com", "Re: Delivery problem", "Final-Recipient: rfc822; missing@example.com\r\nAction: failed\r\nStatus: 5.1.1\r\nDiagnostic-Code: smtp; 550 5.1.1 recipient not found", false)]
    [InlineData("customer@tenant.onmicrosoft.com", "FW: Undeliverable: test", "Final-Recipient: rfc822; missing@example.com\r\nAction: failed\r\nStatus: 5.1.1\r\nDiagnostic-Code: smtp; 550 5.1.1 recipient not found", false)]
    [InlineData("user@notmicrosoft.com", "FW: Undeliverable: test", "Final-Recipient: rfc822; missing@example.com\r\nAction: failed\r\nStatus: 5.1.1\r\nDiagnostic-Code: smtp; 550 5.1.1 recipient not found", false)]
    [InlineData("postmaster@example.com", "Re: Delivery problem", "Final-Recipient: rfc822; missing@example.com\r\nDiagnostic-Code: smtp; 550 5.1.1 recipient not found", true)]
    [InlineData("postmaster@example.com", "Undeliverable: test", "Status: 5.1.1", true)]
    public void IsDeliveryFailureMessage_UsesStrongSignalsAndSystemSenderCombinations(
        string sender,
        string subject,
        string body,
        bool expected)
    {
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = body },
            From = new Recipient { EmailAddress = new EmailAddress { Address = sender } }
        };

        Assert.Equal(expected, GraphEmailProcessor.IsDeliveryFailureMessage(message));
    }

    [Fact]
    public void IsDeliveryFailureMessage_DoesNotTreatAutoSubmittedAloneAsDeliveryFailure()
    {
        var message = MessageWithHeaders(
            "customer@example.com",
            "Re: INC-AUTO-1",
            "This is a normal automatic reply body.",
            new InternetMessageHeader { Name = "Auto-Submitted", Value = "auto-replied" });

        Assert.False(GraphEmailProcessor.IsDeliveryFailureMessage(message));
    }

    [Fact]
    public void IsDeliveryFailureMessage_DoesNotTreatEmptyReturnPathAloneAsDeliveryFailure()
    {
        var message = MessageWithHeaders(
            "postmaster@example.com",
            "Re: INC-RETURN-1",
            "This is a normal ticket reply body.",
            new InternetMessageHeader { Name = "Return-Path", Value = "<>" });

        Assert.False(GraphEmailProcessor.IsDeliveryFailureMessage(message));
    }

    [Fact]
    public void IsDeliveryFailureMessage_DsnMimeProofDoesNotRequireValidFrom()
    {
        var message = MessageWithHeaders(
            null,
            "Delivery Status Notification (Failure)",
            "No usable sender is present.",
            new InternetMessageHeader { Name = "Content-Type", Value = "multipart/report; report-type=delivery-status" });

        Assert.True(GraphEmailProcessor.IsDeliveryFailureMessage(message));
    }

    [Fact]
    public void IsDeliveryFailureMessage_UsesGraphSenderHeadersWhenFromIsMissing()
    {
        var message = MessageWithHeaders(
            null,
            "Delivery Status Notification (Failure)",
            "Status: 5.1.1",
            new InternetMessageHeader { Name = "Sender", Value = "postmaster@example.com" });

        Assert.True(GraphEmailProcessor.IsDeliveryFailureMessage(message));
    }

    private static Message DsnBounceMessage(string subject, string from, string? trackingId)
    {
        var body = trackingId is null
            ? """
              Final-Recipient: rfc822; missing@example.com
              Action: failed
              Status: 5.1.1
              Diagnostic-Code: smtp; 550 5.1.1 recipient not found
              """
            : $"""
               Final-Recipient: rfc822; missing@example.com
               Action: failed
               Status: 5.1.1
               Diagnostic-Code: smtp; 550 5.1.1 recipient not found
               Original message reference: {trackingId}
               """;

        return new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = body },
            From = new Recipient { EmailAddress = new EmailAddress { Address = from, Name = from } },
            InternetMessageHeaders =
            [
                new InternetMessageHeader { Name = "Content-Type", Value = "multipart/report; report-type=delivery-status" },
                new InternetMessageHeader { Name = "Return-Path", Value = "<>" },
                new InternetMessageHeader { Name = "Auto-Submitted", Value = "auto-generated" }
            ]
        };
    }

    private static Message MessageWithHeaders(
        string? from,
        string subject,
        string body,
        params InternetMessageHeader[] headers)
    {
        return new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Text, Content = body },
            From = from is null ? null : new Recipient { EmailAddress = new EmailAddress { Address = from } },
            InternetMessageHeaders = headers.ToList()
        };
    }
}
