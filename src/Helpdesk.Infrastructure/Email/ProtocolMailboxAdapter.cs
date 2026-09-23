using Azure.Core;
using Azure.Identity;
using Helpdesk.Application.Services.Email;
using Helpdesk.Shared.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Helpdesk.Infrastructure.Email;

public sealed class ProtocolMailboxAdapter(
    InboundMailboxProvider provider,
    MailboxDestinationPolicy destinations,
    MailboxCredentialProtector secrets) : IInboundMailboxAdapter
{
    public InboundMailboxProvider Provider { get; } = provider is InboundMailboxProvider.Imap or InboundMailboxProvider.Pop3
        ? provider : throw new ArgumentOutOfRangeException(nameof(provider));

    private async Task AuthenticateAsync(MailService client, EmailInboxSettings settings, CancellationToken ct)
    {
        if (settings.Authentication == MailboxAuthentication.Password)
        {
            await client.AuthenticateAsync(settings.Username, secrets.Unprotect(settings, settings.Password), ct);
            return;
        }
        var credential = new ClientSecretCredential(settings.TenantId, settings.ClientId, secrets.Unprotect(settings, settings.ClientSecret));
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://outlook.office365.com/.default"]), ct);
        await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.MailboxAddress, token.Token), ct);
    }

    private async Task<ImapClient> OpenImapAsync(EmailInboxSettings settings, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 20000 };
        try
        {
            var socket = await destinations.ConnectAsync(settings.MailHost, settings.Port, ct);
            await client.ConnectAsync(socket, settings.MailHost, settings.Port, Tls(settings), ct);
            await AuthenticateAsync(client, settings, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private async Task<Pop3Client> OpenPopAsync(EmailInboxSettings settings, CancellationToken ct)
    {
        var client = new Pop3Client { Timeout = 20000 };
        try
        {
            var socket = await destinations.ConnectAsync(settings.MailHost, settings.Port, ct);
            await client.ConnectAsync(socket, settings.MailHost, settings.Port, Tls(settings), ct);
            await AuthenticateAsync(client, settings, ct);
            if (!client.Capabilities.HasFlag(Pop3Capabilities.UIDL))
                throw new NotSupportedException("Stable UIDL is required for unattended POP3 ingestion.");
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static SecureSocketOptions Tls(EmailInboxSettings settings) => settings.TlsMode switch
    {
        MailboxTlsMode.TlsOnConnect => SecureSocketOptions.SslOnConnect,
        MailboxTlsMode.StartTls => SecureSocketOptions.StartTls,
        _ => throw new InvalidOperationException("Verified TLS is required.")
    };

    public async Task<MailboxConnectionTest> TestAsync(EmailInboxSettings settings, CancellationToken ct)
    {
        if (Provider == InboundMailboxProvider.Pop3)
        {
            using var client = await OpenPopAsync(settings, ct);
            await client.NoOpAsync(ct);
            await client.DisconnectAsync(true, ct);
            return new(true, "POP3 authentication and UIDL available. Messages are retained on the server.");
        }
        using var imap = await OpenImapAsync(settings, ct);
        var folder = await imap.GetFolderAsync(settings.MailboxFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        await imap.DisconnectAsync(true, ct);
        return new(true, "IMAP authentication and read-only folder access succeeded. Write permissions were not tested.");
    }

    public async Task<InboundSourceBatch> FetchAsync(EmailInboxSettings settings, MailboxIngestionState state,
        IReadOnlySet<string> knownKeys, CancellationToken ct)
    {
        if (Provider == InboundMailboxProvider.Pop3) return await FetchPopAsync(settings, state, knownKeys, ct);
        using var client = await OpenImapAsync(settings, ct);
        var folder = await client.GetFolderAsync(settings.MailboxFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var epoch = folder.UidValidity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var uids = await folder.SearchAsync(SearchQuery.All, ct);
        var cursor = state.Cursor?.Split(':');
        var epochChanged = cursor is not null && cursor[0] != epoch;
        var boundary = cursor is { Length: >= 2 } && !epochChanged ? uint.Parse(cursor[1], System.Globalization.CultureInfo.InvariantCulture)
            : uids.Count > 0 ? uids[^1].Id : 0;
        var reviewBoundary = epochChanged ? boundary : cursor is { Length: 3 }
            ? uint.Parse(cursor[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var nextCursor = $"{epoch}:{boundary}:{reviewBoundary}";
        var selected = uids.Where(uid => !knownKeys.Contains($"{epoch}:{uid.Id}")).Take(settings.BatchSize).ToList();
        var result = new List<InboundSourceMessage>();
        foreach (var uid in selected)
        {
            var key = $"{epoch}:{uid.Id}";
            var summary = (await folder.FetchAsync([uid], MessageSummaryItems.Size | MessageSummaryItems.Flags, ct)).SingleOrDefault();
            if (summary is null)
            {
                // SEARCH and FETCH are separate IMAP operations. An expunge between them is
                // definitive for this UID and must not poison later messages in the batch.
                result.Add(new(key, null, Ignore: true, HoldReason: "SourceMessageMissing"));
                continue;
            }
            if ((!state.Initialized && settings.InitialImport == InitialMailImport.NewOnly && uid.Id <= boundary) ||
                (!state.Initialized && settings.InitialImport == InitialMailImport.ExistingUnread && summary.Flags.GetValueOrDefault().HasFlag(MessageFlags.Seen)))
            {
                result.Add(new(key, null, Ignore: true, HoldReason: "InitialBaselineSkipped"));
                continue;
            }
            if (summary.Size > MimeInboundNormalizer.MaxMessageBytes)
            {
                result.Add(new(key, null, HoldReason: "MessageSizeExceeded"));
                continue;
            }
            try
            {
                await using var raw = await folder.GetStreamAsync(uid, string.Empty, ct, new BoundedTransfer());
                var normalized = await MimeInboundNormalizer.ReadAsync(raw, settings, key, ct);
                result.Add(uid.Id <= reviewBoundary ? normalized with { HoldReason = "UidValidityChangedReviewRequired" } : normalized);
            }
            catch (MessageNotFoundException)
            {
                result.Add(new(key, null, Ignore: true, HoldReason: "SourceMessageMissing"));
            }
        }
        await client.DisconnectAsync(true, ct);
        return new(result, nextCursor, selected.Count < settings.BatchSize);
    }

    private async Task<InboundSourceBatch> FetchPopAsync(EmailInboxSettings settings, MailboxIngestionState state,
        IReadOnlySet<string> knownKeys, CancellationToken ct)
    {
        using var client = await OpenPopAsync(settings, ct);
        var uids = await client.GetMessageUidsAsync(ct);
        if (uids.Count > 10000) throw new InvalidOperationException("MailboxCapacityExceeded: reduce server retention before ingestion.");
        if (!state.Initialized && settings.InitialImport == InitialMailImport.NewOnly)
        {
            // Capture the baseline in one bounded durable batch; a later connection sees newly arriving UIDLs.
            return new(uids.Select(key => new InboundSourceMessage(key, null, Ignore: true,
                HoldReason: "InitialBaselineSkipped")).ToArray(), null, true);
        }
        var result = new List<InboundSourceMessage>();
        for (var index = 0; index < uids.Count && result.Count < settings.BatchSize; index++)
        {
            var key = uids[index];
            if (knownKeys.Contains(key)) continue;
            var size = await client.GetMessageSizeAsync(index, ct);
            if (size > MimeInboundNormalizer.MaxMessageBytes)
            {
                result.Add(new(key, null, HoldReason: "MessageSizeExceeded"));
                continue;
            }
            await using var raw = await client.GetStreamAsync(index, false, ct, new BoundedTransfer());
            result.Add(await MimeInboundNormalizer.ReadAsync(raw, settings, key, ct));
        }
        await client.DisconnectAsync(true, ct);
        return new(result, null, true);
    }

    public async Task AcknowledgeAsync(EmailInboxSettings settings, string key, CancellationToken ct)
    {
        // POP3 retention is deliberately non-destructive. UIDL receipts are its acknowledgment.
        if (Provider == InboundMailboxProvider.Pop3) return;
        try
        {
            using var client = await OpenImapAsync(settings, ct);
            var folder = await client.GetFolderAsync(settings.MailboxFolder, ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            var identity = key.Split(':');
            if (identity.Length != 2 || folder.UidValidity.ToString(System.Globalization.CultureInfo.InvariantCulture) != identity[0])
                throw new InvalidOperationException("UidValidityChanged");
            var uid = new UniqueId(uint.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture));
            if (settings.MarkReadAfterSuccess) await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            if (!string.IsNullOrWhiteSpace(settings.ProcessedFolder))
            {
                if (!client.Capabilities.HasFlag(ImapCapabilities.Move))
                    throw new NotSupportedException("Server does not support safe MOVE; configure mark-read disposition instead.");
                var destination = await client.GetFolderAsync(settings.ProcessedFolder, ct);
                await folder.MoveToAsync(uid, destination, ct);
            }
            await client.DisconnectAsync(true, ct);
        }
        catch (MessageNotFoundException)
        {
            throw new InboundSourceMissingException("IMAP source message is already absent.");
        }
    }
    private sealed class BoundedTransfer : ITransferProgress
    {
        public void Report(long bytesTransferred, long totalSize) => Report(bytesTransferred);
        public void Report(long bytesTransferred)
        {
            if (bytesTransferred > MimeInboundNormalizer.MaxMessageBytes) throw new InvalidDataException("MessageSizeExceeded");
        }
    }

}
