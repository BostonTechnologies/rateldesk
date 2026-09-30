using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.API;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.API.Endpoints.Users;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Api;

public sealed class LocalAuthenticationEndpointsTests : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryDatabaseRoot _applicationDatabaseRoot = new();
    private readonly ServiceProvider _applicationDatabaseProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();
    private readonly InMemoryDatabaseRoot _identityDatabaseRoot = new();
    private readonly ServiceProvider _identityDatabaseProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();

    public LocalAuthenticationEndpointsTests()
    {
        var identityDatabaseName = $"local-auth-identity-{Guid.NewGuid():N}";
        var applicationDatabaseName = $"local-auth-application-{Guid.NewGuid():N}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseIsolatedTestStorage();
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Development");
            builder.UseSetting("Authentication:Mode", "Local");
            builder.UseSetting("Authentication:AllowInsecureLocalhost", "true");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Local",
                ["Authentication:AllowInsecureLocalhost"] = "true",
                // This test host skips bootstrap, which normally creates the image-signing key.
                ["StorageOptions:ImageSigningSecret"] = "synthetic-local-auth-image-secret"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<HelpdeskDbContext>>();
                services.AddDbContext<HelpdeskDbContext>(options => options
                    .UseInMemoryDatabase(applicationDatabaseName, _applicationDatabaseRoot)
                    .UseInternalServiceProvider(_applicationDatabaseProvider));
                services.RemoveAll<DbContextOptions<RatelDeskIdentityDbContext>>();
                services.AddDbContext<RatelDeskIdentityDbContext>(options => options
                    .UseInMemoryDatabase(identityDatabaseName, _identityDatabaseRoot)
                    .UseInternalServiceProvider(_identityDatabaseProvider));
            });
        });
    }

    public async Task InitializeAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
        await db.Database.EnsureCreatedAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = await users.CreateAsync(
            new ApplicationUser { UserName = "admin@example.test", Email = "admin@example.test", DisplayName = "Instance Admin", IsInstanceAdministrator = true },
            "correct horse battery staple");
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _applicationDatabaseProvider.DisposeAsync();
        await _identityDatabaseProvider.DisposeAsync();
    }

    private HttpClient CreateClient(WebApplicationFactoryClientOptions? options = null)
    {
        var client = _factory.CreateClient(options ?? new WebApplicationFactoryClientOptions());
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    [Fact]
    public async Task Local_login_issues_a_cookie_that_authenticates_subsequent_api_requests()
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var login = await client.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test",
            "correct horse battery staple"));

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Contains(login.Headers, header => string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase));

        var currentUser = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, currentUser.StatusCode);
        var access = await currentUser.Content.ReadFromJsonAsync<CurrentUserAccessDto>();
        Assert.True(access!.IsHelpdeskAdmin);
        Assert.NotNull(access.UserId);
    }

    [Fact]
    public async Task Switching_to_oidc_rejects_a_previously_issued_local_cookie()
    {
        using var local = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await local.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("RatelDesk.Local=", StringComparison.Ordinal)).Split(';', 2)[0];
        using var oidcFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:Mode", "Oidc");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Authentication:Mode"] = "Oidc" }));
        });
        using var oidc = oidcFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        oidc.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oidc.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Local_login_cookie_contains_the_resolved_scoped_role_permissions()
    {
        const string email = "scoped.technician@example.test";
        const string organizationId = "scoped-technician-organization";
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var create = await users.CreateAsync(
                new ApplicationUser { UserName = email, Email = email, DisplayName = "Scoped technician" },
                "correct horse battery staple");
            Assert.True(create.Succeeded, string.Join(", ", create.Errors.Select(error => error.Description)));
            var account = await users.FindByEmailAsync(email);

            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.Add(new Organization { Id = organizationId, Name = "Scoped technician organization" });
            db.Users.Add(new User { Id = account!.Id, Name = "Scoped technician", Email = email, OrganizationId = organizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = account.Id,
                OrganizationId = organizationId,
                RoleKey = ScopedRoleCatalog.Technician
            });
            await db.SaveChangesAsync();
        }

        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            email, "correct horse battery staple"));

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookieHeader = Assert.Single(login.Headers.GetValues("Set-Cookie"), header => header.StartsWith("RatelDesk.Local=", StringComparison.Ordinal));
        var cookieValue = cookieHeader["RatelDesk.Local=".Length..].Split(';', 2)[0];
        var cookieOptions = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(LocalAuthenticationOptions.Scheme);
        var ticket = cookieOptions.TicketDataFormat.Unprotect(cookieValue);

        Assert.NotNull(ticket);
        Assert.True(ticket!.Principal.IsInRole(HelpdeskPermissions.IncidentWrite));
        Assert.True(ticket.Principal.IsInRole(HelpdeskPermissions.RequestWrite));
        Assert.True(ticket.Principal.IsInRole(HelpdeskPermissions.ChangeWrite));
    }

    [Fact]
    public async Task Notification_hub_rejects_anonymous_negotiation()
    {
        using var anonymous = CreateClient();

        var negotiate = await anonymous.PostAsync("/notification-hub/negotiate?negotiateVersion=1", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task Authenticated_non_administrator_cannot_use_legacy_email_ingestion()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var create = await users.CreateAsync(
            new ApplicationUser { UserName = "operator@example.test", Email = "operator@example.test", DisplayName = "Operator" },
            "correct horse battery staple");
        Assert.True(create.Succeeded, string.Join(", ", create.Errors.Select(error => error.Description)));

        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "operator@example.test", "correct horse battery staple"));
        var ingest = await client.PostAsJsonAsync("/api/v1/ingestEmail", "Subject\nBody");

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ingest.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_create_activate_a_second_administrator_and_last_admin_guard_still_applies()
    {
        using var first = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await first.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        var create = await first.PostAsJsonAsync("/api/v1/local-auth/users",
            new LocalAuthenticationEndpoints.CreateLocalAccountRequest("Second administrator", "second.admin@example.test", true));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var activation = await create.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.LocalAccountActivationResponse>();
        Assert.NotNull(activation);

        using var second = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsJsonAsync("/api/v1/local-auth/activate",
            new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(activation.Email, activation.ActivationToken, "a second strong administrator password"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest(activation.Email, "a second strong administrator password"))).StatusCode);
        var access = await second.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me");
        Assert.True(access!.IsHelpdeskAdmin);
        Assert.Equal(HttpStatusCode.Conflict, (await first.DeleteAsync($"/api/v1/users/{activation.UserId}")).StatusCode);
        Assert.True((await second.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me"))!.IsHelpdeskAdmin);

        await using var scope = _factory.Services.CreateAsyncScope();
        var firstId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("admin@example.test"))!.Id;
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsync($"/api/v1/local-auth/users/{firstId}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync($"/api/v1/local-auth/users/{activation.UserId}/disable", null)).StatusCode);
    }

    [Fact]
    public async Task Local_cookie_mutations_reject_cross_site_requests_even_with_a_custom_header()
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        var result = await client.PostAsJsonAsync("/api/v1/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.StartAuthenticatorSetupRequest("correct horse battery staple"));
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        client.DefaultRequestHeaders.Remove("X-Requested-With");
        var withoutProtection = await client.PostAsJsonAsync("/api/v1/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.StartAuthenticatorSetupRequest("correct horse battery staple"));
        Assert.Equal(HttpStatusCode.Forbidden, withoutProtection.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/local-auth/login")]
    [InlineData("/api/v1/local-auth/login/")]
    public async Task Anonymous_local_login_rejects_cross_site_and_unmarked_requests(string route)
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, login)).StatusCode);
        client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
        client.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, login)).StatusCode);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(route, login)).StatusCode);
    }

    [Fact]
    public async Task More_than_five_successful_logins_can_share_the_web_server_address()
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        for (var attempt = 0; attempt < 8; attempt++)
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/local-auth/login",
                new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
    }

    [Fact]
    public async Task Last_enabled_instance_administrator_cannot_be_disabled()
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test",
            "correct horse battery staple"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var administrator = await users.FindByEmailAsync("admin@example.test");

        var disable = await client.PostAsync($"/api/v1/local-auth/users/{administrator!.Id}/disable", content: null);

        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.True((await users.FindByIdAsync(administrator.Id))!.IsEnabled);
    }

    [Fact]
    public async Task Disabled_local_account_loses_an_existing_cookie_session()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var operatorCreate = await users.CreateAsync(
            new ApplicationUser { UserName = "operator@example.test", Email = "operator@example.test", DisplayName = "Operator" },
            "correct horse battery staple");
        Assert.True(operatorCreate.Succeeded, string.Join(", ", operatorCreate.Errors.Select(error => error.Description)));
        var operatorUser = await users.FindByEmailAsync("operator@example.test");

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var operatorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await operatorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "operator@example.test", "correct horse battery staple"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsync($"/api/v1/local-auth/users/{operatorUser!.Id}/disable", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await operatorClient.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Administrator_can_reenable_a_disabled_local_account()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var operatorCreate = await users.CreateAsync(
            new ApplicationUser { UserName = "reenable.operator@example.test", Email = "reenable.operator@example.test", DisplayName = "Reenable Operator" },
            "correct horse battery staple");
        Assert.True(operatorCreate.Succeeded, string.Join(", ", operatorCreate.Errors.Select(error => error.Description)));
        var operatorUser = await users.FindByEmailAsync("reenable.operator@example.test");

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsync($"/api/v1/local-auth/users/{operatorUser!.Id}/disable", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsync($"/api/v1/local-auth/users/{operatorUser.Id}/enable", content: null)).StatusCode);

        using var operatorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await operatorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "reenable.operator@example.test", "correct horse battery staple"));

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.True((await users.FindByIdAsync(operatorUser.Id))!.IsEnabled);
    }

    [Fact]
    public async Task Administrator_can_create_and_activate_a_local_account_with_a_single_use_token()
    {
        const string organizationId = "local-account-organization";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            setupDb.Organizations.Add(new Organization { Id = organizationId, Name = "Local account organization" });
            await setupDb.SaveChangesAsync();
        }

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);

        var create = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/users", new LocalAuthenticationEndpoints.CreateLocalAccountRequest(
            "New Operator", "new.operator@example.test")
        {
            OrganizationId = organizationId,
            IsTestUser = true
        });
        var activation = await create.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.LocalAccountActivationResponse>();

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(activation);
        Assert.False(string.IsNullOrWhiteSpace(activation.ActivationToken));
        await using var scope = _factory.Services.CreateAsyncScope();
        var domainDb = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var domainUser = await domainDb.Users.SingleAsync(user => user.Id == activation.UserId && user.Email == activation.Email);
        Assert.Equal(organizationId, domainUser.OrganizationId);
        Assert.True(domainUser.IsTestUser);
        Assert.Contains(
            await domainDb.ScopedRoleAssignments.ToListAsync(),
            assignment => assignment.UserId == activation.UserId &&
                          assignment.OrganizationId == organizationId &&
                          assignment.RoleKey == ScopedRoleCatalog.SelfServiceUser);
        var customerLink = await domainDb.CustomerAuthLinks.SingleAsync(link => link.LocalAccountId == activation.UserId);
        Assert.Equal("Local", customerLink.AuthProviderType);
        Assert.Equal(organizationId, (await domainDb.Customers.SingleAsync(customer => customer.Id == customerLink.CustomerId)).OrganizationId);

        var activate = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/activate", new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(
            activation.Email, activation.ActivationToken, "another secure passphrase"));
        var replay = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/activate", new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(
            activation.Email, activation.ActivationToken, "a different secure passphrase"));
        using var accountClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await accountClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            activation.Email, "another secure passphrase"));
        var access = await accountClient.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.NotNull(access);
        Assert.Equal(organizationId, access.PrimaryOrganizationId);
        Assert.Contains(organizationId, access.AllowedOrganizationIds);
        Assert.Contains(Helpdesk.Shared.Auth.HelpdeskPermissions.SelfServiceUser, access.Permissions);
        Assert.Contains(access.ScopedPermissionGrants, grant =>
            grant.OrganizationId == organizationId &&
            grant.Permission == HelpdeskPermissions.SelfServiceUser);
    }

    [Fact]
    public async Task Administrator_cannot_create_a_local_account_for_an_unknown_or_disabled_organization()
    {
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            setupDb.Organizations.Add(new Organization { Id = "disabled-local-account-organization", Name = "Disabled organization", IsEnabled = false });
            await setupDb.SaveChangesAsync();
        }

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);

        var unknownOrganization = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/users", new LocalAuthenticationEndpoints.CreateLocalAccountRequest(
            "Unknown Organization Operator", "unknown.organization.operator@example.test")
        {
            OrganizationId = "missing-organization"
        });
        var disabledOrganization = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/users", new LocalAuthenticationEndpoints.CreateLocalAccountRequest(
            "Disabled Organization Operator", "disabled.organization.operator@example.test")
        {
            OrganizationId = "disabled-local-account-organization"
        });

        Assert.Equal(HttpStatusCode.BadRequest, unknownOrganization.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, disabledOrganization.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByEmailAsync("unknown.organization.operator@example.test"));
        Assert.Null(await users.FindByEmailAsync("disabled.organization.operator@example.test"));
    }

    [Fact]
    public async Task Administrator_cannot_create_a_scoped_local_account_without_an_organization()
    {
        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);

        var create = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/users", new LocalAuthenticationEndpoints.CreateLocalAccountRequest(
            "Unscoped Operator", "unscoped.operator@example.test"));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await users.FindByEmailAsync("unscoped.operator@example.test"));
    }

    [Fact]
    public async Task Administrator_can_assign_the_technician_bundle_to_a_local_account()
    {
        const string organizationId = "local-technician-organization";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            setupDb.Organizations.Add(new Organization { Id = organizationId, Name = "Local technician organization" });
            await setupDb.SaveChangesAsync();
        }

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);

        var create = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/users", new LocalAuthenticationEndpoints.CreateLocalAccountRequest(
            "Local Technician", "local.technician@example.test")
        {
            OrganizationId = organizationId,
            Role = "Technician"
        });
        var activation = await create.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.LocalAccountActivationResponse>();
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(activation);

        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/activate", new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(
            activation!.Email, activation.ActivationToken, "another secure passphrase"))).StatusCode);
        using var technicianClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await technicianClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            activation.Email, "another secure passphrase"))).StatusCode);

        var access = await technicianClient.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me");

        Assert.NotNull(access);
        Assert.Equal(organizationId, access.PrimaryOrganizationId);
        Assert.Contains(Helpdesk.Shared.Auth.HelpdeskPermissions.IncidentWrite, access.Permissions);
        Assert.Contains(Helpdesk.Shared.Auth.HelpdeskPermissions.RequestWrite, access.Permissions);
        Assert.Contains(Helpdesk.Shared.Auth.HelpdeskPermissions.ChangeWrite, access.Permissions);
    }

    [Fact]
    public async Task Administrator_can_replace_a_local_accounts_tenant_scoped_role_assignments()
    {
        const string firstOrganizationId = "first-scoped-role-organization";
        const string secondOrganizationId = "second-scoped-role-organization";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            setupDb.Organizations.AddRange(
                new Organization { Id = firstOrganizationId, Name = "First scoped role organization" },
                new Organization { Id = secondOrganizationId, Name = "Second scoped role organization" });
            await setupDb.SaveChangesAsync();
        }

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);
        var create = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/users", new LocalAuthenticationEndpoints.CreateLocalAccountRequest(
            "Scoped Operator", "scoped.operator@example.test")
        {
            OrganizationId = firstOrganizationId,
            Role = "Technician"
        });
        var activation = await create.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.LocalAccountActivationResponse>();
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(activation);

        var replace = await administratorClient.PutAsJsonAsync($"/api/v1/local-auth/users/{activation!.UserId}/assignments",
            new LocalAuthenticationEndpoints.ReplaceLocalScopedRoleAssignmentsRequest(
            [new LocalAuthenticationEndpoints.LocalScopedRoleAssignment(ScopedRoleCatalog.Technician, secondOrganizationId)]));
        var assignments = await administratorClient.GetFromJsonAsync<LocalAuthenticationEndpoints.LocalScopedRoleAssignmentsResponse>(
            $"/api/v1/local-auth/users/{activation.UserId}/assignments");

        Assert.Equal(HttpStatusCode.NoContent, replace.StatusCode);
        Assert.Equal([new LocalAuthenticationEndpoints.LocalScopedRoleAssignment(ScopedRoleCatalog.Technician, secondOrganizationId)], assignments!.Assignments);
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/activate", new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(
            activation.Email, activation.ActivationToken, "another secure passphrase"))).StatusCode);
        using var accountClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await accountClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            activation.Email, "another secure passphrase"))).StatusCode);
        var access = await accountClient.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me");

        Assert.Contains(access!.ScopedPermissionGrants, grant =>
            grant.OrganizationId == secondOrganizationId && grant.Permission == HelpdeskPermissions.IncidentWrite);
        Assert.DoesNotContain(access.ScopedPermissionGrants, grant => grant.OrganizationId == firstOrganizationId);

        var removeAll = await administratorClient.PutAsJsonAsync($"/api/v1/local-auth/users/{activation.UserId}/assignments",
            new LocalAuthenticationEndpoints.ReplaceLocalScopedRoleAssignmentsRequest([]));
        Assert.Equal(HttpStatusCode.NoContent, removeAll.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await accountClient.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await accountClient.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest(activation.Email, "another secure passphrase"))).StatusCode);
        var revokedAccess = await accountClient.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me");
        Assert.Empty(revokedAccess!.Permissions);
        Assert.Empty(revokedAccess.ScopedPermissionGrants);
        Assert.True(revokedAccess.UsesScopedPermissions);
    }

    [Fact]
    public async Task Role_definition_api_requires_an_administrator_and_protects_built_ins()
    {
        const string organizationId = "custom-role-organization";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            setupDb.Organizations.Add(new Organization { Id = organizationId, Name = "Custom role organization" });
            await setupDb.SaveChangesAsync();
        }

        using var anonymous = CreateClient();
        var anonymousList = await anonymous.GetAsync("/api/v1/admin/role-definitions/");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);

        var invalidWriter = await administratorClient.PostAsJsonAsync("/api/v1/admin/role-definitions/",
            new RoleDefinitionEndpoints.CreateRoleDefinitionRequest(
                "Incomplete Incident Writer",
                null,
                organizationId,
                [HelpdeskPermissions.IncidentWrite]));
        var created = await administratorClient.PostAsJsonAsync("/api/v1/admin/role-definitions/",
            new RoleDefinitionEndpoints.CreateRoleDefinitionRequest(
                "Incident Reader",
                null,
                organizationId,
                [HelpdeskPermissions.IncidentUser]));
        var definitions = await administratorClient.GetFromJsonAsync<List<RoleDefinitionEndpoints.RoleDefinitionResponse>>(
            "/api/v1/admin/role-definitions/");
        var technician = Assert.Single(definitions!, definition => definition.Key == ScopedRoleCatalog.Technician);
        var deleteBuiltIn = await administratorClient.DeleteAsync($"/api/v1/admin/role-definitions/{technician.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, invalidWriter.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(definitions!, definition =>
            definition.Key == "custom.incident-reader" &&
            definition.OwnerOrganizationId == organizationId &&
            definition.Permissions.SequenceEqual([HelpdeskPermissions.IncidentUser]));
        Assert.True(technician.IsBuiltIn);
        Assert.True(technician.IsProtected);
        Assert.Equal(HttpStatusCode.Conflict, deleteBuiltIn.StatusCode);
    }

    [Fact]
    public async Task Tenant_membership_route_allows_operational_roles_but_not_tenant_administrator()
    {
        const string organizationId = "tenant-membership-organization";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            setupDb.Organizations.Add(new Organization { Id = organizationId, Name = "Tenant membership organization" });
            await setupDb.SaveChangesAsync();
        }

        using var administratorClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administratorClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);
        var create = await administratorClient.PostAsJsonAsync("/api/v1/local-auth/users", new LocalAuthenticationEndpoints.CreateLocalAccountRequest(
            "Tenant Member", "tenant.member@example.test")
        { OrganizationId = organizationId });
        var activation = await create.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.LocalAccountActivationResponse>();
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var route = $"/api/v1/tenant-admin/organizations/{organizationId}/users/{activation!.UserId}/assignments";
        var technician = await administratorClient.PutAsJsonAsync(route,
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([ScopedRoleCatalog.Technician]));
        var selfService = await administratorClient.PutAsJsonAsync(route,
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([ScopedRoleCatalog.SelfServiceUser]));
        var membership = await administratorClient.GetFromJsonAsync<TenantAdministrationEndpoints.TenantMembershipResponse>(route);

        var tenantAdministrator = await administratorClient.PutAsJsonAsync(route,
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([ScopedRoleCatalog.TenantAdministrator]));
        Assert.Equal(HttpStatusCode.NoContent, technician.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tenantAdministrator.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, selfService.StatusCode);
        Assert.Equal([ScopedRoleCatalog.SelfServiceUser], membership!.RoleKeys);
    }

    [Fact]
    public async Task Tenant_administrator_can_manage_membership_only_in_its_assigned_organization()
    {
        const string organizationId = "tenant-admin-organization";
        const string otherOrganizationId = "other-tenant-admin-organization";
        string tenantAdministratorId;
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createTenantAdmin = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "tenant.admin@example.test", Email = "tenant.admin@example.test", DisplayName = "Tenant Admin" },
                "correct horse battery staple");
            Assert.True(createTenantAdmin.Succeeded);
            tenantAdministratorId = (await identityUsers.FindByEmailAsync("tenant.admin@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = organizationId, Name = "Tenant admin organization" },
                new Organization { Id = otherOrganizationId, Name = "Other tenant organization" });
            db.Users.Add(new User { Id = tenantAdministratorId, Name = "Tenant Admin", Email = "tenant.admin@example.test", OrganizationId = organizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = tenantAdministratorId, OrganizationId = organizationId, RoleKey = ScopedRoleCatalog.TenantAdministrator });
            await db.SaveChangesAsync();
        }

        using var tenantAdministrator = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await tenantAdministrator.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "tenant.admin@example.test", "correct horse battery staple"))).StatusCode);
        var createTarget = await tenantAdministrator.PostAsJsonAsync(
            $"/api/v1/tenant-admin/organizations/{organizationId}/users/",
            new TenantAdministrationEndpoints.CreateTenantLocalAccountRequest("Tenant Target", "tenant.target@example.test"));
        var crossTenantInvitation = await tenantAdministrator.PostAsJsonAsync(
            $"/api/v1/tenant-admin/organizations/{otherOrganizationId}/users/",
            new TenantAdministrationEndpoints.CreateTenantLocalAccountRequest("Cross tenant", "cross.tenant@example.test"));
        var target = await createTarget.Content.ReadFromJsonAsync<TenantAdministrationEndpoints.TenantLocalAccountInvitationResponse>();
        Assert.Equal(HttpStatusCode.Created, createTarget.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantInvitation.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(target?.ActivationToken));
        await using (var targetScope = _factory.Services.CreateAsyncScope())
        {
            var db = targetScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = target!.UserId,
                OrganizationId = organizationId,
                RoleKey = ScopedRoleCatalog.TenantAdministrator
            });
            await db.SaveChangesAsync();
        }

        var ownRoute = $"/api/v1/tenant-admin/organizations/{organizationId}/users/{target!.UserId}/assignments";
        var otherRoute = $"/api/v1/tenant-admin/organizations/{otherOrganizationId}/users/{target.UserId}/assignments";
        var removeSelfService = await tenantAdministrator.PutAsJsonAsync(ownRoute,
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([]));
        var ownTenant = await tenantAdministrator.PutAsJsonAsync(ownRoute,
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([ScopedRoleCatalog.SelfServiceUser]));
        var otherTenant = await tenantAdministrator.PutAsJsonAsync(otherRoute,
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([ScopedRoleCatalog.SelfServiceUser]));
        var availableOrganizations = await tenantAdministrator.GetFromJsonAsync<List<TenantAdministrationEndpoints.TenantOrganizationResponse>>(
            "/api/v1/tenant-admin/organizations");
        var members = await tenantAdministrator.GetFromJsonAsync<List<TenantAdministrationEndpoints.TenantMemberResponse>>(
            $"/api/v1/tenant-admin/organizations/{organizationId}/users/");

        Assert.Equal(HttpStatusCode.NoContent, removeSelfService.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ownTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
        Assert.Collection(availableOrganizations!, organization => Assert.Equal(organizationId, organization.Id));
        var targetMembership = Assert.Single(members!, member => member.UserId == target.UserId);
        Assert.Equal([ScopedRoleCatalog.SelfServiceUser], targetMembership.RoleKeys);
        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var assignedRoles = await verificationScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().ScopedRoleAssignments
            .Where(assignment => assignment.UserId == target.UserId && assignment.OrganizationId == organizationId)
            .Select(assignment => assignment.RoleKey)
            .ToListAsync();
        Assert.Contains(ScopedRoleCatalog.SelfServiceUser, assignedRoles);
        Assert.Contains(ScopedRoleCatalog.TenantAdministrator, assignedRoles);
        var auditMessages = await verificationScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().ActivityLogs
            .Where(log => log.RelatedEntityId == target.UserId)
            .Select(log => log.Message)
            .ToListAsync();
        Assert.Contains(auditMessages, message => message.Contains("Created a tenant-local self-service invitation", StringComparison.Ordinal));
        Assert.Contains(auditMessages, message => message.Contains("Removed tenant self-service access", StringComparison.Ordinal));
        Assert.Contains(auditMessages, message => message.Contains("Granted tenant self-service access", StringComparison.Ordinal));
        Assert.DoesNotContain(target.ActivationToken, auditMessages);
    }

    [Fact]
    public async Task Tenant_role_assignment_permission_alone_cannot_invite_a_local_account()
    {
        const string organizationId = "role-assignment-only-organization";
        const string roleKey = "role-assignment-only";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "role.assigner@example.test", Email = "role.assigner@example.test", DisplayName = "Role Assigner" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("role.assigner@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var role = new Role
            {
                Id = roleKey,
                Key = roleKey,
                Name = "Role assignment only",
                Scope = RoleScopeKind.Tenant,
                OwnerOrganizationId = organizationId
            };
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = HelpdeskPermissions.TenantRolesAssign });
            db.Organizations.Add(new Organization { Id = organizationId, Name = "Role assignment only organization" });
            db.Roles.Add(role);
            db.Users.Add(new User { Id = userId, Name = "Role Assigner", Email = "role.assigner@example.test", OrganizationId = organizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = userId, OrganizationId = organizationId, RoleKey = roleKey });
            await db.SaveChangesAsync();
        }

        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "role.assigner@example.test", "correct horse battery staple"))).StatusCode);

        var invite = await client.PostAsJsonAsync(
            $"/api/v1/tenant-admin/organizations/{organizationId}/users/",
            new TenantAdministrationEndpoints.CreateTenantLocalAccountRequest("Denied invite", "denied.invite@example.test"));

        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
    }

    [Fact]
    public async Task Tenant_administrator_can_manage_only_its_custom_roles_below_the_delegation_ceiling()
    {
        const string organizationId = "tenant-role-owner";
        const string otherOrganizationId = "other-role-owner";
        string tenantAdministratorId = string.Empty;
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "tenant.role.admin@example.test", Email = "tenant.role.admin@example.test", DisplayName = "Tenant Role Admin" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("tenant.role.admin@example.test"))!.Id;
            tenantAdministratorId = userId;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = organizationId, Name = "Tenant role owner" },
                new Organization { Id = otherOrganizationId, Name = "Other role owner" });
            db.Users.Add(new User { Id = userId, Name = "Tenant Role Admin", Email = "tenant.role.admin@example.test", OrganizationId = organizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = userId, OrganizationId = organizationId, RoleKey = ScopedRoleCatalog.TenantAdministrator });
            db.Roles.Add(new Role
            {
                Id = "other-tenant-role",
                Key = "custom.other-tenant-reader",
                Name = "Other tenant reader",
                Scope = RoleScopeKind.Tenant,
                OwnerOrganizationId = otherOrganizationId,
                Permissions = [new RolePermission { Permission = HelpdeskPermissions.IncidentRead }]
            });
            await db.SaveChangesAsync();
        }

        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "tenant.role.admin@example.test", "correct horse battery staple"))).StatusCode);

        var safeCreate = await client.PostAsJsonAsync("/api/v1/admin/role-definitions/",
            new RoleDefinitionEndpoints.CreateRoleDefinitionRequest(
                "Tenant incident reader", null, organizationId, [HelpdeskPermissions.IncidentRead]));
        var crossTenantCreate = await client.PostAsJsonAsync("/api/v1/admin/role-definitions/",
            new RoleDefinitionEndpoints.CreateRoleDefinitionRequest(
                "Other tenant incident reader", null, otherOrganizationId, [HelpdeskPermissions.IncidentRead]));
        var crossTenantUpdate = await client.PutAsJsonAsync("/api/v1/admin/role-definitions/other-tenant-role",
            new RoleDefinitionEndpoints.UpdateRoleDefinitionRequest("Changed other tenant role", [HelpdeskPermissions.IncidentRead]));
        var crossTenantDelete = await client.DeleteAsync("/api/v1/admin/role-definitions/other-tenant-role");
        var elevatedCreate = await client.PostAsJsonAsync("/api/v1/admin/role-definitions/",
            new RoleDefinitionEndpoints.CreateRoleDefinitionRequest(
                "Tenant account manager", null, organizationId, [HelpdeskPermissions.TenantUsersManage]));
        var safeRole = await safeCreate.Content.ReadFromJsonAsync<RoleDefinitionEndpoints.RoleDefinitionResponse>();
        var ownUpdate = await client.PutAsJsonAsync($"/api/v1/admin/role-definitions/{safeRole!.Id}",
            new RoleDefinitionEndpoints.UpdateRoleDefinitionRequest(
                "Tenant incident manager", [HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite]));
        var invitation = await client.PostAsJsonAsync($"/api/v1/tenant-admin/organizations/{organizationId}/users/",
            new TenantAdministrationEndpoints.CreateTenantLocalAccountRequest("Custom Role Member", "custom.role.member@example.test"));
        var target = await invitation.Content.ReadFromJsonAsync<TenantAdministrationEndpoints.TenantLocalAccountInvitationResponse>();
        var assignment = await client.PutAsJsonAsync(
            $"/api/v1/tenant-admin/organizations/{organizationId}/users/{target!.UserId}/assignments",
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([safeRole!.Key]));
        var visibleRoles = await client.GetFromJsonAsync<List<RoleDefinitionEndpoints.RoleDefinitionResponse>>(
            "/api/v1/admin/role-definitions/");
        var delegableRolesResponse = await client.GetAsync(
            $"/api/v1/tenant-admin/organizations/{organizationId}/users/roles");
        var delegableRoles = await delegableRolesResponse.Content.ReadFromJsonAsync<List<TenantAdministrationEndpoints.TenantDelegableRoleResponse>>();
        var removeAssignment = await client.PutAsJsonAsync(
            $"/api/v1/tenant-admin/organizations/{organizationId}/users/{target!.UserId}/assignments",
            new TenantAdministrationEndpoints.ReplaceTenantMembershipRequest([]));
        var deleteOwnRole = await client.DeleteAsync($"/api/v1/admin/role-definitions/{safeRole.Id}");

        Assert.Equal(HttpStatusCode.Created, safeCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantDelete.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, elevatedCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ownUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, invitation.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, assignment.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removeAssignment.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteOwnRole.StatusCode);
        Assert.Equal(HttpStatusCode.OK, delegableRolesResponse.StatusCode);
        Assert.Contains(visibleRoles!, role => role.Key == "custom.tenant-incident-reader" && role.Name == "Tenant incident manager" && role.OwnerOrganizationId == organizationId);
        Assert.DoesNotContain(visibleRoles!, role => role.Key == "custom.other-tenant-reader");
        Assert.Contains(visibleRoles!, role => role.IsBuiltIn);
        Assert.Contains(delegableRoles!, role => role.Key == safeRole.Key && role.Name == "Tenant incident manager");
        Assert.Contains(delegableRoles!, role => role.Key == ScopedRoleCatalog.SelfServiceUser);
        Assert.Contains(delegableRoles!, role => role.Key == ScopedRoleCatalog.Technician);
        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var roleAudits = await verificationScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().ActivityLogs
            .Where(log => log.RelatedEntityId == safeRole.Id)
            .ToListAsync();
        Assert.All(roleAudits, audit => Assert.Equal(tenantAdministratorId, audit.UserId));
        Assert.Contains(roleAudits, audit => audit.Message.Contains($"Created custom role 'Tenant incident reader' in organization '{organizationId}'", StringComparison.Ordinal));
        Assert.Contains(roleAudits, audit => audit.Message.Contains("Updated custom role 'Tenant incident reader' to 'Tenant incident manager'", StringComparison.Ordinal) && audit.Message.Contains("->", StringComparison.Ordinal));
        Assert.Contains(roleAudits, audit => audit.Message.Contains($"Deleted custom role 'Tenant incident manager' in organization '{organizationId}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_user_cannot_queue_a_knowledge_suggestion_for_a_foreign_tenant_ticket()
    {
        const string userOrganizationId = "knowledge-suggestion-user-organization";
        const string ticketOrganizationId = "knowledge-suggestion-ticket-organization";
        const string ticketId = "11111111-1111-1111-1111-111111111111";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "knowledge.suggestion.user@example.test", Email = "knowledge.suggestion.user@example.test", DisplayName = "Knowledge Suggestion User" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("knowledge.suggestion.user@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = userOrganizationId, Name = "Knowledge suggestion user organization" },
                new Organization { Id = ticketOrganizationId, Name = "Knowledge suggestion ticket organization" });
            db.Users.Add(new User
            {
                Id = userId,
                Name = "Knowledge Suggestion User",
                Email = "knowledge.suggestion.user@example.test",
                OrganizationId = userOrganizationId,
                Role = "User"
            });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = userOrganizationId,
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            db.Incidents.Add(new Incident
            {
                Id = ticketId,
                OrganizationId = ticketOrganizationId,
                RequesterEmail = "foreign.requester@example.test",
                Title = "Foreign tenant incident",
                Description = "A local user from another tenant must not queue AI work for this ticket."
            });
            await db.SaveChangesAsync();
            Assert.True(await db.Tickets.AnyAsync(ticket => ticket.Id == ticketId));

            var authorizationFailure = await TicketEndpoints.AuthorizeTicketViewAsync(
                "incidents",
                ticketId,
                new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim("auth_mode", "local")
                ], "Local")),
                setupScope.ServiceProvider.GetRequiredService<ICurrentUserAccessService>(),
                db,
                CancellationToken.None);

            Assert.IsType<ForbidHttpResult>(authorizationFailure);
        }
    }

    [Fact]
    public async Task Scoped_technician_management_access_is_limited_to_the_assigned_tenant()
    {
        const string assignedOrganizationId = "ticket-management-assigned-organization";
        const string foreignOrganizationId = "ticket-management-foreign-organization";
        const string incidentId = "ticket-management-incident";
        const string requestId = "ticket-management-request";
        const string changeId = "ticket-management-change";
        const string foreignIncidentId = "ticket-management-foreign-incident";

        await using var setupScope = _factory.Services.CreateAsyncScope();
        var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var createUser = await identityUsers.CreateAsync(
            new ApplicationUser { UserName = "ticket.manager@example.test", Email = "ticket.manager@example.test", DisplayName = "Ticket Manager" },
            "correct horse battery staple");
        Assert.True(createUser.Succeeded);
        var userId = (await identityUsers.FindByEmailAsync("ticket.manager@example.test"))!.Id;

        var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        db.Organizations.AddRange(
            new Organization { Id = assignedOrganizationId, Name = "Assigned ticket organization" },
            new Organization { Id = foreignOrganizationId, Name = "Foreign ticket organization" });
        db.Users.Add(new User
        {
            Id = userId,
            Name = "Ticket Manager",
            Email = "ticket.manager@example.test",
            OrganizationId = assignedOrganizationId,
            Role = "User"
        });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
        {
            UserId = userId,
            OrganizationId = assignedOrganizationId,
            RoleKey = ScopedRoleCatalog.Technician
        });
        db.Incidents.AddRange(
            new Incident { Id = incidentId, OrganizationId = assignedOrganizationId, Title = "Managed incident", Description = "Managed incident" },
            new Incident { Id = foreignIncidentId, OrganizationId = foreignOrganizationId, Title = "Foreign incident", Description = "Foreign incident" });
        db.Requests.Add(new Request { Id = requestId, OrganizationId = assignedOrganizationId, Title = "Managed request", Description = "Managed request" });
        db.Changes.Add(new Change { Id = changeId, OrganizationId = assignedOrganizationId, Title = "Managed change", Description = "Managed change" });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("auth_mode", "local")
        ], "Local"));
        var accessService = setupScope.ServiceProvider.GetRequiredService<ICurrentUserAccessService>();

        Assert.Null(await TicketEndpoints.AuthorizeTicketManageAsync(incidentId, principal, accessService, db, CancellationToken.None));
        Assert.Null(await TicketEndpoints.AuthorizeTicketManageAsync(requestId, principal, accessService, db, CancellationToken.None));
        Assert.Null(await TicketEndpoints.AuthorizeTicketManageAsync(changeId, principal, accessService, db, CancellationToken.None));
        Assert.IsType<ForbidHttpResult>(await TicketEndpoints.AuthorizeTicketManageAsync(foreignIncidentId, principal, accessService, db, CancellationToken.None));
    }

    [Fact]
    public async Task Self_service_user_has_no_management_grant_for_foreign_tenant_tickets()
    {
        const string userOrganizationId = "delete-user-organization";
        const string ticketOrganizationId = "delete-ticket-organization";
        const string incidentId = "delete-foreign-incident";
        const string requestId = "delete-foreign-request";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "delete.user@example.test", Email = "delete.user@example.test", DisplayName = "Delete User" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("delete.user@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = userOrganizationId, Name = "Delete user organization" },
                new Organization { Id = ticketOrganizationId, Name = "Delete ticket organization" });
            db.Users.Add(new User { Id = userId, Name = "Delete User", Email = "delete.user@example.test", OrganizationId = userOrganizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = userOrganizationId,
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            db.Incidents.Add(new Incident { Id = incidentId, OrganizationId = ticketOrganizationId, Title = "Foreign incident", Description = "Must not be deleted" });
            db.Requests.Add(new Request { Id = requestId, OrganizationId = ticketOrganizationId, Title = "Foreign request", Description = "Must not be deleted" });
            await db.SaveChangesAsync();

            var access = await setupScope.ServiceProvider.GetRequiredService<ICurrentUserAccessService>().ResolveAsync(
                new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim("auth_mode", "local")
                ], "Local")));

            Assert.False(access.CanManageIncident(ticketOrganizationId));
            Assert.False(access.CanManageRequest(ticketOrganizationId));
        }
    }

    [Fact]
    public async Task Request_workflow_timeline_uses_scoped_manager_grants_instead_of_primary_tenant()
    {
        const string homeOrganizationId = "workflow-timeline-home-organization";
        const string managedOrganizationId = "workflow-timeline-managed-organization";
        const string foreignOrganizationId = "workflow-timeline-foreign-organization";
        const string managedRequestId = "workflow-timeline-managed-request";
        const string foreignRequestId = "workflow-timeline-foreign-request";

        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "workflow.timeline.manager@example.test", Email = "workflow.timeline.manager@example.test", DisplayName = "Workflow Timeline Manager" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("workflow.timeline.manager@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = homeOrganizationId, Name = "Workflow timeline home organization" },
                new Organization { Id = managedOrganizationId, Name = "Workflow timeline managed organization" },
                new Organization { Id = foreignOrganizationId, Name = "Workflow timeline foreign organization" });
            db.Users.Add(new User { Id = userId, Name = "Workflow Timeline Manager", Email = "workflow.timeline.manager@example.test", OrganizationId = homeOrganizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = managedOrganizationId,
                RoleKey = ScopedRoleCatalog.Technician
            });
            db.Requests.AddRange(
                new Request { Id = managedRequestId, OrganizationId = managedOrganizationId, Title = "Managed workflow request", Description = "Managed workflow request" },
                new Request { Id = foreignRequestId, OrganizationId = foreignOrganizationId, Title = "Foreign workflow request", Description = "Foreign workflow request" });
            await db.SaveChangesAsync();
        }

        using var manager = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "workflow.timeline.manager@example.test", "correct horse battery staple"))).StatusCode);

        var managed = await manager.GetAsync($"/api/v1/requests/{managedRequestId}/workflow-timeline");
        var foreign = await manager.GetAsync($"/api/v1/requests/{foreignRequestId}/workflow-timeline");

        Assert.Equal(HttpStatusCode.OK, managed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Ai_assistant_routes_use_scoped_manager_grants_instead_of_primary_tenant()
    {
        const string homeOrganizationId = "ai-assistant-home-organization";
        const string managedOrganizationId = "ai-assistant-managed-organization";
        const string foreignOrganizationId = "ai-assistant-foreign-organization";
        const string managedIncidentId = "ai-assistant-managed-incident";
        const string foreignIncidentId = "ai-assistant-foreign-incident";

        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "ai.assistant.manager@example.test", Email = "ai.assistant.manager@example.test", DisplayName = "AI Assistant Manager" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("ai.assistant.manager@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = homeOrganizationId, Name = "AI assistant home organization" },
                new Organization { Id = managedOrganizationId, Name = "AI assistant managed organization" },
                new Organization { Id = foreignOrganizationId, Name = "AI assistant foreign organization" });
            db.Users.Add(new User { Id = userId, Name = "AI Assistant Manager", Email = "ai.assistant.manager@example.test", OrganizationId = homeOrganizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = managedOrganizationId,
                RoleKey = ScopedRoleCatalog.Technician
            });
            db.Incidents.AddRange(
                new Incident { Id = managedIncidentId, OrganizationId = managedOrganizationId, Title = "Managed AI assistant incident", Description = "Managed AI assistant incident" },
                new Incident { Id = foreignIncidentId, OrganizationId = foreignOrganizationId, Title = "Foreign AI assistant incident", Description = "Foreign AI assistant incident" });
            await db.SaveChangesAsync();
        }

        using var manager = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "ai.assistant.manager@example.test", "correct horse battery staple"))).StatusCode);

        var managed = await manager.GetAsync($"/api/v1/incidents/{managedIncidentId}/ai-assistant/eligible-configurations");
        var foreign = await manager.GetAsync($"/api/v1/incidents/{foreignIncidentId}/ai-assistant/eligible-configurations");

        Assert.Equal(HttpStatusCode.OK, managed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task Incident_activity_is_not_visible_outside_the_principal_tenant_scope()
    {
        const string incidentOrganizationId = "activity-incident-organization";
        const string userOrganizationId = "activity-user-organization";
        const string incidentId = "restricted-incident-activity";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "activity.reader@example.test", Email = "activity.reader@example.test", DisplayName = "Activity Reader" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("activity.reader@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = incidentOrganizationId, Name = "Restricted activity organization" },
                new Organization { Id = userOrganizationId, Name = "Activity reader organization" });
            db.Users.Add(new User { Id = userId, Name = "Activity Reader", Email = "activity.reader@example.test", OrganizationId = userOrganizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = userId, OrganizationId = userOrganizationId, RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.Incidents.Add(new Incident
            {
                Id = incidentId,
                OrganizationId = incidentOrganizationId,
                Title = "Restricted incident",
                Description = "Restricted incident description",
                RequesterEmail = "other.requester@example.test"
            });
            db.ActivityLogs.Add(new ActivityLog { TicketId = incidentId, UserId = "other-user", Message = "Restricted activity" });
            await db.SaveChangesAsync();
        }

        using var reader = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "activity.reader@example.test", "correct horse battery staple"))).StatusCode);
        var denied = await reader.GetAsync($"/api/v1/incidents/{incidentId}/activity");

        using var administrator = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);
        var permitted = await administrator.GetAsync($"/api/v1/incidents/{incidentId}/activity");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
    }

    [Fact]
    public async Task Ticket_attachments_require_parent_ticket_scope_for_reads_and_writes()
    {
        const string managedOrganizationId = "attachment-managed-organization";
        const string foreignOrganizationId = "attachment-foreign-organization";
        const string managedIncidentId = "attachment-managed-incident";
        const string foreignIncidentId = "attachment-foreign-incident";
        const string selfServiceIncidentId = "attachment-self-service-incident";
        var foreignAttachmentId = Guid.NewGuid();

        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "attachment.manager@example.test", Email = "attachment.manager@example.test", DisplayName = "Attachment Manager" },
                "correct horse battery staple");
            var createSelfServiceUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "attachment.self-service@example.test", Email = "attachment.self-service@example.test", DisplayName = "Attachment Self Service" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            Assert.True(createSelfServiceUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("attachment.manager@example.test"))!.Id;
            var selfServiceUserId = (await identityUsers.FindByEmailAsync("attachment.self-service@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = managedOrganizationId, Name = "Attachment managed organization" },
                new Organization { Id = foreignOrganizationId, Name = "Attachment foreign organization" });
            db.Users.Add(new User { Id = userId, Name = "Attachment Manager", Email = "attachment.manager@example.test", OrganizationId = managedOrganizationId, Role = "User" });
            db.Users.Add(new User { Id = selfServiceUserId, Name = "Attachment Self Service", Email = "attachment.self-service@example.test", OrganizationId = managedOrganizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = managedOrganizationId,
                RoleKey = ScopedRoleCatalog.Technician
            });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = selfServiceUserId,
                OrganizationId = managedOrganizationId,
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            var selfServiceCustomerId = $"customer-{selfServiceUserId}";
            db.Customers.Add(new Customer
            {
                Id = selfServiceCustomerId,
                Name = "Attachment Self Service",
                Email = "attachment.self-service@example.test",
                OrganizationId = managedOrganizationId
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = selfServiceCustomerId,
                LocalAccountId = selfServiceUserId,
                AuthProviderType = "Local",
                InviteStatus = CustomerInviteStatus.Active
            });
            db.Incidents.AddRange(
                new Incident { Id = managedIncidentId, OrganizationId = managedOrganizationId, Title = "Managed attachment incident", Description = "Managed attachment incident" },
                new Incident { Id = foreignIncidentId, OrganizationId = foreignOrganizationId, Title = "Foreign attachment incident", Description = "Foreign attachment incident" },
                new Incident { Id = selfServiceIncidentId, OrganizationId = managedOrganizationId, CustomerId = selfServiceCustomerId, Title = "Self-service attachment incident", Description = "Self-service attachment incident", RequesterEmail = "attachment.self-service@example.test" });
            db.Attachments.Add(new Attachment
            {
                Id = foreignAttachmentId,
                TicketId = foreignIncidentId,
                FileName = "foreign.txt",
                FilePath = "foreign.txt",
                ContentType = "text/plain",
                SizeBytes = 1
            });
            await db.SaveChangesAsync();
        }

        using var manager = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await manager.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "attachment.manager@example.test", "correct horse battery staple"))).StatusCode);

        var managedList = await manager.GetAsync($"/api/v1/tickets/{managedIncidentId}/attachments/");
        var foreignList = await manager.GetAsync($"/api/v1/tickets/{foreignIncidentId}/attachments/");
        var foreignDownload = await manager.GetAsync($"/api/v1/attachments/{foreignAttachmentId}");
        using var managedUpload = new MultipartFormDataContent();
        using var foreignUpload = new MultipartFormDataContent();
        managedUpload.Add(new StringContent("not-a-file"), "metadata");
        foreignUpload.Add(new StringContent("not-a-file"), "metadata");
        var managedUploadAttempt = await manager.PostAsync($"/api/v1/tickets/{managedIncidentId}/attachments/", managedUpload);
        var foreignUploadAttempt = await manager.PostAsync($"/api/v1/tickets/{foreignIncidentId}/attachments/", foreignUpload);

        Assert.Equal(HttpStatusCode.OK, managedList.StatusCode);
        Assert.True(foreignList.StatusCode == HttpStatusCode.NotFound, await foreignList.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, foreignDownload.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, managedUploadAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignUploadAttempt.StatusCode);

        using var selfService = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await selfService.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "attachment.self-service@example.test", "correct horse battery staple"))).StatusCode);
        using var selfServiceUpload = new MultipartFormDataContent();
        using var foreignSelfServiceUpload = new MultipartFormDataContent();
        selfServiceUpload.Add(new StringContent("not-a-file"), "metadata");
        foreignSelfServiceUpload.Add(new StringContent("not-a-file"), "metadata");

        var selfServiceUploadAttempt = await selfService.PostAsync($"/api/v1/tickets/{selfServiceIncidentId}/attachments/", selfServiceUpload);
        var foreignSelfServiceUploadAttempt = await selfService.PostAsync($"/api/v1/tickets/{foreignIncidentId}/attachments/", foreignSelfServiceUpload);

        Assert.Equal(HttpStatusCode.BadRequest, selfServiceUploadAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignSelfServiceUploadAttempt.StatusCode);
    }

    [Fact]
    public async Task Ticket_sla_mutation_requires_a_manager_grant_in_the_ticket_tenant()
    {
        const string technicianOrganizationId = "sla-technician-organization";
        const string foreignOrganizationId = "sla-foreign-organization";
        const string ownIncidentId = "sla-own-incident";
        const string foreignIncidentId = "sla-foreign-incident";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "sla.technician@example.test", Email = "sla.technician@example.test", DisplayName = "SLA Technician" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("sla.technician@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = technicianOrganizationId, Name = "SLA technician organization" },
                new Organization { Id = foreignOrganizationId, Name = "SLA foreign organization" });
            db.Users.Add(new User { Id = userId, Name = "SLA Technician", Email = "sla.technician@example.test", OrganizationId = technicianOrganizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = technicianOrganizationId,
                RoleKey = ScopedRoleCatalog.Technician
            });
            db.Incidents.AddRange(
                new Incident { Id = ownIncidentId, OrganizationId = technicianOrganizationId, Title = "Own SLA incident", Description = "Own incident" },
                new Incident { Id = foreignIncidentId, OrganizationId = foreignOrganizationId, Title = "Foreign SLA incident", Description = "Foreign incident" });
            db.TicketSlaStates.AddRange(
                new TicketSlaState { TicketId = ownIncidentId, StartedAt = DateTimeOffset.UtcNow, ResponseDueAt = DateTimeOffset.UtcNow.AddHours(1), ResolutionDueAt = DateTimeOffset.UtcNow.AddHours(2), Status = Helpdesk.Shared.Enums.SlaStatus.InProgress },
                new TicketSlaState { TicketId = foreignIncidentId, StartedAt = DateTimeOffset.UtcNow, ResponseDueAt = DateTimeOffset.UtcNow.AddHours(1), ResolutionDueAt = DateTimeOffset.UtcNow.AddHours(2), Status = Helpdesk.Shared.Enums.SlaStatus.InProgress });
            await db.SaveChangesAsync();
        }

        using var technician = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await technician.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "sla.technician@example.test", "correct horse battery staple"))).StatusCode);

        var ownMutation = await technician.PostAsJsonAsync(
            $"/api/v1/tickets/{ownIncidentId}/sla/pause",
            new { Reason = "Awaiting vendor response" });
        var foreignMutation = await technician.PostAsJsonAsync(
            $"/api/v1/tickets/{foreignIncidentId}/sla/pause",
            new { Reason = "Attempted cross-tenant mutation" });

        Assert.Equal(HttpStatusCode.NoContent, ownMutation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignMutation.StatusCode);
        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var foreignState = await verificationScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().TicketSlaStates
            .SingleAsync(state => state.TicketId == foreignIncidentId);
        Assert.Equal(Helpdesk.Shared.Enums.SlaStatus.InProgress, foreignState.Status);
    }

    [Fact]
    public async Task Incident_worklogs_require_an_incident_manager_grant()
    {
        const string organizationId = "worklog-self-service-organization";
        const string incidentId = "worklog-self-service-incident";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "worklog.user@example.test", Email = "worklog.user@example.test", DisplayName = "Worklog User" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("worklog.user@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.Add(new Organization { Id = organizationId, Name = "Worklog self-service organization" });
            db.Users.Add(new User { Id = userId, Name = "Worklog User", Email = "worklog.user@example.test", OrganizationId = organizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = organizationId,
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            var customerId = $"customer-{userId}";
            db.Customers.Add(new Customer
            {
                Id = customerId,
                Name = "Worklog User",
                Email = "worklog.user@example.test",
                OrganizationId = organizationId
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = customerId,
                LocalAccountId = userId,
                AuthProviderType = "Local",
                InviteStatus = CustomerInviteStatus.Active
            });
            db.Incidents.Add(new Incident
            {
                Id = incidentId,
                OrganizationId = organizationId,
                CustomerId = customerId,
                Title = "Self-service worklog incident",
                Description = "Incident that must not accept a staff worklog from a self-service account",
                RequesterEmail = "worklog.user@example.test"
            });
            db.TicketTimelineEvents.AddRange(
                new TicketTimelineEvent { TicketId = incidentId, EventType = Helpdesk.Shared.Enums.TimelineEventType.Worklog, MessageText = "Customer-visible update" },
                new TicketTimelineEvent { TicketId = incidentId, EventType = Helpdesk.Shared.Enums.TimelineEventType.InternalNote, MessageText = "Private staff note" });
            await db.SaveChangesAsync();
        }

        using var selfServiceUser = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await selfServiceUser.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "worklog.user@example.test", "correct horse battery staple"))).StatusCode);
        var createWorklog = await selfServiceUser.PostAsJsonAsync(
            $"/api/v1/incidents/{incidentId}/worklogs",
            new { Hours = 1d, Notes = "Attempted staff worklog", IsInternalNote = true });
        var timeline = await selfServiceUser.GetAsync($"/api/v1/incidents/{incidentId}/timeline");
        var timelineContent = await timeline.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, createWorklog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        Assert.Contains("Customer-visible update", timelineContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Private staff note", timelineContent, StringComparison.Ordinal);
        await using var verificationScope = _factory.Services.CreateAsyncScope();
        Assert.DoesNotContain(
            await verificationScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().WorkLogs.ToListAsync(),
            workLog => workLog.TicketId == incidentId);
    }

    [Fact]
    public async Task Request_worklogs_require_a_request_manager_grant()
    {
        const string organizationId = "request-worklog-self-service-organization";
        const string requestId = "request-worklog-self-service-request";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "request.worklog.user@example.test", Email = "request.worklog.user@example.test", DisplayName = "Request Worklog User" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("request.worklog.user@example.test"))!.Id;

            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.Add(new Organization { Id = organizationId, Name = "Request worklog self-service organization" });
            db.Users.Add(new User { Id = userId, Name = "Request Worklog User", Email = "request.worklog.user@example.test", OrganizationId = organizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = organizationId,
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            var customerId = $"customer-{userId}";
            db.Customers.Add(new Customer
            {
                Id = customerId,
                Name = "Request Worklog User",
                Email = "request.worklog.user@example.test",
                OrganizationId = organizationId
            });
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = customerId,
                LocalAccountId = userId,
                AuthProviderType = "Local",
                InviteStatus = CustomerInviteStatus.Active
            });
            db.Requests.Add(new Request
            {
                Id = requestId,
                OrganizationId = organizationId,
                CustomerId = customerId,
                Title = "Self-service worklog request",
                Description = "Request that must not accept a staff worklog from a self-service account",
                RequesterEmail = "request.worklog.user@example.test"
            });
            db.TicketTimelineEvents.AddRange(
                new TicketTimelineEvent { TicketId = requestId, EventType = Helpdesk.Shared.Enums.TimelineEventType.Worklog, MessageText = "Customer-visible request update" },
                new TicketTimelineEvent { TicketId = requestId, EventType = Helpdesk.Shared.Enums.TimelineEventType.InternalNote, MessageText = "Private request staff note" });
            await db.SaveChangesAsync();
        }

        using var selfServiceUser = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await selfServiceUser.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "request.worklog.user@example.test", "correct horse battery staple"))).StatusCode);
        var createWorklog = await selfServiceUser.PostAsJsonAsync(
            $"/api/v1/requests/{requestId}/worklogs",
            new { Hours = 1d, Notes = "Attempted request staff worklog", IsInternalNote = true });
        var timeline = await selfServiceUser.GetAsync($"/api/v1/requests/{requestId}/timeline");
        var timelineContent = await timeline.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, createWorklog.StatusCode);
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        Assert.Contains("Customer-visible request update", timelineContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Private request staff note", timelineContent, StringComparison.Ordinal);
        await using var verificationScope = _factory.Services.CreateAsyncScope();
        Assert.DoesNotContain(
            await verificationScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().WorkLogs.ToListAsync(),
            workLog => workLog.TicketId == requestId);
    }

    [Fact]
    public async Task Change_worklogs_require_a_change_manager_grant()
    {
        const string organizationId = "change-worklog-self-service-organization";
        const string changeId = "change-worklog-self-service-change";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "change.worklog.user@example.test", Email = "change.worklog.user@example.test", DisplayName = "Change Worklog User" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("change.worklog.user@example.test"))!.Id;
            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.Add(new Organization { Id = organizationId, Name = "Change worklog self-service organization" });
            db.Users.Add(new User { Id = userId, Name = "Change Worklog User", Email = "change.worklog.user@example.test", OrganizationId = organizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = userId, OrganizationId = organizationId, RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.Changes.Add(new Change { Id = changeId, OrganizationId = organizationId, Title = "Self-service worklog change", Description = "Change that must not accept a staff worklog", RequesterEmail = "change.worklog.user@example.test" });
            await db.SaveChangesAsync();
        }

        using var selfServiceUser = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await selfServiceUser.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "change.worklog.user@example.test", "correct horse battery staple"))).StatusCode);
        var createWorklog = await selfServiceUser.PostAsJsonAsync(
            $"/api/v1/changes/{changeId}/worklogs",
            new { Hours = 1d, Notes = "Attempted change staff worklog", IsInternalNote = true });

        Assert.Equal(HttpStatusCode.Forbidden, createWorklog.StatusCode);
        await using var verificationScope = _factory.Services.CreateAsyncScope();
        Assert.DoesNotContain(await verificationScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().WorkLogs.ToListAsync(), workLog => workLog.TicketId == changeId);
    }

    [Fact]
    public async Task Local_login_requires_an_authenticator_code_after_two_factor_is_enabled()
    {
        using var setupClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await setupClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);

        var setup = await setupClient.PostAsJsonAsync("/api/v1/local-auth/two-factor/setup", new LocalAuthenticationEndpoints.StartAuthenticatorSetupRequest("correct horse battery staple"));
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var setupResult = await setup.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.AuthenticatorSetupResponse>();
        Assert.False(string.IsNullOrWhiteSpace(setupResult?.SharedKey));

        var authenticatorCode = CreateTotp(setupResult!.SharedKey);
        // Continue on the renewed session returned by setup; no hidden second login.
        Assert.Equal(HttpStatusCode.OK, (await setupClient.GetAsync("/api/v1/auth/me")).StatusCode);

        var enable = await setupClient.PostAsJsonAsync("/api/v1/local-auth/two-factor/enable", new LocalAuthenticationEndpoints.EnableTwoFactorRequest(authenticatorCode));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var recoveryCodes = await enable.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.TwoFactorRecoveryCodesResponse>();
        Assert.Equal(10, recoveryCodes!.RecoveryCodes.Count);
        Assert.Equal(HttpStatusCode.OK, (await setupClient.GetAsync("/api/v1/auth/me")).StatusCode);
        var restartSetup = await setupClient.PostAsJsonAsync("/api/v1/local-auth/two-factor/setup",
            new LocalAuthenticationEndpoints.StartAuthenticatorSetupRequest("correct horse battery staple"));
        Assert.Equal(HttpStatusCode.Conflict, restartSetup.StatusCode);

        using var loginClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var missingCode = await loginClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"));
        Assert.Equal(HttpStatusCode.Accepted, missingCode.StatusCode);
        var challengeCookie = missingCode.Headers.GetValues("Set-Cookie")
            .Last(cookie => cookie.StartsWith("RatelDesk.LocalChallenge=", StringComparison.Ordinal));
        Assert.Contains("httponly", challengeCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", challengeCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=300", challengeCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await loginClient.GetAsync("/api/v1/auth/me")).StatusCode);
        var incorrectCode = await loginClient.PostAsJsonAsync("/api/v1/local-auth/login/two-factor",
            new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest("invalid"));
        Assert.Equal(HttpStatusCode.Unauthorized, incorrectCode.StatusCode);
        Assert.False(incorrectCode.Headers.Contains("Set-Cookie"));
        var withAuthenticator = await loginClient.PostAsJsonAsync("/api/v1/local-auth/login/two-factor",
            new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest(authenticatorCode));
        using var recoveryClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.Accepted, (await recoveryClient.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        var withRecoveryCode = await recoveryClient.PostAsJsonAsync("/api/v1/local-auth/login/two-factor",
            new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest(recoveryCodes.RecoveryCodes[0]));

        Assert.Equal(HttpStatusCode.NoContent, withAuthenticator.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await loginClient.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Contains(withAuthenticator.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("RatelDesk.LocalChallenge=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.NoContent, withRecoveryCode.StatusCode);
    }

    [Fact]
    public async Task First_administrator_signs_in_with_email_and_password_only_and_email_whitespace_is_ignored()
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("  admin@example.test  ", "correct horse battery staple"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("admin@example.test");
        Assert.False(account!.TwoFactorEnabled);
    }

    [Fact]
    public async Task Invalid_password_does_not_issue_a_second_factor_challenge()
    {
        await EnrollAdministratorAsync();
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "incorrect password"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith("RatelDesk.LocalChallenge=", StringComparison.Ordinal) && !cookie.StartsWith("RatelDesk.LocalChallenge=;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("password-changed")]
    [InlineData("permissions-changed")]
    [InlineData("disabled")]
    public async Task Second_factor_rejects_missing_expired_or_revoked_password_verification(string state)
    {
        var (key, _) = await EnrollAdministratorAsync();
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var password = await client.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"));
        Assert.Equal(HttpStatusCode.Accepted, password.StatusCode);
        var challengeCookie = password.Headers.GetValues("Set-Cookie")
            .Last(cookie => cookie.StartsWith("RatelDesk.LocalChallenge=", StringComparison.Ordinal)).Split(';', 2)[0];
        if (state == "tampered")
            challengeCookie = "RatelDesk.LocalChallenge=invalid";
        if (state is "expired" or "password-changed" or "permissions-changed" or "disabled")
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = (await users.FindByEmailAsync("admin@example.test"))!;
            if (state == "expired")
            {
                var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector(LocalSignInChallenge.ProtectionPurpose).ToTimeLimitedDataProtector();
                var payload = new LocalSignInChallenge(account.Id, account.SecurityStamp!, account.AuthorizationRevision, false);
                challengeCookie = "RatelDesk.LocalChallenge=" + protector.Protect(JsonSerializer.Serialize(payload), DateTimeOffset.UtcNow.AddMinutes(-1));
            }
            else if (state == "password-changed")
                Assert.True((await users.UpdateSecurityStampAsync(account)).Succeeded);
            else
            {
                if (state == "disabled") account.IsEnabled = false;
                else account.AuthorizationRevision++;
                Assert.True((await users.UpdateAsync(account)).Succeeded);
            }
        }
        if (state != "missing")
            client.DefaultRequestHeaders.Add("Cookie", challengeCookie);

        var response = await client.PostAsJsonAsync("/api/v1/local-auth/login/two-factor",
            new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest(CreateTotp(key)));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("RatelDesk.Local=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_second_factor_locks_the_account_without_password_step_resetting_the_counter()
    {
        await EnrollAdministratorAsync();
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/local-auth/login",
                new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/local-auth/login/two-factor",
                new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest("invalid"))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/local-auth/login",
            new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
    }

    [Fact]
    public async Task Recovery_code_can_only_be_used_once_across_password_challenges()
    {
        var (_, recoveryCode) = await EnrollAdministratorAsync();
        using var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/local-auth/login",
                new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
            var verify = await client.PostAsJsonAsync("/api/v1/local-auth/login/two-factor",
                new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest(recoveryCode));
            Assert.Equal(attempt == 0 ? HttpStatusCode.NoContent : HttpStatusCode.Unauthorized, verify.StatusCode);
        }
    }

    [Theory]
    [InlineData("/api/v1/local-auth/login/two-factor")]
    [InlineData("/api/v1/local-auth/login/two-factor/")]
    public async Task Anonymous_second_factor_rejects_cross_site_and_unmarked_requests(string route)
    {
        using var client = _factory.CreateClient();
        var code = new LocalAuthenticationEndpoints.CompleteTwoFactorLoginRequest("123456");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, code)).StatusCode);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(route, code)).StatusCode);
    }

    private async Task<(string Key, string RecoveryCode)> EnrollAdministratorAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var account = (await users.FindByEmailAsync("admin@example.test"))!;
        Assert.True((await users.ResetAuthenticatorKeyAsync(account)).Succeeded);
        Assert.True((await users.SetTwoFactorEnabledAsync(account, true)).Succeeded);
        var recovery = (await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 2))!.ToArray();
        return ((await users.GetAuthenticatorKeyAsync(account))!, recovery[0]);
    }

    [Fact]
    public async Task Ticket_counts_do_not_disclose_a_ticket_outside_the_principal_scope()
    {
        const string incidentOrganizationId = "count-incident-organization";
        const string userOrganizationId = "count-user-organization";
        const string incidentId = "restricted-count-incident";
        await using (var setupScope = _factory.Services.CreateAsyncScope())
        {
            var identityUsers = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createUser = await identityUsers.CreateAsync(
                new ApplicationUser { UserName = "count.reader@example.test", Email = "count.reader@example.test", DisplayName = "Count Reader" },
                "correct horse battery staple");
            Assert.True(createUser.Succeeded);
            var userId = (await identityUsers.FindByEmailAsync("count.reader@example.test"))!.Id;
            var db = setupScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(
                new Organization { Id = incidentOrganizationId, Name = "Count incident organization" },
                new Organization { Id = userOrganizationId, Name = "Count reader organization" });
            db.Users.Add(new User { Id = userId, Name = "Count Reader", Email = "count.reader@example.test", OrganizationId = userOrganizationId, Role = "User" });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = userId, OrganizationId = userOrganizationId, RoleKey = ScopedRoleCatalog.SelfServiceUser });
            db.Incidents.Add(new Incident { Id = incidentId, OrganizationId = incidentOrganizationId, Title = "Restricted count incident", Description = "Restricted count incident" });
            db.TicketTimelineEvents.Add(new TicketTimelineEvent { TicketId = incidentId, EventType = Helpdesk.Shared.Enums.TimelineEventType.Worklog, MessageText = "Restricted timeline item" });
            await db.SaveChangesAsync();
        }

        using var reader = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "count.reader@example.test", "correct horse battery staple"))).StatusCode);
        var denied = await reader.GetAsync($"/api/v1/incidents/{incidentId}/timeline/count");
        using var administrator = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(
            "admin@example.test", "correct horse battery staple"))).StatusCode);
        var permitted = await administrator.GetAsync($"/api/v1/incidents/{incidentId}/timeline/count");

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        Assert.Equal("1", await permitted.Content.ReadAsStringAsync());
    }

    private static string CreateTotp(string sharedKey)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var bitCount = 0;
        var bytes = new List<byte>();
        foreach (var character in sharedKey.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant())
        {
            var value = alphabet.IndexOf(character);
            if (value < 0)
            {
                throw new ArgumentException("The authenticator key is not valid base32.", nameof(sharedKey));
            }

            bits = (bits << 5) | value;
            bitCount += 5;
            if (bitCount < 8)
            {
                continue;
            }

            bitCount -= 8;
            bytes.Add((byte)(bits >> bitCount));
            bits &= (1 << bitCount) - 1;
        }

        var counter = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Span<byte> counterBytes = stackalloc byte[8];
        for (var index = counterBytes.Length - 1; index >= 0; index--)
        {
            counterBytes[index] = (byte)counter;
            counter >>= 8;
        }

        var hash = HMACSHA1.HashData(bytes.ToArray(), counterBytes);
        var offset = hash[^1] & 0x0f;
        var value32 = ((hash[offset] & 0x7f) << 24) |
                      (hash[offset + 1] << 16) |
                      (hash[offset + 2] << 8) |
                      hash[offset + 3];
        return (value32 % 1_000_000).ToString("D6", global::System.Globalization.CultureInfo.InvariantCulture);
    }
}
