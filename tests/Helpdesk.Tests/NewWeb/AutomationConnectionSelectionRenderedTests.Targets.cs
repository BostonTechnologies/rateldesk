extern alias NewWeb;
using Helpdesk.Shared.DTOs.Orchestration;
using Bunit;
using MudBlazor;
using MudBlazor.Extensions;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using CreateTarget = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.CreateOrchestrationAutomationTargetDialog;
using Builder = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.RequestFormBuilder;
using Browser = NewWeb::HelpDesk.NewWeb.Components.Pages.Admin.Requests.OrchestrationScriptLibraryDialog;

namespace Helpdesk.Tests.NewWeb;

public sealed partial class AutomationConnectionSelectionRenderedTests
{
    [Fact]
    public async Task Empty_catalogue_keeps_browse_open_and_refresh_uses_the_exact_saved_connection()
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId, EmptyCatalogue = true };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>();
        await host.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<Builder>("Synthetic form",
            new DialogParameters { [nameof(Builder.ServiceId)] = "synthetic-service", [nameof(Builder.RequestFormId)] = "synthetic-form" }));
        var page = host.FindComponent<Builder>();
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        await InvokeAsync(page, "ToggleTaskAutomationConfiguration", TaskId);
        var browse = page.FindAll("button").Single(button => button.TextContent.Contains("Browse jobs", StringComparison.Ordinal));
        Assert.False(browse.HasAttribute("disabled"));
        browse.Click();
        var browser = host.FindComponent<Browser>();
        Assert.Contains("No NetRatel jobs match", browser.Markup);
        api.EmptyCatalogue = false;
        browser.FindAll("button").Single(button => button.TextContent.Contains("Refresh jobs", StringComparison.Ordinal)).Click();
        browser.WaitForAssertion(() => Assert.Contains("Synthetic target", browser.Markup));
        Assert.All(api.Paths.Where(path => path.Contains("/catalog/", StringComparison.Ordinal)), path => Assert.Contains("connectionId=" + FirstId, path));
    }

    [Fact]
    public async Task Job_creation_uses_a_real_target_and_content_and_preserves_the_entered_draft_on_provider_failure()
    {
        using var api = new FixtureApi { FailJobCreation = true };
        await using var context = Context(api);
        var host = context.Render<MudDialogProvider>();
        await host.InvokeAsync(() => context.Services.GetRequiredService<IDialogService>().ShowAsync<CreateTarget>("Create job",
            new DialogParameters { [nameof(CreateTarget.InitialJobName)] = "Synthetic authored job", [nameof(CreateTarget.OrganizationId)] = OrganizationId,
                [nameof(CreateTarget.SystemConnectionId)] = FirstId, [nameof(CreateTarget.TenantId)] = 17 }));
        var dialog = host.FindComponent<CreateTarget>();
        dialog.WaitForAssertion(() => Assert.False(dialog.FindAll("button").Single(button => button.TextContent.Contains("Create job", StringComparison.Ordinal)).HasAttribute("disabled")));
        var nodes = dialog.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "Nodes");
        await nodes.InvokeAsync(() => nodes.Instance.SelectOption(FirstId.ToString("D")));
        dialog.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Command").Find("textarea").Change("printf synthetic");
        dialog.FindAll("button").Single(button => button.TextContent.Contains("Create job", StringComparison.Ordinal)).Click();
        dialog.WaitForAssertion(() => Assert.Contains("target. Reference: synthetic-ref", dialog.Markup));
        Assert.Equal("Synthetic authored job", dialog.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Job name").Instance.GetState(field => field.Value));
        Assert.Equal("printf synthetic", dialog.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "Command").Instance.GetState(field => field.Value));
        Assert.Equal("", api.CreatedJob!.ClientIdentity);
        Assert.Equal(new[] { FirstId.ToString("D") }, api.CreatedJob.Target!.AgentIds);
        Assert.Equal("printf synthetic", api.CreatedJob.Command);
        Assert.Contains(api.Paths, path => path.EndsWith("connectionId=" + FirstId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("executionGroup")]
    [InlineData("explicitNodes")]
    public async Task Modern_target_summary_is_rendered_and_the_selected_connection_survives_binding_save(string kind)
    {
        using var api = new FixtureApi { Rows = [Connection(FirstId)], StoredConnectionId = FirstId,
            ModernTarget = new() { Version = 1, Kind = kind, Summary = kind == "executionGroup" ? "HA group: synthetic pool (one eligible worker)" : "5 selected nodes",
                Readiness = "requires-input", RequiredInputs = ["message"] } };
        await using var context = Context(api);
        var page = await RenderAsync(context);
        page.WaitForAssertion(() => Assert.True(Property<bool>(page.Instance, "CanCreateTenantScopedOrchestrationTarget")));
        await InvokeAsync(page, "ToggleTaskAutomationConfiguration", TaskId);
        Assert.Contains(api.ModernTarget.Summary, page.Markup);
        Assert.Contains("Required inputs: message", page.Markup);
        await InvokeAsync(page, "SyncBindingsAsync", "synthetic-form");
        var saved = Assert.Single(api.Updates);
        Assert.Equal(FirstId, saved.SystemConnectionId);
        Assert.Equal("definition-17", saved.OrchestrationRequestDefinitionId);
        Assert.Equal(FirstId, EditorConnection(page));
    }
}
