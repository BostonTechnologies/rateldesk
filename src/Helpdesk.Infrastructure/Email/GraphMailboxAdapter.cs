using Azure.Identity;
using Helpdesk.Application.Services.Email;
using Helpdesk.Shared.Models;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using MimeKit;

namespace Helpdesk.Infrastructure.Email;

public sealed class GraphMailboxAdapter(MailboxCredentialProtector secrets, Func<EmailInboxSettings, GraphServiceClient>? clientFactory = null) : IInboundMailboxAdapter, IHistoricalMailboxAdapter
{
    public InboundMailboxProvider Provider => InboundMailboxProvider.Graph;

    private GraphServiceClient Create(EmailInboxSettings settings) => clientFactory?.Invoke(settings) ?? new(
        new ClientSecretCredential(settings.TenantId, settings.ClientId, secrets.Unprotect(settings, settings.ClientSecret)),
        ["https://graph.microsoft.com/.default"]);

    public async Task<MailboxConnectionTest> TestAsync(EmailInboxSettings settings, CancellationToken ct)
    {
        using var client = Create(settings);
        var folder = await client.Users[settings.MailboxAddress].MailFolders[settings.MailboxFolder].GetAsync(
            request => request.Headers.Add("Prefer", "IdType=\"ImmutableId\""), ct);
        return new(folder?.Id is not null, "Graph authentication and folder read access succeeded. Move/write permissions were not tested.");
    }

    public async Task<IReadOnlyList<HistoricalSourcePreview>> PreviewAsync(EmailInboxSettings settings,
        IReadOnlyList<string> keys, CancellationToken ct)
    {
        if (keys.Count > 50) throw new ArgumentOutOfRangeException(nameof(keys));
        using var client = Create(settings);
        var previews = new List<HistoricalSourcePreview>(keys.Count);
        foreach (var key in keys)
        {
            try
            {
                var item = await client.Users[settings.MailboxAddress].Messages[key].GetAsync(request =>
                {
                    request.Headers.Add("Prefer", "IdType=\"ImmutableId\"");
                    request.QueryParameters.Select = ["id", "from", "subject", "receivedDateTime"];
                }, ct);
                previews.Add(new(key, item?.From?.EmailAddress?.Address, item?.Subject,
                    item?.ReceivedDateTime, item is not null, item is null ? "SourceMessageMissing" : null));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException error) when (error.ResponseStatusCode == 404)
            {
                previews.Add(new(key, null, null, null, false, "SourceMessageMissing"));
            }
        }
        return previews;
    }

    public async Task<InboundSourceMessage> FetchHistoricalAsync(EmailInboxSettings settings, string key, CancellationToken ct)
    {
        using var client = Create(settings);
        Stream raw;
        try
        {
            raw = await client.Users[settings.MailboxAddress].Messages[key].Content.GetAsync(
                request => request.Headers.Add("Prefer", "IdType=\"ImmutableId\""), ct)
                ?? throw new InvalidDataException("Graph returned no MIME content.");
        }
        catch (Microsoft.Kiota.Abstractions.ApiException error) when (error.ResponseStatusCode == 404)
        {
            throw new InboundSourceMissingException("Graph source message is absent.");
        }
        await using (raw)
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[81920];
            int read;
            while ((read = await raw.ReadAsync(bytes, ct)) != 0)
            {
                if (buffer.Length + read > MimeInboundNormalizer.MaxMessageBytes)
                    return new(key, null, HoldReason: "MessageSizeExceeded");
                await buffer.WriteAsync(bytes.AsMemory(0, read), ct);
            }
            buffer.Position = 0;
            var normalized = await MimeInboundNormalizer.ReadAsync(buffer, settings, key, ct);
            return normalized.Message is null ? normalized : normalized with
                { Message = normalized.Message with { GraphMessageId = key } };
        }
    }

    public async Task<InboundSourceBatch> FetchAsync(EmailInboxSettings settings, MailboxIngestionState state,
        IReadOnlySet<string> knownKeys, CancellationToken ct)
    {
        using var client = Create(settings);
        var delta = client.Users[settings.MailboxAddress].MailFolders[settings.MailboxFolder].Messages.Delta;
        if (state.Cursor is not null)
        {
            ValidateCursor(state.Cursor);
            delta = delta.WithUrl(state.Cursor);
        }
        Microsoft.Graph.Users.Item.MailFolders.Item.Messages.Delta.DeltaGetResponse? page;
        try
        {
            page = await delta.GetAsDeltaGetResponseAsync(request =>
        {
            request.Headers.Add("Prefer", $"IdType=\"ImmutableId\",odata.maxpagesize={settings.BatchSize}");
            if (state.Cursor is null) request.QueryParameters.Select = ["id", "isRead", "receivedDateTime"];
        }, ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException error) when (error.ResponseStatusCode == 410 && state.Cursor is not null)
        {
            // An expired delta token requires a full enumeration. Durable source receipts
            // survive this reset and suppress already captured immutable message IDs.
            page = await client.Users[settings.MailboxAddress].MailFolders[settings.MailboxFolder].Messages.Delta
                .GetAsDeltaGetResponseAsync(request =>
                {
                    request.Headers.Add("Prefer", $"IdType=\"ImmutableId\",odata.maxpagesize={settings.BatchSize}");
                    request.QueryParameters.Select = ["id", "isRead", "receivedDateTime"];
                }, ct);
        }
        if (page is null) throw new InvalidOperationException("Graph returned no delta page.");
        var result = new List<InboundSourceMessage>();
        foreach (var item in page.Value ?? [])
        {
            if (string.IsNullOrEmpty(item.Id) || item.AdditionalData.ContainsKey("@removed") || knownKeys.Contains(item.Id)) continue;
            if (settings.InitialImport == InitialMailImport.NewOnly && item.ReceivedDateTime is null)
            {
                result.Add(new(item.Id, null, HoldReason: "GraphReceiveTimeMissingReviewRequired"));
                continue;
            }
            if ((!state.Initialized && settings.InitialImport == InitialMailImport.ExistingUnread && item.IsRead == true) ||
                (settings.InitialImport == InitialMailImport.NewOnly && item.ReceivedDateTime <= settings.CreatedAt))
            {
                result.Add(new(item.Id, null, Ignore: true, HoldReason: "InitialBaselineSkipped"));
                continue;
            }
            // MIME includes the complete attachment collection without assuming one Graph attachment page.
            Stream raw;
            try
            {
                raw = await client.Users[settings.MailboxAddress].Messages[item.Id].Content.GetAsync(
                    request => request.Headers.Add("Prefer", "IdType=\"ImmutableId\""), ct)
                    ?? throw new InvalidDataException("Graph returned no MIME content.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException error) when (error.ResponseStatusCode == 404)
            {
                // Delta can race with deletion or a move. A definitive 404 is a durable
                // tombstone, so neighboring messages and this page's checkpoint can progress.
                result.Add(new(item.Id, null, Ignore: true, HoldReason: "SourceMessageMissing"));
                continue;
            }
            await using (raw)
            {
                using var buffer = new MemoryStream();
                var bytes = new byte[81920];
                int read;
                while ((read = await raw.ReadAsync(bytes, ct)) != 0)
                {
                    if (buffer.Length + read > MimeInboundNormalizer.MaxMessageBytes) break;
                    await buffer.WriteAsync(bytes.AsMemory(0, read), ct);
                }
                if (read != 0)
                {
                    result.Add(new(item.Id, null, HoldReason: "MessageSizeExceeded"));
                    continue;
                }
                buffer.Position = 0;
                var normalized = await MimeInboundNormalizer.ReadAsync(buffer, settings, item.Id, ct);
                result.Add(normalized.Message is null ? normalized : normalized with
                    { Message = normalized.Message with { GraphMessageId = item.Id } });
            }
        }
        var next = page.OdataNextLink ?? page.OdataDeltaLink ?? throw new InvalidDataException("Graph returned no checkpoint.");
        ValidateCursor(next);
        return new(result, next, page.OdataNextLink is null);
    }

    internal static void ValidateCursor(string cursor)
    {
        if (!Uri.TryCreate(cursor, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Invalid Graph checkpoint destination.");
    }

    public async Task AcknowledgeAsync(EmailInboxSettings settings, string key, CancellationToken ct)
    {
        using var client = Create(settings);
        if (settings.MarkReadAfterSuccess)
        {
            try
            {
                await client.Users[settings.MailboxAddress].Messages[key].PatchAsync(new Message { IsRead = true },
                    request => request.Headers.Add("Prefer", "IdType=\"ImmutableId\""), ct);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException error) when (error.ResponseStatusCode == 404)
            {
                // PATCH identifies only the source message, so this response is definitive.
                throw new InboundSourceMissingException("Graph source message is already absent.");
            }
        }
        if (string.IsNullOrWhiteSpace(settings.ProcessedFolder)) return;
        try
        {
            await client.Users[settings.MailboxAddress].Messages[key].Move.PostAsync(
                new Microsoft.Graph.Users.Item.Messages.Item.Move.MovePostRequestBody { DestinationId = settings.ProcessedFolder },
                request => request.Headers.Add("Prefer", "IdType=\"ImmutableId\""), ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException error) when (error.ResponseStatusCode == 404)
        {
            // MOVE can fail because either the source or destination is unavailable. Probe
            // the immutable source ID instead of making a terminal decision from status alone.
            try
            {
                await client.Users[settings.MailboxAddress].Messages[key].GetAsync(request =>
                {
                    request.Headers.Add("Prefer", "IdType=\"ImmutableId\"");
                    request.QueryParameters.Select = ["id"];
                }, ct);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException probeError) when (probeError.ResponseStatusCode == 404)
            {
                throw new InboundSourceMissingException("Graph source message is already absent.");
            }
            throw;
        }
    }
}
