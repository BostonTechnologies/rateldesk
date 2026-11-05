extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using MudBlazor;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.Models;
using TicketStatePicker = NewWeb::HelpDesk.NewWeb.Components.Shared.TicketStatePicker;
using ChangeLifecycleStatePicker = NewWeb::HelpDesk.NewWeb.Components.Shared.ChangeLifecycleStatePicker;
using IncidentList = NewWeb::HelpDesk.NewWeb.Components.Pages.Incidents.IncidentList;
using RequestList = NewWeb::HelpDesk.NewWeb.Components.Pages.Requests.RequestList;
using ChangeList = NewWeb::HelpDesk.NewWeb.Components.Pages.Changes.ChangeList;

namespace Helpdesk.Tests.NewWeb;

public sealed class TicketListRenderedTests
{
    [Theory]
    [InlineData("incidents", typeof(IncidentList))]
    [InlineData("requests", typeof(RequestList))]
    [InlineData("changes", typeof(ChangeList))]
    public async Task Lists_render_shared_controls_without_obsolete_AI_filters_and_restore_query(string module, Type component)
    {
        await using var context = new BunitContext(); context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = context.AddAuthorization(); auth.SetAuthorized("Synthetic reader"); auth.SetRoles("Incident.Read", "Request.Read", "Change.Read");
        var handler = new FixtureClient(); context.Services.AddSingleton<IHttpClientFactory>(handler);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/{module}?search=synthetic&customerOrgId=org&priority=High&aiInvolved=true&aiOnly=true");
        var page = context.Render<DynamicComponent>(p => p.Add(x => x.Type, component));
        page.WaitForAssertion(() => Assert.Contains(handler.Requests, url => url.Contains("q=synthetic") && url.Contains("customerOrgId=org") && url.Contains("priority=High")));
        Assert.DoesNotContain("AI involved only", page.Markup); Assert.DoesNotContain("AI origin only", page.Markup);
        Assert.All(handler.Requests, url => { Assert.DoesNotContain("aiInvolved", url); Assert.DoesNotContain("aiOnly", url); });
        Assert.Contains("Filters (2)", page.Markup); Assert.Contains("No tickets match these filters.", page.Markup);
        page.WaitForAssertion(() => Assert.Contains("Remove Customer: Named organization", page.Markup));
        page.FindAll("button").Single(button => button.TextContent.Contains("Clear all")).Click();
        page.WaitForAssertion(() => Assert.Contains("Filters (0)", page.Markup));
        Assert.DoesNotContain("customerOrgId", navigation.Uri); Assert.DoesNotContain("search=", navigation.Uri);
    }
    [Theory]
    [InlineData("incidents", typeof(IncidentList))]
    [InlineData("requests", typeof(RequestList))]
    [InlineData("changes", typeof(ChangeList))]
    public async Task Actual_list_state_trigger_opens_without_navigation_or_write(string module, Type component)
    {
        using var api = new PopupApi();
        await using var context = PopupContext(api);
        var auth = context.AddAuthorization(); auth.SetAuthorized("Synthetic operator"); auth.SetRoles("HelpdeskAdmin");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/{module}?q=Synthetic&sort=trackingId&descending=false");
        var popovers = context.Render<MudPopoverProvider>();
        var page = context.Render<DynamicComponent>(parameters => parameters.Add(value => value.Type, component));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".state-trigger")));
        var before = navigation.Uri;
        Assert.False(page.Find(".state-trigger").HasAttribute("disabled"));
        page.Find(".state-trigger").Click();
        popovers.WaitForAssertion(() => Assert.Contains("Current:", popovers.Markup));
        var current = popovers.Find(".state-popup-current");
        Assert.Equal("true", current.GetAttribute("aria-disabled"));
        Assert.False(current.HasAttribute("tabindex"));
        Assert.Empty(popovers.FindAll("[role=alert]"));
        Assert.Contains("SYN-STATE", popovers.Markup);
        Assert.Empty(api.Posts);
        Assert.Equal(before, navigation.Uri);
        Assert.DoesNotContain(api.Reads, path => path.EndsWith("/state"));
    }

    [Theory]
    [InlineData("incidents")]
    [InlineData("requests")]
    public async Task Known_write_denial_survives_readback_and_filtered_table_replacement(string module)
    {
        using var api = new PopupApi { Reject = HttpStatusCode.Forbidden };
        await using var context = PopupContext(api);
        var auth = context.AddAuthorization(); auth.SetAuthorized("Synthetic operator"); auth.SetRoles("HelpdeskAdmin");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/{module}");
        var popovers = context.Render<MudPopoverProvider>();
        var component = module == "incidents" ? typeof(IncidentList) : typeof(RequestList);
        var page = context.Render<DynamicComponent>(parameters => parameters.Add(value => value.Type, component));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".state-trigger")));
        await page.Find(".state-trigger").ClickAsync(new());
        await popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "In Progress").ClickAsync(new());
        popovers.WaitForAssertion(() => Assert.Contains("Reload this page to refresh access", popovers.Markup));
        await popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "Reload current state").ClickAsync(new());
        if (page.Find(".state-trigger").GetAttribute("aria-expanded") != "true") await page.Find(".state-trigger").ClickAsync(new());
        popovers.WaitForAssertion(() => Assert.Contains("Reload this page to refresh access", popovers.Markup));
        Assert.Empty(popovers.FindAll(".state-popup-choice"));
        navigation.NavigateTo($"/{module}?q=Synthetic");
        page.WaitForAssertion(() => Assert.Equal("false", page.Find(".state-trigger").GetAttribute("aria-expanded")));
        await page.Find(".state-trigger").ClickAsync(new());
        popovers.WaitForAssertion(() => Assert.Contains("Reload this page to refresh access", popovers.Markup));
        Assert.Empty(popovers.FindAll(".state-popup-choice"));
        Assert.Single(api.Posts);
        Assert.Contains($"/api/v1/{module}/row", api.Reads);
    }

    [Theory]
    [InlineData("incidents")]
    [InlineData("requests")]
    public async Task Ticket_state_uses_confirmed_state_and_suppresses_pending_duplicate(string module)
    {
        using var api = new PopupApi { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var context = PopupContext(api);
        var popovers = context.Render<MudPopoverProvider>();
        var changes = new List<TicketState>();
        var picker = context.Render<TicketStatePicker>(parameters => parameters
            .Add(value => value.TicketId, "row").Add(value => value.TicketType, module)
            .Add(value => value.State, TicketState.New).Add(value => value.StateChanged, state => changes.Add(state)));
        picker.Find("button.state-trigger").Click();
        popovers.WaitForAssertion(() => Assert.Contains("Current: New", popovers.Markup));
        Assert.Empty(api.Posts);
        var option = popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "In Progress");
        var first = option.ClickAsync(new());
        picker.WaitForAssertion(() => Assert.Single(api.Posts));
        await option.ClickAsync(new());
        Assert.Single(api.Posts);
        Assert.True(picker.Find("button.state-trigger").HasAttribute("disabled"));
        api.Pending.SetResult(PopupApi.Json(new { id = "row", state = TicketState.OnHold }));
        await first;
        picker.WaitForAssertion(() => Assert.Equal([TicketState.OnHold], changes));
        Assert.Contains("On Hold", picker.Markup);
        Assert.Equal($"/api/v1/{module}/row/state", Assert.Single(api.Posts));
        Assert.Contains("HelpdeskCommands", api.ClientNames);
    }

    [Fact]
    public async Task Failed_ticket_state_retains_chip_until_explicit_authoritative_reload()
    {
        using var api = new PopupApi { Reject = HttpStatusCode.Conflict, ReadState = TicketState.Replied };
        await using var context = PopupContext(api);
        var popovers = context.Render<MudPopoverProvider>();
        var changes = new List<TicketState>();
        var picker = context.Render<TicketStatePicker>(parameters => parameters
            .Add(value => value.TicketId, "row").Add(value => value.TicketType, "incidents")
            .Add(value => value.State, TicketState.New).Add(value => value.StateChanged, state => changes.Add(state)));
        picker.Find("button.state-trigger").Click();
        popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "In Progress").Click();
        popovers.WaitForAssertion(() => Assert.Contains("changed while you were editing", popovers.Markup));
        Assert.Contains("New", picker.Markup);
        Assert.Empty(changes);
        Assert.DoesNotContain(popovers.FindAll(".mud-menu-item"), element => element.TextContent.Trim() == "In Progress");
        popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "Reload current state").Click();
        picker.WaitForAssertion(() => Assert.Equal([TicketState.Replied], changes));
        Assert.Single(api.Posts);
    }

    [Fact]
    public async Task Late_ticket_state_result_cannot_apply_to_reused_component_scope()
    {
        using var api = new PopupApi { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var context = PopupContext(api);
        var popovers = context.Render<MudPopoverProvider>();
        var changes = new List<TicketState>();
        var picker = context.Render<TicketStatePicker>(parameters => parameters
            .Add(value => value.TicketId, "row").Add(value => value.TicketType, "requests")
            .Add(value => value.State, TicketState.New).Add(value => value.StateChanged, state => changes.Add(state)));
        picker.Find("button.state-trigger").Click();
        var applying = popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "In Progress").ClickAsync(new());
        picker.WaitForAssertion(() => Assert.Single(api.Posts));
        picker.Render(parameters => parameters.Add(value => value.TicketId, "other").Add(value => value.State, TicketState.WaitingReply));
        api.Pending.SetResult(PopupApi.Json(new { id = "row", state = TicketState.Resolved }));
        await applying;
        Assert.Empty(changes);
        Assert.Contains("Waiting Reply", picker.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ticket_reload_resilience_failure_keeps_retry_guidance_and_never_writes(bool openCircuit)
    {
        using var api = new PopupApi { Reject = HttpStatusCode.Conflict, ReadState = TicketState.Replied };
        await using var context = PopupContext(api);
        var popovers = context.Render<MudPopoverProvider>();
        var confirmed = new List<TicketState>();
        var picker = context.Render<TicketStatePicker>(parameters => parameters
            .Add(value => value.TicketId, "row").Add(value => value.TicketType, "requests")
            .Add(value => value.State, TicketState.New).Add(value => value.StateChanged, state => confirmed.Add(state)));
        picker.Find("button.state-trigger").Click();
        await popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "In Progress").ClickAsync(new());
        popovers.WaitForAssertion(() => Assert.Contains("changed while you were editing", popovers.Markup));
        api.ReadFailure = openCircuit ? new BrokenCircuitException("Synthetic open circuit") : new TimeoutRejectedException("Synthetic read timeout");
        popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "Reload current state").Click();
        popovers.WaitForAssertion(() => Assert.Contains("Current state could not be loaded", popovers.Markup));
        Assert.Empty(confirmed); Assert.Single(api.Posts); Assert.Contains("New", picker.Markup);
        Assert.DoesNotContain(popovers.FindAll(".mud-menu-item"), element => element.TextContent.Trim() == "In Progress");
        api.ReadFailure = null;
        popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "Reload current state").Click();
        picker.WaitForAssertion(() => Assert.Equal([TicketState.Replied], confirmed));
        Assert.Single(api.Posts);
    }

    [Fact]
    public async Task Change_option_read_timeout_is_recoverable_without_lifecycle_write()
    {
        using var api = new PopupApi { ReadFailure = new TimeoutRejectedException("Synthetic capability read timeout") };
        await using var context = PopupContext(api);
        var popovers = context.Render<MudPopoverProvider>();
        var picker = context.Render<ChangeLifecycleStatePicker>(parameters => parameters
            .Add(value => value.ChangeId, "row").Add(value => value.CanWrite, true).Add(value => value.LifecycleState, ChangeLifecycleState.Draft));
        picker.Find("button.state-trigger").Click();
        popovers.WaitForAssertion(() => Assert.Contains("Permitted actions could not be loaded", popovers.Markup));
        Assert.Empty(api.Posts); Assert.DoesNotContain("Submit", popovers.Markup);
        api.ReadFailure = null;
        popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "Reload current state").Click();
        popovers.WaitForAssertion(() => Assert.Contains("Submit", popovers.Markup));
        Assert.Empty(api.Posts);
    }

    [Fact]
    public async Task Change_submit_uses_server_options_and_displays_returned_lifecycle()
    {
        using var api = new PopupApi();
        await using var context = PopupContext(api);
        var popovers = context.Render<MudPopoverProvider>();
        var changes = new List<ChangeDto>();
        var picker = context.Render<ChangeLifecycleStatePicker>(parameters => parameters
            .Add(value => value.ChangeId, "row").Add(value => value.CanWrite, true).Add(value => value.CanApprove, false)
            .Add(value => value.LifecycleState, ChangeLifecycleState.Draft).Add(value => value.ChangeUpdated, change => changes.Add(change)));
        picker.Find("button.state-trigger").Click();
        popovers.WaitForAssertion(() => Assert.Contains("Submit", popovers.Markup));
        Assert.Contains("Submit evaluates the saved template", popovers.Markup);
        Assert.Empty(popovers.FindAll("[role=alert]"));
        Assert.Empty(api.Posts);
        Assert.Contains("/api/v1/changes/row/lifecycle-options", api.Reads);
        popovers.FindAll(".mud-menu-item").Single(element => element.TextContent.Trim() == "Submit").Click();
        picker.WaitForAssertion(() => Assert.Equal(ChangeLifecycleState.PendingApproval, Assert.Single(changes).LifecycleState));
        Assert.Contains("Pending Approval", picker.Markup);
        Assert.Equal("/api/v1/changes/row/lifecycle", Assert.Single(api.Posts));
        Assert.Contains("HelpdeskCommands", api.ClientNames);
        Assert.DoesNotContain(api.Posts, path => path.EndsWith("/state"));
    }

    [Theory]
    [InlineData(false, ChangeLifecycleState.Draft)]
    [InlineData(true, ChangeLifecycleState.ImplementedSuccess)]
    [InlineData(true, ChangeLifecycleState.ImplementedBackedOut)]
    public async Task Read_only_or_terminal_changes_have_no_edit_trigger(bool canWrite, ChangeLifecycleState state)
    {
        using var api = new PopupApi();
        await using var context = PopupContext(api);
        var picker = context.Render<ChangeLifecycleStatePicker>(parameters => parameters
            .Add(value => value.ChangeId, "row").Add(value => value.CanWrite, canWrite).Add(value => value.LifecycleState, state));
        Assert.Empty(picker.FindAll("button.state-trigger"));
        Assert.Empty(api.Posts);
        Assert.Empty(api.Reads);
    }

    private static BunitContext PopupContext(PopupApi api)
    {
        var context = new BunitContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api);
        return context;
    }

    private sealed class PopupApi : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Reads { get; } = [];
        public List<string> Posts { get; } = [];
        public List<string> ClientNames { get; } = [];
        public TaskCompletionSource<HttpResponseMessage>? Pending { get; init; }
        public HttpStatusCode? Reject { get; init; }
        public TicketState ReadState { get; init; } = TicketState.New;
        public Exception? ReadFailure { get; set; }
        public HttpClient CreateClient(string name)
        {
            ClientNames.Add(name);
            return new(this) { BaseAddress = new Uri("https://synthetic.example/") };
        }
        public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Posts.Add(path);
                if (Pending is not null) return Pending.Task;
                if (Reject is not null) return Task.FromResult(new HttpResponseMessage(Reject.Value));
                return Task.FromResult(path.EndsWith("/lifecycle")
                    ? Json(new ChangeDto { Id = "row", LifecycleState = ChangeLifecycleState.PendingApproval })
                    : Json(new { id = "row", state = TicketState.InProgress }));
            }
            Reads.Add(path);
            if (ReadFailure is not null) return Task.FromException<HttpResponseMessage>(ReadFailure);
            if (path.EndsWith("/lifecycle-options")) return Task.FromResult(Json(new ChangeLifecycleOptionsDto("row", ChangeLifecycleState.Draft,
                [new(ChangeLifecycleState.Submitted, null)], "Submit evaluates the saved template and approvals.")));
            if (path.EndsWith("/filter-options")) return Task.FromResult(Json(new { items = Array.Empty<object>(), hasMore = false }));
            if (path.EndsWith("/current")) return Task.FromResult(Json(null!));
            if (path.EndsWith("/row")) return Task.FromResult(Json(new { id = "row", state = ReadState }));
            return Task.FromResult(Json(new { items = new[] { new { id = "row", title = "Synthetic state", subject = "Synthetic state", trackingId = "SYN-STATE", state = 0, lifecycleState = 0, organizationId = "org", updatedAt = "2026-01-01T00:00:00Z" } }, totalCount = 1 }));
        }
    }

    private sealed class FixtureClient : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this) { BaseAddress = new Uri("https://synthetic.example/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/filter-options"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { items = new[] { new { id = "org", label = "Named organization" } }, hasMore = false }) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { items = Array.Empty<object>(), totalCount = 0 }) });
        }
    }
}
