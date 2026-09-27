using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Categories;
using Helpdesk.API.Endpoints.Dashboard;
using Helpdesk.API.Endpoints.Services;
using Helpdesk.API.Services;
using Helpdesk.Application.Dashboard;
using Helpdesk.Application.Messaging;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
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
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class ServiceItemsEndpointsTests
{
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
        Assert.Contains(search.Items, item => item.Id == "form-customer-child");
        Assert.Contains(items!, item => item.Id == "form-root-level");

        var hiddenParentItems = await harness.Client.GetFromJsonAsync<List<ServiceItemDto>>(
            "/api/v1/service-items/customer-private-parent");
        Assert.Empty(hiddenParentItems!);

        var visibleChildItems = await harness.Client.GetFromJsonAsync<List<ServiceItemDto>>(
            "/api/v1/service-items/customer-one-child");
        Assert.Contains(visibleChildItems!, item => item.Id == "form-customer-child");

        var formOnlySearch = await harness.Client.GetFromJsonAsync<PagedResponse<ServiceItemDto>>(
            "/api/v1/service-items/search?q=needle&pageSize=25");
        Assert.NotNull(formOnlySearch);
        Assert.Contains(formOnlySearch!.Items, item => item.Id == "form-customer-child");
        Assert.DoesNotContain(formOnlySearch.Items, item => item.Id == "customer-one-child");

        var customerServiceResponse = await harness.Client.GetAsync("/api/v1/services/customer-one");
        Assert.Equal(System.Net.HttpStatusCode.OK, customerServiceResponse.StatusCode);
        var customerService = await customerServiceResponse.Content.ReadFromJsonAsync<ServiceDto>();
        Assert.NotNull(customerService);
        Assert.Empty(customerService!.AllowedCustomerIds);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await harness.Client.GetAsync("/api/v1/services/customer-two")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await harness.Client.GetAsync("/api/v1/services/customer-cross-org")).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await harness.Client.GetAsync("/api/v1/services/customer-one/breadcrumb")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await harness.Client.GetAsync("/api/v1/services/customer-two/breadcrumb")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await harness.Client.GetAsync("/api/v1/services/customer-cross-org/breadcrumb")).StatusCode);

        var child = await harness.Client.GetFromJsonAsync<ServiceDto>(
            "/api/v1/services/customer-one-child");
        Assert.NotNull(child);
        Assert.Equal(0, child!.Depth);

        var childBreadcrumb = await harness.Client.GetFromJsonAsync<List<BreadcrumbDto>>(
            "/api/v1/services/customer-one-child/breadcrumb");
        Assert.Equal("customer-one-child", Assert.Single(childBreadcrumb!).Id);
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

        harness.AccessService.CustomerId = "customer-2";

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

        var response = await harness.Client.GetFromJsonAsync<List<BreadcrumbDto>>(
            "/api/v1/services/tenant-child/breadcrumb");

        var breadcrumb = Assert.Single(response!);
        Assert.Equal("tenant-child", breadcrumb.Id);
        Assert.Equal("Tenant child", breadcrumb.Name);

        var service = await harness.Client.GetFromJsonAsync<ServiceDto>(
            "/api/v1/services/tenant-child");
        Assert.NotNull(service);
        Assert.Equal(0, service!.Depth);
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
            TestDashboardSender dashboardSender,
            TestCurrentUserAccessService accessService)
        {
            _connection = connection;
            _app = app;
            Client = client;
            DashboardSender = dashboardSender;
            AccessService = accessService;
        }

        public HttpClient Client { get; }
        public TestDashboardSender DashboardSender { get; }
        public TestCurrentUserAccessService AccessService { get; }

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
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("tenant-1", "user-1", isHelpdeskAdmin));
            builder.Services.AddScoped<ISelfServiceAudienceService, TestSelfServiceAudienceService>();
            var dashboardSender = new TestDashboardSender();
            builder.Services.AddSingleton<IRequestSender>(dashboardSender);
            var accessService = new TestCurrentUserAccessService(customerId);
            builder.Services.AddSingleton<ICurrentUserAccessService>(accessService);
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

                db.Services.AddRange(
                    new Service { Id = "root-a", Name = "Root A", Description = "Visible root" },
                    new Service { Id = "child-a", Name = "Child A", Description = "Visible child", ParentServiceId = "root-a" },
                    new Service { Id = "root-b", Name = "Root B", Description = "Hidden root", AllowedOrganizationIds = ["tenant-2"] },
                    new Service { Id = "private-parent", Name = "Private parent", Description = "Hidden parent", AllowedOrganizationIds = ["tenant-2"] },
                    new Service { Id = "tenant-child", Name = "Tenant child", Description = "Visible child", ParentServiceId = "private-parent", AllowedOrganizationIds = ["tenant-1"] },
                    new Service { Id = "customer-one", Name = "Customer one", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-1"] },
                    new Service { Id = "customer-two", Name = "Customer two", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-2"] },
                    new Service { Id = "customer-cross-org", Name = "Customer other organization", AllowedOrganizationIds = ["tenant-2"], AllowedCustomerIds = ["customer-1"] },
                    new Service { Id = "customer-private-parent", Name = "Customer restricted parent", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-2"] },
                    new Service { Id = "customer-one-child", Name = "Customer one child", ParentServiceId = "customer-private-parent", AllowedOrganizationIds = ["tenant-1"], AllowedCustomerIds = ["customer-1"] });

                db.RequestForms.AddRange(
                    new RequestForm { Id = "form-root", Title = "Root request", Description = "Direct request", ServiceId = "root-a", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-child-1", Title = "Child request 1", Description = "Child request", ServiceId = "child-a", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-child-2", Title = "Child request 2", Description = "Child request", ServiceId = "child-a", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-hidden", Title = "Hidden request", Description = "Hidden", ServiceId = "root-b", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-testing", Title = "Testing request", Description = "Testing", ServiceId = "root-a", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.InTesting },
                    new RequestForm { Id = "form-root-level", Title = "Root level request", ServiceId = string.Empty, OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-customer-two", Title = "Customer two request", ServiceId = "customer-two", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-customer-parent", Title = "Restricted parent request", ServiceId = "customer-private-parent", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production },
                    new RequestForm { Id = "form-customer-child", Title = "Needle only form", ServiceId = "customer-one-child", OrganizationId = "tenant-1", ReleaseStatus = RequestFormReleaseStatus.Production });

                await db.SaveChangesAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            var actor = isHelpdeskAdmin
                ? "Admin"
                : selfServiceAccess ? "SelfService" : "NoSelfService";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", actor);

            var harness = new ServiceItemsTestHarness(connection, app, client, dashboardSender, accessService);
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestSelfServiceAudienceService : ISelfServiceAudienceService
    {
        public Task<bool> IsTestUserAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> CanAccessRequestFormAsync(RequestForm requestForm, CancellationToken cancellationToken) =>
            Task.FromResult(requestForm.ReleaseStatus == RequestFormReleaseStatus.Production);

        public IQueryable<RequestForm> ApplyAudienceFilter(
            IQueryable<RequestForm> query,
            bool isAdmin,
            bool isTestUser,
            string? tenantId)
        {
            if (isAdmin)
            {
                return query;
            }

            return query.Where(f =>
                (string.IsNullOrWhiteSpace(f.OrganizationId) || f.OrganizationId == tenantId)
                && f.ReleaseStatus == RequestFormReleaseStatus.Production);
        }
    }

    private sealed class TestTenantContext(string? tenantId, string? userId, bool isHelpdeskAdmin) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin { get; } = isHelpdeskAdmin;
    }

    private sealed class TestCurrentUserAccessService(string? customerId) : ICurrentUserAccessService
    {
        public string? CustomerId { get; set; } = customerId;

        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult(CurrentUserAccessProfile.FromClaims(user) with { CustomerId = this.CustomerId });
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
                claims.Add(new Claim(ClaimTypes.Role, HelpdeskPermissions.SelfServiceUser));
            }

            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}
