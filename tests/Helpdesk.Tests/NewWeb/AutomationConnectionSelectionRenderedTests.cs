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
using MudBlazor.Extensions;
using NSubstitute;
using Builder = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.RequestFormBuilder;
using CreateTarget = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.CreateOrchestrationAutomationTargetDialog;
using Selection = NewWeb::HelpDesk.NewWeb.Services.AutomationConnectionSelection;
using TargetFilter = NewWeb::HelpDesk.NewWeb.Services.OrchestrationCatalogTargetFilter;

namespace Helpdesk.Tests.NewWeb;

public sealed partial class AutomationConnectionSelectionRenderedTests
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_binding_can_be_saved_from_the_form_editor(bool newBinding)
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId, HasBinding = !newBinding };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        if (newBinding) await InvokeAsync(page, "OnSelectedCatalogRequestDefinitionChanged", "definition-17");
        await InvokeAsync(page, "OnSelectedBindingEnabledChanged", false);
        Assert.True((bool)typeof(Builder).GetMethod("IsTaskConfigurationValid", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, null)!);
        await InvokeAsync(page, "SyncBindingsAsync", "synthetic-form");
        Assert.False(Assert.Single(api.Updates).Enabled);
        Assert.False(api.BindingEnabled);
    }

    [Fact]
    public async Task Empty_binding_error_identifies_the_failed_stage_and_http_status()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId, FailBindingUpdates = true };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(page, "SyncBindingsAsync", "synthetic-form"));
        Assert.Contains("Updating the automation binding", error.Message);
        Assert.Contains("HTTP 400", error.Message);
    }

    [Fact]
    public async Task Binding_failure_after_create_retains_the_saved_form_for_the_next_save()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId, FailBindingUpdates = true };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        typeof(Builder).GetProperty(nameof(Builder.RequestFormId))!.SetValue(page.Instance, null);
        await InvokeAsync(page, "Save");
        Assert.Equal("saved-synthetic-form", page.Instance.RequestFormId);
        Assert.Equal(1, api.FormCreates);
        await InvokeAsync(page, "Save");
        Assert.Equal(1, api.FormCreates);
        Assert.Equal(1, api.FormUpdates);
    }

    [Fact]
    public async Task Setup_is_default_and_tabs_preserve_the_form_and_selected_task_drafts()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.Equal("true", page.Find("#request-form-tab-0").GetAttribute("aria-selected")));
        Assert.Equal(new[] { "Form setup", "Form fields (0)", "Workflow (1)" }, page.FindAll("[role='tab']").Select(x => x.TextContent.Trim()));
        Assert.False(page.Find("#request-form-panel-0").HasAttribute("hidden"));
        Assert.True(page.Find("#request-form-panel-1").HasAttribute("hidden"));
        Assert.True(page.Find("#request-form-panel-2").HasAttribute("hidden"));
        page.FindComponents<MudTextField<string>>().Single(x => x.Instance.Label == "Form Title").Find("input").Input("Changed title");
        page.Find("#request-form-tab-2").Click();
        Assert.Equal("true", page.Find("#request-form-tab-2").GetAttribute("aria-selected"));
        Assert.Single(page.FindAll(".request-form-task-config-panel [data-testid='task-automation']"));
        Assert.Empty(page.FindAll(".request-form-task-list-panel [data-testid='task-automation']"));
        Assert.Equal(TaskId, Field<RequestTaskTemplateModel>(page.Instance, "selectedTask").Id);
        page.Find("#request-form-tab-0").Click();
        Assert.Equal("Changed title", page.FindComponents<MudTextField<string>>().Single(x => x.Instance.Label == "Form Title").Instance.GetState(x => x.Value));
        Assert.True(Property<bool>(page.Instance, "IsDirty"));
        Assert.Equal(0, api.FormCreates + api.FormUpdates);
    }

    [Fact]
    public async Task Hidden_task_validation_selects_the_invalid_task_and_preserves_order_and_dependencies()
    {
        var secondId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId,
            Schema = JsonSerializer.Serialize(new { fields = Array.Empty<object>(), tasks = new[] {
                new RequestTaskTemplateModel { Id = TaskId, Name = "First", Type = "manual", Order = 1 },
                new RequestTaskTemplateModel { Id = secondId, Name = "Second", Type = "manual", Order = 2, DependsOn = [TaskId] }
            } }) };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-task-id]").Count));
        page.Find($"[data-task-id='{secondId}']").Click();
        page.FindComponents<MudTextField<string>>().Single(x => x.Instance.Label == "Task Name").Find("input").Input("Edited second");
        page.Find("button[aria-label='Move task up']").Click();
        var tasks = Field<List<RequestTaskTemplateModel>>(page.Instance, "Tasks");
        Assert.Equal(secondId, tasks[0].Id);
        Assert.Equal("Edited second", tasks[0].Name);
        Assert.Equal(new[] { TaskId }, tasks[0].DependsOn);
        Assert.Equal(secondId, Field<RequestTaskTemplateModel>(page.Instance, "selectedTask").Id);
        tasks[1].Name = " ";
        page.Find("#request-form-tab-1").Click();
        await InvokeAsync(page, "Save");
        Assert.Equal("true", page.Find("#request-form-tab-2").GetAttribute("aria-selected"));
        Assert.Equal(TaskId, Field<RequestTaskTemplateModel>(page.Instance, "selectedTask").Id);
        Assert.Contains("Every task must have a name", page.Find("[role='alert']").TextContent);
        Assert.Equal(0, api.FormCreates + api.FormUpdates);
    }

    [Fact]
    public async Task Duplicate_keeps_the_selected_position_and_delete_selects_the_neighbor()
    {
        var secondId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId,
            Schema = JsonSerializer.Serialize(new { fields = Array.Empty<object>(), tasks = new[] {
                new RequestTaskTemplateModel { Id = TaskId, Name = "Zulu", Type = "manual", Order = 1 },
                new RequestTaskTemplateModel { Id = secondId, Name = "Alpha", Type = "manual", Order = 2, DependsOn = [TaskId] }
            } }) };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-task-id]").Count));
        page.Find("button[aria-label='Duplicate task']").Click();
        var tasks = Field<List<RequestTaskTemplateModel>>(page.Instance, "Tasks");
        Assert.Equal(TaskId, tasks[0].Id);
        Assert.Equal("Zulu (Copy)", tasks[1].Name);
        Assert.NotEqual(TaskId, tasks[1].Id);
        Assert.Equal(secondId, tasks[2].Id);
        Assert.Equal(tasks[1].Id, Field<RequestTaskTemplateModel>(page.Instance, "selectedTask").Id);
        page.Find("button[aria-label='Delete task']").Click();
        Assert.Equal(secondId, Field<RequestTaskTemplateModel>(page.Instance, "selectedTask").Id);
        Assert.Equal(new[] { TaskId }, Field<List<RequestTaskTemplateModel>>(page.Instance, "Tasks")[1].DependsOn);
    }

    [Fact]
    public async Task Hidden_setup_and_field_errors_activate_their_tab_before_any_save()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        page.FindComponents<MudTextField<string>>().Single(x => x.Instance.Label == "Form Title").Find("input").Input("");
        await InvokeAsync(page, "Save");
        Assert.Equal("true", page.Find("#request-form-tab-0").GetAttribute("aria-selected"));
        page.FindComponents<MudTextField<string>>().Single(x => x.Instance.Label == "Form Title").Find("input").Input("Restored title");
        await InvokeAsync(page, "AddField");
        Field<List<FormField>>(page.Instance, "FormFields")[0].Label = "";
        await InvokeAsync(page, "Save");
        Assert.Equal("true", page.Find("#request-form-tab-1").GetAttribute("aria-selected"));
        Assert.Equal(0, api.FormCreates + api.FormUpdates);
    }

    [Theory]
    [InlineData("binding", "requires an External orchestration binding", false)]
    [InlineData("connection", "requires a current saved connection", false)]
    [InlineData("approval", "requires at least one approver", false)]
    [InlineData("runtime", "expected runtime must be at least one minute", false)]
    [InlineData("grace", "grace runtime cannot be negative", false)]
    [InlineData("sla", "SLA cannot be negative", true)]
    [InlineData("escalation", "escalation threshold cannot be negative", true)]
    [InlineData("escalation-after-sla", "Escalation threshold cannot exceed task SLA", true)]
    [InlineData("two-escalation-targets", "can only target one escalation target", true)]
    [InlineData("cycle", "Circular dependency detected", true)]
    public async Task Save_validates_unselected_task_constraints_before_writing_the_form(string invalidState, string expectedError, bool advanced)
    {
        var firstTaskId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId,
            Schema = JsonSerializer.Serialize(new { fields = Array.Empty<object>(), tasks = new[] {
                new RequestTaskTemplateModel { Id = firstTaskId, Name = "Valid first task", Type = "manual", Order = 1 },
                new RequestTaskTemplateModel { Id = TaskId, Name = "Hidden invalid task", Type = "automation", Order = 2 }
            } }) };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-task-id]").Count));
        var task = Field<List<RequestTaskTemplateModel>>(page.Instance, "Tasks").Single(x => x.Id == TaskId);
        task.Type = invalidState is "binding" or "connection" or "runtime" or "grace" ? "automation" : "manual";
        switch (invalidState)
        {
            case "binding":
                Field<IDictionary>(page.Instance, "automationBindings").Remove(TaskId);
                break;
            case "connection":
                var binding = Field<IDictionary>(page.Instance, "automationBindings")[TaskId]!;
                binding.GetType().GetProperty("SystemConnectionId")!.SetValue(binding, SecondId);
                break;
            case "approval": task.Type = "approval"; task.ApprovalApprovers.Clear(); break;
            case "runtime": task.ExpectedRuntimeMinutes = 0; break;
            case "grace": task.GraceRuntimeMinutes = -1; break;
            case "sla": task.TaskSlaMinutes = -1; break;
            case "escalation": task.EscalateAfterMinutes = -1; break;
            case "escalation-after-sla": task.TaskSlaMinutes = 5; task.EscalateAfterMinutes = 10; break;
            case "two-escalation-targets": task.EscalationUserId = "synthetic-user"; task.EscalationRole = "synthetic-role"; break;
            case "cycle": task.DependsOn = [firstTaskId]; Field<List<RequestTaskTemplateModel>>(page.Instance, "Tasks")[0].DependsOn = [TaskId]; break;
        }

        page.Find("#request-form-tab-1").Click();
        await InvokeAsync(page, "Save");

        Assert.Equal("true", page.Find("#request-form-tab-2").GetAttribute("aria-selected"));
        Assert.Equal(invalidState == "cycle" ? firstTaskId : TaskId, Field<RequestTaskTemplateModel>(page.Instance, "selectedTask").Id);
        Assert.Contains(expectedError, page.Find("[role='alert']").TextContent);
        Assert.Equal(advanced, Field<bool>(page.Instance, "advancedExpanded"));
        Assert.Equal(0, api.FormCreates + api.FormUpdates);
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
        public bool BindingEnabled { get; set; } = true;
        public bool HasBinding { get; set; } = true;
        public bool FailJobCreation { get; set; }
        public CreateOrchestrationCatalogRequestDefinitionDto? CreatedJob { get; private set; }
        public bool EmptyCatalogue { get; set; }
        public OrchestrationAutomationTargetDto? ModernTarget { get; set; }
        public int FormCreates { get; private set; }
        public int FormUpdates { get; private set; }
        public bool FailBindingUpdates { get; set; }
        public string? Schema { get; set; }
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
            else if (path == "/api/v1/request-forms" && request.Method == HttpMethod.Post)
            {
                FormCreates++;
                body = new RequestFormDto { Id = "saved-synthetic-form" };
            }
            else if (path == "/api/v1/request-forms/saved-synthetic-form" && request.Method == HttpMethod.Put)
            {
                FormUpdates++;
                body = new RequestFormDto { Id = "saved-synthetic-form" };
            }
            else if (path == "/api/v1/request-forms/synthetic-form") body = new RequestFormDto { Id = "synthetic-form", Title = "Synthetic form", OrganizationId = OrganizationId,
                JsonSchema = Schema ?? JsonSerializer.Serialize(new { fields = Array.Empty<object>(), tasks = new[] { new { id = TaskId, name = "Automate", type = "automation", order = 1 } } }) };
            else if (path == "/api/v1/admin/system-connections/") body = Rows;
            else if (path == "/api/v1/admin/orchestration/")
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                body = new OrchestrationConnectivitySettingsDto { Enabled = true, Source = "pairing", SourceKey = query["connectionId"], LocalTenantId = OrganizationId, PeerTenantId = MismatchPeerTenant ? "42" : "17" };
            }
            else if (path.EndsWith("/catalog/target-choices", StringComparison.Ordinal)) body = new[]
            { new OrchestrationAutomationTargetChoiceDto { Id = FirstId.ToString("D"), Name = "Synthetic worker", Kind = "explicitNodes", Target = new() { Source = 2, AgentIds = [FirstId.ToString("D")] } } };
            else if (path.EndsWith("/catalog/request-definitions", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                CreatedJob = await request.Content!.ReadFromJsonAsync<CreateOrchestrationCatalogRequestDefinitionDto>(cancellationToken);
                if (FailJobCreation) return new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new OrchestrationProviderErrorDto("create-job", 422, "target_unavailable", "Choose a current tenant target. Reference: synthetic-ref.", "synthetic-ref")) };
                body = new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = "created-job", OrchestrationJobDefinitionId = "created-job", RequestDefinitionName = CreatedJob!.Name, TenantId = 17 };
            }
            else if (path.EndsWith("/catalog/capabilities", StringComparison.Ordinal)) body = new OrchestrationAutomationCapabilitiesDto { ContractVersion = 2, Authoring = true, AggregateExecution = true };
            else if (path.EndsWith("/catalog/request-definitions", StringComparison.Ordinal) && EmptyCatalogue) body = Array.Empty<OrchestrationCatalogRequestDefinitionDto>();
            else if (path.EndsWith("/catalog/request-definitions", StringComparison.Ordinal)) body = new[] { new OrchestrationCatalogRequestDefinitionDto { RequestDefinitionId = "definition-17", TenantId = 17, RequestDefinitionName = "Synthetic target", ContractVersion = ModernTarget is null ? 1 : 2, Target = ModernTarget, ClientIdentity = ModernTarget is null ? "synthetic-client" : "" } };
            else if (path == "/api/v1/admin/orchestration/bindings/synthetic-binding" && request.Method == HttpMethod.Put)
            {
                if (FailBindingUpdates) return new(HttpStatusCode.BadRequest) { Content = new StringContent("") };
                var update = (await request.Content!.ReadFromJsonAsync<UpdateAutomationBindingDto>(cancellationToken))!;
                Updates.Add(update); StoredConnectionId = update.SystemConnectionId;
                BindingEnabled = update.Enabled ?? BindingEnabled;
                body = Binding();
            }
            else if (path == "/api/v1/admin/orchestration/bindings" && request.Method == HttpMethod.Post)
            {
                HasBinding = true;
                body = Binding();
            }
            else if (path.Contains("/bindings", StringComparison.Ordinal)) body = HasBinding ? new[] { Binding() } : Array.Empty<AutomationBindingDto>();
            else body = Array.Empty<object>();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
        private AutomationBindingDto Binding() => new() { Id = "synthetic-binding", RequestFormId = "synthetic-form", TaskTemplateId = TaskId, OrganizationId = OrganizationId,
            SystemConnectionId = StoredConnectionId, OrchestrationRequestDefinitionId = "definition-17", OrchestrationRequestDefinitionName = "Synthetic target", Enabled = BindingEnabled };
    }
}
