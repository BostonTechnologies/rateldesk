using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.API.Endpoints.AiAssistant.Chat;

internal static class OpenAiProviderWebhookEndpoints
{
    private const int MaximumBytes = 128 * 1024;

    internal static void MapOpenAiProviderWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ai/provider-webhooks/openai/{profileId:guid}", ReceiveAsync).AllowAnonymous()
            .WithTags("AI Providers");
    }

    private static async Task<IResult> ReceiveAsync(Guid profileId, HttpContext context, IConfiguration settings,
        HelpdeskDbContext db, [FromServices] TimeProvider time, CancellationToken ct)
    {
        var secret = settings[$"AiProviders:OpenAiWebhooks:{profileId:N}"];
        if (string.IsNullOrEmpty(secret) || !db.Database.IsNpgsql()) return Results.NotFound();
        var req = context.Request;
        if (req.ContentLength > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var payload = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await req.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (payload.Length + length > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            payload.Write(buffer, 0, length);
        }

        var id = req.Headers["webhook-id"].ToString();
        var timestamp = req.Headers["webhook-timestamp"].ToString();
        var bytes = payload.ToArray();
        var now = time.GetUtcNow();
        if (!VerifySignature(secret, id, timestamp, req.Headers["webhook-signature"].ToString(), bytes, now))
            return Results.Unauthorized();

        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException) { return Results.BadRequest(); }
        using (document)
        {
            var root = document.RootElement;
            if (Get(root, "type") is not ("agent.session.idle" or "agent.session.action_required" or "agent.session.failed" or
                "agent.session.in_progress"))
                return Results.NoContent();
            if (!root.TryGetProperty("data", out var data) || Get(data, "id") is not { Length: > 0 and <= 512 } sessionId)
                return Results.BadRequest();

            var conversationId = await db.Set<AiAssistantChatConversation>().AsNoTracking().Where(row =>
                row.ProviderKind == ChatProviderKind.OpenAiAgents && row.ProviderProfileId == profileId &&
                row.AiAssistantSessionId == sessionId && row.State != ChatState.Archived)
                .Select(row => (Guid?)row.Id).SingleOrDefaultAsync(ct);
            if (conversationId is null) return Results.NoContent();
            await WakeAsync(db, conversationId.Value, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"webhook\n{Get(root, "id") ?? id}"))), now, ct);
            return Results.NoContent();
        }
    }

    private static async Task WakeAsync(HelpdeskDbContext db, Guid id, string deliveryKey, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var conversation = await db.Set<AiAssistantChatConversation>().FromSqlInterpolated(
            $"SELECT * FROM \"AiAssistantChatConversations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (conversation.State == ChatState.Archived || await db.Set<OpenAiChatEventReceipt>().AnyAsync(
            row => row.ConversationId == id && row.RemoteKey == deliveryKey, ct))
            return;
        var dispatch = await db.Set<OpenAiChatDispatch>().SingleOrDefaultAsync(row => row.ConversationId == id &&
            row.MessageId == conversation.ActiveMessageId && row.State != OpenAiDispatchState.Completed && row.State != OpenAiDispatchState.Rejected, ct);
        if (dispatch is null || dispatch.NextObservationAtUtc == DateTimeOffset.MaxValue) return;
        db.Add(new OpenAiChatEventReceipt { ConversationId = id, RemoteKey = deliveryKey });
        dispatch.NextObservationAtUtc = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    internal static bool VerifySignature(string secret, string id, string timestamp, string signature,
        ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (id is not { Length: > 0 and <= 200 } || id.Any(char.IsControl) || timestamp.Length > 20 ||
            signature.Length > 4096 || !secret.StartsWith("whsec_", StringComparison.Ordinal) || secret.Length > 512 ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < now.ToUnixTimeSeconds() - 300 || seconds > now.ToUnixTimeSeconds() + 300)
            return false;
        Span<byte> key = stackalloc byte[384];
        if (!Convert.TryFromBase64String(secret[6..], key, out var keyLength) || keyLength < 16) return false;
        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key[..keyLength]);
        hmac.AppendData(prefix);
        hmac.AppendData(body);
        var expected = hmac.GetHashAndReset();
        Span<byte> supplied = stackalloc byte[32];
        foreach (var candidate in signature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (candidate.StartsWith("v1,", StringComparison.Ordinal) &&
                Convert.TryFromBase64Chars(candidate.AsSpan(3), supplied, out var written) && written == expected.Length &&
                CryptographicOperations.FixedTimeEquals(supplied, expected))
                return true;
        }
        return false;
    }

    private static string? Get(JsonElement parent, string property) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
