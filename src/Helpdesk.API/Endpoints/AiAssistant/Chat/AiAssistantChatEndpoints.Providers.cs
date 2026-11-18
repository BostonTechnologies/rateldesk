using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.AiAssistant.Chat;

public static partial class AiAssistantChatEndpoints
{
    private const string ProviderSetupUrl = "/admin/integration?tab=ai-providers";

    private static async Task<ChatCapabilities> ProviderCapabilitiesAsync(HttpContext context, HelpdeskDbContext db,
        IAiAssistantChatRuntimeState runtime, string organization, bool canWrite, bool administrator, CancellationToken ct)
    {
        var setup = administrator ? ProviderSetupUrl : null;
        if (!db.Database.IsNpgsql())
            return new(false, "Native chat requires PostgreSQL. Ticket webhook assistance is available on SQLite.", canWrite, setup);
        var service = context.RequestServices.GetService<IChatProviderProfiles>();
        if (service is null)
            return new(runtime.Current.Enabled, runtime.Current.Enabled ? null : "Connect an AI provider to use chat.", canWrite, setup);
        var profiles = await service.AvailableAsync(organization, ct);
        return new(profiles.Count > 0, profiles.Count == 0 ? "Enable an AI provider profile for this organization to start new work." : null,
            canWrite, setup, profiles, CanViewHistory: true);
    }

    private static void MapProviderCommands(RouteGroupBuilder group, Func<HttpContext, ChatScope> scope)
    {
        group.MapPost("/cancel", async (ChatCancelRequest request, HttpContext ctx, IAiAssistantChatStore store, CancellationToken ct) =>
        {
            await store.RequestCancellationAsync(scope(ctx), request, Actor(ctx), ct);
            return Results.Accepted();
        });

        group.MapPost("/remote-cleanup", async (ChatNewRequest request, HttpContext ctx, IAiAssistantChatStore store, CancellationToken ct) =>
        {
            await store.RequestProviderCleanupAsync(scope(ctx), request.ConversationId, Actor(ctx), ct);
            return Results.Accepted();
        });

        group.MapGet("/artifacts", (Guid conversationId, HttpContext ctx, IAiAssistantChatStore store, CancellationToken ct) =>
            store.ProviderArtifactsAsync(scope(ctx), conversationId, ct));

        group.MapGet("/artifacts/{artifactId:guid}", async (Guid conversationId, Guid artifactId, HttpContext ctx,
            IAiAssistantChatStore store, CancellationToken ct) =>
        {
            var file = await store.DownloadProviderArtifactAsync(scope(ctx), conversationId, artifactId, ct);
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(file.Contents, "application/octet-stream", file.Name);
        });
    }
}
