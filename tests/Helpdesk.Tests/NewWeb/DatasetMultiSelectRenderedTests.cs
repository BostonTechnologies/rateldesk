extern alias NewWeb;
using Bunit;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Resources;
using MudBlazor.Services;
using Picker = NewWeb::HelpDesk.NewWeb.Components.Shared.DatasetMultiSelect;

namespace Helpdesk.Tests.NewWeb;

public sealed class DatasetMultiSelectRenderedTests
{
    [Fact]
    public async Task Checkbox_results_keep_existing_selections_and_allow_removal_outside_the_results()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var first = new DatasetOptionDto { Key = "fleet-1", Label = "Fleet" };
        var second = new DatasetOptionDto { Key = "fleet-2", Label = "Fleet" };
        var warehouse = new DatasetOptionDto { Key = "warehouse", Label = "Warehouse" };
        IReadOnlyList<DatasetOptionDto> selected = [first, second];
        var picker = context.Render<Picker>(parameters => parameters
            .Add(component => component.Label, "Groups")
            .Add(component => component.Selected, selected)
            .Add(component => component.SelectedChanged, values => selected = values)
            .Add(component => component.Search, (_, _, _) => Task.FromResult(new PagedResult<DatasetOptionDto>
            {
                Items = [warehouse], PageSize = 20, Total = 1
            })));

        picker.Find("button[aria-expanded]").Click();
        picker.Find("input[type=text]").Input("ware");
        picker.WaitForAssertion(() => Assert.Contains("warehouse", picker.Markup));
        picker.Find("input[type=checkbox]").Change(true);
        Assert.Equal(["fleet-1", "fleet-2", "warehouse"], selected.Select(option => option.Key));

        picker.Render(parameters => parameters.Add(component => component.Selected, selected));
        picker.Find("button[aria-label='Remove Fleet (fleet-1) from Groups']").Click();
        Assert.Equal(["fleet-2", "warehouse"], selected.Select(option => option.Key));
    }
}
