using System.Security.Claims;
using Helpdesk.API.Middleware;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure;

public class CurrentUserAccessServiceTests
{
    [Fact]
    public async Task Linked_active_customer_gets_default_user_permissions_and_tenant()
    {
        await using var db = CreateDb();
        var organization = new Organization { Id = "org-alpha", Name = "Alpha Organization" };
        var customer = new Customer { Id = "customer-primary", Name = "Primary User", Email = "primary@example.com", OrganizationId = organization.Id };
        db.Organizations.Add(organization);
        db.Customers.Add(customer);
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = customer.Id,
            OidcIssuer = "https://id.example.com/application/o/rateldesk",
            OidcSubject = "subject-1",
            AuthentikEmail = customer.Email,
            InviteStatus = CustomerInviteStatus.Active
        });
        await db.SaveChangesAsync();

        var access = await new CurrentUserAccessService(db).ResolveAsync(User(customer.Email, "subject-1"));

        Assert.Equal(customer.Id, access.CustomerId);
        Assert.Equal(organization.Id, access.PrimaryOrganizationId);
        Assert.Contains(HelpdeskRoleBundles.User, access.RoleBundles);
        Assert.Contains(HelpdeskPermissions.SelfServiceUser, access.Permissions);
        Assert.Contains(HelpdeskPermissions.IncidentUser, access.Permissions);
        Assert.Contains(HelpdeskPermissions.RequestUser, access.Permissions);
    }

    [Fact]
    public async Task Technical_user_gets_msp_managed_tenants()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(
            new Organization { Id = "org-support", Name = "Support Organization" },
            new Organization { Id = "org-alpha", Name = "Alpha Organization", ItSupportOrganizationId = "org-support" });
        db.Customers.Add(new Customer { Id = "customer-tech", Name = "Technician", Email = "tech@example.com", OrganizationId = "org-support" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = "customer-tech",
            OidcIssuer = "https://id.example.com/application/o/rateldesk",
            OidcSubject = "tech-subject",
            AuthentikEmail = "tech@example.com",
            InviteStatus = CustomerInviteStatus.Active
        });
        await db.SaveChangesAsync();

        var access = await new CurrentUserAccessService(db).ResolveAsync(User("tech@example.com", "tech-subject", AuthentikRbacGroups.Technical));

        Assert.Contains("org-support", access.AllowedOrganizationIds);
        Assert.Contains("org-alpha", access.AllowedOrganizationIds);
        Assert.Contains("org-alpha", access.ManagedOrganizationIds);
        Assert.Contains(HelpdeskPermissions.ChangeManager, access.Permissions);
    }

    [Fact]
    public async Task Client_admin_group_does_not_treat_identity_provider_tenant_as_application_scope()
    {
        await using var db = CreateDb();

        var access = await new CurrentUserAccessService(db).ResolveAsync(User(
            "admin@example.com",
            "client-admin-subject",
            "org-alpha",
            [AuthentikRbacGroups.ClientAdmin]));

        Assert.Contains(HelpdeskRoleBundles.DataManagementAdmin, access.RoleBundles);
        Assert.Contains(HelpdeskPermissions.DataManagementAdmin, access.Permissions);
        Assert.Empty(access.AllowedOrganizationIds);
        Assert.False(access.IsHelpdeskAdmin);
    }

    [Fact]
    public async Task Email_match_without_a_verified_identity_link_does_not_grant_access()
    {
        await using var db = CreateDb();
        var organization = new Organization { Id = "org-alpha", Name = "Alpha Organization" };
        var customer = new Customer { Id = "customer-primary", Name = "Primary User", Email = "primary@example.com", OrganizationId = organization.Id };
        db.Organizations.Add(organization);
        db.Customers.Add(customer);
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = customer.Id,
            OidcIssuer = "https://id.example.com/application/o/rateldesk",
            OidcSubject = "subject-1",
            AuthentikEmail = customer.Email,
            InviteStatus = CustomerInviteStatus.Active
        });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, customer.Email), new Claim("email", customer.Email)],
            "test"));

        var access = await new CurrentUserAccessService(db).ResolveAsync(principal);

        Assert.Null(access.CustomerId);
        Assert.Empty(access.AllowedOrganizationIds);
        Assert.Empty(access.Permissions);
    }

    [Fact]
    public async Task Local_account_link_grants_customer_access_without_using_email_as_an_identity_key()
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "org-alpha", Name = "Alpha Organization" });
        db.Customers.Add(new Customer { Id = "customer-primary", Name = "Primary User", Email = "primary@example.com", OrganizationId = "org-alpha" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = "customer-primary",
            LocalAccountId = "local-account-1",
            AuthProviderType = "Local",
            InviteStatus = CustomerInviteStatus.Active
        });
        await db.SaveChangesAsync();

        db.Users.Add(new User { Id = "local-account-1", Name = "Local user", Email = "primary@example.com", OrganizationId = "org-alpha" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
        {
            UserId = "local-account-1", OrganizationId = "org-alpha", RoleKey = ScopedRoleCatalog.SelfServiceUser
        });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "local-account-1"),
                new Claim(ClaimTypes.Email, "different-profile-email@example.test"),
                new Claim("auth_mode", "local")
            ],
            "RatelDeskLocal"));

        var access = await new CurrentUserAccessService(db).ResolveAsync(principal);

        Assert.Equal("customer-primary", access.CustomerId);
        Assert.Equal("org-alpha", access.PrimaryOrganizationId);
        Assert.Contains(HelpdeskPermissions.SelfServiceUser, access.Permissions);
    }

    [Fact]
    public async Task Disabled_customer_gets_no_tenant_permissions()
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "org-alpha", Name = "Alpha Organization" });
        db.Customers.Add(new Customer
        {
            Id = "customer-primary",
            Name = "Primary User",
            Email = "primary@example.com",
            OrganizationId = "org-alpha",
            State = Helpdesk.Shared.Models.EntityState.Blocked
        });
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = "customer-primary",
            OidcIssuer = "https://id.example.com/application/o/rateldesk",
            OidcSubject = "subject-1",
            AuthentikEmail = "primary@example.com",
            InviteStatus = CustomerInviteStatus.Active
        });
        await db.SaveChangesAsync();

        var access = await new CurrentUserAccessService(db).ResolveAsync(User("primary@example.com", "subject-1"));

        Assert.Null(access.CustomerId);
        Assert.Empty(access.Permissions);
        Assert.Empty(access.AllowedOrganizationIds);
    }

    [Fact]
    public async Task Local_scoped_role_assignments_keep_permissions_in_their_assigned_organization()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(
            new Organization { Id = "org-a", Name = "Organization A" },
            new Organization { Id = "org-b", Name = "Organization B" });
        db.Users.Add(new User { Id = "local-user", Name = "Local user", Email = "local@example.test", OrganizationId = "org-a", Role = "User" });
        db.ScopedRoleAssignments.AddRange(
            new ScopedRoleAssignment { UserId = "local-user", OrganizationId = "org-a", RoleKey = ScopedRoleCatalog.Technician },
            new ScopedRoleAssignment { UserId = "local-user", OrganizationId = "org-b", RoleKey = ScopedRoleCatalog.SelfServiceUser });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "local-user"),
                new Claim(ClaimTypes.Email, "local@example.test"),
                new Claim("auth_mode", "local")
            ],
            "RatelDeskLocal"));

        var access = await new CurrentUserAccessService(db).ResolveAsync(principal);

        Assert.True(access.HasPermission(HelpdeskPermissions.IncidentWrite, "org-a"));
        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentManager, "org-b"));
        Assert.True(access.HasPermission(HelpdeskPermissions.IncidentUser, "org-b"));
    }

    [Fact]
    public async Task Local_scoped_role_assignments_for_disabled_organizations_are_ignored()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(
            new Organization { Id = "active-org", Name = "Active organization" },
            new Organization { Id = "disabled-org", Name = "Disabled organization", IsEnabled = false });
        db.Users.Add(new User { Id = "local-user", Name = "Local user", Email = "local@example.test", OrganizationId = "active-org", Role = "User" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
        {
            UserId = "local-user",
            OrganizationId = "disabled-org",
            RoleKey = ScopedRoleCatalog.Technician
        });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "local-user"),
                new Claim("auth_mode", "local")
            ],
            "RatelDeskLocal"));

        var access = await new CurrentUserAccessService(db).ResolveAsync(principal);

        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentManager, "disabled-org"));
        Assert.DoesNotContain("disabled-org", access.AllowedOrganizationIds);
    }

    [Fact]
    public async Task Disabled_local_identity_account_has_no_effective_access()
    {
        await using var db = CreateDb();
        await using var identityDb = CreateIdentityDb();
        db.Organizations.Add(new Organization { Id = "org-a", Name = "Organization A" });
        db.Users.Add(new User { Id = "disabled-local-user", Name = "Disabled local user", Email = "disabled@example.test", OrganizationId = "org-a", Role = "Technician" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
        {
            UserId = "disabled-local-user",
            OrganizationId = "org-a",
            RoleKey = ScopedRoleCatalog.Technician
        });
        identityDb.Users.Add(new ApplicationUser
        {
            Id = "disabled-local-user",
            UserName = "disabled@example.test",
            Email = "disabled@example.test",
            DisplayName = "Disabled local user",
            IsEnabled = false
        });
        await db.SaveChangesAsync();
        await identityDb.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "disabled-local-user"),
                new Claim(ClaimTypes.Email, "disabled@example.test"),
                new Claim("auth_mode", "local")
            ],
            "RatelDeskLocal"));

        var access = await new CurrentUserAccessService(db, identityDb).ResolveAsync(principal);

        Assert.True(access.IsAuthenticated);
        Assert.False(access.CanManageIncident("org-a"));
        Assert.Empty(access.Permissions);
        Assert.Empty(access.AllowedOrganizationIds);
    }

    [Fact]
    public async Task Linked_oidc_identity_resolves_its_persisted_instance_administrator_account()
    {
        await using var db = CreateDb();
        await using var identityDb = CreateIdentityDb();
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = "customer-a",
            LocalAccountId = "application-admin",
            OidcIssuer = "https://id.example.com/application/o/rateldesk",
            OidcSubject = "linked-admin",
            InviteStatus = CustomerInviteStatus.Active
        });
        identityDb.Users.Add(new ApplicationUser
        {
            Id = "application-admin",
            UserName = "admin@example.test",
            Email = "admin@example.test",
            IsEnabled = true,
            IsInstanceAdministrator = true
        });
        await db.SaveChangesAsync();
        await identityDb.SaveChangesAsync();

        var access = await new CurrentUserAccessService(db, identityDb)
            .ResolveAsync(User("admin@example.test", "linked-admin"));

        Assert.True(access.IsHelpdeskAdmin);
    }

    [Fact]
    public async Task Instance_administrator_integration_credential_is_limited_to_its_requested_permission_tuple()
    {
        await using var db = CreateDb();
        await using var identityDb = CreateIdentityDb();
        db.Organizations.AddRange(
            new Organization { Id = "org-a", Name = "Organization A" },
            new Organization { Id = "org-b", Name = "Organization B" });
        identityDb.Users.Add(new ApplicationUser
        {
            Id = "application-admin",
            UserName = "admin@example.test",
            IsEnabled = true,
            IsInstanceAdministrator = true
        });
        await db.SaveChangesAsync();
        await identityDb.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "application-admin"),
                new Claim("auth_mode", "integration"),
                new Claim("integration_organization_id", "org-b"),
                new Claim("integration_permission", HelpdeskPermissions.IncidentRead)
            ],
            "IntegrationCredential"));

        var access = await new CurrentUserAccessService(db, identityDb).ResolveAsync(principal);

        Assert.False(access.IsHelpdeskAdmin);
        Assert.True(access.HasPermission(HelpdeskPermissions.IncidentRead, "org-b"));
        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentRead, "org-a"));
        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentWrite, "org-b"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Integration_credential_projects_scoped_organization_into_tenant_filter_without_foreign_customer(bool isInstanceAdministrator)
    {
        await using var identityDb = CreateIdentityDb();
        var httpContext = new DefaultHttpContext();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var tenantContext = new TenantContext(httpContextAccessor);
        var dbOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new HelpdeskDbContext(dbOptions, tenantContext, httpContextAccessor);

        db.Organizations.AddRange(
            new Organization { Id = "org-a", Name = "Organization A" },
            new Organization { Id = "org-b", Name = "Organization B" });
        db.Customers.Add(new Customer { Id = "customer-a", Name = "Customer A", Email = "owner@example.test", OrganizationId = "org-a" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "customer-a", LocalAccountId = "owner", InviteStatus = CustomerInviteStatus.Active });
        db.Users.Add(new User { Id = "owner", Name = "Owner", Email = "owner@example.test", OrganizationId = "org-a" });
        db.Incidents.AddRange(
            new Incident { Id = "incident-a", OrganizationId = "org-a", CustomerId = "customer-a", Title = "Organization A incident" },
            new Incident { Id = "incident-b", OrganizationId = "org-b", CustomerId = "customer-b", Title = "Organization B incident" });
        if (!isInstanceAdministrator)
        {
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "owner",
                OrganizationId = "org-b",
                RoleKey = ScopedRoleCatalog.IncidentReader
            });
        }
        identityDb.Users.Add(new ApplicationUser
        {
            Id = "owner",
            UserName = "owner@example.test",
            Email = "owner@example.test",
            IsEnabled = true,
            IsInstanceAdministrator = isInstanceAdministrator
        });
        await db.SaveChangesAsync();
        await identityDb.SaveChangesAsync();

        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "owner"),
            new Claim("auth_mode", "integration"),
            new Claim("integration_organization_id", "org-b"),
            new Claim("integration_permission", HelpdeskPermissions.IncidentRead)
        ], "IntegrationCredential"));
        var accessService = new CurrentUserAccessService(db, identityDb);
        await new UserAccessClaimsMiddleware(_ => Task.CompletedTask).InvokeAsync(httpContext, accessService);

        var access = await accessService.ResolveAsync(httpContext.User);
        Assert.Equal("org-b", access.PrimaryOrganizationId);
        Assert.Equal("Organization B", access.PrimaryOrganizationName);
        Assert.Null(access.CustomerId);
        Assert.Equal(new[] { "org-b" }, access.AllowedOrganizationIds);
        Assert.True(access.HasPermission(HelpdeskPermissions.IncidentRead, "org-b"));
        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentRead, "org-a"));
        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentWrite, "org-b"));
        Assert.Equal("org-b", tenantContext.TenantId);
        Assert.Null(httpContext.User.FindFirstValue("customer_id"));
        Assert.Contains(httpContext.User.Claims, claim => claim.Type == "allowed_organization_id" && claim.Value == "org-b");
        Assert.DoesNotContain(httpContext.User.Claims, claim => claim.Type == "allowed_organization_id" && claim.Value == "org-a");
        Assert.Equal(new[] { "incident-b" }, await db.Incidents.Select(incident => incident.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData("org-b", null)]
    [InlineData("org-a", "customer-a")]
    public async Task Integration_credential_projects_scoped_organization_and_only_keeps_matching_customer_identity(
        string requestedOrganizationId,
        string? expectedCustomerId)
    {
        await using var db = CreateDb();
        await using var identityDb = CreateIdentityDb();
        db.Organizations.AddRange(
            new Organization { Id = "org-a", Name = "Organization A" },
            new Organization { Id = "org-b", Name = "Organization B" });
        db.Customers.Add(new Customer { Id = "customer-a", Name = "Customer A", Email = "owner@example.test", OrganizationId = "org-a" });
        db.Users.Add(new User { Id = "owner", Name = "Owner", Email = "owner@example.test", OrganizationId = "org-a" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "customer-a", LocalAccountId = "owner", InviteStatus = CustomerInviteStatus.Active });
        db.ScopedRoleAssignments.AddRange(
            new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-a", RoleKey = ScopedRoleCatalog.SelfServiceUser },
            new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-b", RoleKey = ScopedRoleCatalog.SelfServiceUser });
        identityDb.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner@example.test", IsEnabled = true });
        await db.SaveChangesAsync();
        await identityDb.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "owner"),
            new Claim("auth_mode", "integration"),
            new Claim("integration_organization_id", requestedOrganizationId),
            new Claim("integration_permission", HelpdeskPermissions.SelfServiceUser)
        ], "IntegrationCredential"));

        var access = await new CurrentUserAccessService(db, identityDb).ResolveAsync(principal);

        Assert.Equal(expectedCustomerId, access.CustomerId);
        Assert.Equal(requestedOrganizationId, access.PrimaryOrganizationId);
        Assert.Equal(requestedOrganizationId == "org-b" ? "Organization B" : "Organization A", access.PrimaryOrganizationName);
        Assert.Equal([requestedOrganizationId], access.AllowedOrganizationIds);
        Assert.True(access.HasPermission(HelpdeskPermissions.SelfServiceUser, requestedOrganizationId));
        Assert.False(access.HasPermission(HelpdeskPermissions.SelfServiceUser, requestedOrganizationId == "org-b" ? "org-a" : "org-b"));
    }

    [Fact]
    public async Task Persisted_custom_role_permissions_are_scoped_to_the_assigned_tenant()
    {
        await using var db = CreateDb();
        db.Organizations.AddRange(
            new Organization { Id = "org-a", Name = "Organization A" },
            new Organization { Id = "org-b", Name = "Organization B" });
        db.Users.Add(new User { Id = "local-user", Name = "Local user", Email = "local@example.test", OrganizationId = "org-a", Role = "User" });
        db.Roles.Add(new Role
        {
            Id = "custom-incident-reader",
            Key = "custom.incident-reader",
            Name = "Incident Reader",
            Scope = RoleScopeKind.Tenant,
            OwnerOrganizationId = "org-a",
            Permissions = [new RolePermission { Permission = HelpdeskPermissions.IncidentUser }]
        });
        db.ScopedRoleAssignments.AddRange(
            new ScopedRoleAssignment
            {
                UserId = "local-user",
                OrganizationId = "org-a",
                RoleKey = "custom.incident-reader"
            },
            new ScopedRoleAssignment
            {
                UserId = "local-user",
                OrganizationId = "org-b",
                RoleKey = "custom.incident-reader"
            },
            new ScopedRoleAssignment
            {
                UserId = "local-user",
                OrganizationId = "org-b",
                RoleKey = ScopedRoleCatalog.TenantAdministrator
            });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "local-user"),
                new Claim(ClaimTypes.Email, "local@example.test"),
                new Claim("auth_mode", "local")
            ],
            "RatelDeskLocal"));

        var access = await new CurrentUserAccessService(db).ResolveAsync(principal);

        Assert.True(access.HasPermission(HelpdeskPermissions.IncidentUser, "org-a"));
        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentUser, "org-b"));
        Assert.False(access.HasPermission(HelpdeskPermissions.IncidentManager, "org-a"));
        Assert.True(access.HasPermission(HelpdeskPermissions.TenantUsersManage, "org-b"));
        Assert.False(access.HasPermission(HelpdeskPermissions.TenantUsersManage, "org-a"));
    }

    [Fact]
    public async Task External_identity_uses_its_verified_link_for_scoped_custom_role_grants()
    {
        await using var db = CreateDb();
        db.Organizations.Add(new Organization { Id = "org-a", Name = "Organization A" });
        db.Customers.Add(new Customer { Id = "customer-a", Name = "External user", Email = "shared@example.test", OrganizationId = "org-a" });
        db.Users.Add(new User { Id = "external-domain-user", Name = "External user", Email = "shared@example.test", OrganizationId = "org-a", Role = "Customer" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = "customer-a",
            DomainUserId = "external-domain-user",
            OidcIssuer = "https://id.example.com/application/o/rateldesk",
            OidcSubject = "external-subject",
            InviteStatus = CustomerInviteStatus.Active
        });
        db.Roles.Add(new Role
        {
            Id = "custom-external-reader",
            Key = "custom.external-reader",
            Name = "External incident reader",
            Scope = RoleScopeKind.Tenant,
            OwnerOrganizationId = "org-a",
            Permissions = [new RolePermission { Permission = HelpdeskPermissions.IncidentUser }]
        });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
        {
            UserId = "external-domain-user",
            OrganizationId = "org-a",
            RoleKey = "custom.external-reader"
        });
        await db.SaveChangesAsync();

        var access = await new CurrentUserAccessService(db).ResolveAsync(User("different-profile-email@example.test", "external-subject"));

        Assert.True(access.HasPermission(HelpdeskPermissions.IncidentUser, "org-a"));
        Assert.Equal("customer-a", access.CustomerId);
    }

    [Fact]
    public async Task Protected_built_in_role_definitions_seed_idempotently()
    {
        await using var db = CreateDb();

        await RoleDefinitionSeeder.EnsureBuiltInsAsync(db);
        await RoleDefinitionSeeder.EnsureBuiltInsAsync(db);

        var roles = await db.Roles.Include(role => role.Permissions).ToListAsync();
        Assert.Equal(RoleDefinitionCatalog.BuiltIns.Count, roles.Count);
        Assert.All(roles, role =>
        {
            Assert.True(role.IsBuiltIn);
            Assert.True(role.IsProtected);
        });
        Assert.Contains(roles, role =>
            role.Key == ScopedRoleCatalog.Technician &&
            role.Permissions.Select(permission => permission.Permission)
                .OrderBy(permission => permission)
                .SequenceEqual(HelpdeskPermissions.OperatorBundle.OrderBy(permission => permission)));
    }

    [Fact]
    public async Task Existing_tenant_administrator_definition_is_reconciled_with_scoped_customer_and_sla_permissions()
    {
        await using var db = CreateDb();
        db.Roles.Add(new Role
        {
            Id = "legacy-tenant-administrator",
            Key = ScopedRoleCatalog.TenantAdministrator,
            Name = "Tenant Administrator",
            Scope = RoleScopeKind.Tenant,
            IsBuiltIn = true,
            IsProtected = true,
            Permissions =
            [
                new RolePermission { Permission = HelpdeskPermissions.TenantUsersManage },
                new RolePermission { Permission = HelpdeskPermissions.TenantRolesAssign },
                new RolePermission { Permission = HelpdeskPermissions.TenantSettingsManage }
            ]
        });
        await db.SaveChangesAsync();

        await RoleDefinitionSeeder.EnsureBuiltInsAsync(db);

        var tenantAdministrator = await db.Roles.Include(role => role.Permissions)
            .SingleAsync(role => role.Key == ScopedRoleCatalog.TenantAdministrator);
        Assert.Contains(HelpdeskPermissions.TenantCustomersManage, tenantAdministrator.Permissions.Select(permission => permission.Permission));
        Assert.Contains(HelpdeskPermissions.TenantSlaManage, tenantAdministrator.Permissions.Select(permission => permission.Permission));
        Assert.DoesNotContain(HelpdeskPermissions.TenantCustomersManage, RoleDefinitionCatalog.TenantAdministratorPermissionCeiling);
        Assert.DoesNotContain(HelpdeskPermissions.TenantSlaManage, RoleDefinitionCatalog.TenantAdministratorPermissionCeiling);
    }

    private static ClaimsPrincipal User(string email, string subject, params string[] groups)
        => User(email, subject, tenantId: null, groups);

    private static ClaimsPrincipal User(string email, string subject, string? tenantId, string[] groups)
    {
        var claims = new List<Claim>
        {
            new("iss", "https://id.example.com/application/o/rateldesk/"),
            new("sub", subject),
            new("email", email),
            new(ClaimTypes.Email, email)
        };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }
        claims.AddRange(groups.Select(group => new Claim("groups", group)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static HelpdeskDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new HelpdeskDbContext(options, new EmptyTenantContext(), new HttpContextAccessor());
    }

    private static RatelDeskIdentityDbContext CreateIdentityDb()
    {
        var options = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RatelDeskIdentityDbContext(options);
    }

    private sealed class EmptyTenantContext : ITenantContext
    {
        public string? TenantId => null;
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }
}
