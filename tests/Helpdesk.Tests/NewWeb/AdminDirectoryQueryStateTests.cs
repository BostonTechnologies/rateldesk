extern alias NewWeb;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NewWeb::HelpDesk.NewWeb.Models;

namespace Helpdesk.Tests.NewWeb;

public sealed class AdminDirectoryQueryStateTests
{
    [Fact]
    public void Full_query_survives_navigation_and_preserves_delegated_route_context()
    {
        using var context = new BunitContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/admin/organizations?section=members&organizationId=selected&search=long%20name&enabled=false&page=5&pageSize=100&sort=contact&descending=true");
        var query = new AdminDirectoryQueryState("enabled", "linked", "supportOrganizationId");
        Assert.True(query.Read(navigation.Uri));
        Assert.Equal("long name", query.Search); Assert.Equal(5, query.Page); Assert.Equal(100, query.PageSize);
        Assert.Equal("false", query.Get("enabled")); Assert.Equal(SortDirection.Descending, query.Direction("contact"));
        query.Set("linked", "true");
        Assert.Equal(0, query.Page);
        query.Remember(navigation);
        Assert.Contains("section=members", navigation.Uri); Assert.Contains("organizationId=selected", navigation.Uri);
        Assert.Contains("linked=true", navigation.Uri); Assert.DoesNotContain("page=5", navigation.Uri);
        var restored = new AdminDirectoryQueryState("enabled", "linked", "supportOrganizationId");
        restored.Read(navigation.Uri);
        Assert.Equal(query.Search, restored.Search); Assert.Equal(query.Sort, restored.Sort); Assert.Equal(query.PageSize, restored.PageSize);
        Assert.Equal("false", restored.Get("enabled")); Assert.Equal("true", restored.Get("linked"));
        var api = restored.ApiUrl("api/v1/admin/directories/organizations", new TableState { Page = 1, PageSize = 50, SortLabel = "contact", SortDirection = SortDirection.Descending });
        Assert.Contains("page=1", api); Assert.Contains("pageSize=50", api); Assert.Contains("enabled=false", api);
        Assert.DoesNotContain("section=", api); Assert.DoesNotContain("organizationId=selected", api);
    }
}
