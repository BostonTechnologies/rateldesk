extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using MudBlazor.Extensions;
using CreateWizard = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Resources.CreateDatasetWizard;
using GraphWizard = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Resources.GraphSetupWizard;
using KeyDialog = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Resources.DatasetIngestionKeyDialog;
using SchemaDesigner = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Resources.DatasetSchemaDesigner;
using SchemaDraft = NewWeb::HelpDesk.NewWeb.Models.Resources.DatasetSchemaDraft;
using DatasetService = NewWeb::HelpDesk.NewWeb.Services.ResourceDatasetService;
using IDatasetService = NewWeb::HelpDesk.NewWeb.Services.IResourceDatasetService;

namespace Helpdesk.Tests.NewWeb;

public sealed class DataManagementWizardRenderedTests
{
    [Fact]
    public async Task Schema_rename_order_and_removal_keep_roles_and_require_valid_references()
    {
        using var api = new FixtureApi(); await using var context = Context(api);
        var draft = new SchemaDraft();
        var schema = context.Render<SchemaDesigner>(parameters => parameters.Add(x => x.Columns, draft.Columns));
        Assert.True(schema.Find("input[aria-label='Use id as key column']").HasAttribute("checked"));
        Assert.False(schema.Find("input[aria-label='Use name as key column']").HasAttribute("checked"));
        Assert.All(schema.FindAll("input[type='radio']"), radio => Assert.False(radio.HasAttribute("disabled")));
        schema.Find("input[aria-label='Use id as display column']").Change(true);
        Assert.True(draft.Columns[0].IsDisplay); Assert.False(draft.Columns[1].IsDisplay);
        schema.Find("input[aria-label='Use name as display column']").Change(true);
        var fields = schema.FindAll("input[aria-label='Column name']");
        Assert.Equal("id", fields[0].GetAttribute("value"));
        fields[0].Input("asset_id");
        Assert.True(draft.Columns[0].IsKey);
        Assert.Equal("asset_id", draft.ToCreateRequest("org").KeyColumn);
        schema.Find("button[aria-label='Move name up']").Click();
        Assert.Equal(["name", "asset_id"], draft.Columns.Select(x => x.Name));
        Assert.Equal([0, 1], draft.Columns.Select(x => x.SortOrder));
        schema.FindAll("input[aria-label='Column name']")[0].Input("ASSET_ID");
        Assert.Contains("unique", SchemaDraft.ValidateColumns(draft.Columns)["Columns[1].Name"]);
        schema.Find("button[aria-label='Remove column asset_id']").Click();
        Assert.Contains("KeyColumn", SchemaDraft.ValidateColumns(draft.Columns).Keys);
        Assert.Empty(api.Creates);
    }

    [Fact]
    public async Task Create_wizard_validates_retains_back_draft_and_creates_once_without_a_key()
    {
        using var api = new FixtureApi(); await using var context = Context(api);
        var (host, wizard) = await OpenAsync<CreateWizard>(context, new() { [nameof(CreateWizard.OrganizationId)] = "org", [nameof(CreateWizard.OrganizationName)] = "Synthetic organization" });
        host.WaitForAssertion(() => Assert.Equal("New custom dataset", host.Find("[role='dialog']").GetAttribute("aria-label")));
        Button(host, "Continue").Click();
        Assert.Contains("Enter a dataset name.", wizard.Markup);
        Assert.Empty(api.Creates);
        Input(wizard, "Dataset name").Change("Synthetic inventory");
        Button(host, "Continue").Click();
        Assert.Contains("Design schema", wizard.Markup);
        host.WaitForAssertion(() => Assert.Contains("Schema", host.Find("li[aria-current='step']").TextContent));
        Button(host, "Continue").Click();
        Assert.Contains("Review &amp; create", wizard.Markup);
        Assert.Empty(api.Creates);
        Button(host, "Back").Click(); Button(host, "Back").Click();
        Assert.Equal("Synthetic inventory", Input(wizard, "Dataset name").GetAttribute("value"));
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        await Button(host, "Create dataset").ClickAsync(new());
        wizard.WaitForAssertion(() => Assert.Contains("Skipping key creation", wizard.Markup));
        host.WaitForAssertion(() => Assert.Equal("Dataset created", host.Find("[role='dialog']").GetAttribute("aria-label")));
        var created = Assert.Single(api.Creates);
        Assert.Equal("org", created.OrganizationId);
        Assert.NotNull(created.RequestId);
        Assert.Equal("id", created.KeyColumn); Assert.Equal("name", created.DisplayColumn);
        Assert.Equal(["id", "name"], created.Columns.Select(x => x.Name));
        Assert.Empty(api.KeyCreates);
        Button(host, "Done").Click();
        Assert.Empty(api.KeyCreates);
    }

    [Fact]
    public async Task Uncertain_create_requires_reconciliation_and_reuses_request_identity()
    {
        using var api = new FixtureApi { RejectFirstCreate = true }; await using var context = Context(api);
        var (host, wizard) = await OpenAsync<CreateWizard>(context, new() { [nameof(CreateWizard.OrganizationId)] = "org" });
        Input(wizard, "Dataset name").Change("Retained inventory");
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        await Button(host, "Create dataset").ClickAsync(new());
        Assert.True(Button(host, "Retry create").HasAttribute("disabled"));
        Assert.Contains("retained", wizard.Markup, StringComparison.OrdinalIgnoreCase);
        await Button(host, "Check creation status").ClickAsync(new());
        Assert.False(Button(host, "Retry create").HasAttribute("disabled"));
        await Button(host, "Retry create").ClickAsync(new());
        Assert.Equal(2, api.Creates.Count);
        Assert.Equal(api.Creates[0].RequestId, api.Creates[1].RequestId);
        Assert.Equal("Retained inventory", api.Creates[1].Name);
        Assert.Single(api.StoredDatasets);
    }

    [Fact]
    public async Task Pending_create_disables_double_submission()
    {
        using var api = new FixtureApi { PendingCreate = new(TaskCreationOptions.RunContinuationsAsynchronously) }; await using var context = Context(api);
        var (host, wizard) = await OpenAsync<CreateWizard>(context, new() { [nameof(CreateWizard.OrganizationId)] = "org" });
        Input(wizard, "Dataset name").Change("One inventory");
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        var save = Button(host, "Create dataset").ClickAsync(new());
        wizard.WaitForAssertion(() => Assert.Single(api.Creates));
        var pendingAction = Button(host, "Retry create");
        Assert.True(pendingAction.HasAttribute("disabled"));
        await pendingAction.ClickAsync(new());
        Assert.Single(api.Creates);
        api.PendingCreate.SetResult(Json(new DatasetDefinitionDto { Id = "created", Name = "One inventory", OrganizationId = "org" }));
        await save;
        Assert.Single(api.Creates);
    }

    [Fact]
    public async Task Optional_named_key_renders_actual_one_time_secret_and_reports_clipboard_denial()
    {
        using var api = new FixtureApi(); await using var context = Context(api);
        context.JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(new Microsoft.JSInterop.JSException("Denied"));
        var (host, dialog) = await OpenAsync<KeyDialog>(context, new() { [nameof(KeyDialog.DatasetId)] = "created", [nameof(KeyDialog.DatasetName)] = "Synthetic inventory" });
        host.WaitForAssertion(() => Assert.Equal("Create ingestion API key", host.Find("[role='dialog']").GetAttribute("aria-label")));
        Input(dialog, "API key name").Blur();
        await Button(host, "Create key").ClickAsync(new());
        Assert.Contains("Enter a name for this integration.", dialog.Markup);
        Assert.Empty(api.KeyCreates);
        Input(dialog, "API key name").Change("Inventory integration");
        await Button(host, "Create key").ClickAsync(new());
        Assert.Equal("Inventory integration", Assert.Single(api.KeyCreates).Name);
        Assert.Equal("one-time-synthetic-key", Input(dialog, "One-time API key").GetAttribute("value"));
        await Button(host, "Copy API key").ClickAsync(new());
        Assert.Contains("copy it manually", dialog.Markup);
        Assert.Equal("one-time-synthetic-key", Assert.Single(context.JSInterop.Invocations, x => x.Identifier == "navigator.clipboard.writeText").Arguments[0]);
        Button(host, "Done").Click();
        host.WaitForAssertion(() => Assert.DoesNotContain("one-time-synthetic-key", host.Markup));
    }

    [Fact]
    public async Task Graph_test_is_explicit_and_changed_sources_invalidate_import_gate()
    {
        using var api = new FixtureApi(); await using var context = Context(api);
        var (host, wizard) = await OpenGraphAsync(context);
        host.WaitForAssertion(() => Assert.Equal("Microsoft Graph setup", host.Find("[role='dialog']").GetAttribute("aria-label")));
        Assert.Equal("synthetic-tenant", Input(wizard, "Tenant ID").GetAttribute("value"));
        Assert.Equal("synthetic-client", Input(wizard, "Application / client ID").GetAttribute("value"));
        Assert.Equal("", Input(wizard, "Client secret").GetAttribute("value") ?? "");
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        host.WaitForAssertion(() => Assert.Contains("Test access", host.Find("li[aria-current='step']").TextContent));
        Assert.Empty(api.Tests); Assert.Empty(api.Imports); Assert.Empty(api.SettingsSaves);
        Assert.True(Button(host, "Continue").HasAttribute("disabled"));
        await Button(host, "Test connection").ClickAsync(new());
        Assert.Single(api.Tests); Assert.Empty(api.Imports); Assert.Empty(api.SettingsSaves);
        Assert.False(Button(host, "Continue").HasAttribute("disabled"));
        Button(host, "Back").Click();
        wizard.FindComponents<MudCheckBox<bool>>().Single(x => x.Instance.Label == "Groups").Find("input").Change(true);
        Button(host, "Continue").Click();
        Assert.True(Button(host, "Continue").HasAttribute("disabled"));
        Assert.DoesNotContain("Read access passed", wizard.Markup);
        await Button(host, "Test connection").ClickAsync(new());
        Button(host, "Continue").Click();
        Assert.Empty(api.Imports);
        Assert.Contains("removes records absent", wizard.Markup);
        await Button(host, "Save and import").ClickAsync(new());
        var imported = Assert.Single(api.Imports);
        Assert.True(imported.Connection.UseStoredSecret); Assert.Null(imported.Connection.ClientSecret);
        Assert.True(imported.Connection.EnableUsers); Assert.True(imported.Connection.EnableGroups);
        Assert.True(imported.PreserveExistingSchedule);
        Assert.Equal("verified-current-inputs", imported.TestToken);
    }

    [Fact]
    public async Task Graph_failed_access_retains_draft_and_requires_correction_before_import()
    {
        using var api = new FixtureApi { DenySource = true }; await using var context = Context(api);
        var (host, wizard) = await OpenGraphAsync(context);
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        await Button(host, "Test connection").ClickAsync(new());
        Assert.Contains("Read access failed", wizard.Markup);
        Assert.True(Button(host, "Continue").HasAttribute("disabled"));
        Button(host, "Back").Click(); Button(host, "Back").Click();
        Assert.Equal("synthetic-tenant", Input(wizard, "Tenant ID").GetAttribute("value"));
        Assert.Empty(api.Imports); Assert.Empty(api.SettingsSaves); Assert.Empty(api.Creates); Assert.Empty(api.KeyCreates);
    }

    [Fact]
    public async Task Graph_partial_retry_preserves_successful_source_selection_and_schedule()
    {
        using var api = new FixtureApi { PartialImport = true }; await using var context = Context(api);
        var (host, wizard) = await OpenGraphAsync(context, groups: true);
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        await Button(host, "Test connection").ClickAsync(new()); Button(host, "Continue").Click();
        await Button(host, "Save and import").ClickAsync(new());
        Assert.Contains("PartiallySucceeded", wizard.Markup);
        await Button(host, "Retest selected sources").ClickAsync(new());
        Assert.True(wizard.FindComponents<MudCheckBox<bool>>().Single(x => x.Instance.Label == "Users").Instance.GetState(x => x.Value));
        Assert.True(wizard.FindComponents<MudCheckBox<bool>>().Single(x => x.Instance.Label == "Groups").Instance.GetState(x => x.Value));
        Button(host, "Continue").Click();
        Assert.True(Button(host, "Continue").HasAttribute("disabled"));
        Assert.Single(api.Imports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Graph_uncertain_import_reconciles_without_another_import(bool timeout)
    {
        using var api = new FixtureApi { LoseImportResponse = true, TimeoutImportResponse = timeout }; await using var context = Context(api);
        var (host, wizard) = await OpenGraphAsync(context);
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        await Button(host, "Test connection").ClickAsync(new()); Button(host, "Continue").Click();
        await Button(host, "Save and import").ClickAsync(new());
        Assert.Contains("completion is uncertain", wizard.Markup);
        await Button(host, "Check import status").ClickAsync(new());
        Assert.Contains("Succeeded", wizard.Markup);
        Assert.Single(api.Imports);
    }

    [Fact]
    public async Task Explicit_save_without_import_preserves_schedule_and_does_not_need_an_access_test()
    {
        using var api = new FixtureApi(); await using var context = Context(api);
        var (host, _) = await OpenGraphAsync(context, saveWithoutImport: true);
        Button(host, "Continue").Click(); Button(host, "Continue").Click(); Button(host, "Continue").Click();
        await Button(host, "Save connection only").ClickAsync(new());
        var saved = Assert.Single(api.SettingsSaves);
        Assert.True(saved.BackgroundSyncEnabled); Assert.Equal("0 */6 * * *", saved.BackgroundSyncCronExpression);
        Assert.Null(saved.ClientSecret); Assert.Empty(api.Tests); Assert.Empty(api.Imports);
    }

    [Fact]
    public async Task Reopened_interrupted_result_exposes_reconciliation_before_testing_or_importing()
    {
        using var api = new FixtureApi(); await using var context = Context(api);
        var recorded = new GraphDatasetImportStatusDto { RequestId = Guid.NewGuid(), OrganizationId = "org", ConfigurationSaved = true, Status = "Interrupted", CanReconcile = true };
        var (host, wizard) = await OpenGraphAsync(context, initialStatus: recorded);
        Assert.Contains("Interrupted", wizard.Markup);
        Assert.NotNull(Button(host, "Check import status"));
        await Button(host, "Reconcile interrupted import").ClickAsync(new());
        Assert.Contains("reconciled without importing", wizard.Markup);
        Assert.Empty(api.Tests); Assert.Empty(api.Imports); Assert.Empty(api.SettingsSaves);
    }

    [Fact]
    public async Task Configuration_failure_returns_to_the_original_transient_secret_draft()
    {
        using var api = new FixtureApi { FailConfiguration = true }; await using var context = Context(api);
        var (host, wizard) = await OpenGraphAsync(context, storedSecret: false);
        Input(wizard, "Client secret").Change("synthetic-draft-secret");
        Button(host, "Continue").Click(); Button(host, "Continue").Click();
        await Button(host, "Test connection").ClickAsync(new()); Button(host, "Continue").Click();
        await Button(host, "Save and import").ClickAsync(new());
        await Button(host, "Return to draft").ClickAsync(new());
        Assert.Equal("synthetic-draft-secret", Input(wizard, "Client secret").GetAttribute("value"));
        Assert.Equal("synthetic-tenant", Input(wizard, "Tenant ID").GetAttribute("value"));
        Assert.Single(api.Imports); Assert.Empty(api.SettingsSaves);
    }

    private static BunitContext Context(FixtureApi api)
    {
        var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.Services.AddMudServices();
        context.Services.AddSingleton<IHttpClientFactory>(api); context.Services.AddScoped<IDatasetService, DatasetService>();
        context.Render<MudPopoverProvider>();
        return context;
    }
    private static async Task<(IRenderedComponent<MudDialogProvider> Host, IRenderedComponent<T> Dialog)> OpenAsync<T>(BunitContext context, DialogParameters parameters) where T : class, IComponent
    {
        var host = context.Render<MudDialogProvider>();
        await host.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<T>("Synthetic dialog", parameters, new() { BackdropClick = false }));
        host.WaitForAssertion(() => Assert.Single(host.FindComponents<T>()));
        return (host, host.FindComponent<T>());
    }
    private static Task<(IRenderedComponent<MudDialogProvider> Host, IRenderedComponent<GraphWizard> Dialog)> OpenGraphAsync(BunitContext context, bool groups = false, bool saveWithoutImport = false, GraphDatasetImportStatusDto? initialStatus = null, bool storedSecret = true)
        => OpenAsync<GraphWizard>(context, new()
        {
            [nameof(GraphWizard.OrganizationName)] = "Synthetic organization",
            [nameof(GraphWizard.SaveWithoutImport)] = saveWithoutImport,
            [nameof(GraphWizard.InitialImportStatus)] = initialStatus,
            [nameof(GraphWizard.Settings)] = new TenantGraphDatasetSettingsDto
            {
                OrganizationId = "org", TenantId = "synthetic-tenant", ClientId = "synthetic-client", HasStoredSecret = storedSecret,
                EnableUsers = true, EnableGroups = groups, BackgroundSyncEnabled = true, BackgroundSyncCronExpression = "0 */6 * * *"
            }
        });
    private static IElement Button(IRenderedComponent<MudDialogProvider> fragment, string label) => fragment.FindAll("button").Single(element => element.TextContent.Trim() == label);
    private static IElement Input<T>(IRenderedComponent<T> component, string label) where T : class, IComponent
        => component.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == label).Find("input");
    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };

    private sealed class FixtureApi : HttpMessageHandler, IHttpClientFactory
    {
        public List<CreateDatasetDefinitionDto> Creates { get; } = [];
        public List<DatasetDefinitionDto> StoredDatasets { get; } = [];
        public List<CreateDatasetIngestCredentialDto> KeyCreates { get; } = [];
        public List<GraphConnectionTestRequestDto> Tests { get; } = [];
        public List<GraphDatasetImportRequestDto> Imports { get; } = [];
        public List<TenantGraphDatasetSettingsDto> SettingsSaves { get; } = [];
        public bool RejectFirstCreate { get; init; }
        public bool DenySource { get; init; }
        public bool PartialImport { get; init; }
        public bool LoseImportResponse { get; init; }
        public bool TimeoutImportResponse { get; init; }
        public bool FailConfiguration { get; init; }
        public TaskCompletionSource<HttpResponseMessage>? PendingCreate { get; init; }
        private GraphDatasetImportStatusDto? status;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://desk.example.test/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/resources/datasets" && request.Method == HttpMethod.Post)
            {
                var dto = (await request.Content!.ReadFromJsonAsync<CreateDatasetDefinitionDto>(cancellationToken))!; Creates.Add(dto);
                if (PendingCreate is not null) return await PendingCreate.Task;
                if (RejectFirstCreate && Creates.Count == 1) return Json(new { }, HttpStatusCode.ServiceUnavailable);
                var dataset = new DatasetDefinitionDto { Id = "created", OrganizationId = dto.OrganizationId, Name = dto.Name, KeyColumn = dto.KeyColumn, DisplayColumn = dto.DisplayColumn, Columns = dto.Columns };
                StoredDatasets.Add(dataset); return Json(dataset);
            }
            if (path.Contains("/create-requests/", StringComparison.Ordinal)) return StoredDatasets.Count == 0 ? Json(new { }, HttpStatusCode.NotFound) : Json(StoredDatasets[0]);
            if (path.EndsWith("/credentials", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                KeyCreates.Add((await request.Content!.ReadFromJsonAsync<CreateDatasetIngestCredentialDto>(cancellationToken))!);
                return Json(new DatasetIngestCredentialCreatedDto { PlainTextKey = "one-time-synthetic-key" });
            }
            if (path.EndsWith("/graph-settings/test", StringComparison.Ordinal))
            {
                var dto = (await request.Content!.ReadFromJsonAsync<GraphConnectionTestRequestDto>(cancellationToken))!; Tests.Add(dto);
                var sources = new List<GraphDatasetAccessResultDto>();
                if (dto.EnableUsers) sources.Add(new() { SourceType = DatasetSourceType.GraphUsers, Succeeded = !DenySource, Outcome = DenySource ? GraphAccessOutcome.AccessDenied : GraphAccessOutcome.Succeeded, Message = DenySource ? "Permission required." : "Read passed." });
                if (dto.EnableGroups) sources.Add(new() { SourceType = DatasetSourceType.GraphGroups, Succeeded = true, Message = "Read passed." });
                return Json(new GraphConnectionTestResultDto { AuthenticationSucceeded = true, Sources = sources, TestToken = DenySource ? null : "verified-current-inputs", TestedAtUtc = DateTimeOffset.UtcNow });
            }
            if (path.EndsWith("/graph-settings/import", StringComparison.Ordinal))
            {
                var dto = (await request.Content!.ReadFromJsonAsync<GraphDatasetImportRequestDto>(cancellationToken))!; Imports.Add(dto);
                if (FailConfiguration) return Json(new GraphDatasetImportStatusDto { RequestId = dto.RequestId, OrganizationId = "org", Status = "Failed", ConfigurationSaved = false, CompletedAtUtc = DateTimeOffset.UtcNow });
                status = new() { RequestId = dto.RequestId, OrganizationId = "org", ConfigurationSaved = true, Status = PartialImport ? "PartiallySucceeded" : "Succeeded", CompletedAtUtc = DateTimeOffset.UtcNow, Sources = [new() { SourceType = DatasetSourceType.GraphUsers, Status = "Succeeded", RowCount = 0 }] };
                if (dto.Connection.EnableGroups) status.Sources.Add(new() { SourceType = DatasetSourceType.GraphGroups, Status = PartialImport ? "Failed" : "Succeeded", Message = PartialImport ? "Source unavailable." : null });
                if (LoseImportResponse) { if (TimeoutImportResponse) throw new TaskCanceledException("Synthetic transport timeout."); throw new HttpRequestException("Synthetic transport loss."); }
                return Json(status);
            }
            if (path.EndsWith("/import-status", StringComparison.Ordinal)) return status is null ? Json(new { }, HttpStatusCode.NotFound) : Json(status);
            if (path.EndsWith("/reconcile", StringComparison.Ordinal)) return Json(new GraphDatasetImportStatusDto { OrganizationId = "org", ConfigurationSaved = true, Status = "Interrupted", CompletedAtUtc = DateTimeOffset.UtcNow });
            if (path.EndsWith("/graph-settings", StringComparison.Ordinal))
            {
                if (FailConfiguration && request.Method == HttpMethod.Get) return Json(new { }, HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Put) SettingsSaves.Add((await request.Content!.ReadFromJsonAsync<TenantGraphDatasetSettingsDto>(cancellationToken))!);
                return Json(new TenantGraphDatasetSettingsDto { OrganizationId = "org", TenantId = "synthetic-tenant", ClientId = "synthetic-client", HasStoredSecret = true, EnableUsers = true, EnableGroups = true, BackgroundSyncEnabled = true, BackgroundSyncCronExpression = "0 */6 * * *" });
            }
            return Json(new { });
        }
    }
}
