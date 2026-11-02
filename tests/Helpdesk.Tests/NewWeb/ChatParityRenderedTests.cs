extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using NewWeb::HelpDesk.NewWeb.Services;
using Activity = NewWeb::HelpDesk.NewWeb.Components.Shared.AiAssistantTurnActivity;
using Markdown = NewWeb::HelpDesk.NewWeb.Components.Shared.AiAssistantMarkdown;
using Menu = NewWeb::HelpDesk.NewWeb.Components.Layout.NavMenu;
using Chat = NewWeb::HelpDesk.NewWeb.Components.Shared.AiAssistantChat;
using Composer = NewWeb::HelpDesk.NewWeb.Components.Shared.AiAssistantComposer;

namespace Helpdesk.Tests.NewWeb;

public sealed class ChatParityRenderedTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Authentication_refresh_preserves_draft_but_changed_access_clears_it(bool changeAccount)
    {
        await using var context = Context();
        var authorization = context.AddAuthorization();
        authorization.SetAuthorized("Synthetic operator");
        context.Services.AddHttpClient("HelpdeskApi", client => client.BaseAddress = new("https://desk.example.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => new ChatApi());
        context.ComponentFactories.AddStub<Composer>();
        var chat = context.Render<Chat>(p => p.Add(x => x.OrganizationId, "org-a"));
        var composer = chat.FindComponent<Stub<Composer>>();
        await chat.InvokeAsync(() => composer.Instance.Parameters.Get(x => x.ValueChanged).InvokeAsync("Unsent draft"));
        authorization.SetAuthorized("Synthetic operator");
        await chat.InvokeAsync(() => { });
        Assert.Equal("Unsent draft", chat.FindComponent<Stub<Composer>>().Instance.Parameters.Get(x => x.Value));
        if (changeAccount) authorization.SetAuthorized("Another synthetic operator");
        else authorization.SetRoles("Incident.Read");
        chat.WaitForAssertion(() => Assert.Contains("Your account changed", chat.Markup));
        Assert.Empty(chat.FindComponents<Stub<Composer>>());
    }

    [Fact]
    public async Task Completed_tools_remain_identifiable_without_expanding_group_and_recorded_details_are_inert()
    {
        await using var context = Context();
        var events = new[] { E(1, "operator"), E(2, "tool_call", "one"), E(3, "tool_call", "two"), E(4, "tool_result", "two"), E(5, "turn_completed") };
        foreach (var item in events.Where(x => x.Type.StartsWith("tool_")))
            item.MetadataJson = JsonSerializer.Serialize(new ChatActivityMetadata("lookup", "mcp", "provider", null, 25, "completed",
                new(Result: "<script>private()</script>", SourceTimestampUtc: DateTimeOffset.Parse("2026-11-02T08:00:00Z"))));
        var turn = Assert.Single(AiAssistantChatPresentation.Build(events));
        var rendered = context.Render<Activity>(p => p.Add(x => x.Turn, turn));
        Assert.Equal("false", rendered.Find(".activity-heading").GetAttribute("aria-expanded"));
        Assert.Equal(2, rendered.FindAll(".tool-summary").Count);
        Assert.Contains("provider/lookup: result received", rendered.FindAll(".tool-summary")[1].TextContent);
        rendered.FindAll(".tool-summary")[1].Click();
        Assert.Empty(rendered.FindAll("script"));
        Assert.Contains("<script>private()</script>", rendered.Find("pre code").TextContent);
        Assert.Contains("Native source time", rendered.Markup);
        Assert.Contains("Locally observed duration", rendered.Markup);
    }

    [Fact]
    public async Task Markdown_formats_code_and_headings_without_enabling_html()
    {
        await using var context = Context();
        var rendered = context.Render<Markdown>(p => p.Add(x => x.Text, "# Report\n**Evidence** and `code`\n```html\n<script>alert(1)</script>\n```"));
        Assert.Equal("Report", rendered.Find("h2").TextContent);
        Assert.Equal("Evidence", rendered.Find("strong").TextContent);
        Assert.Empty(rendered.FindAll("script"));
        Assert.Contains("<script>alert(1)</script>", rendered.Find("pre code").TextContent);
        Assert.Contains("Copy", rendered.Markup);
    }

    [Theory]
    [InlineData("HelpdeskAdmin", true, true, true)]
    [InlineData("Incident.Write", true, true, false)]
    [InlineData("Incident.Read", true, false, false)]
    [InlineData("SelfService.User", false, false, false)]
    public async Task AI_navigation_keeps_one_authorized_subgroup_and_deep_link_selection(string role, bool chat, bool investigations, bool prompts)
    {
        await using var context = Context();
        var authorization = context.AddAuthorization();
        context.Services.RemoveAll<IAuthorizationService>(); context.Services.RemoveAll<IAuthorizationPolicyProvider>(); context.Services.AddAuthorizationCore();
        authorization.SetAuthorized("Synthetic operator"); authorization.SetRoles(role);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/ai/chat?organizationId=org");
        var menu = context.Render<Menu>();
        Assert.Equal(chat ? 1 : 0, menu.FindAll("a[href='/ai/chat']").Count);
        Assert.Equal(investigations ? 1 : 0, menu.FindAll("a[href='/ai/ops']").Count);
        Assert.Equal(prompts ? 1 : 0, menu.FindAll("a[href='/ai/prompts']").Count);
        if (chat) Assert.Equal("/ai/chat", Assert.Single(menu.FindAll("a[aria-current='page']")).GetAttribute("href"));
    }

    private static BunitContext Context()
    {
        var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddMudServices(); return context;
    }
    private sealed class ChatApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = request.RequestUri!.AbsolutePath.EndsWith("/capabilities", StringComparison.Ordinal)
                ? JsonContent.Create(new ChatCapabilities(true, null, true))
                : request.RequestUri.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal)
                    ? new StringContent("")
                    : JsonContent.Create(new ChatSnapshot(Guid.NewGuid(), ChatState.Idle, 0, []));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private static AiAssistantChatEvent E(long sequence, string type, string? call = null) => new() { Sequence = sequence, Type = type, CallId = call };
}
