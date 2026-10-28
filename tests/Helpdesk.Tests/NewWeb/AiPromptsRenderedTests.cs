extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using BlazorMonaco.Editor;
using Bunit;
using Helpdesk.Shared.DTOs.AiOps;
using Helpdesk.Shared.DTOs.Organization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Page = NewWeb::HelpDesk.NewWeb.Components.Pages.AiOps.Prompts;
using Editor = NewWeb::HelpDesk.NewWeb.Components.Shared.MonacoTextEditor;

namespace Helpdesk.Tests.NewWeb;

public sealed class AiPromptsRenderedTests
{
    [Fact]
    public async Task Library_selects_saved_guidance_and_saves_the_editor_as_a_new_revision()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid=prompt-item]").Count));
        Assert.Equal("Original guidance", page.FindComponent<Editor>().Instance.Value);
        Assert.True(page.FindComponent<Editor>().Instance.ReadOnly);

        page.Find("[data-testid=prompt-edit]").Click();
        page.Find("[data-testid=prompt-name]").Input("Revised default");
        await SetGuidanceAsync(context, page, "Edited guidance from Monaco");
        page.Find("[data-testid=prompt-save]").Click();

        page.WaitForAssertion(() => Assert.Single(api.Saves));
        var request = api.Saves[0];
        Assert.Equal(api.Primary.Id, request.Id);
        Assert.Equal("org-a", request.Request.OrganizationId);
        Assert.Equal(3, request.Request.ExpectedRevision);
        Assert.Equal("Edited guidance from Monaco", request.Request.Content);
        page.WaitForAssertion(() => Assert.True(page.FindComponent<Editor>().Instance.ReadOnly));
        Assert.Contains("Revision 4", page.Markup);
        Assert.Contains("Revised default", page.Find(".prompt-default-status").TextContent);
        Assert.Contains("SystemPairingApi", api.Clients);
    }

    [Fact]
    public async Task Dirty_draft_requires_an_explicit_choice_before_prompt_or_organization_changes()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid=prompt-item]").Count));
        page.Find("[data-testid=prompt-edit]").Click();
        page.Find("[data-testid=prompt-name]").Input("Retain this draft");
        await SetGuidanceAsync(context, page, "Unsaved organization A guidance");

        page.FindAll("[data-testid=prompt-item]")[1].Click();
        Assert.Contains("Discard unsaved prompt changes?", page.Markup);
        Button(page, "Keep editing").Click();
        Assert.Equal("Retain this draft", page.Find("[data-testid=prompt-name]").GetAttribute("value"));
        Assert.Equal("Unsaved organization A guidance", page.FindComponent<Editor>().Instance.Value);

        page.Find("[data-testid=prompt-organization]").Change("org-b");
        Assert.Single(api.LoadedOrganizations);
        Assert.Equal("Retain this draft", page.Find("[data-testid=prompt-name]").GetAttribute("value"));
        Button(page, "Discard changes").Click();
        page.WaitForAssertion(() => Assert.Equal(new[] { "org-a", "org-b" }, api.LoadedOrganizations));
        Assert.Equal("Organization B guidance", page.FindComponent<Editor>().Instance.Value);
        Assert.True(page.FindComponent<Editor>().Instance.ReadOnly);
        Assert.DoesNotContain("Retain this draft", page.Markup);
        Assert.Empty(api.Saves);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("lost-response")]
    [InlineData("server-error")]
    public async Task Failed_or_unconfirmed_save_preserves_the_draft_and_requires_saved_revision_reload(string failure)
    {
        using var api = new Api { RejectSave = failure == "conflict", LoseSaveResponse = failure == "lost-response", ServerErrorAfterSave = failure == "server-error" };
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=prompt-item]")));
        page.Find("[data-testid=prompt-edit]").Click();
        page.Find("[data-testid=prompt-name]").Input("My revised title");
        await SetGuidanceAsync(context, page, "Keep this exact draft");
        page.Find("[data-testid=prompt-save]").Click();

        page.WaitForAssertion(() => Assert.Contains("draft is preserved", page.Markup));
        Assert.Equal("My revised title", page.Find("[data-testid=prompt-name]").GetAttribute("value"));
        Assert.Equal("Keep this exact draft", page.FindComponent<Editor>().Instance.Value);
        Assert.True(page.Find("[data-testid=prompt-save]").HasAttribute("disabled"));
        Assert.Single(api.Saves);
        Button(page, "Reload saved revision").Click();
        Assert.Single(api.LoadedOrganizations);
        Assert.Contains("Discard unsaved prompt changes?", page.Markup);
        Button(page, "Discard changes").Click();
        page.WaitForAssertion(() => Assert.Equal(2, api.LoadedOrganizations.Count));
        Assert.True(page.FindComponent<Editor>().Instance.ReadOnly);
        Assert.Equal(api.Primary.Content, page.FindComponent<Editor>().Instance.Value);
        Assert.Single(api.Saves);
    }

    [Fact]
    public async Task Organization_switch_reads_the_latest_editor_text_before_deciding_whether_a_draft_is_dirty()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=prompt-item]")));
        page.Find("[data-testid=prompt-edit]").Click();
        // The browser has accepted typing but its change callback has not reached the page yet.
        context.JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult("Latest browser draft");
        page.Find("[data-testid=prompt-organization]").Change("org-b");
        page.WaitForAssertion(() => Assert.Contains("Discard unsaved prompt changes?", page.Markup));
        Assert.Single(api.LoadedOrganizations);
        Assert.Equal("Latest browser draft", page.FindComponent<Editor>().Instance.Value);
        Button(page, "Keep editing").Click();
        Assert.Equal("Latest browser draft", page.FindComponent<Editor>().Instance.Value);
        Assert.Empty(api.Saves);
    }

    [Fact]
    public async Task Archive_and_default_actions_preserve_revision_and_existing_default_semantics()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid=prompt-item]").Count));
        page.FindAll("[data-testid=prompt-item]")[1].Click();
        Button(page, "Set as default").Click();
        Assert.Contains("Remove the default setting", page.Markup);
        Assert.Empty(api.Saves);

        page.FindAll("[data-testid=prompt-item]")[0].Click();
        context.JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult(api.Primary.Content);
        Button(page, "Archive").Click();
        Assert.Empty(api.Saves);
        Assert.Contains("removed as the organization default", page.Markup);
        Button(page, "Confirm archive").Click();
        page.WaitForAssertion(() => Assert.Single(api.Saves));
        Assert.True(api.Saves[0].Request.Archived);
        Assert.False(api.Saves[0].Request.IsDefault);
        Assert.Equal(3, api.Saves[0].Request.ExpectedRevision);
        Assert.Contains("No organization default", page.Find(".prompt-default-status").TextContent);
        Assert.Single(page.FindAll("[data-testid=prompt-item]"));

        page.Find(".prompt-toolbar input[type=checkbox]").Change(true);
        Assert.Equal(3, page.FindAll("[data-testid=prompt-item]").Count);
        page.Find("input[type=search]").Input("Retired");
        Assert.Single(page.FindAll("[data-testid=prompt-item]"));
        Assert.Contains("Retired guidance", page.Find("[data-testid=prompt-item]").TextContent);
    }

    [Fact]
    public async Task Copy_creates_a_separate_prompt_without_carrying_default_or_archive_flags()
    {
        using var api = new Api();
        await using var context = Context(api);
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=prompt-item]")));
        Button(page, "Copy").Click();
        Assert.Equal("A default (copy)", page.Find("[data-testid=prompt-name]").GetAttribute("value"));
        context.JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult(api.Primary.Content);
        page.Find("[data-testid=prompt-save]").Click();
        page.WaitForAssertion(() => Assert.Single(api.Saves));
        Assert.NotEqual(api.Primary.Id, api.Saves[0].Id);
        Assert.Equal(0, api.Saves[0].Request.ExpectedRevision);
        Assert.False(api.Saves[0].Request.IsDefault);
        Assert.False(api.Saves[0].Request.Archived);
    }

    private static BunitContext Context(Api api)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./js/helpdesk-monaco-text.js").Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static async Task SetGuidanceAsync(BunitContext context, IRenderedComponent<Page> page, string value)
    {
        context.JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult(value);
        await page.InvokeAsync(() => page.FindComponent<StandaloneCodeEditor>().Instance.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Page> page, string label) => page.FindAll("button").Single(button => button.TextContent.Trim() == label);

    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public AiPromptDto Primary { get; private set; } = new(Guid.NewGuid(), "org-a", "A default", "Primary guidance", "Original guidance", 3, true, false);
        private readonly AiPromptDto second = new(Guid.NewGuid(), "org-a", "B response", "Second guidance", "Second body", 1, false, false);
        private readonly AiPromptDto retired = new(Guid.NewGuid(), "org-a", "Retired guidance", "Archived", "Old body", 2, false, true);
        private readonly AiPromptDto other = new(Guid.NewGuid(), "org-b", "Other organization", "", "Organization B guidance", 2, false, false);
        private readonly List<AiPromptDto> additional = [];
        public bool RejectSave { get; init; }
        public bool LoseSaveResponse { get; init; }
        public bool ServerErrorAfterSave { get; init; }
        public List<string> LoadedOrganizations { get; } = [];
        public List<string> Clients { get; } = [];
        public List<(Guid Id, SaveAiPromptDto Request)> Saves { get; } = [];

        public HttpClient CreateClient(string name) { Clients.Add(name); return new(this, false) { BaseAddress = new("https://desk.example/") }; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/organizations")
                return Json(new[] { new OrganizationDto { Id = "org-a", Name = "Organization A", IsEnabled = true }, new OrganizationDto { Id = "org-b", Name = "Organization B", IsEnabled = true } });
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/ai/ops/prompts")
            {
                var org = request.RequestUri.Query.Contains("org-b", StringComparison.Ordinal) ? "org-b" : "org-a";
                LoadedOrganizations.Add(org);
                return Json(org == "org-b" ? [other] : new[] { Primary, second, retired }.Concat(additional).ToArray());
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            var id = Guid.Parse(request.RequestUri!.Segments[^1]);
            var update = (await request.Content!.ReadFromJsonAsync<SaveAiPromptDto>(cancellationToken))!;
            Saves.Add((id, update));
            if (RejectSave) return new(HttpStatusCode.Conflict);
            var saved = new AiPromptDto(id, update.OrganizationId, update.Name, update.Description, update.Content, update.ExpectedRevision + 1, update.IsDefault, update.Archived);
            if (id == Primary.Id) Primary = saved; else additional.Add(saved);
            if (LoseSaveResponse) throw new HttpRequestException("Synthetic loss after commit.");
            if (ServerErrorAfterSave) return new(HttpStatusCode.InternalServerError);
            return Json(saved);
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}

public sealed class MonacoTextEditorRenderedTests
{
    [Fact]
    public async Task Leaving_during_asset_loading_disposes_the_module_once_after_loading_finishes()
    {
        await using var context = new BunitContext();
        var module = new PendingModule();
        context.Services.AddSingleton<IJSRuntime>(new PendingImportRuntime(module));
        var editor = context.Render<Editor>(parameters => parameters.Add(x => x.Value, "Guidance"));
        editor.WaitForAssertion(() => Assert.True(module.LoadStarted));
        await context.DisposeAsync();
        Assert.Equal(0, module.DisposeCount);
        module.CompleteLoad.SetResult();
        await module.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, module.DisposeCount);
    }

    [Fact]
    public async Task Fullscreen_can_be_closed_with_escape_and_releases_the_browser_editor()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./js/helpdesk-monaco-text.js");
        module.Mode = JSRuntimeMode.Loose;
        var editor = context.Render<Editor>(parameters => parameters.Add(x => x.Value, "Guidance"));
        editor.WaitForAssertion(() => Assert.NotEmpty(editor.FindAll(".monaco-editor-container")));
        editor.Find("button").Click();
        Assert.Contains("fullscreen", editor.Find("[data-testid=monaco-text-editor]").ClassList);
        editor.Find("[data-testid=monaco-text-editor]").KeyDown("Escape");
        Assert.DoesNotContain("fullscreen", editor.Find("[data-testid=monaco-text-editor]").ClassList);
        await context.DisposeAsync();
        Assert.Single(module.Invocations["disposeEditor"]);
    }

    [Fact]
    public async Task Asset_failure_keeps_editable_guidance_available()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./js/helpdesk-monaco-text.js");
        module.SetupVoid("ensureBlazorMonaco").SetException(new JSException("Synthetic asset unavailable"));
        string? changed = null;
        var editor = context.Render<Editor>(parameters => parameters.Add(x => x.Value, "Original guidance").Add(x => x.ValueChanged, value => changed = value));
        editor.WaitForAssertion(() => Assert.Single(editor.FindAll("textarea")));
        editor.Find("textarea").Input("Retained fallback draft");
        Assert.Equal("Retained fallback draft", changed);
        Assert.Equal("Retained fallback draft", await editor.Instance.GetValueAsync());
    }

    private sealed class PendingImportRuntime(PendingModule module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            Assert.Equal("./js/helpdesk-monaco-text.js", Assert.Single(args!));
            return ValueTask.FromResult((TValue)(object)module);
        }
    }

    private sealed class PendingModule : IJSObjectReference
    {
        public bool LoadStarted { get; private set; }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource CompleteLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("ensureBlazorMonaco", identifier);
            LoadStarted = true;
            await CompleteLoad.Task;
            return default!;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
