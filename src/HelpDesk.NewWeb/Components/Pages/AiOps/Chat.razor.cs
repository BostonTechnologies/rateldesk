using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.DTOs.AiOps;
using HelpDesk.NewWeb.Components.Shared;
using HelpDesk.NewWeb.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;

namespace HelpDesk.NewWeb.Components.Pages.AiOps;

public partial class Chat
{
    [SupplyParameterFromQuery(Name = "organizationId")] public string? Organization { get; set; }
    [SupplyParameterFromQuery(Name = "conversationId")] public Guid? Conversation { get; set; }
    private List<AiOpsOrganizationDto> organizations = [];
    private List<ChatConversationSummary> history = [];
    private List<AiPromptDto> prompts = [];
    private readonly HashSet<Guid> promptIds = [];
    private ChatOrganizationConversation? selected;
    private ChatCapabilities? capabilities;
    private ChatStartRequest? pendingStart;
    private AiAssistantComposer? composer;
    private (string? Organization, Guid? Conversation)? loadedRoute;
    private string organization = "", search = "", message = "";
    private bool loading = true, busy, includeArchived, hasMore, disposed;
    private ChatIdentitySnapshot? identity;
    private string? error;
    private int generation, historyGeneration;
    private CancellationTokenSource cancellation = new();
    private bool CanStart => capabilities is { Enabled: true, CanWrite: true };
    private string Route => $"api/v1/ai/chat/organizations/{Uri.EscapeDataString(organization)}";
    private static string Url(string org, Guid? id = null) => $"/ai/chat?organizationId={Uri.EscapeDataString(org)}" + (id is null ? "" : $"&conversationId={id}");
    private string OrganizationName(string org) => organizations.FirstOrDefault(x => x.Id == org)?.Name ?? org;
    private bool Current(int value) => !disposed && value == generation;
    private static string StateLabel(ChatState state) => state switch { ChatState.Idle => "Ready", ChatState.Processing => "Working", ChatState.WaitingForChildren => "Waiting for delegated follow-ups", ChatState.AwaitingApproval => "Needs a decision", ChatState.DeliveryUnknown => "Needs recovery", _ => "Archived" };
    private static string StateClass(ChatState state) => state switch { ChatState.Processing or ChatState.WaitingForChildren => "active", ChatState.AwaitingApproval or ChatState.DeliveryUnknown => "attention", _ => "" };
    private sealed record ChatStarter(string Title, string Description, string Icon, string Prompt);
    private static readonly ChatStarter[] Starters =
    [
        new("Create a script", "Turn a task into a reusable script.", Icons.Material.Outlined.Code,
            "Help me create a script. Ask me what it should do and which environment it will run in."),
        new("Review notifications", "Find out what needs attention.", Icons.Material.Outlined.NotificationsNone,
            "Review the notifications I can access in this organization. Summarize what needs attention and suggest next steps."),
        new("Plan a flow", "Work through an automation idea.", Icons.Material.Outlined.AccountTree,
            "Help me plan an automation flow. Ask me about the trigger, the desired outcome and the steps involved."),
        new("Explore a question", "Get help with an operational task.", Icons.Material.Outlined.HelpOutline,
            "Help me work through an operational question. Ask me what I want to understand or achieve.")
    ];

    protected override async Task OnInitializedAsync()
    {
        identity = new((await Authentication.GetAuthenticationStateAsync()).User);
        Authentication.AuthenticationStateChanged += IdentityChanged;
    }
    protected override Task OnParametersSetAsync() => loadedRoute == (Organization, Conversation) ? Task.CompletedTask : LoadWorkspaceAsync();

    private async Task LoadWorkspaceAsync()
    {
        var current = ++generation;
        var previous = cancellation; cancellation = new();
        var ct = cancellation.Token;
        loadedRoute = (Organization, Conversation);
        organization = Organization ?? ""; selected = null; capabilities = null; history = []; prompts = []; promptIds.Clear();
        message = ""; pendingStart = null; loading = true; busy = false; error = null; hasMore = false; search = "";
        await previous.CancelAsync(); previous.Dispose();
        try
        {
            using var api = Clients.CreateClient("HelpdeskApi");
            var orgs = await api.GetFromJsonAsync<List<AiOpsOrganizationDto>>("api/v1/ai/chat/organizations", ct) ?? [];
            if (!Current(current)) return;
            organizations = orgs;
            if (organization.Length == 0 && orgs.Count > 0)
            {
                organization = orgs[0].Id;
                loadedRoute = (organization, Conversation);
                Navigation.NavigateTo(Url(organization, Conversation), replace: true);
            }
            if (organization.Length == 0) return;
            if (!orgs.Any(x => x.Id == organization)) { organization = ""; error = "Organization access is unavailable."; return; }
            var caps = await api.GetFromJsonAsync<ChatCapabilities>($"{Route}/capabilities", ct);
            if (!Current(current)) return;
            capabilities = caps;
            if (caps?.Enabled != true) return;
            await ReadHistoryAsync();
            if (!Current(current)) return;
            if (caps.CanWrite)
            {
                var guidance = await api.GetFromJsonAsync<List<AiPromptDto>>($"api/v1/ai/ops/prompts?organizationId={Uri.EscapeDataString(organization)}", ct) ?? [];
                if (!Current(current)) return;
                prompts = guidance.Where(x => !x.Archived).ToList();
            }
            if (Conversation is { } id)
            {
                var chat = await api.GetFromJsonAsync<ChatOrganizationConversation>($"{Route}/conversations/{id}", ct);
                if (Current(current) && chat?.OrganizationId == organization) selected = chat;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        { if (Current(current)) { selected = null; error = "Unable to load this organization chat. Check your access and connection, then refresh."; } }
        finally { if (Current(current)) loading = false; }
    }

    private void ChangeOrganization(ChangeEventArgs e)
    {
        var next = e.Value?.ToString() ?? "";
        if (next == organization) return;
        ++generation; selected = null; capabilities = null; history = []; message = ""; pendingStart = null;
        Navigation.NavigateTo(Url(next));
    }
    private void StartNew()
    {
        if (!CanStart || busy) return;
        selected = null; message = ""; pendingStart = null; promptIds.Clear(); error = null;
        loadedRoute = (organization, null);
        Navigation.NavigateTo(Url(organization));
    }
    private async Task UseStarterAsync(string prompt)
    {
        if (!CanStart || busy || pendingStart is not null) return;
        message = prompt;
        if (composer is not null) await composer.FocusAsync();
    }
    private void SelectPrompt(Guid id, ChangeEventArgs e) { if (e.Value is true && promptIds.Count < 8) promptIds.Add(id); else if (e.Value is false) promptIds.Remove(id); }
    private async Task SearchChangedAsync(ChangeEventArgs e) { search = e.Value?.ToString() ?? ""; await ReadHistoryAsync(); }
    private Task SearchAsync() => ReadHistoryAsync();
    private Task MoreAsync() => ReadHistoryAsync(true);
    private async Task ArchivedChangedAsync(ChangeEventArgs e) { includeArchived = e.Value is true; await ReadHistoryAsync(); }
    private async Task ReadHistoryAsync(bool more = false)
    {
        if (capabilities?.Enabled != true) return;
        var current = generation; var request = ++historyGeneration; var route = Route; var ct = cancellation.Token;
        try
        {
            using var api = Clients.CreateClient("HelpdeskApi");
            var rows = await api.GetFromJsonAsync<List<ChatConversationSummary>>($"{route}/history?skip={(more ? history.Count : 0)}&q={Uri.EscapeDataString(search)}&archived={includeArchived.ToString().ToLowerInvariant()}", ct) ?? [];
            if (!Current(current) || request != historyGeneration) return;
            hasMore = rows.Count == 50;
            history = more ? history.Concat(rows).DistinctBy(x => x.ConversationId).ToList() : rows;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        { if (Current(current) && request == historyGeneration) error = "History could not be loaded. Refresh to try again."; }
    }

    private async Task SendFirstAsync()
    {
        if (!CanStart || busy || string.IsNullOrWhiteSpace(message)) return;
        var current = generation; var route = Route; var org = organization; var ct = cancellation.Token;
        var request = pendingStart ??= new(Guid.NewGuid(), Guid.NewGuid(), message, promptIds.ToArray());
        busy = true; error = null;
        try
        {
            using var api = Clients.CreateClient("HelpdeskChatCommands");
            using var response = await api.PostAsJsonAsync($"{route}/start", request, ct);
            response.EnsureSuccessStatusCode();
            var saved = await response.Content.ReadFromJsonAsync<ChatOrganizationConversation>(ct);
            if (!Current(current)) return;
            if (saved is null || saved.OrganizationId != org || saved.ConversationId != request.ConversationId)
                throw new JsonException("The conversation response did not match its requested scope.");
            selected = saved; pendingStart = null; message = "";
            loadedRoute = (org, saved.ConversationId);
            Navigation.NavigateTo(Url(org, saved.ConversationId), replace: true);
            await ReadHistoryAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (!Current(current)) return;
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest }) pendingStart = null;
            error = "The first message could not be confirmed. Retry uses the original conversation and message IDs; it does not send a second copy. You can also reopen the conversation from history.";
        }
        finally { if (Current(current)) busy = false; }
    }

    private void StateChanged(Guid id, ChatState state)
    {
        if (selected?.ConversationId != id) return;
        selected = selected with { State = state };
        history = history.Select(x => x.ConversationId == id ? x with { State = state } : x).Where(x => includeArchived || x.State != ChatState.Archived).ToList();
    }
    private async Task ArchiveAsync()
    {
        if (busy || selected is not { State: ChatState.Idle } chat || capabilities?.CanWrite != true) return;
        var current = generation; busy = true; error = null;
        try
        {
            using var api = Clients.CreateClient("HelpdeskChatCommands");
            using var response = await api.PostAsJsonAsync($"{Route}/archive", new ChatNewRequest(chat.ConversationId), cancellation.Token);
            if (!Current(current)) return;
            response.EnsureSuccessStatusCode(); StateChanged(chat.ConversationId, ChatState.Archived);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { if (Current(current)) error = "Archive could not be confirmed. Refresh before retrying."; }
        finally { if (Current(current)) busy = false; }
    }
    private void IdentityChanged(Task<AuthenticationState> changed) => _ = InvokeAsync(async () =>
    {
        var user = (await changed).User;
        if (disposed || identity?.Matches(user) == true) return;
        identity = new(user);
        loadedRoute = null; await LoadWorkspaceAsync(); StateHasChanged();
    });
    public async ValueTask DisposeAsync()
    {
        disposed = true; ++generation;
        Authentication.AuthenticationStateChanged -= IdentityChanged;
        await cancellation.CancelAsync(); cancellation.Dispose();
    }
}
