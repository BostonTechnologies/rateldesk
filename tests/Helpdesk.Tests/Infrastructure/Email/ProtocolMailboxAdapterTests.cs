using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Tests.Infrastructure.Email;

public sealed class ProtocolMailboxAdapterTests
{
    [Fact]
    public async Task Pop3_uses_verified_TLS_UIDL_and_complete_MIME_without_Microsoft_credentials()
    {
        await using var server = new PopFixture();
        var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        var settings = new EmailInboxSettings { Id = Guid.NewGuid(), Provider = InboundMailboxProvider.Pop3,
            Authentication = MailboxAuthentication.Password, MailHost = "localhost", Port = server.Port,
            Username = "fixture", MailboxAddress = "support@example.test", InitialImport = InitialMailImport.All,
            CredentialVersion = 1, MarkReadAfterSuccess = false };
        settings.Password = secrets.Protect(settings.Id, "synthetic password");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EmailIngestion:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var adapter = new ProtocolMailboxAdapter(InboundMailboxProvider.Pop3, new MailboxDestinationPolicy(config), secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await adapter.FetchAsync(settings, new MailboxIngestionState(), new HashSet<string>(), deadline.Token);
        var received = Assert.Single(result.Messages);
        Assert.Equal("stable-UIDL", received.Key);
        Assert.Equal("requester@example.test", received.Message!.FromEmail);
        Assert.Contains("hello", received.Message.TextBody);
        Assert.DoesNotContain(server.Commands, command => command.StartsWith("DELE", StringComparison.Ordinal));
        Assert.Equal(string.Empty, settings.TenantId ?? string.Empty);
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Imap_fetches_configured_folder_with_UIDVALIDITY_and_no_IDLE_or_Graph()
    {
        await using var server = new PopFixture(imap: true);
        var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        var settings = new EmailInboxSettings { Id = Guid.NewGuid(), Provider = InboundMailboxProvider.Imap,
            Authentication = MailboxAuthentication.Password, MailHost = "localhost", Port = server.Port,
            Username = "fixture", MailboxAddress = "support@example.test", MailboxFolder = "Support",
            InitialImport = InitialMailImport.All, CredentialVersion = 1 };
        settings.Password = secrets.Protect(settings.Id, "synthetic password");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EmailIngestion:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var adapter = new ProtocolMailboxAdapter(InboundMailboxProvider.Imap, new MailboxDestinationPolicy(config), secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await adapter.FetchAsync(settings, new MailboxIngestionState(), new HashSet<string>(), deadline.Token);
        var received = Assert.Single(result.Messages);
        Assert.Equal("7:42", received.Key);
        Assert.Contains("hello", received.Message!.TextBody);
        Assert.Contains(server.Commands, command => command.Contains("EXAMINE Support"));
        Assert.DoesNotContain(server.Commands, command => command.Contains("IDLE"));
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Untrusted_TLS_certificate_is_rejected_without_password_fallback()
    {
        await using var server = new PopFixture(trusted: false);
        var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        var settings = new EmailInboxSettings { Id = Guid.NewGuid(), Provider = InboundMailboxProvider.Pop3,
            Authentication = MailboxAuthentication.Password, MailHost = "localhost", Port = server.Port,
            Username = "fixture", CredentialVersion = 1 };
        settings.Password = secrets.Protect(settings.Id, "synthetic-password");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EmailIngestion:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var adapter = new ProtocolMailboxAdapter(InboundMailboxProvider.Pop3, new MailboxDestinationPolicy(config), secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<MailKit.Security.SslHandshakeException>(() => adapter.TestAsync(settings, deadline.Token));
        Assert.DoesNotContain("PASS", server.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pop3_requires_UIDL_and_durable_keys_skip_retained_messages_on_reconnect(bool supportsUidl)
    {
        await using var server = new PopFixture(supportsUidl: supportsUidl);
        var secrets = new MailboxCredentialProtector(new EphemeralDataProtectionProvider());
        var settings = new EmailInboxSettings { Id = Guid.NewGuid(), Provider = InboundMailboxProvider.Pop3,
            Authentication = MailboxAuthentication.Password, MailHost = "localhost", Port = server.Port,
            Username = "fixture", CredentialVersion = 1, InitialImport = InitialMailImport.All };
        settings.Password = secrets.Protect(settings.Id, "synthetic-password");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EmailIngestion:AllowedPrivateHosts:0"] = "localhost" }).Build();
        var adapter = new ProtocolMailboxAdapter(InboundMailboxProvider.Pop3, new MailboxDestinationPolicy(config), secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (!supportsUidl)
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.FetchAsync(settings, new MailboxIngestionState(), new HashSet<string>(), deadline.Token));
        else
        {
            var batch = await adapter.FetchAsync(settings, new MailboxIngestionState { Initialized = true }, new HashSet<string> { "stable-UIDL" }, deadline.Token);
            Assert.Empty(batch.Messages);
            Assert.DoesNotContain("RETR", server.Commands);
            Assert.DoesNotContain("DELE", server.Commands);
        }
    }

    [Theory]
    [InlineData("169.254.169.254", false)]
    [InlineData("169.254.169.254", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.1.2.3", false)]
    public void Egress_blocks_unapproved_or_metadata_destinations(string address, bool allowPrivate) =>
        Assert.False(MailboxDestinationPolicy.IsAllowed(IPAddress.Parse(address), allowPrivate));

    internal sealed class PopFixture : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private readonly X509Store roots = new(StoreName.Root, StoreLocation.CurrentUser);
        public int Port { get; }
        public Task Completion { get; }
        public List<string> Commands { get; } = [];
        private readonly bool imap;
        private readonly bool trusted;
        private readonly bool supportsUidl;
        public PopFixture(bool imap = false, bool trusted = true, bool supportsUidl = true)
        {
            this.imap = imap;
            this.trusted = trusted;
            this.supportsUidl = supportsUidl;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            roots.Open(OpenFlags.ReadWrite); if (trusted) roots.Add(certificate);
            listener = new TcpListener(Dns.GetHostAddresses("localhost")[0], 0); listener.Start();
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
            if (imap)
            {
                await ServeImapAsync(reader, writer);
                return;
            }
            await writer.WriteLineAsync("+OK fixture");
            while (await reader.ReadLineAsync() is { } line)
            {
                Commands.Add(line.Split(' ')[0]);
                var verb = line.Split(' ')[0].ToUpperInvariant();
                var response = verb switch
                {
                    "CAPA" => supportsUidl ? "+OK\r\nUSER\r\nUIDL\r\n." : "+OK\r\nUSER\r\n.",
                    "USER" or "PASS" => "+OK",
                    "STAT" => "+OK 1 200",
                    "UIDL" => !supportsUidl ? "-ERR UIDL unavailable" : line.Contains(' ') ? "+OK 1 stable-UIDL" : "+OK\r\n1 stable-UIDL\r\n.",
                    "LIST" => "+OK 1 200",
                    "RETR" => "+OK\r\nFrom: requester@example.test\r\nTo: support@example.test\r\nSubject: Protocol fixture\r\nContent-Type: text/plain\r\n\r\nhello\r\n.",
                    "QUIT" => "+OK goodbye",
                    _ => "-ERR unsupported"
                };
                await writer.WriteLineAsync(response);
                if (verb == "QUIT") break;
            }
        }
        private async Task ServeImapAsync(StreamReader reader, StreamWriter writer)
        {
            const string mime = "From: requester@example.test\r\nTo: support@example.test\r\nSubject: IMAP fixture\r\nContent-Type: text/plain\r\n\r\nhello\r\n";
            await writer.WriteLineAsync("* OK fixture");
            while (await reader.ReadLineAsync() is { } line)
            {
                var tag = line.Split(' ')[0];
                var command = line[(tag.Length + 1)..];
                Commands.Add(command.StartsWith("LOGIN", StringComparison.Ordinal) ? "LOGIN" : command);
                if (command.StartsWith("CAPABILITY")) await writer.WriteLineAsync("* CAPABILITY IMAP4rev1");
                else if (command.StartsWith("LIST")) await writer.WriteLineAsync("* LIST (\\HasNoChildren) \"/\" \"Support\"");
                else if (command.StartsWith("EXAMINE")) await writer.WriteLineAsync("* FLAGS (\\Seen)\r\n* 1 EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY 7] stable\r\n* OK [UIDNEXT 43] next");
                else if (command.StartsWith("UID SEARCH")) await writer.WriteLineAsync("* SEARCH 42");
                else if (command.StartsWith("UID FETCH") && command.Contains("BODY.PEEK"))
                    await writer.WriteLineAsync($"* 1 FETCH (UID 42 BODY[] {{{Encoding.ASCII.GetByteCount(mime)}}}\r\n{mime})");
                else if (command.StartsWith("UID FETCH")) await writer.WriteLineAsync($"* 1 FETCH (UID 42 FLAGS () RFC822.SIZE {Encoding.ASCII.GetByteCount(mime)})");
                else if (command.StartsWith("LOGOUT")) await writer.WriteLineAsync("* BYE logout");
                else if (!command.StartsWith("LOGIN")) throw new InvalidOperationException("Unexpected IMAP fixture command: " + command);
                await writer.WriteLineAsync(tag + (command.StartsWith("EXAMINE") ? " OK [READ-ONLY] completed" : " OK completed"));
                if (command.StartsWith("LOGOUT")) return;
            }
        }

        public async ValueTask DisposeAsync()
        {
            listener.Stop(); if (trusted) roots.Remove(certificate); roots.Dispose(); certificate.Dispose();
            if (Completion.IsCompletedSuccessfully) await Completion;
        }
    }
}
