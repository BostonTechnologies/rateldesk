using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Categories;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Category;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed class TicketCategoryEndpointsTests
{
    private static readonly Guid Tenant = Guid.Parse("78000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("78000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compact_organization_ID_supports_scoped_category_CRUD(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var organizationId = Guid.NewGuid();
        await h.WithDb(async db => { db.Organizations.Add(new Organization { Id = organizationId.ToString("N"), Name = "Compact organization" }); await db.SaveChangesAsync(); });
        var create = await h.Client.PostAsJsonAsync("/api/v1/categories", new CreateTicketCategoryDto { Name = "Scoped", Type = TicketCategoryType.Request, TenantId = organizationId });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var category = (await create.Content.ReadFromJsonAsync<TicketCategoryDto>())!;
        Assert.Equal(organizationId, category.TenantId);
        var update = await h.Client.PutAsJsonAsync($"/api/v1/categories/{category.Id}", new UpdateTicketCategoryDto { Name = "Updated", Type = TicketCategoryType.Request, IsActive = true });
        update.EnsureSuccessStatusCode();
        var scoped = await h.Client.GetFromJsonAsync<List<TicketCategoryDto>>($"/api/v1/categories?tenantId={organizationId}");
        Assert.Equal("Updated", Assert.Single(scoped!).Name);
        Assert.Equal(HttpStatusCode.Forbidden, await DeniedAsync());
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "admin");
        (await h.Client.DeleteAsync($"/api/v1/categories/{category.Id}")).EnsureSuccessStatusCode();
        Beta9Evidence.Record("API-B8-CATEGORY-TENANT-ID-FORMAT", $"http-{postgres}",
            new { storedOrganizationFormat = "N", typedTenantFormat = "D", createStatus = (int)create.StatusCode,
                roundTripTenantMatches = category.TenantId == organizationId, checkedOperations = new[] { "create", "update", "list", "delete" }, foreignScopeStatus = 403 });
        async Task<HttpStatusCode> DeniedAsync()
        {
            h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
            return (await h.Client.GetAsync($"/api/v1/categories?tenantId={organizationId}")).StatusCode;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_is_bounded_and_hydration_preserves_inactive_scoped_ids(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var inactive = Guid.NewGuid(); var foreign = Guid.NewGuid(); var literal = Guid.NewGuid();
        await h.WithDb(async db =>
        {
            db.TicketCategories.AddRange(Enumerable.Range(0, 120).Select(i => new TicketCategory { Id = Guid.NewGuid(), Name = $"Category {i:D3}", Type = TicketCategoryType.Incident }));
            db.TicketCategories.AddRange(
                new TicketCategory { Id = inactive, Name = "Historical category", IsActive = false, TenantId = Tenant, Type = TicketCategoryType.Incident },
                new TicketCategory { Id = foreign, Name = "Other scope", TenantId = OtherTenant, Type = TicketCategoryType.Incident },
                new TicketCategory { Id = literal, Name = "R&D + 100%_\\", TenantId = Tenant, Type = TicketCategoryType.Service });
            await db.SaveChangesAsync();
        });
        var first = await h.Client.GetFromJsonAsync<List<TicketCategoryDto>>($"/api/v1/categories?type={(int)TicketCategoryType.Incident}&take=30&tenantId={Tenant}");
        Assert.Equal(30, first!.Count);
        var escaped = await h.Client.GetFromJsonAsync<List<TicketCategoryDto>>($"/api/v1/categories?type={(int)TicketCategoryType.Incident}&take=30&tenantId={Tenant}&search={Uri.EscapeDataString("R&D + 100%_\\")}");
        Assert.Equal(literal, Assert.Single(escaped!).Id);
        var hydrated = await h.Client.GetFromJsonAsync<List<TicketCategoryDto>>($"/api/v1/categories?ids={inactive},{foreign}&tenantId={Tenant}");
        Assert.Equal(inactive, Assert.Single(hydrated!).Id);
        Assert.False(hydrated![0].IsActive);
        var all = await h.Client.GetFromJsonAsync<List<TicketCategoryDto>>("/api/v1/categories?allScopes=true&includeInactive=true");
        Assert.Equal(123, all!.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/v1/categories?ids=invalid")).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync($"/api/v1/categories?tenantId={OtherTenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/categories?allScopes=true")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_normalized_creation_selects_one_catalogue_entry_in_each_scope(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var requests = Enumerable.Range(0, 6).Select(i => h.Client.PostAsJsonAsync("/api/v1/categories", new CreateTicketCategoryDto
        {
            Name = i % 2 == 0 ? " Network   Operations " : "network operations", Type = TicketCategoryType.Incident, TenantId = Tenant
        }));
        var responses = await Task.WhenAll(requests);
        var ids = new List<Guid>();
        foreach (var response in responses)
        {
            response.EnsureSuccessStatusCode();
            ids.Add((await response.Content.ReadFromJsonAsync<TicketCategoryDto>())!.Id);
            response.Dispose();
        }
        Assert.Single(ids.Distinct());
        await h.WithDb(async db => Assert.Equal(1, await db.TicketCategories.CountAsync()));
        var otherScope = await h.Client.PostAsJsonAsync("/api/v1/categories", new CreateTicketCategoryDto { Name = "Network Operations", Type = TicketCategoryType.Incident, TenantId = OtherTenant });
        otherScope.EnsureSuccessStatusCode();
        Assert.NotEqual(ids[0], (await otherScope.Content.ReadFromJsonAsync<TicketCategoryDto>())!.Id);
        var global = await h.Client.PostAsJsonAsync("/api/v1/categories", new CreateTicketCategoryDto { Name = "Network Operations", Type = TicketCategoryType.Incident });
        global.EnsureSuccessStatusCode();
        var globalAgain = await h.Client.PostAsJsonAsync("/api/v1/categories", new CreateTicketCategoryDto { Name = " NETWORK  operations ", Type = TicketCategoryType.Incident });
        Assert.Equal((await global.Content.ReadFromJsonAsync<TicketCategoryDto>())!.Id, (await globalAgain.Content.ReadFromJsonAsync<TicketCategoryDto>())!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_enforces_permission_validation_and_inactive_duplicate_state(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var dto = new CreateTicketCategoryDto { Name = "Retired category", Type = TicketCategoryType.Change, TenantId = Tenant, IsActive = false };
        (await h.Client.PostAsJsonAsync("/api/v1/categories", dto)).EnsureSuccessStatusCode();
        dto.Name = "  RETIRED   category "; dto.IsActive = true;
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync("/api/v1/categories", dto)).StatusCode);
        dto.Name = new string('x', 121);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync("/api/v1/categories", dto)).StatusCode);
        dto.Name = "Valid"; dto.Type = (TicketCategoryType)999;
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsJsonAsync("/api/v1/categories", dto)).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.PostAsJsonAsync("/api/v1/categories", new CreateTicketCategoryDto { Name = "Forbidden" })).StatusCode);
        await h.WithDb(async db => Assert.Equal(1, await db.TicketCategories.CountAsync()));
    }

    private sealed class Harness(WebApplication app, HttpClient client, string sqlitePath, PostgreSqlContainer? postgres) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public static async Task<Harness> Create(bool usePostgres)
        {
            var path = Path.Combine(Path.GetTempPath(), $"category-{Guid.NewGuid():N}.db");
            PostgreSqlContainer? postgres = null;
            if (usePostgres) { postgres = new PostgreSqlBuilder("postgres:16").Build(); await postgres.StartAsync(); }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders(); builder.WebHost.UseTestServer(); builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<ITenantContext, TestTenant>();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => { if (postgres is not null) options.UseNpgsql(postgres.GetConnectionString()); else options.UseSqlite($"Data Source={path};Default Timeout=30"); });
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            builder.Services.AddAuthorization(options => { options.AddPolicy("TicketReadAccess", p => p.RequireAuthenticatedUser()); options.AddPolicy("HelpdeskAdmin", p => p.RequireRole("HelpdeskAdmin")); });
            var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapTicketCategoryEndpoints(); await app.StartAsync();
            var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "admin");
            var h = new Harness(app, client, path, postgres);
            await h.WithDb(async db => { await db.Database.EnsureCreatedAsync(); db.Organizations.AddRange(new Organization { Id = Tenant.ToString(), Name = "Synthetic tenant" }, new Organization { Id = OtherTenant.ToString(), Name = "Other synthetic tenant" }); await db.SaveChangesAsync(); });
            return h;
        }
        public async Task WithDb(Func<HelpdeskDbContext, Task> action) { await using var scope = app.Services.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()); }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); if (postgres is not null) await postgres.DisposeAsync(); if (File.Exists(sqlitePath)) File.Delete(sqlitePath); }
    }
    private sealed class TestTenant(IHttpContextAccessor context) : ITenantContext
    {
        public string? TenantId => Tenant.ToString();
        public string? UserId => "category-test";
        public bool IsHelpdeskAdmin => context.HttpContext is null || context.HttpContext.User.IsInRole("HelpdeskAdmin");
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "Synthetic operator") };
            if (Request.Headers.Authorization.ToString().EndsWith("admin", StringComparison.Ordinal)) claims.Add(new(ClaimTypes.Role, "HelpdeskAdmin"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
