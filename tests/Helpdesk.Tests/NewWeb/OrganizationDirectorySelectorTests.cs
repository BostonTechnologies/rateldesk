extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Bunit;
using Helpdesk.Shared.DTOs;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Selector = NewWeb::HelpDesk.NewWeb.Components.Shared.OrganizationDirectorySelector;

namespace Helpdesk.Tests.NewWeb;

public sealed class OrganizationDirectorySelectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hydration_failure_retains_the_selected_id_without_showing_a_previous_organization(bool circuitOpen)
    {
        using var api = new Api();
        await using var context = Context(api);
        var changed = new List<string?>();
        var picker = context.Render<Selector>(p => p.Add(x => x.Value, "first").Add(x => x.ValueChanged, id => changed.Add(id)));
        var first = await api.Pending.Reader.ReadAsync();
        await picker.InvokeAsync(() => first.SetResult(Response("first", "First organization")));
        picker.WaitForAssertion(() => Assert.Equal("First organization", picker.Find("input").GetAttribute("value")));

        picker.Render(p => p.Add(x => x.Value, "second"));
        var second = await api.Pending.Reader.ReadAsync();
        Assert.DoesNotContain("First organization", picker.Markup);
        await picker.InvokeAsync(() => second.SetException(Failure(circuitOpen)));
        picker.WaitForAssertion(() => Assert.Contains("The saved filter is retained", picker.Markup));
        Assert.Equal("second", picker.Instance.Value);
        Assert.Equal("Saved organization (unavailable)", picker.Find("input").GetAttribute("value"));
        Assert.DoesNotContain("private", picker.Markup);
        Assert.Empty(changed);

        picker.Render(p => p.Add(x => x.Value, "third"));
        var third = await api.Pending.Reader.ReadAsync();
        await picker.InvokeAsync(() => third.SetResult(Response("third", "Recovered organization")));
        picker.WaitForAssertion(() => Assert.Equal("Recovered organization", picker.Find("input").GetAttribute("value")));
        Assert.DoesNotContain("saved filter is retained", picker.Markup);
    }

    [Fact]
    public async Task Late_hydration_results_and_errors_cannot_replace_the_current_selection()
    {
        using var api = new Api();
        await using var context = Context(api);
        var picker = context.Render<Selector>(p => p.Add(x => x.Value, "old"));
        var old = await api.Pending.Reader.ReadAsync();
        picker.Render(p => p.Add(x => x.Value, "middle"));
        var middle = await api.Pending.Reader.ReadAsync();
        picker.Render(p => p.Add(x => x.Value, "current"));
        var current = await api.Pending.Reader.ReadAsync();
        await picker.InvokeAsync(() => current.SetResult(Response("current", "Current organization")));
        picker.WaitForAssertion(() => Assert.Equal("Current organization", picker.Find("input").GetAttribute("value")));
        await picker.InvokeAsync(() => old.SetResult(Response("old", "Stale organization")));
        await picker.InvokeAsync(() => middle.SetException(new BrokenCircuitException("private stale failure")));
        picker.WaitForAssertion(() => Assert.Equal("Current organization", picker.Find("input").GetAttribute("value")));
        Assert.DoesNotContain("Stale organization", picker.Markup);
        Assert.DoesNotContain("saved filter is retained", picker.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_resilience_errors_are_rendered_and_recover_without_private_details(bool circuitOpen)
    {
        using var api = new Api();
        await using var context = Context(api);
        var picker = context.Render<Selector>();
        var autocomplete = picker.FindComponent<MudAutocomplete<TenantLookupDto>>();
        Task<IEnumerable<TenantLookupDto>>? failed = null;
        await picker.InvokeAsync(() => { failed = autocomplete.Instance.SearchFunc!("search", CancellationToken.None); });
        var pending = await api.Pending.Reader.ReadAsync();
        await picker.InvokeAsync(() => pending.SetException(Failure(circuitOpen)));
        Assert.Empty(await failed!);
        picker.WaitForAssertion(() => Assert.Contains("Organizations could not be loaded. Try searching again.", picker.Markup));
        Assert.DoesNotContain("private", picker.Markup);

        Task<IEnumerable<TenantLookupDto>>? recovery = null;
        await picker.InvokeAsync(() => { recovery = autocomplete.Instance.SearchFunc!("recovery", CancellationToken.None); });
        var retry = await api.Pending.Reader.ReadAsync();
        await picker.InvokeAsync(() => retry.SetResult(Response("recovered", "Recovered organization")));
        Assert.Equal("recovered", Assert.Single(await recovery!).Id);
        picker.WaitForAssertion(() => Assert.DoesNotContain("Organizations could not be loaded", picker.Markup));
    }

    private static BunitContext Context(Api api)
    {
        var context = new BunitContext(); context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api); context.Render<MudPopoverProvider>(); return context;
    }
    private static Exception Failure(bool circuitOpen) => circuitOpen
        ? new BrokenCircuitException("private upstream circuit") : new TimeoutRejectedException("private upstream timeout");
    private static HttpResponseMessage Response(string id, string name) => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(new[] { new TenantLookupDto { Id = id, Name = name } }) };
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public Channel<TaskCompletionSource<HttpResponseMessage>> Pending { get; } = Channel.CreateUnbounded<TaskCompletionSource<HttpResponseMessage>>();
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Model a response already in flight, including a late fault after a selection changed.
            var result = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Pending.Writer.TryWrite(result)) throw new InvalidOperationException("Test request channel closed.");
            return result.Task;
        }
    }
}
