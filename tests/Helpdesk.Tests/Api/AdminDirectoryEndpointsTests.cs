using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Categories;
using Helpdesk.API.Endpoints.Organization;
using Helpdesk.Infrastructure.Auth.Authentik;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Category;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed class AdminDirectoryEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Customers_query_complete_dataset_and_account_state_in_bounded_queries(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        await h.Seed(async (db, identity) =>
        {
            db.Organizations.Add(new Organization { Id = "customers-org", Name = "Synthetic directory" });
            db.Customers.AddRange(Enumerable.Range(0, 133).Select(i => new Customer
            {
                Id = $"customer-{i:D3}", Name = i == 132 ? "Équipe client" : "Duplicate name", Email = $"person{i:D3}@example.invalid", OrganizationId = "customers-org"
            }));
            db.CustomerAuthLinks.AddRange(
                new CustomerAuthLink { Id = "link-120", CustomerId = "customer-120", AuthProviderType = "Local", LocalAccountId = "local-disabled" },
                new CustomerAuthLink { Id = "link-121", CustomerId = "customer-121", AuthProviderType = "Local", LocalAccountId = "local-pending" },
                new CustomerAuthLink { Id = "link-122", CustomerId = "customer-122", AuthProviderType = "Authentik", InviteStatus = CustomerInviteStatus.Pending, InviteLinkExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-1) },
                new CustomerAuthLink { Id = "link-123", CustomerId = "customer-123", AuthProviderType = "Authentik", InviteStatus = CustomerInviteStatus.Failed },
                new CustomerAuthLink { Id = "link-124-a", CustomerId = "customer-124", AuthProviderType = "Authentik", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { Id = "link-124-b", CustomerId = "customer-124", AuthProviderType = "Local", LocalAccountId = "local-multiple" },
                new CustomerAuthLink { Id = "link-125", CustomerId = "customer-125", AuthProviderType = "Local", LocalAccountId = "local-active" });
            identity.Users.AddRange(
                new ApplicationUser { Id = "local-disabled", UserName = "disabled", IsEnabled = false, EmailConfirmed = true, PasswordHash = "test-only" },
                new ApplicationUser { Id = "local-pending", UserName = "pending", IsEnabled = true, EmailConfirmed = false },
                new ApplicationUser { Id = "local-multiple", UserName = "multiple", IsEnabled = true, EmailConfirmed = true, PasswordHash = "test-only" },
                new ApplicationUser { Id = "local-active", UserName = "active", IsEnabled = true, EmailConfirmed = true, PasswordHash = "test-only" });
            await identity.SaveChangesAsync(); await db.SaveChangesAsync();
        });
        foreach (var size in new[] { 25, 50, 100 })
        {
            var sized = await h.Get<CustomerDto>($"customers?pageSize={size}");
            Assert.Equal(size, sized.Items.Count); Assert.Equal(133, sized.TotalCount);
        }
        h.Counter.Reset();
        var page = await h.Get<CustomerDto>("customers?page=5&pageSize=25&sort=name");
        Assert.Equal(133, page.TotalCount); Assert.Equal(5, page.Page); Assert.Equal(8, page.Items.Count);
        Assert.Equal("customer-125", page.Items[0].Id);
        Assert.InRange(h.Counter.Reads, 1, 2);
        var disabled = await h.Get<CustomerDto>("customers?access=disabled");
        Assert.Equal("customer-120", Assert.Single(disabled.Items).Id); Assert.Equal(1, disabled.TotalCount);
        Assert.False(disabled.Items[0].AuthStatus!.CanInvite);
        Assert.False(disabled.Items[0].AuthStatus!.CanResend);
        var pending = await h.Get<CustomerDto>("customers?access=pending");
        Assert.Equal("Activation pending", Assert.Single(pending.Items).AuthStatus!.StatusText);
        var expired = await h.Get<CustomerDto>("customers?access=expired");
        Assert.Equal("customer-122", Assert.Single(expired.Items).Id);
        var failures = await h.Get<CustomerDto>("customers?access=failed&sort=email&descending=true");
        Assert.Equal(2, failures.TotalCount); Assert.Equal("customer-124", failures.Items[0].Id);
        Assert.True(failures.Items[0].AuthStatus!.HasMultipleIdentityLinks); Assert.False(failures.Items[0].AuthStatus!.CanResend);
        var result = await h.Get<CustomerDto>("customers?search=person132&organizationId=customers-org&page=99");
        Assert.Equal(0, result.Page); Assert.Equal("customer-132", Assert.Single(result.Items).Id);
        var unicode = await h.Get<CustomerDto>("customers?search=%C3%A9QUIPE");
        Assert.Equal("customer-132", Assert.Single(unicode.Items).Id);
        var unauthorized = await h.Get<CustomerDto>("customers?organizationId=foreign");
        Assert.Equal(0, unauthorized.TotalCount);
        var linked = await h.Get<CustomerDto>("customers?access=linked&pageSize=100");
        Assert.Equal(6, linked.TotalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hybrid_external_logins_do_not_require_a_local_password_and_disabled_links_override_accounts(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        await h.Seed(async (db, identity) =>
        {
            db.Organizations.Add(new Organization { Id = "hybrid-org", Name = "Hybrid organization" });
            db.Customers.AddRange(Enumerable.Range(0, 6).Select(i => new Customer { Id = $"hybrid-{i}", Name = $"Hybrid {i}", Email = $"hybrid{i}@example.invalid", OrganizationId = "hybrid-org" }));
            identity.Users.AddRange(
                new ApplicationUser { Id = "local-label-external", UserName = "linked-oidc", IsEnabled = true, PasswordHash = null, EmailConfirmed = false },
                new ApplicationUser { Id = "external-active", UserName = "external", IsEnabled = true, PasswordHash = null, EmailConfirmed = false },
                new ApplicationUser { Id = "external-disabled", UserName = "disabled-external", IsEnabled = false },
                new ApplicationUser { Id = "local-link-disabled", UserName = "disabled-link", IsEnabled = true, PasswordHash = "test-only", EmailConfirmed = true },
                new ApplicationUser { Id = "local-timestamp-disabled", UserName = "disabled-timestamp", IsEnabled = true, PasswordHash = "test-only", EmailConfirmed = true });
            identity.UserLogins.Add(new IdentityUserLogin<string> { LoginProvider = "synthetic-oidc", ProviderKey = "synthetic-subject", UserId = "local-label-external" });
            db.CustomerAuthLinks.AddRange(
                new CustomerAuthLink { CustomerId = "hybrid-5", AuthProviderType = "Local", LocalAccountId = "local-label-external", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "hybrid-0", AuthProviderType = "OIDC", LocalAccountId = "external-active", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "hybrid-1", AuthProviderType = "Authentik", LocalAccountId = "external-disabled", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "hybrid-2", AuthProviderType = "Local", LocalAccountId = "local-link-disabled", InviteStatus = CustomerInviteStatus.Disabled },
                new CustomerAuthLink { CustomerId = "hybrid-3", AuthProviderType = "Local", LocalAccountId = "local-timestamp-disabled", InviteStatus = CustomerInviteStatus.Active, DisabledAtUtc = DateTimeOffset.UtcNow },
                new CustomerAuthLink { CustomerId = "hybrid-4", AuthProviderType = "OIDC", LocalAccountId = "missing-account", InviteStatus = CustomerInviteStatus.Failed });
            await identity.SaveChangesAsync(); await db.SaveChangesAsync();
        });
        var active = await h.Get<CustomerDto>("customers?access=active");
        Assert.Equal(2, active.TotalCount);
        var external = Assert.Single(active.Items, row => row.Id == "hybrid-0");
        Assert.Equal("hybrid-0", external.Id); Assert.Equal("OIDC", external.AuthStatus!.AuthProviderType);
        Assert.False(external.AuthStatus.CanInvite); Assert.False(external.AuthStatus.CanResend);
        var disabled = await h.Get<CustomerDto>("customers?access=disabled");
        Assert.Equal(new[] { "hybrid-1", "hybrid-2", "hybrid-3" }, disabled.Items.Select(row => row.Id));
        Assert.All(disabled.Items, row => { Assert.False(row.AuthStatus!.CanInvite); Assert.False(row.AuthStatus.CanResend); });
        var unavailable = await h.Get<CustomerDto>("customers?access=failed");
        Assert.Equal("hybrid-4", Assert.Single(unavailable.Items).Id); Assert.False(unavailable.Items[0].AuthStatus!.CanResend);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_or_orphaned_customer_identities_filter_in_SQL_and_managed_profiles_cannot_be_invited(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        await h.Seed(async (db, identity) =>
        {
            db.Organizations.Add(new Organization { Id = "retirement-org", Name = "Retirement directory" });
            db.Customers.AddRange(Enumerable.Range(0, 122).Select(i => new Customer
            {
                Id = $"retirement-{i:D3}", Name = i < 110 ? "Ordinary contact" : $"Retirement {i}",
                Email = $"retirement{i}@example.invalid", OrganizationId = "retirement-org",
                IdentityUserId = i switch { 110 => "deleted-key", 111 => "missing-key", 112 => "active-key", 113 => "domain-only", 121 => "deleted-linked-key", _ => null }
            }));
            db.Users.AddRange(
                new User { Id = "deleted-key", IsDeleted = true },
                new User { Id = "active-key" },
                new User { Id = "domain-only" },
                new User { Id = "deleted-domain", IsDeleted = true },
                new User { Id = "legacy-deleted-local", IsDeleted = true },
                new User { Id = "external-domain" },
                new User { Id = "deleted-linked-key", IsDeleted = true });
            identity.Users.AddRange(
                // IsDeleted must fail closed independently of an old IsEnabled flag.
                new ApplicationUser { Id = "deleted-local", IsDeleted = true, IsEnabled = true, EmailConfirmed = true, PasswordHash = "test-only" },
                new ApplicationUser { Id = "deleted-external", IsDeleted = true, IsEnabled = true },
                new ApplicationUser { Id = "legacy-deleted-local", IsEnabled = true, EmailConfirmed = true, PasswordHash = "test-only" },
                new ApplicationUser { Id = "active-key-account", IsEnabled = true, EmailConfirmed = true, PasswordHash = "test-only" });
            db.CustomerAuthLinks.AddRange(
                new CustomerAuthLink { CustomerId = "retirement-113", AuthProviderType = "Domain", DomainUserId = "domain-only", InviteStatus = CustomerInviteStatus.NotInvited },
                new CustomerAuthLink { CustomerId = "retirement-114", AuthProviderType = "OIDC", DomainUserId = "deleted-domain", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "retirement-115", AuthProviderType = "OIDC", DomainUserId = "missing-domain", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "retirement-116", AuthProviderType = "Local", LocalAccountId = "deleted-local", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "retirement-117", AuthProviderType = "OIDC", LocalAccountId = "deleted-external", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "retirement-118", AuthProviderType = "Local", LocalAccountId = "legacy-deleted-local", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "retirement-119", AuthProviderType = "OIDC", LocalAccountId = "missing-account", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "retirement-120", AuthProviderType = "OIDC", DomainUserId = "external-domain", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "retirement-121", AuthProviderType = "Local", LocalAccountId = "active-key-account", InviteStatus = CustomerInviteStatus.Active });
            await identity.SaveChangesAsync(); await db.SaveChangesAsync();
        });

        h.Counter.Reset();
        var last = await h.Get<CustomerDto>("customers?page=4&pageSize=25&sort=email");
        Assert.Equal(122, last.TotalCount); Assert.Equal(22, last.Items.Count);
        Assert.InRange(h.Counter.Reads, 1, 2);
        var disabled = await h.Get<CustomerDto>("customers?search=Retirement&access=disabled&sort=name");
        Assert.Equal(6, disabled.TotalCount);
        Assert.Equal(new[] { "retirement-110", "retirement-114", "retirement-116", "retirement-117", "retirement-118", "retirement-121" }, disabled.Items.Select(row => row.Id));
        Assert.All(disabled.Items, row => Assert.Equal("Application access deleted", row.AuthStatus!.StatusText));
        var unavailable = await h.Get<CustomerDto>("customers?access=failed&sort=name");
        Assert.Equal(3, unavailable.TotalCount);
        Assert.Equal(new[] { "retirement-111", "retirement-115", "retirement-119" }, unavailable.Items.Select(row => row.Id));
        Assert.All(unavailable.Items, row => Assert.Equal("Linked identity unavailable", row.AuthStatus!.StatusText));
        foreach (var row in disabled.Items.Concat(unavailable.Items))
        {
            Assert.False(row.AuthStatus!.CanInvite); Assert.False(row.AuthStatus.CanResend);
            Assert.False(string.IsNullOrWhiteSpace(row.AuthStatus.IdentitySummary));
        }

        var active = await h.Get<CustomerDto>("customers?access=active");
        Assert.Equal("retirement-120", Assert.Single(active.Items).Id);
        foreach (var id in new[] { "112", "113" })
        {
            var managed = Assert.Single((await h.Get<CustomerDto>($"customers?search=retirement{id}@")).Items);
            Assert.Equal(CustomerInviteStatus.NotInvited, managed.AuthStatus!.InviteStatus);
            Assert.Equal("Managed requester profile", managed.AuthStatus.StatusText);
            Assert.False(managed.AuthStatus.CanInvite); Assert.False(managed.AuthStatus.CanResend);
        }
        var ordinary = Assert.Single((await h.Get<CustomerDto>("customers?search=retirement0@")).Items);
        Assert.True(ordinary.AuthStatus!.CanInvite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Organizations_search_filter_sort_and_clamp_over_full_dataset(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        await h.Seed(async (db, _) =>
        {
            db.Organizations.Add(new Organization { Id = "support", Name = "Support team" });
            db.Organizations.AddRange(Enumerable.Range(0, 137).Select(i => new Organization
            {
                Id = $"organization-{i:D3}", Name = "Duplicate name", DnsName = $"org{i:D3}.example.invalid",
                ContactInfo = i == 136 ? "Find final record" : "Synthetic contact", IsEnabled = i % 2 == 0,
                ItSupportOrganizationId = i % 2 == 0 ? "support" : null,
                OrchestrationTenantId = i % 3 == 0 ? i : null, OrchestrationTenantName = i % 3 == 0 ? "Saved tenant" : null
            }));
            await db.SaveChangesAsync();
        });
        var first = await h.Get<OrganizationDto>("organizations?pageSize=100&sort=name");
        var second = await h.Get<OrganizationDto>("organizations?pageSize=100&page=1&sort=name");
        Assert.Equal(138, first.TotalCount); Assert.Equal(100, first.Items.Count); Assert.Equal(38, second.Items.Count);
        Assert.Empty(first.Items.Select(row => row.Id).Intersect(second.Items.Select(row => row.Id)));
        var filtered = await h.Get<OrganizationDto>("organizations?enabled=true&linked=true&supportOrganizationId=support&sort=dns");
        Assert.Equal(23, filtered.TotalCount); Assert.All(filtered.Items, row => Assert.Equal("Support team", row.SupportOrganizationName));
        var final = await h.Get<OrganizationDto>("organizations?search=final&page=99");
        Assert.Equal(0, final.Page); Assert.Equal("organization-136", Assert.Single(final.Items).Id);
        var saved = await h.Get<OrganizationDto>("organizations?search=org132");
        Assert.Equal(132, Assert.Single(saved.Items).OrchestrationTenantId);
        Assert.Equal("Saved tenant", saved.Items[0].OrchestrationTenantName);
        var lookup = await h.Client.GetFromJsonAsync<List<TenantLookupDto>>("/api/v1/admin/directories/organizations/lookup");
        Assert.Equal(50, lookup!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Category_admin_exact_filters_and_all_preserve_legacy_picker_service_inclusion(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var organizationId = Guid.NewGuid();
        await h.Seed(async (db, _) =>
        {
            db.Organizations.Add(new Organization { Id = organizationId.ToString("N"), Name = "Compact organization" });
            db.TicketCategories.AddRange(Enumerable.Range(0, 143).Select(i => new TicketCategory
            {
                Id = Guid.NewGuid(), Name = i % 3 == 0 ? $"Category {i:D3}" : "Duplicate category", Description = $"description {i:D3}",
                Type = i % 2 == 0 ? TicketCategoryType.Request : TicketCategoryType.Service,
                TenantId = i % 3 == 0 ? organizationId : null, IsSystem = i % 3 != 0,
                IsActive = i % 5 != 0, SortOrder = i
            }));
            await db.SaveChangesAsync();
        });
        var all = await h.Get<TicketCategoryDto>("categories?pageSize=100");
        Assert.Equal(143, all.TotalCount); Assert.Equal(100, all.Items.Count);
        var exact = await h.Get<TicketCategoryDto>($"categories?type={(int)TicketCategoryType.Request}&scope=organization&system=false&active=true&sort=order");
        Assert.All(exact.Items, row => { Assert.Equal(TicketCategoryType.Request, row.Type); Assert.False(row.IsSystem); Assert.True(row.IsActive); Assert.Equal("Compact organization", row.TenantName); });
        Assert.Equal(19, exact.TotalCount);
        var final = await h.Get<TicketCategoryDto>("categories?search=description%20142&page=99");
        Assert.Equal(0, final.Page); Assert.Equal("description 142", Assert.Single(final.Items).Description);
        var legacy = await h.Client.GetFromJsonAsync<List<TicketCategoryDto>>($"/api/v1/categories?type={(int)TicketCategoryType.Request}&allScopes=true&includeInactive=true");
        Assert.Equal(143, legacy!.Count); Assert.Contains(legacy, row => row.Type == TicketCategoryType.Service);
        var ids = all.Items.Select(row => row.Id).ToHashSet();
        var last = await h.Get<TicketCategoryDto>("categories?pageSize=100&page=1");
        Assert.DoesNotContain(last.Items, row => ids.Contains(row.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Directory_contracts_reject_bad_queries_and_delegated_or_ordinary_actors(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        foreach (var path in new[] { "customers?pageSize=1000", "organizations?page=-1", "categories?type=999", "categories?scope=foreign", "customers?access=unknown" })
            Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/v1/admin/directories/" + path)).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "delegated");
        foreach (var path in new[] { "customers", "organizations", "categories", "organizations/lookup" })
            Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/admin/directories/" + path)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Category_delete_preserves_historical_assignments_and_child_dependencies(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var historical = Guid.NewGuid(); var parent = Guid.NewGuid(); var child = Guid.NewGuid(); var removable = Guid.NewGuid(); var system = Guid.NewGuid();
        await h.Seed(async (db, _) =>
        {
            db.TicketCategories.AddRange(
                new TicketCategory { Id = historical, Name = "Historical", Type = TicketCategoryType.Request },
                new TicketCategory { Id = parent, Name = "Parent", Type = TicketCategoryType.Request },
                new TicketCategory { Id = child, Name = "Child", Type = TicketCategoryType.Request, ParentCategoryId = parent },
                new TicketCategory { Id = removable, Name = "Removable", Type = TicketCategoryType.Request },
                new TicketCategory { Id = system, Name = "Built-in", Type = TicketCategoryType.Request, IsSystem = true });
            db.Requests.Add(new Request { Id = "historical-request", Title = "Attribution retained", TrackingId = "REQ-SYN-HISTORY" });
            db.RequestCategoryLinks.Add(new RequestCategoryLink { RequestId = "historical-request", TicketCategoryId = historical });
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.DeleteAsync($"/api/v1/categories/{historical}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.DeleteAsync($"/api/v1/categories/{parent}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.DeleteAsync($"/api/v1/categories/{system}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"/api/v1/categories/{removable}")).StatusCode);
        await h.Seed(async (db, _) =>
        {
            Assert.True(await db.RequestCategoryLinks.AnyAsync(link => link.TicketCategoryId == historical));
            Assert.True(await db.TicketCategories.AnyAsync(row => row.Id == historical));
            Assert.True(await db.TicketCategories.AnyAsync(row => row.Id == child));
            Assert.False(await db.TicketCategories.AnyAsync(row => row.Id == removable));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_the_only_final_page_row_clamps_the_preserved_query(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var ids = Enumerable.Range(0, 126).Select(_ => Guid.NewGuid()).ToArray();
        await h.Seed(async (db, _) =>
        {
            db.TicketCategories.AddRange(ids.Select((id, index) => new TicketCategory { Id = id, Name = $"Paging {index:D3}", Type = TicketCategoryType.Request, IsActive = true }));
            await db.SaveChangesAsync();
        });
        var last = await h.Get<TicketCategoryDto>("categories?search=Paging&type=2&page=5&pageSize=25");
        Assert.Equal(ids[125], Assert.Single(last.Items).Id);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"/api/v1/categories/{ids[125]}")).StatusCode);
        var clamped = await h.Get<TicketCategoryDto>("categories?search=Paging&type=2&page=5&pageSize=25");
        Assert.Equal(4, clamped.Page); Assert.Equal(125, clamped.TotalCount); Assert.Equal(25, clamped.Items.Count);
        Assert.Equal(ids[100], clamped.Items[0].Id); Assert.Equal(ids[124], clamped.Items[^1].Id);
    }

    private sealed class Harness(WebApplication app, HttpClient client, string path, PostgreSqlContainer? postgres, ReadCounter counter) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public ReadCounter Counter { get; } = counter;
        public static async Task<Harness> Create(bool usePostgres)
        {
            var path = Path.Combine(Path.GetTempPath(), $"admin-directory-{Guid.NewGuid():N}.db");
            PostgreSqlContainer? postgres = null;
            if (usePostgres) { postgres = new PostgreSqlBuilder("postgres:16").Build(); await postgres.StartAsync(); }
            var counter = new ReadCounter();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.WebHost.UseTestServer(); builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ITenantContext, DirectoryTenant>(); builder.Services.Configure<AuthentikOptions>(options => { options.BaseUrl = "https://identity.example.invalid/"; options.ApiToken = "synthetic-configured-token"; });
            builder.Services.AddDbContext<HelpdeskDbContext>(options => { Configure(options); options.AddInterceptors(counter); });
            builder.Services.AddDbContext<RatelDeskIdentityDbContext>(Configure);
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, DirectoryAuth>("Test", _ => { });
            builder.Services.AddAuthorization(options => { options.AddPolicy("HelpdeskAdmin", p => p.RequireRole("HelpdeskAdmin")); options.AddPolicy("TicketReadAccess", p => p.RequireAuthenticatedUser()); });
            var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapAdminDirectoryEndpoints(); app.MapTicketCategoryEndpoints(); await app.StartAsync();
            var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "admin");
            var h = new Harness(app, client, path, postgres, counter);
            await h.Seed(async (db, identity) => { await db.Database.EnsureCreatedAsync(); await identity.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(); });
            return h;
            void Configure(DbContextOptionsBuilder options) { if (postgres is not null) options.UseNpgsql(postgres.GetConnectionString()); else options.UseSqlite($"Data Source={path};Default Timeout=30"); }
        }
        public async Task<PagedResponse<T>> Get<T>(string path)
        {
            using var response = await Client.GetAsync("/api/v1/admin/directories/" + path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {body}");
            return (await response.Content.ReadFromJsonAsync<PagedResponse<T>>())!;
        }
        public async Task Seed(Func<HelpdeskDbContext, RatelDeskIdentityDbContext, Task> action)
        {
            await using var scope = app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>(), scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>());
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); if (postgres is not null) await postgres.DisposeAsync(); if (File.Exists(path)) File.Delete(path); }
    }
    private sealed class ReadCounter : DbCommandInterceptor
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);
        public void Reset() => Interlocked.Exchange(ref reads, 0);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref reads); return ValueTask.FromResult(result); }
    }
    private sealed class DirectoryTenant : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => "synthetic-directory-admin";
        public bool IsHelpdeskAdmin => true;
    }
    private sealed class DirectoryAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "synthetic-admin") };
            if (Request.Headers.Authorization.ToString().EndsWith("admin", StringComparison.Ordinal)) claims.Add(new(ClaimTypes.Role, "HelpdeskAdmin"));
            else claims.Add(new(ClaimTypes.Role, "Tenant.Roles.Assign"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
