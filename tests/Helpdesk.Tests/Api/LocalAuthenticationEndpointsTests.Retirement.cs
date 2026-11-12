using System.Net;
using System.Net.Http.Json;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class LocalAuthenticationEndpointsTests
{
    [Theory]
    [InlineData("pending", CustomerInviteStatus.Pending, true)]
    [InlineData("external", CustomerInviteStatus.Active, false)]
    [InlineData("deleted-account", CustomerInviteStatus.Disabled, false)]
    [InlineData("deleted-domain", CustomerInviteStatus.Disabled, false)]
    [InlineData("disabled-link", CustomerInviteStatus.Disabled, false)]
    [InlineData("missing-domain", CustomerInviteStatus.Disabled, false)]
    public async Task Legacy_customer_local_activation_respects_retirement_link_state_and_external_ownership(string shape, CustomerInviteStatus expected, bool canActivate)
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = new ApplicationUser { Id = "linked-account", UserName = "linked@example.invalid", Email = "linked@example.invalid", IsEnabled = true, IsDeleted = shape == "deleted-account" };
            Assert.True((await users.CreateAsync(account)).Succeeded);
            if (shape == "external") Assert.True((await users.AddLoginAsync(account, new UserLoginInfo("SyntheticOIDC", "verified-subject", "Synthetic OIDC"))).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.Add(new Organization { Id = "linked-organization", Name = "Linked tenant" });
            db.Customers.Add(new Customer { Id = "linked-contact", Name = "Linked contact", Email = "contact@example.invalid", OrganizationId = "linked-organization" });
            if (shape != "missing-domain") db.Users.Add(new User { Id = "linked-domain", Name = "Linked profile", OrganizationId = "linked-organization", IsDeleted = shape == "deleted-domain" });
            db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "linked-contact", DomainUserId = "linked-domain", LocalAccountId = account.Id, AuthProviderType = "Local",
                InviteStatus = shape == "disabled-link" ? CustomerInviteStatus.Disabled : CustomerInviteStatus.Pending });
            await db.SaveChangesAsync();
        }
        using var administrator = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        var status = await administrator.GetFromJsonAsync<CustomerAuthStatusDto>("/api/v1/customers/linked-contact/auth/status");
        Assert.Equal(expected, status!.InviteStatus);
        if (shape == "external") Assert.Equal("OIDC", status.AuthProviderType);
        var activation = await administrator.PostAsync("/api/v1/customers/linked-contact/auth/local-activation-token", null);
        Assert.Equal(canActivate ? HttpStatusCode.OK : HttpStatusCode.Conflict, activation.StatusCode);
        if (canActivate) Assert.True(activation.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Delegated_tenant_user_management_never_grants_global_deletion_or_sign_in_controls()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = new ApplicationUser { UserName = "delegate@example.invalid", Email = "delegate@example.invalid", DisplayName = "Tenant delegate" };
            Assert.True((await accounts.CreateAsync(account, "synthetic delegate passphrase")).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.AddRange(new Organization { Id = "own", Name = "Own tenant" }, new Organization { Id = "foreign", Name = "Foreign tenant" });
            db.Users.AddRange(new User { Id = account.Id, Name = "Delegate", OrganizationId = "own", Role = "User" },
                new User { Id = "own-profile", Name = "Own profile", OrganizationId = "own" },
                new User { Id = "foreign-profile", Name = "Foreign profile", OrganizationId = "foreign" });
            var role = new Role { Id = "delegated-team", Key = "delegated-team", Name = "Delegated team", Scope = RoleScopeKind.Tenant, OwnerOrganizationId = "own" };
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = HelpdeskPermissions.TenantUsersManage });
            role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = HelpdeskPermissions.TenantRolesAssign });
            db.Roles.Add(role);
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = account.Id, OrganizationId = "own", RoleKey = role.Key });
            await db.SaveChangesAsync();
        }
        using var delegated = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await delegated.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest("delegate@example.invalid", "synthetic delegate passphrase"))).StatusCode);
        var access = await delegated.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me");
        Assert.Contains(HelpdeskPermissions.TenantUsersManage, access!.Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await delegated.GetAsync("/api/v1/users/query")).StatusCode);
        foreach (var id in new[] { "own-profile", "foreign-profile" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await delegated.DeleteAsync($"/api/v1/users/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await delegated.PostAsync($"/api/v1/local-auth/users/{id}/disable", null)).StatusCode);
        }
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(3, await verify.Users.CountAsync(user => !user.IsDeleted));
        Assert.Empty(await verify.ActivityLogs.Where(log => log.RelatedEntityId == "own-profile" || log.RelatedEntityId == "foreign-profile").ToListAsync());
    }

    [Fact]
    public async Task Delete_user_revokes_existing_cookie_and_password_and_cannot_be_reversed_with_Enable()
    {
        string targetId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var target = new ApplicationUser { UserName = "retire@example.invalid", Email = "retire@example.invalid", DisplayName = "Retirement target" };
            Assert.True((await accounts.CreateAsync(target, "synthetic target passphrase")).Succeeded);
            targetId = target.Id;
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Users.Add(new User { Id = targetId, Name = "Retirement target", Email = "retire@example.invalid", Role = "User" });
            await db.SaveChangesAsync();
        }
        using var targetClient = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await targetClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest("retire@example.invalid", "synthetic target passphrase"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.DeleteAsync($"/api/v1/users/{targetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await targetClient.GetAsync("/api/v1/users/query")).StatusCode);

        using var administrator = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest("admin@example.test", "correct horse battery staple"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"/api/v1/users/{targetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.PostAsJsonAsync("/api/v1/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest("retire@example.invalid", "synthetic target passphrase"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await administrator.PostAsync($"/api/v1/local-auth/users/{targetId}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await administrator.PostAsync($"/api/v1/local-auth/users/{targetId}/activation-token", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await administrator.GetAsync($"/api/v1/users/{targetId}")).StatusCode);
        Assert.DoesNotContain((await administrator.GetFromJsonAsync<List<UserDto>>("/api/v1/users"))!, user => user.Id == targetId);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"/api/v1/users/{targetId}")).StatusCode);
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal("Retirement target", (await verify.Users.SingleAsync(user => user.Id == targetId)).Name);
        Assert.Single(await verify.ActivityLogs.Where(log => log.RelatedEntityId == targetId).ToListAsync());
    }
}
