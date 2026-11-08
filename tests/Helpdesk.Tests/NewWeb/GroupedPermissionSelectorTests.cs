extern alias NewWeb;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Selector = NewWeb::HelpDesk.NewWeb.Components.Shared.GroupedPermissionSelector;

namespace Helpdesk.Tests.NewWeb;

public sealed class GroupedPermissionSelectorTests
{
    [Fact]
    public void Family_selection_reports_mixed_state_and_search_preserves_hidden_choices()
    {
        using var context = new BunitContext();
        var state = new Selection { Values = ["Incident.Read"] };
        var page = context.Render<Harness>(p => p.Add(x => x.State, state));
        Assert.Equal("mixed", page.Find("button[aria-label='Select all Incidents permissions']").GetAttribute("aria-checked"));
        page.Find("button[aria-label='Select all Incidents permissions']").Click();
        Assert.Equal(new[] { "Incident.Read", "Incident.Write" }, state.Values);
        Assert.Equal("true", page.Find("button[aria-label='Select all Incidents permissions']").GetAttribute("aria-checked"));
        page.Find("input[type=search]").Input("Requests");
        page.FindAll("button").Single(x => x.TextContent == "Select shown permissions").Click();
        Assert.Equal(new[] { "Incident.Read", "Incident.Write", "Request.Read", "Request.Write" }, state.Values);
        Assert.Equal(4, page.FindAll("input[type=hidden]").Count);
        page.Find("button[aria-label='Select all Requests permissions']").Click();
        Assert.Equal(new[] { "Incident.Read", "Incident.Write" }, state.Values);
        page.Find("input[type=search]").Input("no matching permissions");
        Assert.Contains("Your selection is retained", page.Markup);
        Assert.Equal(2, page.FindAll("input[type=hidden]").Count);
    }

    [Fact]
    public void Family_action_selects_the_full_named_family_even_when_search_filters_its_rows()
    {
        using var context = new BunitContext();
        var state = new Selection();
        var page = context.Render<Harness>(p => p.Add(x => x.State, state));
        page.Find("input[type=search]").Input("Incident.Read");
        Assert.Single(page.FindAll("input[type=checkbox]"));
        page.Find("button[aria-label='Select all Incidents permissions']").Click();
        Assert.Equal(new[] { "Incident.Read", "Incident.Write" }, state.Values);
    }

    [Fact]
    public void Authority_reduction_removes_hidden_submissions_and_disabled_actions_do_not_emit_changes()
    {
        using var context = new BunitContext();
        var calls = 0;
        var page = context.Render<Selector>(p => p.Add(x => x.EligiblePermissions, new[] { "Incident.Read", "Incident.Write", "HelpdeskAdmin" })
            .Add(x => x.SelectedValues, new[] { "Incident.Read", "Incident.Write", "HelpdeskAdmin", "*" })
            .Add(x => x.FormFieldName, "permissions").Add(x => x.SelectedValuesChanged, _ => calls++));
        page.Render(p => p.Add(x => x.EligiblePermissions, new[] { "Incident.Read" }).Add(x => x.Disabled, true));
        Assert.Single(page.FindAll("input[type=hidden]"));
        Assert.Equal("Incident.Read", page.Find("input[type=hidden]").GetAttribute("value"));
        Assert.DoesNotContain("Incident.Write", page.Markup);
        Assert.DoesNotContain("HelpdeskAdmin", page.Markup);
        Assert.All(page.FindAll("button"), button => Assert.True(button.HasAttribute("disabled")));
        page.Find("button[role=checkbox]").Click();
        Assert.Equal(0, calls);
    }

    public sealed class Selection { public string[] Values { get; set; } = []; }
    public sealed class Harness : ComponentBase
    {
        [Parameter] public Selection State { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<Selector>(0);
            builder.AddAttribute(1, nameof(Selector.EligiblePermissions), new[] { "Incident.Read", "Incident.Write", "Request.Read", "Request.Write", "HelpdeskAdmin" });
            builder.AddAttribute(2, nameof(Selector.SelectedValues), State.Values);
            builder.AddAttribute(3, nameof(Selector.SelectedValuesChanged), EventCallback.Factory.Create<IEnumerable<string>>(this, values => State.Values = values.ToArray()));
            builder.AddAttribute(4, nameof(Selector.FormFieldName), "permissions");
            builder.CloseComponent();
        }
    }
}
