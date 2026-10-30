extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.DTOs.AiOps;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Page = NewWeb::HelpDesk.NewWeb.Components.Pages.AiOps.Investigations;
using Chat = NewWeb::HelpDesk.NewWeb.Components.Shared.AiAssistantChat;

namespace Helpdesk.Tests.NewWeb;

public sealed class AiInvestigationsWorkspaceTests
{
    [Fact]
    public async Task Scoped_staff_workspace_uses_investigation_directory_instead_of_admin_organization_list()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll(".ai-investigation").Count));
        Assert.Contains("/api/v1/ai/ops/organizations", api.Requests);
        Assert.DoesNotContain("/api/v1/organizations", api.Requests);
        Assert.False(page.Find(".ai-toolbar .ai-primary").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Refresh_retries_unavailable_investigation_directory_without_admin_fallback()
    {
        using var api = new Api { FailOrganizations = true };
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[role='alert']")));
        Assert.True(page.Find(".ai-toolbar .ai-primary").HasAttribute("disabled"));
        api.FailOrganizations = false;
        page.FindAll(".ai-toolbar button").Single(x => x.TextContent == "Refresh").Click();
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll(".ai-investigation").Count));
        Assert.Empty(page.FindAll("[role='alert']"));
        Assert.Equal(2, api.Requests.Count(x => x == "/api/v1/ai/ops/organizations"));
        Assert.DoesNotContain("/api/v1/organizations", api.Requests);
    }

    [Fact]
    public async Task History_has_focused_search_and_status_filters_and_creation_is_explicit()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll(".ai-investigation").Count));
        Assert.Equal(new[] { "1", "1", "1" }, page.FindAll(".ai-metrics strong").Select(x => x.TextContent));
        Assert.Empty(page.FindAll(".ai-create"));
        page.Find(".ai-investigation-list select").Change("approval");
        Assert.Contains("Review proposed change", Assert.Single(page.FindAll(".ai-investigation")).TextContent);
        page.Find(".ai-investigation-list select").Change("all");
        page.Find("#investigation-search").Input("Delivery");
        page.Find(".ai-search").Submit();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".ai-investigation")));
        Assert.Contains(api.Requests, request => request.Contains("q=Delivery", StringComparison.Ordinal));
        Assert.Empty(api.Creations);
        page.Find(".ai-toolbar .ai-primary").Click();
        Assert.Contains("Choose an organization", page.Find(".ai-create").TextContent);
        Assert.Contains("Saving context does not send a message", page.Find(".ai-create").TextContent);
        Assert.Empty(api.Creations);
    }

    [Fact]
    public async Task Failed_history_is_unavailable_instead_of_healthy_zero_and_refresh_recovers()
    {
        using var api = new Api { FailHistory = true };
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("History is unavailable", page.Find(".ai-list-note").TextContent));
        Assert.Equal(new[] { "—", "—", "—" }, page.FindAll(".ai-metrics strong").Select(x => x.TextContent));
        api.FailHistory = false;
        page.FindAll(".ai-toolbar button").Single(x => x.TextContent == "Refresh").Click();
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll(".ai-investigation").Count));
        Assert.Equal(new[] { "1", "1", "1" }, page.FindAll(".ai-metrics strong").Select(x => x.TextContent));
        Assert.Empty(page.FindAll("[role='alert']"));
    }

    [Fact]
    public async Task Selected_tickets_survive_search_and_failed_save_and_retry_reuses_same_context_identity()
    {
        using var api = new Api { RejectCreate = true };
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll(".ai-investigation").Count));
        page.Find(".ai-toolbar select").Change("org-a");
        page.Find(".ai-toolbar .ai-primary").Click();
        page.Find(".ai-create > label input").Input("Investigate delivery delay");
        page.FindAll(".ai-ticket-picker input")[0].Change(true);
        page.Find(".ai-prompt-picker input").Change(true);
        Assert.Contains("Always included: Baseline", page.Find(".ai-default-guidance").TextContent);
        Assert.DoesNotContain("Archived guidance", page.Find(".ai-create").TextContent);
        page.Find("#investigation-ticket-search").Input("second");
        page.Find(".ai-create .ai-search").Submit();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".ai-ticket-picker input").Count));
        Assert.True(page.FindAll(".ai-ticket-picker input")[0].HasAttribute("checked"));
        page.Find(".ai-create-actions .ai-primary").Click();
        page.WaitForAssertion(() => Assert.Contains("Your selections are preserved", page.Find("[role='alert']").TextContent));
        var first = Assert.Single(api.Creations);
        Assert.Equal("org-a", first.OrganizationId);
        Assert.Equal(new[] { "first" }, first.TicketIds);
        Assert.Equal(new[] { api.OptionalPromptId }, first.PromptIds);
        Assert.Equal("Investigate delivery delay", first.Title);
        Assert.True(page.Find(".ai-prompt-picker input").HasAttribute("checked"));
        api.RejectCreate = false;
        page.Find(".ai-create-actions .ai-primary").Click();
        page.WaitForAssertion(() => Assert.Equal(2, api.Creations.Count));
        Assert.Equal(first.Id, api.Creations[1].Id);
        Assert.Equal(first.TicketIds, api.Creations[1].TicketIds);
        Assert.EndsWith($"/ai/ops?id={first.Id}", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Requests, request => request.Contains("/messages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Switching_organization_clears_context_choices_and_scopes_history_and_guidance()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll(".ai-investigation").Count));
        page.Find(".ai-toolbar select").Change("org-a");
        page.Find(".ai-toolbar .ai-primary").Click();
        page.FindAll(".ai-ticket-picker input")[0].Change(true);
        page.Find(".ai-prompt-picker input").Change(true);
        page.Find(".ai-toolbar select").Change("org-b");
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".ai-prompt-picker input")));
        Assert.Contains("0 / 12 selected", page.Find(".ai-create").TextContent);
        Assert.Contains("0 / 8 optional prompts", page.Find(".ai-create").TextContent);
        Assert.Empty(page.FindAll(".ai-investigation"));
        Assert.DoesNotContain("Baseline", page.Find(".ai-create").TextContent);
        Assert.Contains(api.Requests, request => request.Contains("investigations?q=&organizationId=org-b", StringComparison.Ordinal));
        Assert.Empty(api.Creations);
    }

    [Fact]
    public async Task Deep_link_keeps_history_visible_and_live_state_updates_archive_availability_and_summary()
    {
        using var api = new Api();
        await using var context = Context(api);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/ai/ops?id={api.Rows[0].Id}");
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Single(page.FindComponents<Stub<Chat>>()));
        Assert.Equal(4, page.FindAll(".ai-investigation").Count);
        Assert.Contains("Synthetic saved ticket and guidance", page.Find(".ai-snapshot").TextContent);
        var chat = page.FindComponent<Stub<Chat>>();
        Assert.Equal(api.Rows[0].Id, chat.Instance.Parameters.Get(x => x.InvestigationId));
        Assert.Equal("ticket-a", chat.Instance.Parameters.Get(x => x.TicketId));
        Assert.True(chat.Instance.Parameters.Get(x => x.Workspace));
        Assert.True(page.Find(".ai-chat-heading button").HasAttribute("disabled"));
        await page.InvokeAsync(() => chat.Instance.Parameters.Get(x => x.StateChanged).InvokeAsync(ChatState.Idle));
        Assert.False(page.Find(".ai-chat-heading button").HasAttribute("disabled"));
        Assert.Equal("0", page.FindAll(".ai-metrics strong")[0].TextContent);
        page.Find(".ai-chat-heading button").Click();
        page.WaitForAssertion(() => Assert.Single(api.Archived));
        Assert.Equal(api.Rows[0].Id, api.Archived[0]);
        Assert.Contains("Archived", page.Find(".ai-chat-heading").TextContent);
        Assert.True(page.Find(".ai-chat-heading button").HasAttribute("disabled"));
    }

    private static BunitContext Context(Api api)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        context.ComponentFactories.AddStub<Chat>();
        return context;
    }

    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public Guid OptionalPromptId { get; } = Guid.NewGuid();
        public bool RejectCreate { get; set; }
        public bool FailHistory { get; set; }
        public bool FailOrganizations { get; set; }
        public List<string> Requests { get; } = [];
        public List<CreateAiInvestigationDto> Creations { get; } = [];
        public List<Guid> Archived { get; } = [];
        public AiInvestigationDto[] Rows { get; private set; } =
        [
            Row("Active investigation", ChatState.Processing), Row("Review proposed change", ChatState.AwaitingApproval),
            Row("Delivery unconfirmed", ChatState.DeliveryUnknown), Row("Resolved investigation", ChatState.Archived)
        ];
        private static AiInvestigationDto Row(string title, ChatState state) => new(Guid.NewGuid(), "org-a", title, "ticket-a", "incidents", "Synthetic saved ticket and guidance", DateTime.UtcNow, state);
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://desk.example/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/api/v1/organizations") return new(HttpStatusCode.Forbidden);
                if (path == "/api/v1/ai/ops/organizations" && FailOrganizations) return new(HttpStatusCode.ServiceUnavailable);
                if (path == "/api/v1/ai/ops/organizations")
                    return Json(new[] { new AiOpsOrganizationDto("org-a", "Organization A"), new AiOpsOrganizationDto("org-b", "Organization B") });
                if (path.EndsWith("/investigations", StringComparison.Ordinal) && FailHistory) return new(HttpStatusCode.ServiceUnavailable);
                if (path.EndsWith("/investigations", StringComparison.Ordinal))
                    return Json(Rows.Where(x => (!query.TryGetValue("organizationId", out var org) || x.OrganizationId == org.ToString()) && x.Title.Contains(query["q"].ToString(), StringComparison.OrdinalIgnoreCase)).Select(x => x with { ContextSnapshot = "" }).ToArray());
                if (path.Contains("/investigations/", StringComparison.Ordinal)) return Json(Rows.Single(x => path.EndsWith(x.Id.ToString(), StringComparison.Ordinal)));
                if (path.EndsWith("/prompts", StringComparison.Ordinal)) return Json(query["organizationId"] == "org-b" ? Array.Empty<AiPromptDto>() : new[]
                {
                    new AiPromptDto(Guid.NewGuid(), "org-a", "Baseline", "Included automatically", "Synthetic default guidance", 2, true, false),
                    new AiPromptDto(OptionalPromptId, "org-a", "Diagnostic guide", "Check the evidence", "Synthetic optional guidance", 3, false, false),
                    new AiPromptDto(Guid.NewGuid(), "org-a", "Archived guidance", "Historical", "Old content", 1, false, true)
                });
                if (path.EndsWith("/tickets", StringComparison.Ordinal)) return Json(query["organizationId"] == "org-b" ? Array.Empty<AiContextTicketDto>() : query["q"] == "second"
                    ? new[] { new AiContextTicketDto("second", "requests", "REQ-2", "Second ticket") }
                    : new[] { new AiContextTicketDto("first", "incidents", "INC-1", "First ticket") });
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/archive", StringComparison.Ordinal))
            {
                Archived.Add(Guid.Parse(path.Split('/')[6]));
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/investigations", StringComparison.Ordinal))
            {
                var saved = (await request.Content!.ReadFromJsonAsync<CreateAiInvestigationDto>(cancellationToken))!;
                Creations.Add(saved);
                if (!RejectCreate && !Rows.Any(row => row.Id == saved.Id))
                    Rows = [.. Rows, new AiInvestigationDto(saved.Id, saved.OrganizationId, saved.Title,
                        saved.TicketIds[0], "incidents", "Synthetic saved ticket and guidance", DateTime.UtcNow)];
                return new(RejectCreate ? HttpStatusCode.Conflict : HttpStatusCode.Created);
            }
            throw new InvalidOperationException($"Unexpected fixture request: {request.Method} {path}");
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
