using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class SmtpMailboxSenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dedicated_email_and_confirmed_sample_use_selected_smtp_without_constructing_legacy_Graph_sender(bool sample)
    {
        await using var server = new SmtpFixture();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
        await using var db = new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "tenant-a", Name = "Tenant A" });
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), Scope = MailboxScope.Organization,
            OrganizationId = "tenant-a", Provider = InboundMailboxProvider.Imap,
            Authentication = MailboxAuthentication.Password, MailboxAddress = "support@tenant-a.example.test",
            SourceKey = "tenant-a-imap", Enabled = true, BackgroundSyncEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var protection = new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider());
        var outgoing = new MailboxOutgoingSettings { MailboxId = mailbox.Id, Enabled = true,
            Transport = MailboxOutgoingTransport.Smtp, DisplayName = "Tenant A support",
            SmtpHost = "localhost", SmtpPort = server.Port, SmtpTlsMode = MailboxTlsMode.TlsOnConnect,
            SmtpUsername = mailbox.MailboxAddress };
        outgoing.ProtectedSmtpPassword = protection.Protect(outgoing, "synthetic-password");
        db.EmailInboxSettings.Add(mailbox);
        db.Set<MailboxOutgoingSettings>().Add(outgoing);
        db.Incidents.Add(new Incident { Id = "ticket-a", TrackingId = "INC-123", OrganizationId = "tenant-a" });
        await db.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["EmailSending:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var context = new IngressEffectContext();
        var service = new MailboxEmailService(new MailboxSenderResolver(db),
            new SmtpMailboxSender(new MailboxDestinationPolicy(configuration), protection, new HtmlToPlainTextConverter()),
            new GraphMailboxSender(_ => throw new InvalidOperationException("Graph must not be constructed.")),
            new MailboxOutboxStore(db, context, TimeProvider.System), context,
            NullLogger<MailboxEmailService>.Instance);

        if (sample)
            Assert.Equal("Accepted by provider", (await service.SendTestAsync(mailbox.Id,
                "requester@example.test", default)).Status);
        else
        {
            Assert.True(await service.SendEmailAsync(["requester@example.test"], "Ticket update", "<p>Update</p>",
                ticketId: "ticket-a"));
            var queued = await db.Set<MailboxOutboxEffect>().SingleAsync(x => x.Kind == MailboxEffectKind.Email);
            Assert.Equal(MailboxEffectState.Pending, queued.State);
            Assert.Null(queued.ReceiptId);
            Assert.NotNull(queued.DeliveryEventId);
            var changedRoute = await service.SendPinnedAsync(mailbox.Id, "tenant-a", "ticket-a",
                ["requester@example.test"], [], "Ticket update", "<p>Update</p>", [], null,
                queued.Id, default, mailbox.Version, outgoing.Version + 1);
            Assert.Equal("Needs review", changedRoute.Status);
            Assert.Equal("SenderConfigurationChanged", changedRoute.ErrorCode);
            Assert.Equal("Accepted by provider", (await service.SendPinnedAsync(mailbox.Id,
                "tenant-a", "ticket-a", ["requester@example.test"], [], "Ticket update", "<p>Update</p>",
                [], null, queued.Id, default, mailbox.Version, outgoing.Version)).Status);
        }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(server.Commands, command => command.StartsWith("MAIL FROM:<support@tenant-a.example.test>", StringComparison.OrdinalIgnoreCase));
        if (sample) Assert.Contains("RatelDesk mailbox send test", server.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_sender_submits_with_selected_mailbox_identity_and_verified_TLS()
    {
        await using var server = new SmtpFixture();
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@tenant-a.example.test", Enabled = true };
        var protection = new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider());
        var outgoing = new MailboxOutgoingSettings { MailboxId = mailbox.Id, Enabled = true,
            Transport = MailboxOutgoingTransport.Smtp, DisplayName = "Tenant A support",
            SmtpHost = "localhost", SmtpPort = server.Port, SmtpTlsMode = MailboxTlsMode.TlsOnConnect,
            SmtpUsername = mailbox.MailboxAddress };
        outgoing.ProtectedSmtpPassword = protection.Protect(outgoing, "synthetic-password");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["EmailSending:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var sender = new SmtpMailboxSender(new MailboxDestinationPolicy(configuration),
            protection, new HtmlToPlainTextConverter());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var result = await sender.SendAsync(mailbox, outgoing,
            ["requester@example.test", mailbox.MailboxAddress], ["tech@example.test"],
            "INC-123 update", "<p>Hello requester</p>", [], Guid.Parse("11111111-1111-4111-8111-111111111111"), deadline.Token);

        Assert.Equal("Accepted by provider", result.Status);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("AUTH PLAIN", server.Commands);
        Assert.Contains(server.Commands, command => command.StartsWith("MAIL FROM:<support@tenant-a.example.test>", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(server.Commands, command => command.Contains("RCPT TO:<support@tenant-a.example.test>", StringComparison.Ordinal));
        Assert.Contains("RCPT TO:<requester@example.test>", server.Commands);
        Assert.Contains("RCPT TO:<tech@example.test>", server.Commands);
        using var parsed = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(server.Message)));
        Assert.Equal(mailbox.MailboxAddress, Assert.Single(parsed.From.Mailboxes).Address);
        Assert.Equal("Tenant A support", Assert.Single(parsed.From.Mailboxes).Name);
        Assert.Equal(mailbox.MailboxAddress, Assert.Single(parsed.ReplyTo.Mailboxes).Address);
        Assert.Equal("11111111111141118111111111111111@tenant-a.example.test", parsed.MessageId);
        Assert.Contains("Hello requester", parsed.TextBody);
    }

    [Fact]
    public async Task Partial_smtp_recipient_rejection_is_held_without_resending_accepted_recipient()
    {
        const string rejected = "rejected@example.test";
        await using var server = new SmtpFixture(rejected);
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@tenant-a.example.test", Enabled = true };
        var protection = new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider());
        var outgoing = new MailboxOutgoingSettings
        {
            MailboxId = mailbox.Id, Enabled = true, Transport = MailboxOutgoingTransport.Smtp,
            SmtpHost = "localhost", SmtpPort = server.Port, SmtpTlsMode = MailboxTlsMode.TlsOnConnect,
            SmtpUsername = mailbox.MailboxAddress
        };
        outgoing.ProtectedSmtpPassword = protection.Protect(outgoing, "synthetic-password");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["EmailSending:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var sender = new SmtpMailboxSender(new MailboxDestinationPolicy(configuration), protection,
            new HtmlToPlainTextConverter());

        var result = await sender.SendAsync(mailbox, outgoing,
            ["accepted@example.test", rejected], null, "Update", "<p>Body</p>", [], Guid.NewGuid(), default);

        Assert.Equal("Needs review", result.Status);
        Assert.Equal("SmtpPartialRecipientAcceptance", result.ErrorCode);
        Assert.Equal(new[] { "accepted@example.test" }, result.AcceptedRecipients);
        Assert.Equal(new[] { rejected }, result.RejectedRecipients);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Body", server.Message);
    }

    [Fact]
    public async Task Smtp_rejecting_every_recipient_never_submits_message_data()
    {
        const string rejected = "rejected@example.test";
        await using var server = new SmtpFixture(rejected);
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@tenant-a.example.test", Enabled = true };
        var protection = new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider());
        var outgoing = new MailboxOutgoingSettings
        {
            MailboxId = mailbox.Id, Enabled = true, Transport = MailboxOutgoingTransport.Smtp,
            SmtpHost = "localhost", SmtpPort = server.Port, SmtpTlsMode = MailboxTlsMode.TlsOnConnect,
            SmtpUsername = mailbox.MailboxAddress
        };
        outgoing.ProtectedSmtpPassword = protection.Protect(outgoing, "synthetic-password");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["EmailSending:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var sender = new SmtpMailboxSender(new MailboxDestinationPolicy(configuration), protection,
            new HtmlToPlainTextConverter());

        var result = await sender.SendAsync(mailbox, outgoing,
            [rejected], null, "Update", "<p>Body</p>", [], Guid.NewGuid(), default);

        Assert.Equal("Failed", result.Status);
        Assert.Equal("SmtpRecipientRejected", result.ErrorCode);
        Assert.Empty(result.AcceptedRecipients ?? []);
        Assert.Equal(new[] { rejected }, result.RejectedRecipients);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("DATA", server.Commands);
    }

    [Fact]
    public async Task Smtp_private_destination_is_blocked_without_explicit_fixture_policy()
    {
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@tenant-a.example.test", Enabled = true };
        var protection = new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider());
        var outgoing = new MailboxOutgoingSettings { MailboxId = mailbox.Id, Enabled = true,
            Transport = MailboxOutgoingTransport.Smtp, SmtpHost = "localhost", SmtpPort = 465,
            SmtpUsername = mailbox.MailboxAddress };
        outgoing.ProtectedSmtpPassword = protection.Protect(outgoing, "synthetic-password");
        var sender = new SmtpMailboxSender(new MailboxDestinationPolicy(new ConfigurationBuilder().Build()),
            protection, new HtmlToPlainTextConverter());
        var result = await sender.SendAsync(mailbox, outgoing, ["requester@example.test"], null,
            "Update", "<p>Hello</p>", [], Guid.NewGuid(), default);
        Assert.Equal("Failed", result.Status);
    }

    [Fact]
    public async Task Invalid_recipient_fails_as_a_delivery_result_before_connecting()
    {
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), MailboxAddress = "support@tenant-a.example.test", Enabled = true };
        var outgoing = new MailboxOutgoingSettings { MailboxId = mailbox.Id, Enabled = true,
            SmtpHost = "localhost", SmtpPort = 465, SmtpUsername = mailbox.MailboxAddress };
        var sender = new SmtpMailboxSender(new MailboxDestinationPolicy(new ConfigurationBuilder().Build()),
            new MailboxOutgoingCredentialProtector(new EphemeralDataProtectionProvider()), new HtmlToPlainTextConverter());
        var result = await sender.SendAsync(mailbox, outgoing, ["not an address"], null,
            "Update", "<p>Hello</p>", [], Guid.NewGuid(), default);
        Assert.Equal("InvalidMailboxAddress", result.ErrorCode);
    }

    private sealed class SmtpFixture : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly string? rejectRecipient;
        private readonly X509Certificate2 certificate;
        private readonly X509Store roots = new(StoreName.Root, StoreLocation.CurrentUser);
        public int Port { get; }
        public Task Completion { get; }
        public List<string> Commands { get; } = [];
        public string Message { get; private set; } = string.Empty;

        public SmtpFixture(string? rejectRecipient = null)
        {
            this.rejectRecipient = rejectRecipient;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            roots.Open(OpenFlags.ReadWrite);
            roots.Add(certificate);
            // Bind one address family so destination selection also works when
            // localhost resolves the other family first on the CI runner.
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Completion = ServeAsync();
        }

        private async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(certificate);
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(tls, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost ESMTP fixture");
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    Commands.Add("DATA");
                    await writer.WriteLineAsync("354 End data with <CRLF>.<CRLF>");
                    var body = new StringBuilder();
                    while (await reader.ReadLineAsync() is { } data && data != ".")
                        body.AppendLine(data);
                    Message = body.ToString();
                    await writer.WriteLineAsync("250 2.0.0 queued");
                    continue;
                }
                Commands.Add(line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase) ? "AUTH PLAIN" : line);
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("250-localhost\r\n250-AUTH PLAIN\r\n250 SIZE 20000000");
                else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("235 2.7.0 authenticated");
                else if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase) &&
                         rejectRecipient is not null && line.Contains(rejectRecipient, StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("550 5.1.1 recipient rejected");
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("250 2.1.0 ok");
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 goodbye");
                    break;
                }
                else throw new InvalidOperationException("Unexpected SMTP fixture command: " + line.Split(' ')[0]);
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            roots.Remove(certificate);
            roots.Dispose();
            certificate.Dispose();
            if (Completion.IsCompletedSuccessfully) await Completion;
        }
    }
}
