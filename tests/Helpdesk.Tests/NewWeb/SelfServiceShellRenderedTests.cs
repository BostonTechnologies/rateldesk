extern alias NewWeb;

using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shell = NewWeb::HelpDesk.NewWeb.Components.Layout.SelfServiceLayout;

namespace Helpdesk.Tests.NewWeb;

public sealed class SelfServiceShellRenderedTests
{
    [Theory]
    [InlineData("/self-service", "Service Catalog")]
    [InlineData("/self-service/folder?search=access", "Service Catalog")]
    [InlineData("/self-service/request/form", "Service Catalog")]
    [InlineData("/self-service/my-requests?q=laptop&page=3", "My Requests")]
    [InlineData("/self-service/my-requests/request-id", "My Requests")]
    public async Task Deep_links_select_the_owning_section_without_loading_another_section(string location, string expected)
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(location);
        var shell = context.Render<Shell>();
        Assert.Equal(2, shell.FindAll("nav[aria-label='Self Service sections'] a").Count);
        Assert.Equal(expected, shell.Find("a[aria-current='page']").TextContent.Trim());
        Assert.Empty(shell.FindAll("h1"));
    }

    [Fact]
    public async Task Section_links_retain_deliberate_list_queries_across_details_and_other_section_navigation()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/self-service/hardware?search=laptop");
        var shell = context.Render<Shell>();
        await shell.InvokeAsync(() => navigation.NavigateTo("/self-service/my-requests?q=access&page=3&pageSize=50"));
        await shell.InvokeAsync(() => navigation.NavigateTo("/self-service/my-requests/request-id"));
        shell.WaitForAssertion(() =>
        {
            Assert.Equal("/self-service/hardware?search=laptop", shell.FindAll("a")[0].GetAttribute("href"));
            Assert.Equal("/self-service/my-requests?q=access&page=3&pageSize=50", shell.FindAll("a")[1].GetAttribute("href"));
        });
        await shell.InvokeAsync(() => navigation.NavigateTo("/self-service/request/form-id"));
        shell.WaitForAssertion(() => Assert.Equal("Service Catalog", shell.Find("a[aria-current='page']").TextContent.Trim()));
        Assert.Equal("/self-service/hardware?search=laptop", shell.FindAll("a")[0].GetAttribute("href"));
    }
}
