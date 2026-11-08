extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using MudBlazor.Extensions;
using RoleDialog = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Role.RoleEditorDialog;
using RolePage = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Role.RoleManagement;
using Helpdesk.Shared.Models;

namespace Helpdesk.Tests.NewWeb;

public sealed class RoleEditorDialogTests
{
    [Fact]
    public async Task Edit_round_trips_existing_permissions_and_retains_draft_after_rejected_save()
    {
        using var api = new FixtureApi { Reject = true };
        await using var context = Context(api);
        var role = Role();
        var (page, dialog) = await RenderAsync(context, role);
        var name = page.FindComponents<MudTextField<string>>().Single(x => x.Instance.Label == "Role name");
        name.Find("input").Input("Updated responder");
        page.FindAll("button").Single(x => x.TextContent.Contains("Save role")).Click();
        page.WaitForAssertion(() => Assert.Contains("Synthetic role conflict", page.Find("[role=alert]").TextContent));
        Assert.Equal("Updated responder", name.Instance.GetState(x => x.Value));
        Assert.Equal("/api/v1/admin/role-definitions/custom", api.Path);
        Assert.Equal(new[] { "Incident.Read", "Request.Read" }, api.Payload!.Value.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()));
        Assert.False(api.Payload.Value.TryGetProperty("ownerOrganizationId", out _));
        Assert.False(dialog.Result.IsCompleted);
        api.Reject = false;
        page.FindAll("button").Single(x => x.TextContent.Contains("Save role")).Click();
        Assert.False((await dialog.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Canceled);
        Assert.Equal(2, api.Sends);
    }

    [Fact]
    public async Task Dependency_errors_do_not_send_requests_and_create_keeps_the_selected_owner()
    {
        using var api = new FixtureApi();
        await using var context = Context(api);
        var (page, dialog) = await RenderAsync(context, null);
        page.FindComponents<MudTextField<string>>().Single(x => x.Instance.Label == "Role name").Find("input").Input("Responder");
        page.FindAll("label.permission-choice").Single(x => x.TextContent.Contains("Incident.Write")).QuerySelector("input")!.Change(true);
        page.FindAll("button").Single(x => x.TextContent.Contains("Save role")).Click();
        Assert.Contains("matching Read permission", page.Find("[role=alert]").TextContent);
        Assert.Equal(0, api.Sends);
        page.FindAll("label.permission-choice").Single(x => x.TextContent.Contains("Incident.Read")).QuerySelector("input")!.Change(true);
        page.FindAll("button").Single(x => x.TextContent.Contains("Save role")).Click();
        Assert.False((await dialog.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Canceled);
        Assert.Equal("org-1", api.Payload!.Value.GetProperty("ownerOrganizationId").GetString());
        Assert.Equal(HttpMethod.Post, api.Method);
    }

    [Fact]
    public async Task A_role_above_the_editors_authority_is_read_only_instead_of_silently_dropping_saved_grants()
    {
        using var api = new FixtureApi();
        await using var context = Context(api);
        var (page, _) = await RenderAsync(context, Role() with { Permissions = ["Incident.Read", "Tenant.Users.Manage"] });
        Assert.Contains("saved permissions are preserved", page.Markup);
        Assert.True(page.FindAll("button").Single(x => x.TextContent.Contains("Save role")).HasAttribute("disabled"));
        Assert.All(page.FindAll("input[type=checkbox]"), x => Assert.True(x.HasAttribute("disabled")));
        Assert.Equal(0, api.Sends);
    }

    private static RolePage.RoleDefinition Role() => new("custom", "custom.responder", "Responder", RoleScopeKind.Tenant, "org-1", false, false, ["Incident.Read", "Request.Read"], 0);
    private static BunitContext Context(FixtureApi api)
    {
        var context = new BunitContext(); context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api); context.Render<MudPopoverProvider>(); return context;
    }
    private static async Task<(IRenderedComponent<RoleDialog> Page, IDialogReference Dialog)> RenderAsync(BunitContext context, RolePage.RoleDefinition? role)
    {
        var host = context.Render<MudDialogProvider>();
        IDialogReference? dialog = null;
        await host.InvokeAsync(async () => dialog = await context.Services.GetRequiredService<IDialogService>().ShowAsync<RoleDialog>("Role editor",
            new DialogParameters
            {
                [nameof(RoleDialog.Role)] = role,
                [nameof(RoleDialog.Organizations)] = new[] { new RolePage.TenantOrganization("org-1", "Example organization") },
                [nameof(RoleDialog.EligiblePermissions)] = new[] { "Incident.Read", "Incident.Write", "Request.Read", "Request.Write" }
            }));
        return (host.FindComponent<RoleDialog>(), dialog!);
    }
    private sealed class FixtureApi : HttpMessageHandler, IHttpClientFactory
    {
        public bool Reject { get; set; }
        public int Sends { get; private set; }
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public JsonElement? Payload { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://synthetic.example.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++; Path = request.RequestUri!.AbsolutePath; Method = request.Method;
            Payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return new HttpResponseMessage(Reject ? HttpStatusCode.Conflict : HttpStatusCode.OK) { Content = JsonContent.Create(new { message = "Synthetic role conflict" }) };
        }
    }
}
