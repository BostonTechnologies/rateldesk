extern alias NewWeb;
using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using Page = NewWeb::HelpDesk.NewWeb.Components.Pages.IntegrationCredentials;

namespace Helpdesk.Tests.NewWeb;

public sealed class IntegrationCredentialManagementPageTests
{
    [Fact]
    public async Task Compact_list_counts_twenty_five_rows_and_cleanup_targets_only_explicit_inactive_selection_with_partial_failures()
    {
        using var api = new Api();
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await RenderAsync(renderer, reference);
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("All (25)", html);
        Assert.Contains("Active (9)", html);
        Assert.Contains("Revoked (8)", html);
        Assert.Contains("Expired (8)", html);
        var rows = Rows(reference.Page!);
        var first = rows.Single(row => Id(row) == api.Ids[1]);
        var second = rows.Single(row => Id(row) == api.Ids[2]);
        var active = rows.Single(row => Id(row) == api.Ids[0]);
        api.FailedDelete = api.Ids[2];
        await InvokeAsync(renderer, reference.Page!, "SelectInactive", first, new ChangeEventArgs { Value = true });
        await InvokeAsync(renderer, reference.Page!, "SelectInactive", second, new ChangeEventArgs { Value = true });
        await InvokeAsync(renderer, reference.Page!, "SelectInactive", active, new ChangeEventArgs { Value = true });
        await InvokeAsync(renderer, reference.Page!, "BeginCleanup");
        Assert.Equal(2, Field<IList>(reference.Page!, "confirmation").Count);
        await InvokeAsync(renderer, reference.Page!, "ConfirmMutationAsync");
        Assert.Equal(new[] { api.Ids[1], api.Ids[2] }.Order(), api.Deletes.Order());
        Assert.Equal(24, Rows(reference.Page!).Count);
        Assert.Equal(api.Ids[2], Id(Assert.Single(Field<IList>(reference.Page!, "confirmation").Cast<object>())));
        Assert.Equal([api.Ids[2]], Field<HashSet<Guid>>(reference.Page!, "selectedInactive"));
        html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("credential 02: action failed; retry", html);
        Assert.Contains("role=\"dialog\"", html);
        api.FailedDelete = null;
        await InvokeAsync(renderer, reference.Page!, "ConfirmMutationAsync");
        Assert.Empty(Field<IList>(reference.Page!, "confirmation"));
        Assert.Equal(23, Rows(reference.Page!).Count);
    }

    [Fact]
    public async Task Filter_changes_clear_selection_and_hidden_inactive_records_are_never_included()
    {
        using var api = new Api();
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        await RenderAsync(renderer, reference);
        var row = Rows(reference.Page!).Single(row => Id(row) == api.Ids[1]);
        await InvokeAsync(renderer, reference.Page!, "SelectInactive", row, new ChangeEventArgs { Value = true });
        await InvokeAsync(renderer, reference.Page!, "SearchCredentialsChanged", new ChangeEventArgs { Value = "credential 02" });
        await InvokeAsync(renderer, reference.Page!, "BeginCleanup");
        await InvokeAsync(renderer, reference.Page!, "ConfirmMutationAsync");
        Assert.Empty(api.Deletes);
        Assert.Empty(Field<HashSet<Guid>>(reference.Page!, "selectedInactive"));
    }

    [Fact]
    public async Task A_list_response_started_before_delete_cannot_restore_acknowledged_deleted_access()
    {
        using var api = new Api();
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        await RenderAsync(renderer, reference);
        var row = Rows(reference.Page!).Single(row => Id(row) == api.Ids[0]);
        api.PendingList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reload = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() => { reload = (Task)typeof(Page).GetMethod("ReloadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(reference.Page, null)!; });
        await InvokeAsync(renderer, reference.Page!, "BeginDelete", row);
        await InvokeAsync(renderer, reference.Page!, "ConfirmMutationAsync");
        api.PendingList.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(api.Metadata()) });
        await renderer.Dispatcher.InvokeAsync(async () => await reload);
        Assert.DoesNotContain(Rows(reference.Page!), candidate => Id(candidate) == api.Ids[0]);
        api.PendingList = null;
        await InvokeAsync(renderer, reference.Page!, "ReloadAsync");
        Assert.DoesNotContain(Rows(reference.Page!), candidate => Id(candidate) == api.Ids[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_creation_blocks_blind_replay_and_recovers_the_list(bool malformed)
    {
        using var api = new Api { MalformedCreate = malformed, ThrowCreate = !malformed };
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        await RenderAsync(renderer, reference);
        await InvokeAsync(renderer, reference.Page!, "StartCreate");
        var draft = Field<object>(reference.Page!, "draft");
        draft.GetType().GetProperty("Name")!.SetValue(draft, "uncertain synthetic create");
        draft.GetType().GetProperty("OrganizationId")!.SetValue(draft, "org-a");
        draft.GetType().GetProperty("Permissions")!.SetValue(draft, new HashSet<string> { "Incident.Read" });
        await InvokeAsync(renderer, reference.Page!, "CreateAsync");
        Assert.True(Field<bool>(reference.Page!, "creationUncertain"));
        Assert.False(Field<bool>(reference.Page!, "showCreate"));
        Assert.Contains("result could not be confirmed", Field<string>(reference.Page!, "error"));
        await InvokeAsync(renderer, reference.Page!, "StartCreate");
        Assert.False(Field<bool>(reference.Page!, "showCreate"));
        await InvokeAsync(renderer, reference.Page!, "CreateAsync");
        Assert.Equal(1, api.Creates);
        Assert.Equal(25, Rows(reference.Page!).Count);
        Assert.Contains("SystemPairingApi", api.ClientNames);
    }

    [Fact]
    public async Task Per_item_timeout_remains_a_retryable_partial_failure()
    {
        using var api = new Api();
        api.TimeoutDelete = api.Ids[2];
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        await RenderAsync(renderer, reference);
        var row = Rows(reference.Page!).Single(row => Id(row) == api.Ids[2]);
        await InvokeAsync(renderer, reference.Page!, "BeginDelete", row);
        await InvokeAsync(renderer, reference.Page!, "ConfirmMutationAsync");
        Assert.Equal(api.Ids[2], Id(Assert.Single(Field<IList>(reference.Page!, "confirmation").Cast<object>())));
        Assert.Contains("timed out", Assert.Single(Field<List<string>>(reference.Page!, "cleanupFailures")));
        Assert.False(Field<bool>(reference.Page!, "saving"));
    }

    private static Task<HtmlRootComponent> RenderAsync(Renderer renderer, Reference reference) => renderer.Dispatcher.InvokeAsync(() =>
        renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
    private static List<object> Rows(Page page) => Field<IList>(page, "credentials").Cast<object>().ToList();
    private static Guid Id(object row) => (Guid)row.GetType().GetProperty("Id")!.GetValue(row)!;
    private static T Field<T>(Page page, string name) => (T)typeof(Page).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
    private static Task InvokeAsync(Renderer renderer, Page page, string name, params object?[] args) => renderer.Dispatcher.InvokeAsync(() =>
        ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(async () =>
        { if (typeof(Page).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args) is Task task) await task; })), null));
    private static ServiceProvider Services(Api api)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddMudServices();
        services.AddSingleton<IHttpClientFactory>(api);
        services.AddSingleton<AuthenticationStateProvider>(new Authentication());
        services.AddSingleton<NavigationManager>(new Navigation());
        services.AddSingleton(Substitute.For<IJSRuntime>());
        return services.BuildServiceProvider();
    }
    private sealed class Authentication : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "fixture")))); }
    private sealed class Navigation : NavigationManager
    { public Navigation() => Initialize("https://desk.example/", "https://desk.example/account/integration-credentials"); }
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public Guid[] Ids { get; } = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();
        public Guid? FailedDelete { get; set; }
        public bool MalformedCreate { get; set; }
        public bool ThrowCreate { get; set; }
        public Guid? TimeoutDelete { get; set; }
        public int Creates { get; private set; }
        public List<string> ClientNames { get; } = [];
        public List<Guid> Deletes { get; } = [];
        public TaskCompletionSource<HttpResponseMessage>? PendingList { get; set; }
        public object[] Metadata() => Ids.Select((id, i) => (object)new
        {
            id, name = $"credential {i:00}", prefix = $"rdk_test{i:00}", purpose = "api", organizationId = "org-a", permissions = new[] { "Incident.Read" },
            expiresAtUtc = DateTimeOffset.UtcNow.AddDays(i % 3 == 2 ? -1 : 10), createdAtUtc = DateTimeOffset.UtcNow.AddDays(-2), lastUsedAtUtc = (DateTimeOffset?)null,
            revokedAtUtc = i % 3 == 1 ? (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-1) : null
        }).ToArray();
        public HttpClient CreateClient(string name) { ClientNames.Add(name); return new(this, false) { BaseAddress = new("https://desk.example/") }; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/organizations"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
            if (request.Method == HttpMethod.Get)
                return PendingList?.Task ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Metadata()) });
            if (request.Method == HttpMethod.Post)
            {
                Creates++;
                if (ThrowCreate) throw new HttpRequestException("Synthetic connection lost after possible issuance.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(MalformedCreate ? "malformed-json" : "{}") });
            }
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.EndsWith("/record", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var id = Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[4]);
            Deletes.Add(id);
            if (id == TimeoutDelete) throw new TaskCanceledException("Synthetic bounded mutation timeout.");
            return Task.FromResult(new HttpResponseMessage(id == FailedDelete ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent));
        }
    }
    private sealed class Renderer(IServiceProvider services, ILoggerFactory loggers) : StaticHtmlRenderer(services, loggers)
    { public async Task<HtmlRootComponent> RenderAsync<T>(ParameterView parameters) where T : IComponent { var root = BeginRenderingComponent(typeof(T), parameters); await root.QuiescenceTask; return root; } }
    public sealed class Reference { public Page? Page { get; set; } }
    public sealed class Harness : ComponentBase
    {
        [Parameter] public Reference Reference { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        { builder.OpenComponent<Page>(0); builder.AddComponentReferenceCapture(1, value => Reference.Page = (Page)value); builder.CloseComponent(); }
    }
}
