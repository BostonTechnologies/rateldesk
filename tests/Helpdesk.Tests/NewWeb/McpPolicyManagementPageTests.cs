extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Helpdesk.Shared.DTOs.McpPolicies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using Page = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.McpPolicies;

namespace Helpdesk.Tests.NewWeb;

public sealed class McpPolicyManagementPageTests
{
    [Fact]
    public async Task Explicit_bulk_delete_reviews_each_revision_and_retains_failed_selection_without_touching_unselected_policy()
    {
        using var api = new Api();
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        await RenderAsync(renderer, reference);
        var rows = Field<List<McpPolicyDto>>(reference.Page!, "policies");
        await InvokeAsync(renderer, reference.Page!, "SelectPolicy", rows[0], new ChangeEventArgs { Value = true });
        await InvokeAsync(renderer, reference.Page!, "SelectPolicy", rows[1], new ChangeEventArgs { Value = true });
        await InvokeAsync(renderer, reference.Page!, "PreviewBulkDeleteAsync");
        Assert.Equal([rows[0].CredentialId, rows[1].CredentialId], api.Previewed);
        Assert.Empty(api.Confirmed);
        await InvokeAsync(renderer, reference.Page!, "ConfirmBulkDeleteAsync");
        Assert.Empty(api.Confirmed);
        api.FailedConfirm = rows[1].CredentialId;
        typeof(Page).GetField("bulkApproved", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(reference.Page, true);
        await InvokeAsync(renderer, reference.Page!, "ConfirmBulkDeleteAsync");
        Assert.Equal([rows[1].CredentialId], Field<HashSet<Guid>>(reference.Page!, "selectedPolicies"));
        Assert.Equal(rows[1].CredentialId, Assert.Single(Field<List<McpPolicyPreview>>(reference.Page!, "bulkReviews")).Policy.CredentialId);
        Assert.Equal("Active", api.Policies[2].State);
        api.FailedConfirm = null;
        typeof(Page).GetField("bulkApproved", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(reference.Page, true);
        await InvokeAsync(renderer, reference.Page!, "ConfirmBulkDeleteAsync");
        Assert.Empty(Field<HashSet<Guid>>(reference.Page!, "selectedPolicies"));
        Assert.Empty(Field<List<McpPolicyPreview>>(reference.Page!, "bulkReviews"));
        Assert.DoesNotContain(rows[2].CredentialId, api.Confirmed);
        Assert.Equal("Active", api.Policies[2].State);
    }

    [Fact]
    public async Task Retained_policy_draft_blocks_lifecycle_actions_and_retired_policies_require_the_history_filter()
    {
        using var api = new Api();
        api.Policies[2] = api.Policies[2] with { State = "Revoked" };
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        await RenderAsync(renderer, reference);
        var rows = Field<List<McpPolicyDto>>(reference.Page!, "policies");
        Assert.Equal(2, rows.Count);
        await InvokeAsync(renderer, reference.Page!, "Edit", rows[0]);
        await InvokeAsync(renderer, reference.Page!, "CloseEditor");
        Assert.True(Field<bool>(reference.Page!, "draftRetained"));
        await InvokeAsync(renderer, reference.Page!, "PreviewDeleteAsync", rows[1]);
        await InvokeAsync(renderer, reference.Page!, "PreviewLifecycleAsync", rows[1], "Disabled");
        Assert.Empty(api.Previewed);
        Assert.Equal(rows[0].CredentialId, Field<Guid>(reference.Page!, "credentialId"));
        await InvokeAsync(renderer, reference.Page!, "DiscardDraft");
        await InvokeAsync(renderer, reference.Page!, "FilterChangedAsync", new ChangeEventArgs { Value = "All" });
        Assert.Equal(3, Field<List<McpPolicyDto>>(reference.Page!, "policies").Count);
        Assert.True(api.HistoryRequested);
    }

    [Fact]
    public async Task Editing_a_disabled_policy_retains_its_state_and_enable_is_a_separate_review()
    {
        using var api = new Api();
        api.Policies[0] = api.Policies[0] with { State = "Disabled" };
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        var view = await RenderAsync(renderer, reference);
        await InvokeAsync(renderer, reference.Page!, "FilterChangedAsync", new ChangeEventArgs { Value = "All" });
        var policy = Field<List<McpPolicyDto>>(reference.Page!, "policies").Single(x => x.CredentialId == api.Policies[0].CredentialId);
        await InvokeAsync(renderer, reference.Page!, "Edit", policy);
        var html = await renderer.Dispatcher.InvokeAsync(view.ToHtmlString);
        Assert.Contains("Saving edits keeps it disabled", html);
        await InvokeAsync(renderer, reference.Page!, "PreviewAsync");
        Assert.Equal("Disabled", Assert.Single(api.Drafts).State);
        Assert.Equal("Disabled", Field<McpPolicyPreview>(reference.Page!, "preview").Policy.State);
        await InvokeAsync(renderer, reference.Page!, "ClosePreview");
        await InvokeAsync(renderer, reference.Page!, "DiscardDraft");
        await InvokeAsync(renderer, reference.Page!, "PreviewLifecycleAsync", policy, "Active");
        Assert.Equal("Active", api.Drafts[1].State);
        Assert.Empty(api.Confirmed);
    }

    [Fact]
    public async Task A_mismatched_confirmation_receipt_keeps_the_review_instead_of_reporting_success()
    {
        using var api = new Api { WrongConfirmationIdentity = true };
        await using var services = Services(api);
        await using var renderer = new Renderer(services, services.GetRequiredService<ILoggerFactory>());
        var reference = new Reference();
        await RenderAsync(renderer, reference);
        var policy = Field<List<McpPolicyDto>>(reference.Page!, "policies")[0];
        await InvokeAsync(renderer, reference.Page!, "PreviewDeleteAsync", policy);
        typeof(Page).GetField("approved", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(reference.Page, true);
        await InvokeAsync(renderer, reference.Page!, "ConfirmAsync");
        Assert.NotNull(Field<McpPolicyPreview?>(reference.Page!, "preview"));
        Assert.Contains("no matching receipt", Field<string>(reference.Page!, "error"));
    }

    private static Task<HtmlRootComponent> RenderAsync(Renderer renderer, Reference reference) => renderer.Dispatcher.InvokeAsync(() =>
        renderer.RenderAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Harness.Reference)] = reference })));
    private static T Field<T>(Page page, string name) => (T)typeof(Page).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
    private static Task InvokeAsync(Renderer renderer, Page page, string name, params object?[] args) => renderer.Dispatcher.InvokeAsync(() =>
        ((IHandleEvent)page).HandleEventAsync(new EventCallbackWorkItem((Func<Task>)(async () =>
        { if (typeof(Page).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args) is Task task) await task; })), null));
    private static ServiceProvider Services(Api api)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddMudServices();
        services.AddSingleton<IHttpClientFactory>(api); services.AddSingleton<NavigationManager>(new Navigation());
        services.AddSingleton(Substitute.For<IJSRuntime>());
        return services.BuildServiceProvider();
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("https://desk.example/", "https://desk.example/admin/operator-policies");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public List<McpPolicyDto> Policies { get; } = Enumerable.Range(1, 3).Select(index => new McpPolicyDto(Guid.NewGuid(), "org-a", ["Incident.Read"], DateTime.UtcNow.AddDays(1), 1, "Active")
        { CredentialName = $"Synthetic integration {index}", OwnerName = "Example owner", OrganizationName = "Example organization" }).ToList();
        public List<Guid> Previewed { get; } = [];
        public List<Guid> Confirmed { get; } = [];
        public List<McpPolicyDraft> Drafts { get; } = [];
        public Guid? FailedConfirm { get; set; }
        public bool WrongConfirmationIdentity { get; set; }
        public bool HistoryRequested { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://desk.example/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/directory"))
                return Json(new McpPolicyDirectory(Policies.Select(x => new McpPolicyCredential(x.CredentialId, x.CredentialName!, "owner", x.OrganizationId, new(x.ExpiresUtc))
                { OwnerName = "Example owner", Permissions = x.Permissions, PolicyState = x.State, PolicyRevision = x.Revision }).ToArray(), [new("org-a", "Example organization")], []));
            if (request.Method == HttpMethod.Get)
            {
                var history = request.RequestUri.Query.Contains("includeRetired=true", StringComparison.Ordinal);
                HistoryRequested |= history;
                return Json(Policies.Where(x => history || x.State == "Active").ToArray());
            }
            if (path.EndsWith("/delete/preview"))
            {
                var id = Guid.Parse(path.Split('/')[5]);
                var policy = Policies.Single(x => x.CredentialId == id);
                var input = await request.Content!.ReadFromJsonAsync<McpPolicyDeleteDraft>(ct);
                Assert.Equal(policy.Revision, input!.ExpectedRevision);
                Previewed.Add(id);
                return Json(new McpPolicyPreview(id.ToString(), new(id, policy.OrganizationId, policy.Permissions, policy.ExpiresUtc, policy.Revision, "Deleted"), "Delete this policy and retain its audit."));
            }
            if (path.EndsWith("/preview"))
            {
                var draft = await request.Content!.ReadFromJsonAsync<McpPolicyDraft>(ct);
                Assert.NotNull(draft);
                Drafts.Add(draft);
                return Json(new McpPolicyPreview(draft.CredentialId.ToString(), draft, "Apply the reviewed policy state and bounds."));
            }
            Assert.EndsWith("/confirm", path, StringComparison.Ordinal);
            var receipt = await request.Content!.ReadFromJsonAsync<ConfirmMcpPolicy>(ct);
            var credentialId = Guid.Parse(receipt!.Confirmation);
            Confirmed.Add(credentialId);
            if (credentialId == FailedConfirm) return new(HttpStatusCode.ServiceUnavailable);
            if (WrongConfirmationIdentity) return Json(Policies[2] with { State = "Deleted", Revision = 2 });
            var index = Policies.FindIndex(x => x.CredentialId == credentialId);
            Policies[index] = Policies[index] with { State = "Deleted", Revision = Policies[index].Revision + 1 };
            return Json(Policies[index]);
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
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
