extern alias NewWeb;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.DTOs.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Editor = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Organization.EditOrganizationDialog;

namespace Helpdesk.Tests.NewWeb;

public sealed class OrganizationMappingDialogTests
{
    private static readonly DateTimeOffset LinkedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");

    [Theory]
    [InlineData("http", "Saved tenant")]
    [InlineData("circuit", "Saved tenant")]
    [InlineData("timeout", "Saved tenant")]
    [InlineData("missing", "Saved tenant")]
    [InlineData("missing", null)]
    [InlineData("renamed", "Saved tenant")]
    public async Task An_unrelated_edit_preserves_the_saved_mapping_when_the_remote_catalog_is_unavailable_or_missing(string mode, string? savedName)
    {
        using var api = new Api { Mode = mode };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>();
        IDialogReference? dialog = null;
        await host.InvokeAsync(async () => dialog = await context.Services.GetRequiredService<IDialogService>().ShowAsync<Editor>("Edit organization",
            new DialogParameters { [nameof(Editor.Organization)] = Organization(savedName) }));
        var editor = host.FindComponent<Editor>();
        editor.FindAll("[role=tab]").Single(tab => tab.TextContent.Contains("External orchestration", StringComparison.Ordinal)).Click();
        editor.WaitForAssertion(() => Assert.Contains(mode == "renamed" ? "Current catalog tenant" : savedName ?? "Saved tenant (name unavailable)", editor.Markup));
        Assert.DoesNotContain("private", editor.Markup);
        editor.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "General").Click();
        editor.FindAll("input").Single(input => input.GetAttribute("value") == "Synthetic organization").Change("Edited organization");
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();
        Assert.False((await dialog!.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Canceled);
        var saved = Assert.Single(api.Updates);
        Assert.Equal("Edited organization", saved.GetProperty("name").GetString());
        Assert.Equal(132, saved.GetProperty("orchestrationTenantId").GetInt32());
        Assert.Equal(mode == "renamed" ? "Current catalog tenant" : savedName, saved.GetProperty("orchestrationTenantName").GetString());
        Assert.Equal(LinkedAt, saved.GetProperty("orchestrationTenantLinkedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task An_empty_save_error_retains_the_dialog_mapping_and_edited_inputs_for_retry()
    {
        using var api = new Api { Mode = "http", RejectSave = true };
        await using var context = Context(api);
        var snackbars = context.Render<MudSnackbarProvider>();
        var host = context.Render<MudDialogProvider>();
        IDialogReference? dialog = null;
        await host.InvokeAsync(async () => dialog = await context.Services.GetRequiredService<IDialogService>().ShowAsync<Editor>("Edit organization",
            new DialogParameters { [nameof(Editor.Organization)] = Organization("Saved tenant") }));
        var editor = host.FindComponent<Editor>();
        editor.FindAll("input").Single(input => input.GetAttribute("value") == "Synthetic organization").Change("Retained edit");
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();
        editor.WaitForAssertion(() => Assert.Single(api.Updates));
        Assert.False(dialog!.Result.IsCompleted);
        Assert.Contains("Retained edit", editor.Markup);
        snackbars.WaitForAssertion(() => Assert.Contains("HTTP 409", snackbars.Markup));
        api.RejectSave = false;
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();
        Assert.False((await dialog.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Canceled);
        Assert.Equal(2, api.Updates.Count);
        Assert.All(api.Updates, saved => Assert.Equal("Saved tenant", saved.GetProperty("orchestrationTenantName").GetString()));
    }

    [Fact]
    public async Task Saving_an_unchanged_legacy_mapping_does_not_invent_a_link_timestamp()
    {
        using var api = new Api { Mode = "missing" };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>();
        var organization = Organization(null); organization.OrchestrationTenantLinkedAtUtc = null;
        IDialogReference? dialog = null;
        await host.InvokeAsync(async () => dialog = await context.Services.GetRequiredService<IDialogService>().ShowAsync<Editor>("Edit organization",
            new DialogParameters { [nameof(Editor.Organization)] = organization }));
        var editor = host.FindComponent<Editor>();
        editor.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();
        Assert.False((await dialog!.Result.WaitAsync(TimeSpan.FromSeconds(5)))!.Canceled);
        var saved = Assert.Single(api.Updates);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("orchestrationTenantName").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("orchestrationTenantLinkedAtUtc").ValueKind);
    }

    private static OrganizationDto Organization(string? name) => new()
    {
        Id = "synthetic-organization", Name = "Synthetic organization", IsEnabled = true,
        OrchestrationTenantId = 132, OrchestrationTenantName = name, OrchestrationTenantLinkedAtUtc = LinkedAt
    };
    private static BunitContext Context(Api api)
    {
        var context = new BunitContext(); context.Services.AddMudServices(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var authorization = context.AddAuthorization(); authorization.SetAuthorized("Synthetic administrator"); authorization.SetRoles("HelpdeskAdmin");
        context.Services.AddSingleton<IHttpClientFactory>(api); context.Render<MudPopoverProvider>(); return context;
    }
    private sealed class Api : HttpMessageHandler, IHttpClientFactory
    {
        public required string Mode { get; init; }
        public bool RejectSave { get; set; }
        public List<JsonElement> Updates { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://synthetic.example.invalid/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/admin/orchestration/catalog/tenants")
                return Mode switch
                {
                    "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private upstream failure") },
                    "circuit" => throw new BrokenCircuitException("private upstream circuit"),
                    "timeout" => throw new TimeoutRejectedException("private upstream timeout"),
                    _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Mode == "renamed"
                        ? new[] { new OrchestrationCatalogTenantDto { TenantId = 132, Name = "Current catalog tenant" } } : []) }
                };
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/api/v1/organizations/synthetic-organization")
            {
                Updates.Add(await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken));
                return new HttpResponseMessage(RejectSave ? HttpStatusCode.Conflict : HttpStatusCode.NoContent) { Content = new StringContent(string.Empty) };
            }
            throw new InvalidOperationException($"Unexpected synthetic request: {request.Method} {request.RequestUri!.AbsolutePath}");
        }
    }
}
