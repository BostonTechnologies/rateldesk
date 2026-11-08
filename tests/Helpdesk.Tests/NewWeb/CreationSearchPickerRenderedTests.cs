extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Organization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Polly.CircuitBreaker;
using Polly.Timeout;
using OrganizationPicker = NewWeb::HelpDesk.NewWeb.Components.Shared.OrganizationSearchPicker;
using ParticipantPicker = NewWeb::HelpDesk.NewWeb.Components.Shared.ChangeParticipantSearchPicker;
using MultiPicker = NewWeb::HelpDesk.NewWeb.Components.Shared.CreationSearchPicker<Helpdesk.Shared.DTOs.Organization.OrganizationDto>;

namespace Helpdesk.Tests.NewWeb;

public sealed class CreationSearchPickerRenderedTests
{
    [Fact]
    public async Task Filtered_single_result_does_not_auto_select_when_other_eligible_organizations_exist()
    {
        await using var context = Context(out var api);
        var selections = new List<string?>();
        var picker = context.Render<OrganizationPicker>(p => p.Add(x => x.SelectionChanged, item => selections.Add(item?.Id)));
        await picker.InvokeAsync(() => api.Calls[0].Response.SetResult(Page("candidate", "Only suggestion", 55)));
        picker.WaitForAssertion(() => Assert.DoesNotContain("Loading organizations", picker.Markup));
        Assert.Empty(selections);
        Assert.False(picker.Find("input").HasAttribute("disabled"));
        Assert.Equal("", picker.Find("input").GetAttribute("value"));
    }

    [Fact]
    public async Task Genuine_single_eligibility_auto_selects_and_stable_id_hydration_preserves_a_label_outside_suggestions()
    {
        await using var context = Context(out var api);
        var selections = new List<string?>();
        var picker = context.Render<OrganizationPicker>(p => p.Add(x => x.SelectionChanged, item => selections.Add(item?.Id)));
        await picker.InvokeAsync(() => api.Calls[0].Response.SetResult(Page("only", "Only eligible", 1)));
        picker.WaitForAssertion(() => Assert.Equal("only", Assert.Single(selections)));

        picker.Render(p => p.Add(x => x.Value, "late").Add(x => x.Module, "request"));
        await picker.InvokeAsync(() => api.Calls[1].Response.SetResult(Page("early", "Initial suggestion", 55)));
        var hydration = await api.WaitForCallAsync("id=late");
        Assert.Equal(3, api.Calls.Count);
        await picker.InvokeAsync(() => hydration.Response.SetResult(Page("late", "Zulu hydrated organization", 55)));
        picker.WaitForAssertion(() => Assert.Equal("Zulu hydrated organization", picker.Find("input").GetAttribute("value")));
        Assert.Single(selections);
    }

    [Fact]
    public async Task Unavailable_selected_id_is_cleared_after_authorized_hydration_instead_of_accepting_free_text()
    {
        await using var context = Context(out var api);
        var selections = new List<string?>();
        var picker = context.Render<OrganizationPicker>(p => p.Add(x => x.Value, "foreign")
            .Add(x => x.SelectionChanged, item => selections.Add(item?.Id)));
        await picker.InvokeAsync(() => api.Calls[0].Response.SetResult(Page("allowed", "Allowed organization", 2)));
        var hydration = await api.WaitForCallAsync("id=foreign");
        Assert.Equal(2, api.Calls.Count);
        await picker.InvokeAsync(() => hydration.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new CreationLookupResult<OrganizationDto>([], 2, false)) }));
        picker.WaitForAssertion(() => Assert.Null(Assert.Single(selections)));
        Assert.Equal("", picker.Find("input").GetAttribute("value"));
    }

    [Fact]
    public async Task Context_change_cancels_old_lookup_and_its_late_response_cannot_select_or_replace_new_labels()
    {
        await using var context = Context(out var api);
        var selections = new List<string?>();
        var picker = context.Render<OrganizationPicker>(p => p.Add(x => x.Module, "incident").Add(x => x.SelectionChanged, item => selections.Add(item?.Id)));
        picker.Render(p => p.Add(x => x.Module, "data-management"));
        Assert.True(api.Calls[0].Token.IsCancellationRequested);
        await picker.InvokeAsync(() => api.Calls[1].Response.SetResult(Page("current", "Current Data Management organization", 1)));
        picker.WaitForAssertion(() => Assert.Equal(new[] { "current" }, selections));
        await picker.InvokeAsync(() => api.Calls[0].Response.SetResult(Page("stale", "Old incident organization", 1)));
        picker.WaitForAssertion(() => Assert.Equal(new[] { "current" }, selections));
        Assert.DoesNotContain("Old incident organization", picker.Markup);
    }

    [Fact]
    public async Task Repeated_results_preserve_option_identity_and_changed_labels_remain_selectable_only_in_their_original_scope()
    {
        await using var context = Context(out var api);
        var selections = new List<string?>();
        var picker = context.Render<OrganizationPicker>(p => p.Add(x => x.SelectionChanged, item => selections.Add(item?.Id)));
        await picker.InvokeAsync(() => api.Calls[0].Response.SetResult(Page("candidate", "Initial organization", 2)));
        picker.WaitForAssertion(() => Assert.DoesNotContain("Loading organizations", picker.Markup));
        var autocomplete = picker.FindComponent<MudAutocomplete<OrganizationDto>>().Instance;
        Task<IEnumerable<OrganizationDto>> Search(string text) => autocomplete.SearchFunc?.Invoke(text, CancellationToken.None)
            ?? throw new InvalidOperationException("The rendered autocomplete must have a wired search callback and task.");

        IEnumerable<OrganizationDto>? firstOptions = null;
        var firstSearch = picker.InvokeAsync(async () => { firstOptions = await Search("first"); });
        var firstCall = await api.WaitForCallAsync("q=first");
        firstCall.Response.SetResult(Page("candidate", "Visible organization", 2));
        await firstSearch;
        var visibleOption = Assert.Single(firstOptions!);
        IEnumerable<OrganizationDto>? unchangedOptions = null;
        var unchanged = picker.InvokeAsync(async () => { unchangedOptions = await Search("unchanged"); });
        var unchangedCall = await api.WaitForCallAsync("q=unchanged");
        unchangedCall.Response.SetResult(Page("candidate", "Visible organization", 2));
        await unchanged;
        Assert.Same(visibleOption, Assert.Single(unchangedOptions!));
        IEnumerable<OrganizationDto>? refreshedOptions = null;
        var refresh = picker.InvokeAsync(async () => { refreshedOptions = await Search("refresh"); });
        var refreshCall = await api.WaitForCallAsync("q=refresh");
        refreshCall.Response.SetResult(Page("candidate", "Updated organization label", 2));
        await refresh;
        var updatedOption = Assert.Single(refreshedOptions!);
        Assert.NotSame(visibleOption, updatedOption);
        Assert.Equal("Updated organization label", updatedOption.Name);
        await picker.InvokeAsync(() => autocomplete.ValueChanged.InvokeAsync(visibleOption));
        Assert.Equal(new[] { "candidate" }, selections);

        picker.Render(p => p.Add(x => x.Module, "request"));
        var nextScope = await api.WaitForCallAsync("module=request");
        await picker.InvokeAsync(() => nextScope.Response.SetResult(Page("candidate", "Different scoped organization", 2)));
        picker.WaitForAssertion(() => Assert.DoesNotContain("Loading organizations", picker.Markup));
        await picker.InvokeAsync(() => autocomplete.ValueChanged.InvokeAsync(visibleOption));
        Assert.Equal(new[] { "candidate" }, selections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Superseded_callbacks_return_current_suggestions_without_clearing_the_newer_selection(bool olderCompletesFirst)
    {
        await using var context = Context(out var api);
        var selections = new List<string?>();
        var picker = context.Render<OrganizationPicker>(p => p.Add(x => x.SelectionChanged, item => selections.Add(item?.Id)));
        await picker.InvokeAsync(() => api.Calls[0].Response.SetResult(Page("initial", "Initial organization", 55)));
        var autocomplete = picker.FindComponent<MudAutocomplete<OrganizationDto>>().Instance;
        Task<IEnumerable<OrganizationDto>> Search(string term) => autocomplete.SearchFunc?.Invoke(term, CancellationToken.None)
            ?? throw new InvalidOperationException("A wired search callback is required.");
        IEnumerable<OrganizationDto>? oldOptions = null, currentOptions = null;
        var oldSearch = picker.InvokeAsync(async () => oldOptions = await Search("older"));
        var oldRequest = await api.WaitForCallAsync("q=older");
        var currentSearch = picker.InvokeAsync(async () => currentOptions = await Search("current"));
        var currentRequest = await api.WaitForCallAsync("q=current");
        if (olderCompletesFirst) oldRequest.Response.SetResult(Page("stale", "Stale organization", 55));
        currentRequest.Response.SetResult(Page("current", "Current organization", 55));
        await currentSearch;
        if (!olderCompletesFirst) oldRequest.Response.SetResult(Page("stale", "Stale organization", 55));
        await oldSearch;
        Assert.Equal("current", Assert.Single(currentOptions!).Id);
        var retained = Assert.Single(oldOptions!);
        Assert.Equal("current", retained.Id);
        await picker.InvokeAsync(() => autocomplete.ValueChanged.InvokeAsync(retained));
        Assert.Equal(new[] { "current" }, selections);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("circuit")]
    public async Task Failed_initial_load_is_retryable_and_does_not_become_a_successful_empty_population(string failure)
    {
        await using var context = Context(out var api);
        var selections = new List<string?>();
        var picker = context.Render<OrganizationPicker>(p => p.Add(x => x.SelectionChanged, item => selections.Add(item?.Id)));
        await picker.InvokeAsync(() =>
        {
            if (failure == "http") api.Calls[0].Response.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            else api.Calls[0].Response.SetException(ResilienceFailure(failure == "circuit"));
        });
        picker.WaitForAssertion(() => Assert.Contains("Could not load organizations.", picker.Markup));
        Assert.DoesNotContain("No eligible organizations.", picker.Markup);
        picker.FindAll("button").Single(button => button.TextContent.Contains("Retry organization")).Click();
        await picker.InvokeAsync(() => api.Calls[1].Response.SetResult(Page("recovered", "Recovered organization", 1)));
        picker.WaitForAssertion(() => Assert.Equal("recovered", Assert.Single(selections)));
    }

    [Fact]
    public async Task Multi_selection_serializes_clearing_and_late_callbacks_cannot_unlock_another_scope_or_erase_its_query()
    {
        await using var context = Context(out var api);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var choices = new List<string?>();
        async Task Choose(OrganizationDto? item)
        {
            choices.Add(item?.Id);
            if (choices.Count == 1) { firstEntered.SetResult(); await firstRelease.Task; }
            else if (choices.Count == 2) { secondEntered.SetResult(); await secondRelease.Task; }
            else if (choices.Count == 4) { disposalEntered.SetResult(); await disposalRelease.Task; }
        }
        const string firstUrl = "api/v1/ticketing/organizations/search?module=change";
        var picker = context.Render<MultiPicker>(p => p.Add(x => x.LookupUrl, firstUrl)
            .Add(x => x.ItemId, item => item.Id).Add(x => x.ItemLabel, item => item.Name)
            .Add(x => x.ClearAfterSelection, true).Add(x => x.SelectionChanged, Choose));
        await picker.InvokeAsync(() => api.Calls[0].Response.SetResult(Page("first", "First choice", 2)));
        picker.WaitForAssertion(() => Assert.DoesNotContain("Loading choices", picker.Markup));
        async Task<OrganizationDto> Candidate(MudAutocomplete<OrganizationDto> autocomplete, string term, string id)
        {
            IEnumerable<OrganizationDto>? items = null;
            var search = picker.InvokeAsync(async () => items = await (autocomplete.SearchFunc?.Invoke(term, CancellationToken.None)
                ?? throw new InvalidOperationException("A search task is required.")));
            var request = await api.WaitForCallAsync("q=" + term);
            request.Response.SetResult(Page(id, id + " choice", 2));
            await search;
            return Assert.Single(items!);
        }
        var firstAutocomplete = picker.FindComponent<MudAutocomplete<OrganizationDto>>().Instance;
        var first = await Candidate(firstAutocomplete, "first", "first");
        var firstSelection = picker.InvokeAsync(() => firstAutocomplete.ValueChanged.InvokeAsync(first));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(picker.Find("input").HasAttribute("readonly"));

        picker.Render(p => p.Add(x => x.LookupUrl, "api/v1/ticketing/organizations/search?module=request"));
        var middleScope = await api.WaitForCallAsync("module=request");
        await picker.InvokeAsync(() => middleScope.Response.SetResult(Page("middle", "Middle scope", 2)));
        picker.Render(p => p.Add(x => x.LookupUrl, firstUrl));
        var returnedScope = await api.WaitForCallAsync("module=change&q=&", 1);
        await picker.InvokeAsync(() => returnedScope.Response.SetResult(Page("second", "Current scope", 2)));
        picker.WaitForAssertion(() => Assert.False(picker.Find("input").HasAttribute("readonly")));
        var currentAutocomplete = picker.FindComponent<MudAutocomplete<OrganizationDto>>().Instance;
        var second = await Candidate(currentAutocomplete, "second", "second");
        var secondSelection = picker.InvokeAsync(() => currentAutocomplete.ValueChanged.InvokeAsync(second));
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        firstRelease.SetResult();
        await firstSelection;
        Assert.True(picker.Find("input").HasAttribute("readonly"));

        secondRelease.SetException(new InvalidOperationException("Draft callback rejected."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondSelection);
        picker.WaitForAssertion(() => Assert.False(picker.Find("input").HasAttribute("readonly")));
        await picker.InvokeAsync(() => currentAutocomplete.ValueChanged.InvokeAsync(second));
        Assert.Equal(new[] { "first", "second", "second" }, choices);
        Assert.Equal("", picker.Find("input").GetAttribute("value") ?? "");
        var disposingSelection = picker.InvokeAsync(() => currentAutocomplete.ValueChanged.InvokeAsync(second));
        await disposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        picker.Instance.Dispose();
        disposalRelease.SetResult();
        await disposingSelection;
        Assert.Equal(new[] { "first", "second", "second", "second" }, choices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approver_label_resilience_failures_preserve_selected_ids_and_retry_without_emitting_a_draft_change(bool circuit)
    {
        await using var context = Context(out var api);
        var changes = new List<HashSet<string>>();
        var picker = context.Render<ParticipantPicker>(p => p.Add(x => x.OrganizationId, "org").Add(x => x.Multiple, true)
            .Add(x => x.SelectedIds, new[] { "selected" }).Add(x => x.SelectedIdsChanged, ids => changes.Add(ids)));
        var hydration = await api.WaitForCallAsync("id=selected");
        await picker.InvokeAsync(() => hydration.Response.SetException(ResilienceFailure(circuit)));
        foreach (var call in api.Calls.Where(call => !call.Url.Contains("id=selected", StringComparison.Ordinal)))
            await picker.InvokeAsync(() => call.Response.TrySetResult(Participants("suggestion")));
        picker.WaitForAssertion(() => Assert.Contains("Some selected approvers could not be loaded.", picker.Markup));
        Assert.Equal("selected", picker.Find(".mud-chip").GetAttribute("title"));
        picker.FindAll("button").Single(button => button.TextContent.Contains("Retry approver labels", StringComparison.Ordinal)).Click();
        var retry = await api.WaitForCallAsync("id=selected", 1);
        await picker.InvokeAsync(() => retry.Response.SetResult(Participants("selected")));
        picker.WaitForAssertion(() => Assert.Contains("selected@example.test", picker.Markup));
        Assert.DoesNotContain("Some selected approvers could not be loaded.", picker.Markup);
        Assert.Empty(changes);
    }

    [Fact]
    public async Task Approver_chips_preserve_stable_ids_and_removal_changes_only_the_draft_selection()
    {
        await using var context = Context(out var api);
        HashSet<string>? changed = null;
        var picker = context.Render<ParticipantPicker>(p => p.Add(x => x.OrganizationId, "org")
            .Add(x => x.Multiple, true).Add(x => x.SelectedIds, new[] { "first", "second" })
            .Add(x => x.SelectedIdsChanged, ids => changed = ids));
        // Child autocomplete and chip hydration are independent bounded requests.
        foreach (var call in api.Calls.ToArray())
        {
            var id = call.Url.Contains("id=first", StringComparison.Ordinal) ? "first" : call.Url.Contains("id=second", StringComparison.Ordinal) ? "second" : "suggestion";
            await picker.InvokeAsync(() => call.Response.SetResult(Participants(id)));
        }
        var second = await api.WaitForCallAsync("id=second");
        await picker.InvokeAsync(() => second.Response.TrySetResult(Participants("second")));
        picker.WaitForAssertion(() => Assert.Contains("Duplicate (second@example.test)", picker.Markup));
        picker.Find(".mud-chip-close-button").Click();
        picker.WaitForAssertion(() => Assert.Equal(new[] { "second" }, changed));
        Assert.All(api.Calls, call => Assert.DoesNotContain("/changes", call.Url));
    }

    private static HttpResponseMessage Page(string id, string name, int total) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new CreationLookupResult<OrganizationDto>([new() { Id = id, Name = name }], total, false)) };
    private static HttpResponseMessage Participants(string id) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new CreationLookupResult<Helpdesk.Shared.DTOs.Change.ChangeParticipantUserDto>([new(id, "Duplicate", id + "@example.test", "Customer", "org")], 55, true)) };
    private static Exception ResilienceFailure(bool circuit) => circuit
        ? new BrokenCircuitException("Synthetic open circuit.") : new TimeoutRejectedException("Synthetic request deadline.");
    private static BunitContext Context(out DeferredApi api)
    {
        var context = new BunitContext();
        context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        api = new();
        var client = new HttpClient(api) { BaseAddress = new Uri("https://desk.example.test/") };
        var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient("HelpdeskApi").Returns(client);
        context.Services.AddSingleton(factory);
        context.Render<MudPopoverProvider>();
        return context;
    }
    private sealed class DeferredApi : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly List<LookupCall> calls = [];
        private TaskCompletionSource callCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<LookupCall> Calls { get { lock (gate) return calls.ToArray(); } }

        public async Task<LookupCall> WaitForCallAsync(string urlPart, int skip = 0)
        {
            while (true)
            {
                Task nextCall;
                lock (gate)
                {
                    var match = calls.Where(call => call.Url.Contains(urlPart, StringComparison.Ordinal)).Skip(skip).FirstOrDefault();
                    if (match is not null) return match;
                    nextCall = callCreated.Task;
                }
                // An awaited HTTP call cannot trigger another render until its response arrives.
                // Observe the handler directly instead of coupling this wait to bUnit render events.
                await nextCall.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                calls.Add(new(request.RequestUri!.PathAndQuery, token, response));
                var notification = callCreated;
                callCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
                notification.SetResult();
            }
            return response.Task;
        }

        public sealed record LookupCall(string Url, CancellationToken Token, TaskCompletionSource<HttpResponseMessage> Response);
    }
}
