using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Categories;
using Helpdesk.API.Endpoints.Dashboard;
using Helpdesk.API.Endpoints.Services;
using Helpdesk.API.Services;
using Helpdesk.Application.Dashboard;
using Helpdesk.Application.Events;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Resources;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Resources;
using System.Text.Json;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.DTOs.Service;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using System.Net;

namespace Helpdesk.Tests.Api;

public sealed class ServiceItemsEndpointsTests
{
    [Fact]
    public async Task Form_authoring_saves_a_multi_select_list_and_rejects_invalid_modes_and_conditions()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(isHelpdeskAdmin: true);
        const string valid = """
            {"fields":[{"key":"groups","label":"Groups","type":"predefined","multiSelect":true,"predefinedOptions":["Fleet","Warehouse"]}]}
            """;
        using var saved = await harness.Client.PutAsJsonAsync("/api/v1/request-forms/form-root", new { jsonSchema = valid });
        saved.EnsureSuccessStatusCode();
        var form = await harness.Client.GetFromJsonAsync<RequestFormDto>("/api/v1/request-forms/form-root");
        using var schema = JsonDocument.Parse(form!.JsonSchema);
        var field = Assert.Single(new RequestFormSchemaParser().Parse(schema).Fields);
        Assert.True(field.MultiSelect);
        Assert.Equal("groups", field.Key);

        string[] invalid =
        [
            valid.Replace("predefined", "number", StringComparison.Ordinal),
            """{"fields":[{"label":"Groups","type":"text","multiSelect":true}]}""",
            """{"fields":[{"label":"Groups","type":"text","multiSelect":true,"dataBinding":{"datasetId":"groups","allowFreeText":true}}]}""",
            """{"fields":[{"label":"Groups","type":"predefined","multiSelect":"yes"}]}""",
            """{"fields":[{"key":"groups","type":"predefined","multiSelect":true}],"tasks":[{"name":"Manual","conditionExpression":"payload.groups == 1"}]}"""
        ];
        foreach (var configuration in invalid)
        {
            using var rejected = await harness.Client.PutAsJsonAsync("/api/v1/request-forms/form-root", new { jsonSchema = configuration });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        var retained = await harness.Client.GetFromJsonAsync<RequestFormDto>("/api/v1/request-forms/form-root");
        Assert.Equal(valid, retained!.JsonSchema);
    }

    [Fact]
    public async Task Service_create_update_get_and_list_preserve_organization_audience()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(isHelpdeskAdmin: true);
        var created = await harness.Client.PostAsJsonAsync("/api/v1/services", new CreateServiceDto { Name = "Scoped", AllowedOrganizationIds = ["tenant-1"] });
        created.EnsureSuccessStatusCode();
        var service = (await created.Content.ReadFromJsonAsync<ServiceDto>())!;
        Assert.Equal(["tenant-1"], service.AllowedOrganizationIds);
        var update = await harness.Client.PutAsJsonAsync($"/api/v1/services/{service.Id}", new { name = "Updated", allowedOrganizationIds = new[] { "tenant-2" } });
        update.EnsureSuccessStatusCode();
        Assert.Equal(["tenant-2"], (await update.Content.ReadFromJsonAsync<ServiceDto>())!.AllowedOrganizationIds);
        Assert.Equal(["tenant-2"], (await harness.Client.GetFromJsonAsync<ServiceDto>($"/api/v1/services/{service.Id}"))!.AllowedOrganizationIds);
        var list = await harness.Client.GetFromJsonAsync<List<ServiceDto>>("/api/v1/services");
        Assert.Equal(["tenant-2"], list!.Single(x => x.Id == service.Id).AllowedOrganizationIds);
        Beta9Evidence.Record("API-B8-SERVICE-SCOPE-PROJECTION", "http-crud", new
        {
            createdAudience = service.AllowedOrganizationIds,
            updatedAudience = list.Single(x => x.Id == service.Id).AllowedOrganizationIds,
            checkedResponses = new[] { "create", "update", "get", "list" }
        });
    }

    [Fact]
    public async Task Folder_audience_edits_leave_content_and_parent_until_an_explicit_move()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(isHelpdeskAdmin: true);
        using var clear = await harness.Client.PutAsJsonAsync("/api/v1/services/child-a", new { allowedOrganizationIds = Array.Empty<string>() });
        clear.EnsureSuccessStatusCode();
        var child = (await harness.Client.GetFromJsonAsync<ServiceDto>("/api/v1/services/child-a"))!;
        Assert.Empty(child.AllowedOrganizationIds);
        Assert.Equal("root-a", child.ParentServiceId);
        Assert.Equal("Visible child", child.Description);
        Assert.Equal("Child A", child.Name);
        using var invalid = await harness.Client.PutAsJsonAsync("/api/v1/services/root-a", new { parentId = "child-a" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var move = await harness.Client.PutAsJsonAsync("/api/v1/services/child-a", new UpdateServiceDto { ParentIdHasValue = true });
        move.EnsureSuccessStatusCode();
        Assert.Null((await move.Content.ReadFromJsonAsync<ServiceDto>())!.ParentServiceId);
    }

    [Fact]
    public async Task Request_form_edit_preserves_omitted_audience_and_accepts_explicit_clear()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(isHelpdeskAdmin: true);
        using var configured = await harness.Client.PutAsJsonAsync("/api/v1/request-forms/form-root", new { allowedOrganizationIds = new[] { "tenant-1", "tenant-2" } });
        configured.EnsureSuccessStatusCode();
        using var renamed = await harness.Client.PutAsJsonAsync("/api/v1/request-forms/form-root", new { title = "Reviewed title" });
        renamed.EnsureSuccessStatusCode();
        var retained = await harness.Client.GetFromJsonAsync<RequestFormDto>("/api/v1/request-forms/form-root");
        Assert.Equal("Reviewed title", retained!.Title);
        Assert.Equal(["tenant-1", "tenant-2"], retained.AllowedOrganizationIds);
        using var cleared = await harness.Client.PutAsJsonAsync("/api/v1/request-forms/form-root", new { allowedOrganizationIds = Array.Empty<string>() });
        cleared.EnsureSuccessStatusCode();
        var after = await harness.Client.GetFromJsonAsync<RequestFormDto>("/api/v1/request-forms/form-root");
        Assert.Empty(after!.AllowedOrganizationIds);
        Assert.Equal("tenant-1", after.OrganizationId);
        using var reopenedSave = await harness.Client.PutAsJsonAsync("/api/v1/request-forms/form-root",
            new { organizationId = after.OrganizationId, description = "Updated after reopening", allowedOrganizationIds = after.AllowedOrganizationIds });
        reopenedSave.EnsureSuccessStatusCode();
        Assert.Empty((await reopenedSave.Content.ReadFromJsonAsync<RequestFormDto>())!.AllowedOrganizationIds);
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "SelfService");
        using var hidden = await harness.Client.GetAsync("/api/v1/request-forms/form-root");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task MCP_service_search_maps_query_alias_to_real_q_filter_and_authorized_totals()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(customerId: "customer-1");
        var ignored = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>("/api/v1/service-items/search?query=needle&pageSize=25");
        var expected = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>("/api/v1/service-items/search?q=needle&pageSize=25");
        Assert.NotEmpty(expected!.Items);
        Assert.True(ignored!.TotalCount > expected.TotalCount);
        var tools = Helpdesk.Tests.Mcp.TicketAdapterHttpTests.Tools(harness.Client, "SelfService.User");
        foreach (var alias in new[] { "query", "q" })
        {
            var result = await tools.helpdesk_services("search_items", System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object> { [alias] = "needle", ["pageSize"] = 25, ["includeTotal"] = true }));
            Assert.True(result.Success, result.Summary);
            var actual = System.Text.Json.JsonSerializer.Deserialize<PagedResponse<ServiceItemDto>>(result.Data!.ToJsonString(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.Equal(expected.TotalCount, actual!.TotalCount);
            Assert.Equal(expected.Items.Select(item => item.Id).Order(), actual.Items.Select(item => item.Id).Order());
            Assert.DoesNotContain(actual.Items, item => item.Id is "form-hidden" or "form-customer-two" or "form-customer-parent");
            var noMatch = await tools.helpdesk_services("search_items", System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object> { [alias] = "campaign-impossible-search", ["pageSize"] = 25 }));
            Assert.Equal(0, noMatch.Data!["totalCount"]!.GetValue<int>());
            Assert.Empty(noMatch.Data!["items"]!.AsArray());
        }
    }

    [Fact]
    public async Task GetServiceItems_ReturnsRecursiveVisibleRequestCounts_ForServiceFolders()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync();

        var rootResponse = await harness.Client.GetFromJsonAsync<List<ServiceItemDto>>("/api/v1/service-items");
        var nestedResponse = await harness.Client.GetFromJsonAsync<List<ServiceItemDto>>("/api/v1/service-items/root-a");

        Assert.NotNull(rootResponse);
        var rootService = Assert.Single(rootResponse!, item => item.Id == "root-a");
        Assert.Equal(ServiceItemType.Service, rootService.ItemType);
        Assert.Equal(3, rootService.AvailableRequestCount);
        Assert.DoesNotContain(rootResponse!, item => item.Id == "root-b");

        Assert.NotNull(nestedResponse);
        var childService = Assert.Single(nestedResponse!, item => item.Id == "child-a");
        Assert.Equal(2, childService.AvailableRequestCount);
        Assert.Contains(nestedResponse!, item => item.Id == "form-root" && item.ItemType == ServiceItemType.RequestForm);
        Assert.DoesNotContain(nestedResponse!, item => item.Id == "form-testing");
    }

    [Fact]
    public async Task SearchServiceItems_ReturnsServicesAndRequestForms()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(isHelpdeskAdmin: true);

        var response = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>(
            "/api/v1/service-items/search?pageSize=25");

        Assert.NotNull(response);
        Assert.Equal(1, response!.Page);
        Assert.Equal(25, response.PageSize);
        Assert.Contains(response.Items, item => item.Id == "root-a" && item.ItemType == ServiceItemType.Service);
        Assert.Contains(response.Items, item => item.Id == "form-root" && item.ItemType == ServiceItemType.RequestForm);
        Assert.Contains(response.Items, item => item.Id == "root-b" && item.ItemType == ServiceItemType.Service);
        Assert.Contains(response.Items, item => item.Id == "customer-two" && item.ItemType == ServiceItemType.Service);
        Assert.Contains(response.Items, item => item.Id == "customer-cross-org" && item.ItemType == ServiceItemType.Service);
        Assert.Contains(response.Items, item => item.Id == "form-testing" && item.ItemType == ServiceItemType.RequestForm);

        var adminService = await harness.Client.GetFromJsonAsync<ServiceDto>(
            "/api/v1/services/customer-two");
        Assert.NotNull(adminService);
        Assert.Equal(["customer-2"], adminService!.AllowedCustomerIds);
    }

    [Fact]
    public async Task ServiceVisibility_EnforcesCustomerAndOrganizationAllowLists()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(customerId: "customer-1");

        var items = await harness.Client.GetFromJsonAsync<List<ServiceItemDto>>("/api/v1/service-items");
        var search = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>(
            "/api/v1/service-items/search?pageSize=25");

        Assert.NotNull(items);
        Assert.Contains(items!, item => item.Id == "customer-one");
        Assert.DoesNotContain(items!, item => item.Id == "customer-two");
        Assert.DoesNotContain(items!, item => item.Id == "customer-cross-org");

        Assert.NotNull(search);
        Assert.Contains(search!.Items, item => item.Id == "customer-one");
        Assert.DoesNotContain(search.Items, item => item.Id == "customer-two");
        Assert.DoesNotContain(search.Items, item => item.Id == "customer-cross-org");
        Assert.DoesNotContain(search.Items, item => item.Id == "form-hidden");
        Assert.DoesNotContain(search.Items, item => item.Id == "form-customer-two");
        Assert.DoesNotContain(search.Items, item => item.Id == "form-customer-parent");
        Assert.DoesNotContain(search.Items, item => item.Id == "form-customer-child");
        Assert.Contains(items!, item => item.Id == "form-root-level");

        var hiddenParentItems = await harness.Client.GetFromJsonAsync<List<ServiceItemDto>>(
            "/api/v1/service-items/customer-private-parent");
        Assert.Empty(hiddenParentItems!);

        var visibleChildItems = await harness.Client.GetFromJsonAsync<List<ServiceItemDto>>(
            "/api/v1/service-items/customer-one-child");
        Assert.Empty(visibleChildItems!);

        var formOnlySearch = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>(
            "/api/v1/service-items/search?q=needle&pageSize=25");
        Assert.NotNull(formOnlySearch);
        Assert.Equal("form-root", Assert.Single(formOnlySearch!.Items).Id);
        Assert.DoesNotContain(formOnlySearch.Items, item => item.Id == "customer-one-child");

        var customerServiceResponse = await harness.Client.GetAsync("/api/v1/services/customer-one");
        Assert.Equal(System.Net.HttpStatusCode.OK, customerServiceResponse.StatusCode);
        var customerService = await customerServiceResponse.Content.ReadFromJsonAsync<ServiceDto>();
        Assert.NotNull(customerService);
        Assert.Empty(customerService!.AllowedCustomerIds);
        Assert.Empty(customerService.AllowedOrganizationIds);
        Assert.Equal(HttpStatusCode.NotFound,
            (await harness.Client.GetAsync("/api/v1/services/customer-two")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await harness.Client.GetAsync("/api/v1/services/customer-cross-org")).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await harness.Client.GetAsync("/api/v1/services/customer-one/breadcrumb")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await harness.Client.GetAsync("/api/v1/services/customer-two/breadcrumb")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await harness.Client.GetAsync("/api/v1/services/customer-cross-org/breadcrumb")).StatusCode);

        foreach (var route in new[] { "/api/v1/services/customer-one-child", "/api/v1/services/customer-one-child/breadcrumb",
            "/api/v1/request-forms/form-customer-child" })
        {
            using var denied = await harness.Client.GetAsync(route);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
    }

    [Fact]
    public async Task ServiceVisibility_UsesCurrentCustomerAfterModelWasInitializedOutsideRequest()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(customerId: "customer-1");

        var customerOne = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>(
            "/api/v1/service-items/search?pageSize=25");
        Assert.NotNull(customerOne);
        Assert.Contains(customerOne!.Items, item => item.Id == "customer-one");
        Assert.DoesNotContain(customerOne.Items, item => item.Id == "customer-two");

        await harness.SwitchCustomerAsync("customer-2");

        var customerTwo = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>(
            "/api/v1/service-items/search?pageSize=25");
        Assert.NotNull(customerTwo);
        Assert.Contains(customerTwo!.Items, item => item.Id == "customer-two");
        Assert.DoesNotContain(customerTwo.Items, item => item.Id == "customer-one");
    }

    [Fact]
    public async Task SearchServiceItems_UsesSqliteCompatibleCaseInsensitiveSearch()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(isHelpdeskAdmin: true);

        var response = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>(
            "/api/v1/service-items/search?q=ROOT&pageSize=10");

        Assert.NotNull(response);
        Assert.Contains(response!.Items, item => item.Id == "root-a");
        Assert.Contains(response.Items, item => item.Id == "form-root");
    }

    [Fact]
    public async Task SearchServiceItems_FiltersServiceAllowedOrganizationsInMemory_ForTenantUsers()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync();

        var httpResponse = await harness.Client.GetAsync(
            "/api/v1/service-items/search?pageSize=25");
        var body = await httpResponse.Content.ReadAsStringAsync();
        Assert.True(httpResponse.IsSuccessStatusCode, body);
        var response = await httpResponse.Content.ReadFromJsonAsync<PagedResponse<ServiceItemDto>>();

        Assert.NotNull(response);
        Assert.Contains(response!.Items, item => item.Id == "root-a" && item.ItemType == ServiceItemType.Service);
        Assert.Contains(response.Items, item => item.Id == "form-root" && item.ItemType == ServiceItemType.RequestForm);
        Assert.DoesNotContain(response.Items, item => item.Id == "root-b");
        Assert.DoesNotContain(response.Items, item => item.Id == "form-hidden");
        Assert.DoesNotContain(response.Items, item => item.Id == "form-testing");
    }

    [Fact]
    public async Task GetServiceBreadcrumb_DoesNotRevealAnInaccessibleParent()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync();

        using var breadcrumb = await harness.Client.GetAsync("/api/v1/services/tenant-child/breadcrumb");
        using var service = await harness.Client.GetAsync("/api/v1/services/tenant-child");
        Assert.Equal(HttpStatusCode.NotFound, breadcrumb.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, service.StatusCode);
    }

    [Fact]
    public async Task ServiceCatalog_RequiresSelfServiceAccess()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(selfServiceAccess: false);

        var serviceItemsResponse = await harness.Client.GetAsync("/api/v1/service-items");
        var serviceResponse = await harness.Client.GetAsync("/api/v1/services/root-a");
        var requestFormResponse = await harness.Client.GetAsync("/api/v1/request-forms/form-root");
        var categoriesResponse = await harness.Client.GetAsync("/api/v1/categories");
        var customerDashboardResponse = await harness.Client.GetAsync("/api/v1/dashboard/customer-summary");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, serviceItemsResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, serviceResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, requestFormResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, categoriesResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, customerDashboardResponse.StatusCode);
    }

    [Fact]
    public async Task CustomerDashboard_UsesTheResolvedCustomerLink_NotTheIdentitySubject()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync();

        var response = await harness.Client.GetFromJsonAsync<CustomerDashboardDto>(
            "/api/v1/dashboard/customer-summary");

        Assert.NotNull(response);
        Assert.Equal(2, response!.OpenTicketsCount);
        Assert.Equal(1, response.ResolvedTicketsCount);
        Assert.Equal("customer-1", harness.DashboardSender.CustomerId);
    }

    [Fact]
    public async Task CustomerDashboard_ReturnsEmptyCounts_WhenNoCustomerLinkExists()
    {
        await using var harness = await ServiceItemsTestHarness.CreateAsync(customerId: null);

        var response = await harness.Client.GetFromJsonAsync<CustomerDashboardDto>(
            "/api/v1/dashboard/customer-summary");

        Assert.NotNull(response);
        Assert.Equal(0, response!.OpenTicketsCount);
        Assert.Equal(0, response.ResolvedTicketsCount);
        Assert.Null(harness.DashboardSender.CustomerId);
    }

    private sealed class ServiceItemsTestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly WebApplication _app;

        private ServiceItemsTestHarness(
            SqliteConnection connection,
            WebApplication app,
            HttpClient client,
            TestDashboardSender dashboardSender)
        {
            _connection = connection;
            _app = app;
            Client = client;
            DashboardSender = dashboardSender;
        }

        public HttpClient Client { get; }
        public TestDashboardSender DashboardSender { get; }
        public async Task SwitchCustomerAsync(string customerId)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            (await db.CustomerAuthLinks.SingleAsync()).CustomerId = customerId;
            await db.SaveChangesAsync();
        }

        public static async Task<ServiceItemsTestHarness> CreateAsync(
            bool isHelpdeskAdmin = false,
            bool selfServiceAccess = true,
            string? customerId = "customer-1")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });

            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddScoped<IRepository<Service>, EfRepository<Service>>();
            builder.Services.AddScoped<IRepository<RequestForm>, EfRepository<RequestForm>>();
            builder.Services.AddSingleton<IRequestFormSchemaParser, RequestFormSchemaParser>();
            builder.Services.AddSingleton<IRequestTaskDependencyGraphValidator, RequestTaskDependencyGraphValidator>();
            builder.Services.AddSingleton(Substitute.For<IDataManagementService>());
            builder.Services.AddScoped<IRequestFormDatasetBindingValidator, RequestFormDatasetBindingValidator>();
            builder.Services.AddSingleton(Substitute.For<IDomainEventPublisher>());
            builder.Services.AddSingleton(Substitute.For<ICorrelationContext>());
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("tenant-1", "user-1", isHelpdeskAdmin));
            builder.Services.AddScoped<SelfServiceAudienceService>();
            var dashboardSender = new TestDashboardSender();
            builder.Services.AddSingleton<IRequestSender>(dashboardSender);
            builder.Services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(
                    HelpdeskPermissions.SelfServiceUser,
                    policy => policy.RequireRole(
                        HelpdeskPermissions.SelfServiceUser,
                        HelpdeskPermissions.HelpdeskAdmin));
                options.AddPolicy("TicketReadAccess", policy => policy.RequireRole(HelpdeskPermissions.SelfServiceUser, HelpdeskPermissions.HelpdeskAdmin));
                options.AddPolicy("HelpdeskAdmin", policy => policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin));
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapServiceEndpoints();
            app.MapTicketCategoryEndpoints();
            app.MapDashboardEndpoints();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Organizations.AddRange(new Organization { Id = "tenant-1", Name = "Organization A" },
                    new Organization { Id = "tenant-2", Name = "Organization B" });
                db.Users.Add(new User { Id = "user-1", Name = "Requester", Email = "owner@example.test", OrganizationId = "tenant-1" });
                if (selfServiceAccess)
                    db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
                    { UserId = "user-1", RoleKey = ScopedRoleCatalog.SelfServiceUser, OrganizationId = "tenant-1" });
                db.Customers.AddRange(new Customer { Id = "customer-1", OrganizationId = "tenant-1", Email = "owner@example.test" },
                    new Customer { Id = "customer-2", OrganizationId = "tenant-1", Email = "other@example.test" });
                if (customerId is not null)
                    db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = customerId, LocalAccountId = "user-1", DomainUserId = "user-1" });

                db.Services.AddRange(
                    new Service { Id = "root-a", Name = "Root A", Description = "Visible root", AllowedOrganizationIds = ["tenant-1"] },
                    new Service { Id = "child-a", Name = "Child A", Description = "Visible child", ParentServiceId = "root-a", AllowedOrganizationIds = ["tenant-1"] },
                    new Service { Id = "root-b", Name = "Root B", Description = "Hidden root", AllowedOrganizationIds = ["tenant-2"] },
                    new Service { Id = "private-parent", Name = "Private parent", Description = "Hidden parent", AllowedOrganizationIds = ["tenant-2"] },
                    new Service { Id = "tenant-child", Name = "Tenant child", Description = "Visible child", ParentServiceId = "private-parent", AllowedOrganizationIds = ["tenant-1"] },
                    new Service { Id = "customer-one", Name = "Customer one", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-1"] },
                    new Service { Id = "customer-two", Name = "Customer two", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-2"] },
                    new Service { Id = "customer-cross-org", Name = "Customer other organization", AllowedOrganizationIds = ["tenant-2"], AllowedCustomerIds = ["customer-1"] },
                    new Service { Id = "customer-private-parent", Name = "Customer restricted parent", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-2"] },
                    new Service { Id = "customer-one-child", Name = "Customer one child", ParentServiceId = "customer-private-parent", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-1"] });

                db.RequestForms.AddRange(
                    new RequestForm { Id = "form-root", Title = "Root request", Description = "Needle eligible request", ServiceId = "root-a", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-child-1", Title = "Child request 1", Description = "Child request", ServiceId = "child-a", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-child-2", Title = "Child request 2", Description = "Child request", ServiceId = "child-a", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-hidden", Title = "Hidden request", Description = "Hidden", ServiceId = "root-b", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-testing", Title = "Testing request", Description = "Testing", ServiceId = "root-a", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.InTesting },
                    new RequestForm { Id = "form-root-level", Title = "Root level request", ServiceId = string.Empty, OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-customer-two", Title = "Customer two request", ServiceId = "customer-two", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-customer-parent", Title = "Restricted parent request", ServiceId = "customer-private-parent", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-customer-child", Title = "Needle only form", ServiceId = "customer-one-child", OrganizationId = "tenant-1", AllowedOrganizationIds = ["tenant-1"], ReleaseStatus = RequestFormReleaseStatus.Production });

                await db.SaveChangesAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            var actor = isHelpdeskAdmin
                ? "Admin"
                : selfServiceAccess ? "SelfService" : "NoSelfService";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", actor);

            var harness = new ServiceItemsTestHarness(connection, app, client, dashboardSender);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestTenantContext(string? tenantId, string? userId, bool isHelpdeskAdmin) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin { get; } = isHelpdeskAdmin;
    }

    private sealed class TestDashboardSender : IRequestSender
    {
        public string? CustomerId { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetCustomerDashboardQuery customerDashboardQuery)
            {
                CustomerId = customerDashboardQuery.CustomerId;
                return Task.FromResult((TResponse)(object)new CustomerDashboardDto(2, 1));
            }

            throw new InvalidOperationException($"Unexpected request type: {request.GetType().Name}");
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var authorization)
                ? authorization.Parameter
                : null;
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                new Claim(ClaimTypes.Name, "Requester")
            };

            if (string.Equals(actor, "Admin", StringComparison.Ordinal))
            {
                claims.Add(new Claim(ClaimTypes.Role, HelpdeskPermissions.HelpdeskAdmin));
            }
            else if (!string.Equals(actor, "NoSelfService", StringComparison.Ordinal))
            {
                claims.Add(new Claim("auth_mode", "local"));
                claims.Add(new Claim(ClaimTypes.Role, HelpdeskPermissions.SelfServiceUser));
            }

            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}
