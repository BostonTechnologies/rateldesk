using Bunit;
using Helpdesk.Shared.DTOs.Organization;
using Microsoft.AspNetCore.Components.Sections;
using MudBlazor;

namespace Helpdesk.Tests.NewWeb;

internal static class WorkspaceOrganizationSelection
{
    internal static IRenderedComponent<SectionOutlet> RenderHeader(BunitContext context)
    {
        context.Render<MudPopoverProvider>();
        return context.Render<SectionOutlet>(p => p.Add(x => x.SectionName, "workspace-context"));
    }

    internal static async Task SelectAsync(IRenderedComponent<SectionOutlet> header, string name, string id)
    {
        var picker = header.FindComponent<MudAutocomplete<OrganizationDto>>();
        await picker.InvokeAsync(async () =>
        {
            var matches = await picker.Instance.SearchFunc!(name, CancellationToken.None);
            await picker.Instance.ValueChanged.InvokeAsync(matches.Single(row => row.Id == id));
        });
    }
}
