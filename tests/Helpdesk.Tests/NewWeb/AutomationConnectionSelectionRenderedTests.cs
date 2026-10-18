extern alias NewWeb;
using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Pairing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Builder = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.RequestFormBuilder;
using CreateTarget = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.CreateOrchestrationAutomationTargetDialog;
using Selection = NewWeb::HelpDesk.NewWeb.Services.AutomationConnectionSelection;
using TargetFilter = NewWeb::HelpDesk.NewWeb.Services.OrchestrationCatalogTargetFilter;

namespace Helpdesk.Tests.NewWeb;

public sealed class AutomationConnectionSelectionRenderedTests
{
    private const string OrganizationId = "synthetic-organization";
    private static readonly Guid TaskId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Reload_uses_stored_connection_after_form_organization_and_preserves_it_on_save()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId), Connection(SecondId)], StoredConnectionId = SecondId };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        var firstCatalog = api.Paths.FindIndex(x => x.Contains("/catalog/", StringComparison.Ordinal));
        Assert.True(firstCatalog > api.Paths.IndexOf("/api/v1/request-forms/synthetic-form"));
        Assert.All(api.Paths.Where(x => x.Contains("/catalog/", StringComparison.Ordinal)), x =>
        {
            Assert.Contains("organizationId=" + OrganizationId, x);
            Assert.Contains("connectionId=" + SecondId, x);
        });
        await InvokeAsync(page, "ToggleTaskAutomationConfiguration", TaskId);
        Assert.Contains("Automation system connection", page.Markup);
        Assert.DoesNotContain("Organization Management first", page.Markup);
        Assert.Equal(SecondId, EditorConnection(page));
        await InvokeAsync(page, "SyncBindingsAsync", "synthetic-form");
        Assert.Equal(SecondId, Assert.Single(api.Updates).SystemConnectionId);
        Assert.Null(api.Organization.OrchestrationTenantId);
    }

    [Fact]
    public async Task Unbound_multiple_connections_require_choice_and_invalid_stored_connection_never_falls_back()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId), Connection(SecondId)], StoredConnectionId = null };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.Contains("2 saved automation", page.Markup));
        Assert.False(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget"));
        Assert.DoesNotContain(api.Paths, x => x.Contains("/catalog/", StringComparison.Ordinal));
        await InvokeAsync(page, "OnAutomationConnectionChangedAsync", (Guid?)SecondId);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        Assert.Equal(SecondId, EditorConnection(page));

        api.Rows = [Connection(FirstId)];
        var callsBeforeRemoval = api.Paths.Count(x => x.Contains("/catalog/", StringComparison.Ordinal));
        await InvokeAsync(page, "ReloadAutomationConnectionsAsync");
        Assert.False(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget"));
        Assert.Equal(SecondId, EditorConnection(page));
        Assert.Equal(callsBeforeRemoval, api.Paths.Count(x => x.Contains("/catalog/", StringComparison.Ordinal)));
        Assert.Contains("stored automation connection is unavailable", Property<string>(page.Instance, "OrchestrationTargetCreationReadinessMessage"));
    }

    [Fact]
    public async Task Only_one_eligible_connection_is_selected_and_mismatched_resolved_peer_is_not_ready()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId), Connection(SecondId) with { Status = "Systems paired" }], StoredConnectionId = null };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        Assert.Equal(FirstId, EditorConnection(page));
        api.MismatchPeerTenant = true;
        await InvokeAsync(page, "ReloadAutomationConnectionsAsync");
        Assert.False(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget"));
        Assert.Contains("no longer matches", Field<string>(page.Instance, "orchestrationCatalogError"));
    }

    [Fact]
    public void Selection_excludes_wrong_org_disabled_and_incident_only_rows_and_never_matches_by_tenant_name()
    {
        var valid = Connection(FirstId);
        var invalid = Connection(SecondId);
        var choices = Selection.ForOrganization([valid,
            invalid with { Status = "disabled" },
            invalid with { Mapping = invalid.Mapping! with { RunAutomation = false } },
            invalid with { Mapping = invalid.Mapping! with { RatelDeskOrganizationId = "foreign" } },
            invalid with { Mapping = invalid.Mapping! with { NetRatelTenantId = "not-numeric" } }], OrganizationId);
        Assert.Equal(FirstId, Assert.Single(choices).ConnectionId);
        Assert.Null(Selection.Resolve(choices, SecondId));
        var definitions = new[] {
            new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = "own", TenantId = 17, TenantName = "Same label" },
            new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = "foreign", TenantId = 42, TenantName = "Same label" },
            new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = "unknown", TenantName = "Same label" }
        };
        Assert.Equal("own", Assert.Single(TargetFilter.FilterForConnection(definitions, 17)).RequestDefinitionId);
        Assert.Empty(TargetFilter.FilterForConnection(definitions, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_target_dialog_cannot_attach_old_target_after_connection_switch_or_same_connection_tenant_remap(bool remapTenant)
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId), Connection(SecondId)], StoredConnectionId = FirstId };
        var dialogs = Substitute.For<IDialogService>();
        var reference = Substitute.For<IDialogReference>();
        var result = new TaskCompletionSource<DialogResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        reference.Result.Returns(result.Task);
        dialogs.ShowAsync<CreateTarget>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>()).Returns(reference);
        await using var context = Context(api, dialogs);
        var page = context.Render<Builder>(p => p.Add(x => x.ServiceId, "synthetic-service").Add(x => x.RequestFormId, "synthetic-form"));
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        var pendingDialog = InvokeAsync(page, "OpenCreateOrchestrationTargetDialogAsync");
        await dialogs.Received(1).ShowAsync<CreateTarget>(Arg.Any<string>(), Arg.Any<DialogParameters>(), Arg.Any<DialogOptions>());
        if (remapTenant)
        {
            api.Rows = [Connection(FirstId) with { Mapping = Connection(FirstId).Mapping! with { NetRatelTenantId = "42" } }, Connection(SecondId)];
            api.MismatchPeerTenant = true;
            await InvokeAsync(page, "ReloadAutomationConnectionsAsync");
        }
        else await InvokeAsync(page, "OnAutomationConnectionChangedAsync", (Guid?)SecondId);
        result.SetResult(DialogResult.Ok(new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = "created-on-first-peer", TenantId = 17 }));
        await pendingDialog;
        Assert.Equal(remapTenant ? FirstId : SecondId, EditorConnection(page));
        Assert.Equal(remapTenant ? "definition-17" : null,
            Property<string?>(Field<IDictionary>(page.Instance, "automationBindings")[TaskId]!, "OrchestrationRequestDefinitionId"));
        Assert.Empty(api.Updates);
    }

    private static BunitContext Context(FixtureApi api, IDialogService? dialogs = null)
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IHttpClientFactory>(api);
        if (dialogs is not null) context.Services.AddSingleton(dialogs);
        context.Render<MudPopoverProvider>();
        return context;
    }

    private static async Task InvokeAsync(IRenderedComponent<Builder> page, string method, params object?[] args)
        => await page.InvokeAsync(async () =>
        {
            var result = typeof(Builder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, args);
            if (result is Task task) await task;
            typeof(Microsoft.AspNetCore.Components.ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, null);
        });
    private static async Task<IRenderedComponent<Builder>> RenderAsync(BunitContext context)
    {
        var host = context.Render<MudDialogProvider>();
        await host.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<Builder>("Synthetic form",
            new DialogParameters { [nameof(Builder.ServiceId)] = "synthetic-service", [nameof(Builder.RequestFormId)] = "synthetic-form" }));
        return host.FindComponent<Builder>();
    }
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(instance)!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static Guid? EditorConnection(IRenderedComponent<Builder> page)
        => Property<Guid?>(Field<IDictionary>(page.Instance, "automationBindings")[TaskId]!, "SystemConnectionId");
    private static PairingConnectionDto Connection(Guid id) => new(id.ToString("D"), "synthetic-pair",
        new(id.ToString("D"), "synthetic-pair", "Synthetic automation", "17", OrganizationId, null, false, true),
        new(PairingContract.Version, "netratel", "synthetic-peer", "Synthetic peer", "https://peer.example.test", "https://peer-api.example.test", null), "Connected", null);

    private sealed class FixtureApi : HttpMessageHandler, IHttpClientFactory
    {
        public List<PairingConnectionDto> Rows { get; set; } = [];
        public Guid? StoredConnectionId { get; set; }
        public bool MismatchPeerTenant { get; set; }
        public List<string> Paths { get; } = [];
        public List<UpdateAutomationBindingDto> Updates { get; } = [];
        public OrganizationDto Organization { get; } = new() { Id = OrganizationId, Name = "Synthetic organization" };
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://synthetic.example.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(request.RequestUri.PathAndQuery);
            object body;
            if (path == "/api/v1/organizations") body = new[] { Organization };
            else if (path == "/api/v1/users") body = Array.Empty<UserDto>();
            else if (path == "/api/v1/request-forms/synthetic-form") body = new RequestFormDto { Id = "synthetic-form", Title = "Synthetic form", OrganizationId = OrganizationId,
                JsonSchema = JsonSerializer.Serialize(new { fields = Array.Empty<object>(), tasks = new[] { new { id = TaskId, name = "Automate", type = "automation", order = 1 } } }) };
            else if (path == "/api/v1/admin/system-connections/") body = Rows;
            else if (path == "/api/v1/admin/orchestration/")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                body = new OrchestrationConnectivitySettingsDto { Enabled = true, Source = "pairing", SourceKey = query["connectionId"], LocalTenantId = OrganizationId, PeerTenantId = MismatchPeerTenant ? "42" : "17" };
            }
            else if (path.EndsWith("/catalog/request-definitions", StringComparison.Ordinal)) body = new[] { new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = "definition-17", TenantId = 17, RequestDefinitionName = "Synthetic target", ClientIdentity = "synthetic-client" } };
            else if (path == "/api/v1/admin/orchestration/bindings/synthetic-binding" && request.Method == HttpMethod.Put)
            {
                var update = (await request.Content!.ReadFromJsonAsync<UpdateAutomationBindingDto>(cancellationToken))!;
                Updates.Add(update); StoredConnectionId = update.SystemConnectionId;
                body = Binding();
            }
            else if (path.Contains("/bindings", StringComparison.Ordinal)) body = new[] { Binding() };
            else body = Array.Empty<object>();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
        private AutomationBindingDto Binding() => new() { Id = "synthetic-binding", RequestFormId = "synthetic-form", TaskTemplateId = TaskId, OrganizationId = OrganizationId,
            SystemConnectionId = StoredConnectionId, OrchestrationRequestDefinitionId = "definition-17", OrchestrationRequestDefinitionName = "Synthetic target", Enabled = true };
    }
}
