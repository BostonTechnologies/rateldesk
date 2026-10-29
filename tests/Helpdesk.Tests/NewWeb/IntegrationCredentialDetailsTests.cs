extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.DTOs.Organization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Details = NewWeb::HelpDesk.NewWeb.Components.Shared.IntegrationCredentialDetails;
using DetailsModel = NewWeb::HelpDesk.NewWeb.Components.Shared.CredentialDetailsModel;
using Page = NewWeb::HelpDesk.NewWeb.Components.Pages.IntegrationCredentials;

namespace Helpdesk.Tests.NewWeb;

public sealed class IntegrationCredentialDetailsTests
{
    [Fact]
    public async Task Existing_complete_selection_requires_review_and_human_consent_then_refreshes_grants_and_admission()
    {
        using var api = new Api();
        await using var context = Context(api);
        var readbacks = new List<IntegrationCredentialAccessDetails>();
        var view = context.Render<Details>(parameters => parameters
            .Add(x => x.Credential, api.Credential)
            .Add(x => x.OnAccessChanged, value => readbacks.Add(value)));
        view.Find("[data-testid=credential-edit-access]").Click();
        Assert.Equal("Read incidents", PermissionChoice(view, "Incident.Read").QuerySelector("strong")!.TextContent.Trim());
        Assert.Equal("Create and update incidents", PermissionChoice(view, "Incident.Write").QuerySelector("strong")!.TextContent.Trim());
        Assert.Equal("Read requests", PermissionChoice(view, "Request.Read").QuerySelector("strong")!.TextContent.Trim());
        Assert.True(Checked(view, "Incident.Read"));
        Assert.True(Checked(view, "Request.Read"));
        Assert.False(Checked(view, "Incident.Write"));
        Toggle(view, "Incident.Write", true);
        view.Find("input[type=search]").Input("Incident");
        Assert.Contains("Request.Read", view.Find(".permission-selected").TextContent);
        Button(view, "Review changes").Click();
        Assert.Equal(["Incident.Read", "Incident.Write", "Request.Read"], Assert.Single(api.Drafts).Permissions);
        Assert.Equal("v1", api.Drafts[0].ExpectedVersion);
        Assert.Contains("Add (1)", view.Find("[data-testid=credential-access-review]").TextContent);
        Assert.Empty(api.Applies);
        Assert.True(Button(view, "Apply access changes").HasAttribute("disabled"));
        view.Find(".credential-consent input").Change(true);
        Assert.False(Button(view, "Apply access changes").HasAttribute("disabled"));
        Button(view, "Apply access changes").Click();
        view.WaitForAssertion(() => Assert.Empty(view.FindAll("[data-testid=credential-access-editor]")));
        Assert.True(Assert.Single(api.Applies).Consent);
        Assert.Equal(api.LastReview!.Confirmation, api.Applies[0].Confirmation);
        Assert.Equal(api.Access.Permissions, readbacks.Last().Permissions);
        Assert.Contains("3 permissions", view.Find(".credential-access").TextContent);
        Assert.Contains("3 of 3 configured permissions", view.Find(".credential-admission").TextContent);
        Assert.Contains(api.Credential.ExpiresAtUtc.UtcDateTime.ToString("O"), view.Find(".credential-facts").TextContent);
        Assert.Contains("secret and exact expiry are unchanged", view.Markup);
        Assert.Contains("SystemPairingApi", api.ClientNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_or_unconfirmed_apply_preserves_draft_and_requires_saved_access_refresh(bool lostResponseAfterCommit)
    {
        using var api = new Api();
        await using var context = Context(api);
        var view = context.Render<Details>(parameters => parameters.Add(x => x.Credential, api.Credential));
        view.Find("[data-testid=credential-edit-access]").Click();
        Toggle(view, "Incident.Write", true);
        Button(view, "Review changes").Click();
        view.Find(".credential-consent input").Change(true);
        api.LoseApplyResponseAfterCommit = lostResponseAfterCommit;
        if (!lostResponseAfterCommit)
        {
            api.RejectApply = HttpStatusCode.Conflict;
            api.Access = api.Access with
            {
                Permissions = ["Incident.Read", "Request.Read", "Change.Read"],
                EffectivePermissions = ["Incident.Read", "Request.Read", "Change.Read"], Version = "v2"
            };
        }
        Button(view, "Apply access changes").Click();
        view.WaitForAssertion(() => Assert.NotEmpty(view.FindAll("[data-testid=credential-access-editor]")));
        Assert.True(Checked(view, "Incident.Read"));
        Assert.True(Checked(view, "Incident.Write"));
        Assert.True(Checked(view, "Request.Read"));
        Assert.Empty(view.FindAll("[data-testid=credential-access-review]"));
        Assert.Single(api.Applies);
        Assert.Contains(lostResponseAfterCommit ? "save result could not be confirmed" : "Credential access changed after review", view.Markup);
        Assert.Contains("draft is preserved", view.Markup);
        Button(view, "Refresh current access").Click();
        view.WaitForAssertion(() => Assert.Contains(lostResponseAfterCommit ? "draft matches the currently saved" : "Current access refreshed", view.Markup));
        Assert.True(Checked(view, "Incident.Write"));
        if (lostResponseAfterCommit)
        {
            Assert.True(Button(view, "Review changes").HasAttribute("disabled"));
            Toggle(view, "Request.Read", false);
        }
        Button(view, "Review changes").Click();
        Assert.Equal(api.Access.Version, api.Drafts.Last().ExpectedVersion);
        Assert.Contains(lostResponseAfterCommit ? "Request.Read" : "Change.Read", api.LastReview!.RemovedPermissions);
        Assert.False(view.Find(".credential-consent input").HasAttribute("checked"));
        Assert.True(Button(view, "Apply access changes").HasAttribute("disabled"));
        Assert.Single(api.Applies);
    }

    [Fact]
    public async Task Saved_permission_outside_current_authority_survives_selector_changes_until_explicit_removal()
    {
        using var api = new Api();
        api.Access = api.Access with { Permissions = ["Incident.Read", "Incident.Delete"], EffectivePermissions = ["Incident.Read"], DeniedPermissions = ["Incident.Delete"] };
        await using var context = Context(api);
        var view = context.Render<Details>(parameters => parameters.Add(x => x.Credential, api.Credential));
        view.Find("[data-testid=credential-edit-access]").Click();
        Assert.Contains("Incident.Delete", view.Find(".credential-unavailable").TextContent);
        Toggle(view, "Incident.Write", true);
        view.Find("input[type=search]").Input("Incident.Write");
        api.RejectNextReview = HttpStatusCode.Forbidden;
        Button(view, "Review changes").Click();
        Assert.Equal(["Incident.Delete", "Incident.Read", "Incident.Write"], Assert.Single(api.Drafts).Permissions);
        Assert.Contains("Incident.Delete", view.Find(".credential-unavailable").TextContent);
        Assert.Contains("draft is preserved", view.Markup);
        view.Find(".credential-unavailable-row button").Click();
        Assert.Empty(view.FindAll(".credential-unavailable"));
        Button(view, "Review changes").Click();
        Assert.Equal(["Incident.Read", "Incident.Write"], api.Drafts.Last().Permissions);
        Assert.Equal(["Incident.Delete"], api.LastReview!.RemovedPermissions);
    }

    [Theory]
    [InlineData("mcp", true, "Revoked credentials cannot be edited.")]
    [InlineData(IntegrationCredentialGrantKinds.OwnerAdministrationPurpose, false, "This grant uses its dedicated consent flow.")]
    public async Task Inactive_and_dedicated_consent_credentials_keep_details_without_an_edit_action(string purpose, bool revoked, string reason)
    {
        using var api = new Api();
        api.Access = api.Access with { CanEdit = false, EditUnavailableReason = reason };
        await using var context = Context(api);
        var view = context.Render<Details>(parameters => parameters.Add(x => x.Credential,
            api.Credential with { Purpose = purpose, RevokedAtUtc = revoked ? DateTimeOffset.UtcNow : null }));
        Assert.Empty(view.FindAll("[data-testid=credential-edit-access]"));
        Assert.Contains(reason, view.Find(".credential-edit-reason").TextContent);
        Assert.Contains("Configured access", view.Markup);
        Assert.Contains("Exact expiry (UTC)", view.Markup);
        Assert.Empty(api.Drafts);
    }

    [Fact]
    public async Task Escape_and_close_keep_a_dirty_draft_until_the_operator_discards_it()
    {
        using var api = new Api();
        await using var context = Context(api);
        var closed = 0;
        var view = context.Render<Details>(parameters => parameters
            .Add(x => x.Credential, api.Credential).Add(x => x.OnClose, () => closed++));
        view.Find("[data-testid=credential-edit-access]").Click();
        Toggle(view, "Incident.Write", true);
        view.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(0, closed);
        Assert.Contains("Discard it and close?", view.Find(".credential-discard").TextContent);
        Button(view, "Keep editing").Click();
        Assert.Empty(view.FindAll(".credential-discard"));
        Assert.True(Checked(view, "Incident.Write"));
        view.Find("button[aria-label='Close credential details']").Click();
        Assert.Equal(0, closed);
        Assert.True(Checked(view, "Incident.Write"));
        Button(view, "Discard draft").Click();
        Assert.Equal(1, closed);
        Assert.Empty(api.Applies);
    }

    [Fact]
    public async Task Saved_details_readback_updates_parent_list_counts_and_fences_a_stale_list_response()
    {
        using var api = new Api();
        await using var context = Context(api, accountPage: true);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/integration?tab=credentials");
        context.Render<MudPopoverProvider>();
        var page = context.Render<Page>();
        page.WaitForAssertion(() => Assert.Contains("2 permissions", page.Find(".credential-table").TextContent));
        Button(page, "Details").Click();
        var details = page.FindComponent<Details>();
        details.Find("[data-testid=credential-edit-access]").Click();
        Toggle(details, "Incident.Write", true);
        Button(details, "Review changes").Click();
        details.Find(".credential-consent input").Change(true);

        var oldList = api.Metadata();
        api.PendingList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = Button(page, "Refresh").ClickAsync(new MouseEventArgs());
        Button(details, "Apply access changes").Click();
        page.WaitForAssertion(() => Assert.Contains("3 permissions", page.Find(".credential-table").TextContent));
        api.PendingList.SetResult(Json(oldList));
        await refresh;
        Assert.Contains("3 permissions", page.Find(".credential-table").TextContent);
        Assert.Contains("3 permissions", details.Find(".credential-access").TextContent);
        Assert.Contains("3 of 3 configured permissions", details.Find(".credential-admission").TextContent);
        Assert.Single(api.Applies);

        // A revoke in another session must be reflected by the same admission readback
        // in both the open dialog and the parent list, without a separate list reload.
        api.Access = api.Access with
        {
            RevokedAtUtc = DateTimeOffset.UtcNow, CanEdit = false, EditUnavailableReason = "Revoked credentials cannot be edited.",
            EffectivePermissions = [], DeniedPermissions = api.Access.Permissions
        };
        Button(details, "Refresh admission").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Revoked", details.Find(".credential-badges").TextContent);
            Assert.Contains("Revoked", page.Find(".credential-table").TextContent);
            Assert.Contains("Active (0)", page.Find(".credential-filters select").TextContent);
            Assert.Contains("Revoked (1)", page.Find(".credential-filters select").TextContent);
            Assert.Empty(details.FindAll("[data-testid=credential-edit-access]"));
        });
    }

    private static BunitContext Context(Api api, bool accountPage = false)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddSingleton<IHttpClientFactory>(api);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./js/credentialReview.js").Mode = JSRuntimeMode.Loose;
        if (accountPage)
        {
            var authorization = context.AddAuthorization();
            authorization.SetAuthorized("Credential owner");
            authorization.SetRoles("Incident.Read", "Incident.Write", "Request.Read");
            authorization.SetClaims(new Claim("auth_mode", "local"), new Claim("organization_id", "org-a"), new Claim("allowed_organization_id", "org-a"));
        }
        // Setting renderer metadata resolves the renderer and freezes bUnit's service
        // provider, so every service and authorization registration must happen first.
        context.SetRendererInfo(new RendererInfo("Server", true));
        return context;
    }

    private static IElement Button<T>(IRenderedComponent<T> view, string text) where T : IComponent =>
        Assert.Single(view.FindAll("button"), button => button.TextContent.Trim() == text);

    // The canonical permission token is a stable identity; the adjacent heading
    // intentionally presents the readable action rather than repeating that token.
    private static IElement PermissionChoice(IRenderedComponent<Details> view, string name) =>
        Assert.Single(view.FindAll(".permission-choice"), label => label.QuerySelector("small")?.TextContent.Trim() == name);
    private static IElement Permission(IRenderedComponent<Details> view, string name) =>
        Assert.Single(PermissionChoice(view, name).QuerySelectorAll("input[type=checkbox]"));
    private static bool Checked(IRenderedComponent<Details> view, string name) => Permission(view, name).HasAttribute("checked");
    private static void Toggle(IRenderedComponent<Details> view, string name, bool value) => Permission(view, name).Change(value);
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };

    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public DetailsModel Credential { get; } = new(Guid.NewGuid(), "Existing NetClaw connection", "rdk_synthetic", "mcp", "org-a", "Organization A",
            ["Incident.Read", "Request.Read"], DateTimeOffset.UtcNow.AddDays(14), DateTimeOffset.UtcNow.AddDays(-1), null, null, "https://desk.example/mcp");
        public IntegrationCredentialAccessDetails Access { get; set; }
        public List<IntegrationCredentialAccessDraft> Drafts { get; } = [];
        public List<ApplyIntegrationCredentialAccess> Applies { get; } = [];
        public List<string> ClientNames { get; } = [];
        public IntegrationCredentialAccessReview? LastReview { get; private set; }
        public HttpStatusCode? RejectNextReview { get; set; }
        public HttpStatusCode? RejectApply { get; set; }
        public bool LoseApplyResponseAfterCommit { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? PendingList { get; set; }

        public Api() => Access = new(Credential.Id, "org-a", "Organization A", Credential.Permissions,
            ["Incident.Read", "Incident.Write", "Request.Read", "Change.Read"], Credential.Permissions, [], "v1", true, null, "None",
            "No additional MCP policy is configured. Current account and credential grants determine access.");

        public HttpClient CreateClient(string name)
        {
            ClientNames.Add(name);
            return new(this, false) { BaseAddress = new("https://desk.example/") };
        }

        public object[] Metadata() => [new
        {
            Credential.Id, Credential.Name, Credential.Prefix, Credential.Purpose, Credential.OrganizationId,
            Permissions = Access.Permissions.ToArray(), Credential.ExpiresAtUtc, Credential.CreatedAtUtc, Credential.LastUsedAtUtc,
            Credential.RevokedAtUtc, Credential.McpResourceUri, GrantKind = "ordinary"
        }];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/organizations")
                return Json(new[] { new OrganizationDto { Id = "org-a", Name = "Organization A", IsEnabled = true } });
            if (request.Method == HttpMethod.Get && path == "/api/v1/integration-credentials/")
                return PendingList is null ? Json(Metadata()) : await PendingList.Task;
            if (request.Method == HttpMethod.Get && path.EndsWith("/access", StringComparison.Ordinal)) return Json(Access);
            if (request.Method == HttpMethod.Post && path.EndsWith("/access/review", StringComparison.Ordinal))
            {
                var draft = (await request.Content!.ReadFromJsonAsync<IntegrationCredentialAccessDraft>(cancellationToken))!;
                Drafts.Add(draft);
                if (RejectNextReview is { } rejected)
                {
                    RejectNextReview = null;
                    return Json(new { message = "The owner cannot grant this access." }, rejected);
                }
                LastReview = new("review-" + Drafts.Count, draft.Permissions,
                    draft.Permissions.Except(Access.Permissions).ToArray(), Access.Permissions.Except(draft.Permissions).ToArray(), DateTimeOffset.UtcNow.AddMinutes(5));
                return Json(LastReview);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/access/apply", StringComparison.Ordinal))
            {
                var apply = (await request.Content!.ReadFromJsonAsync<ApplyIntegrationCredentialAccess>(cancellationToken))!;
                Applies.Add(apply);
                if (RejectApply is { } rejected)
                    return Json(new { message = "Credential access changed after review. Refresh the current access and review your draft again." }, rejected);
                Access = Access with { Permissions = LastReview!.Permissions, EffectivePermissions = LastReview.Permissions, DeniedPermissions = [], Version = "v" + (Applies.Count + 1) };
                if (LoseApplyResponseAfterCommit) throw new HttpRequestException("Synthetic response loss after persisted access update.");
                return Json(Access);
            }
            throw new InvalidOperationException($"Unexpected test request: {request.Method} {path}");
        }
    }
}

