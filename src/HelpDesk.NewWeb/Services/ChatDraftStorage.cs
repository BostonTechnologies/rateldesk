using System.Security.Claims;
using System.Text.Json;
using Microsoft.JSInterop;

namespace HelpDesk.NewWeb.Services;

internal sealed record SavedChatDraft<T>(string Text, T? Pending) where T : class;

internal sealed class ChatDraftStorage(IJSRuntime js, ILogger logger) : IAsyncDisposable
{
    private IJSObjectReference? module;

    public static string? Actor(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
        ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") : null;

    public static string? Scope(string? actor, string route, Guid? conversation) =>
        actor is null ? null : JsonSerializer.Serialize(new[] { actor, route, conversation?.ToString("D") ?? "new" });

    public async Task<SavedChatDraft<T>?> ReadAsync<T>(string? key) where T : class
    {
        if (key is null) return null;
        try
        {
            module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/chat-drafts.js");
            return module is null ? null : await module.InvokeAsync<SavedChatDraft<T>?>("read", key);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            logger.LogDebug(ex, "Chat draft storage is unavailable.");
            return null;
        }
    }

    public async Task WriteAsync<T>(string? key, string text, T? pending) where T : class
    {
        if (key is null || module is null) return;
        try { await module.InvokeVoidAsync("write", key, new SavedChatDraft<T>(text, pending)); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException) { logger.LogDebug(ex, "Chat draft could not be stored."); }
    }

    public async Task ClearActorAsync(string? actor)
    {
        if (actor is null || module is null) return;
        try { await module.InvokeVoidAsync("clearActor", actor); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException) { logger.LogDebug(ex, "Chat drafts could not be cleared."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (module is null) return;
        try { await module.DisposeAsync(); }
        catch (JSDisconnectedException) { logger.LogDebug("Chat draft circuit disconnected during disposal."); }
    }
}
