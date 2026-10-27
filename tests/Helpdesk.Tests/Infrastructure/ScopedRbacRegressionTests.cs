using System.Security.Claims;
using Helpdesk.API.Middleware;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure;

public sealed class ScopedRbacRegressionTests
{
    [Fact]
    public async Task Support_assignment_delegates_ticket_access_only_and_revokes_with_link()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(new Organization { Id = "a", Name = "Support" },
            new Organization { Id = "b", Name = "Customer", ItSupportOrganizationId = "a" },
            new Organization { Id = "c", Name = "Other" },
            new Organization { Id = "d", Name = "Second hop", ItSupportOrganizationId = "b" });
        db.Users.Add(new User { Id = "local", Name = "Local", OrganizationId = "a" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "local", OrganizationId = "a", RoleKey = ScopedRoleCatalog.Technician });
        await db.SaveChangesAsync();
        var service = new CurrentUserAccessService(db);
        var access = await service.ResolveAsync(LocalPrincipal());
        Assert.True(access.CanManageIncident("b"));
        Assert.True(access.CanManageRequest("b"));
        Assert.True(access.CanManageChange("b"));
        Assert.False(access.HasPermission(HelpdeskPermissions.TenantRolesAssign, "b"));
        Assert.False(access.CanManageIncident("c"));
        Assert.False(access.CanManageIncident("d"));
        (await db.Organizations.FindAsync("b"))!.ItSupportOrganizationId = null;
        await db.SaveChangesAsync();
        Assert.False((await service.ResolveAsync(LocalPrincipal())).CanManageIncident("b"));
    }

    [Fact]
    public async Task Persisted_support_ticket_grant_does_not_expand_unrelated_provider_permissions()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(new Organization { Id = "a", Name = "Primary" }, new Organization { Id = "b", Name = "Support" }, new Organization { Id = "c", Name = "Supported", ItSupportOrganizationId = "b" });
        db.Users.Add(new User { Id = "external", Name = "External", OrganizationId = "a" });
        db.Customers.Add(new Customer { Id = "contact", Name = "External", OrganizationId = "a" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "contact", DomainUserId = "external", OidcIssuer = "https://issuer", OidcSubject = "subject" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "external", OrganizationId = "b", RoleKey = ScopedRoleCatalog.IncidentWriter });
        await db.SaveChangesAsync();
        var principal = ExternalPrincipal();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, HelpdeskPermissions.RequestRead));
        var access = await new CurrentUserAccessService(db).ResolveAsync(principal);
        Assert.True(access.CanManageIncident("c"));
        Assert.True(access.HasPermission(HelpdeskPermissions.RequestRead, "a"));
        Assert.False(access.HasPermission(HelpdeskPermissions.RequestRead, "c"));
    }

    [Fact]
    public async Task Removing_last_assignment_revokes_local_access_even_after_role_claim_replay()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(new Organization { Id = "a", Name = "A" },
            new Organization { Id = "b", Name = "B", ItSupportOrganizationId = "a" });
        db.Users.Add(new User { Id = "local", Name = "Local", OrganizationId = "a", Role = "Technician" });
        db.Customers.Add(new Customer { Id = "contact", Name = "Local", OrganizationId = "a" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "contact", LocalAccountId = "local", AuthProviderType = "Local" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "local", OrganizationId = "a", RoleKey = ScopedRoleCatalog.Technician });
        await db.SaveChangesAsync();
        var principal = LocalPrincipal();
        var service = new CurrentUserAccessService(db);
        Assert.True((await service.ResolveAsync(principal)).CanManageIncident("a"));
        db.ScopedRoleAssignments.RemoveRange(db.ScopedRoleAssignments);
        await db.SaveChangesAsync();
        var access = await service.ResolveAsync(principal);
        Assert.Empty(access.Permissions);
        Assert.Empty(access.ScopedPermissionGrants);
        Assert.False(access.CanViewIncident("a", "contact", null));
        Assert.False(access.CanManageIncident("b"));
    }

    [Fact]
    public async Task Linked_external_user_does_not_regain_default_role_after_last_assignment_removed()
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "a", Name = "A" });
        db.Users.Add(new User { Id = "external", Name = "External", OrganizationId = "a", Role = "Technician" });
        db.Customers.Add(new Customer { Id = "contact", Name = "External", OrganizationId = "a" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "contact", DomainUserId = "external", OidcIssuer = "https://issuer", OidcSubject = "subject" });
        await db.SaveChangesAsync();
        var access = await new CurrentUserAccessService(db).ResolveAsync(ExternalPrincipal());
        Assert.Empty(access.Permissions);
        Assert.False(access.CanViewIncident("a", "contact", null));
    }

    [Fact]
    public async Task Provider_permissions_stay_in_provider_scopes_after_application_projection()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(new Organization { Id = "a", Name = "A" }, new Organization { Id = "b", Name = "B" });
        db.Users.Add(new User { Id = "external", Name = "External", OrganizationId = "a" });
        db.Customers.Add(new Customer { Id = "contact", Name = "External", OrganizationId = "a" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "contact", DomainUserId = "external", OidcIssuer = "https://issuer", OidcSubject = "subject" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "external", OrganizationId = "b", RoleKey = ScopedRoleCatalog.IncidentWriter });
        await db.SaveChangesAsync();
        var principal = ExternalPrincipal();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, HelpdeskPermissions.RequestRead));
        var service = new CurrentUserAccessService(db);
        var context = new DefaultHttpContext { User = principal };
        await new UserAccessClaimsMiddleware(_ => Task.CompletedTask).InvokeAsync(context, service);
        // Endpoints resolve again after middleware. Projected Incident.Write in B
        // must never become provider authority in the primary organization A.
        var projected = await service.ResolveAsync(context.User);
        Assert.True(projected.CanManageIncident("b"));
        Assert.False(projected.CanManageIncident("a"));
        Assert.True(projected.CanViewRequest("a", "another-contact", null));
        Assert.False(projected.CanViewRequest("b", "another-contact", null));
    }

    [Theory]
    [InlineData(ScopedRoleCatalog.IncidentReader, "incident")]
    [InlineData(ScopedRoleCatalog.RequestReader, "request")]
    [InlineData(ScopedRoleCatalog.ChangeReader, "change")]
    public async Task Reader_roles_can_read_other_contacts_without_mutation(string roleKey, string module)
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "a", Name = "A" });
        db.Users.Add(new User { Id = "local", Name = "Local", OrganizationId = "a" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "local", OrganizationId = "a", RoleKey = roleKey });
        await db.SaveChangesAsync();
        var access = await new CurrentUserAccessService(db).ResolveAsync(LocalPrincipal());
        Assert.True(module switch {
            "incident" => access.CanViewIncident("a", "someone-else", null),
            "request" => access.CanViewRequest("a", "someone-else", null),
            _ => access.CanViewChange("a", "someone-else", null)
        });
        Assert.False(access.CanManageIncident("a"));
        Assert.False(access.CanManageRequest("a"));
        Assert.False(access.CanManageChange("a"));
        Assert.False(access.CanCreateIncident("a", "someone-else", null));
        Assert.False(access.CanContributeRequest("a", "someone-else", null));
        Assert.False(access.CanDeleteChange("a"));
        Assert.False(access.CanApproveChange("a"));
    }

    [Fact]
    public void Writer_delete_and_approval_grants_are_independent_and_require_read()
    {
        Assert.False(RoleDefinitionCatalog.SatisfiesDependencies([HelpdeskPermissions.IncidentWrite]));
        Assert.False(RoleDefinitionCatalog.SatisfiesDependencies([HelpdeskPermissions.ChangeApprove]));
        Assert.True(RoleDefinitionCatalog.SatisfiesDependencies([HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeApprove]));
        Assert.DoesNotContain(HelpdeskPermissions.ChangeApprove, ScopedRoleCatalog.PermissionsFor(ScopedRoleCatalog.ChangeWriter));
        Assert.DoesNotContain(HelpdeskPermissions.ChangeDelete, ScopedRoleCatalog.PermissionsFor(ScopedRoleCatalog.Technician));
        Assert.DoesNotContain(HelpdeskPermissions.RequestExecute, ScopedRoleCatalog.PermissionsFor(ScopedRoleCatalog.RequestWriter));
        Assert.False(RoleDefinitionCatalog.SatisfiesDependencies([HelpdeskPermissions.RequestExecute]));
        Assert.True(RoleDefinitionCatalog.SatisfiesDependencies([HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestExecute]));
        Assert.All(RoleDefinitionCatalog.BuiltIns, role => Assert.True(RoleDefinitionCatalog.SatisfiesDependencies(role.Permissions.ToArray())));
    }

    private static ClaimsPrincipal LocalPrincipal() => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, "local"), new Claim("auth_mode", "local"),
        new Claim(ClaimTypes.Role, HelpdeskPermissions.IncidentManager)
    ], "RatelDeskLocal"));

    private static ClaimsPrincipal ExternalPrincipal() => new(new ClaimsIdentity([
        new Claim("iss", "https://issuer/"), new Claim("sub", "subject")
    ], "oidc"));

    private static HelpdeskDbContext CreateDb() => new(
        new DbContextOptionsBuilder<HelpdeskDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new TenantContext(), new HttpContextAccessor());

    private sealed class TenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
