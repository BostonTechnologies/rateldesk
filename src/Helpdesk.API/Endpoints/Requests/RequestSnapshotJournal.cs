using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.DTOs.Notification;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Requests;

/// <summary>Stages private stream invalidations in the request mutation's transaction.</summary>
internal static class RequestSnapshotJournal
{
    internal const string Category = "Internal.RequestSnapshot";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task StageAsync(HelpdeskDbContext db, IEnumerable<RequestSnapshotChangedEvent> changes,
        CancellationToken ct)
    {
        var markers = changes.Select(CreateMarker).DistinctBy(marker => marker.Id).ToArray();
        var trackedIds = db.Notifications.Local.Select(marker => marker.Id).ToHashSet();
        var candidateIds = markers.Where(marker => !trackedIds.Contains(marker.Id)).Select(marker => marker.Id).ToArray();
        if (candidateIds.Length == 0) return;
        var persistedIds = await db.Notifications.AsNoTracking()
            .Where(marker => candidateIds.Contains(marker.Id)).Select(marker => marker.Id).ToArrayAsync(ct);
        var existingIds = persistedIds.Concat(trackedIds).ToHashSet();
        db.Notifications.AddRange(markers.Where(marker => !existingIds.Contains(marker.Id)));
        // The caller saves this row before committing the existing mutation transaction.
        // Nothing is sent to notification subscribers before that commit.
    }

    private static NotificationEntity CreateMarker(RequestSnapshotChangedEvent change)
    {
        var payload = JsonSerializer.Serialize(change, JsonOptions);
        return new NotificationEntity
        {
            Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(Category + "\n" + payload)).AsSpan(0, 16)),
            Title = change.EventType,
            Message = payload,
            Severity = NotificationSeverity.Info,
            CreatedUtc = change.OccurredUtc,
            Source = change.Source,
            Category = Category,
            TenantId = change.TenantId,
            Reference = change.Reference,
            CorrelationId = change.CorrelationId,
            // No recipient and no global visibility keeps a stream marker out of user inboxes.
            UserId = null,
            IsGlobal = false
        };
    }
}
