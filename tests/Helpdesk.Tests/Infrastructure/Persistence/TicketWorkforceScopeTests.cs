using System.Security.Claims;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Ticketing;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class TicketWorkforceScopeTests
{
    private const string ManagedCustomer = "managed-customer";
    private const string Msp = "msp";
    private const string Vendor = "vendor";
    private const string VendorQueue = "app-vendor";
    private const string Wintel = "wintel-l2";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_queue_grants_survive_credential_narrowing_without_unioning_groups_and_operations(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        await RoleDefinitionSeeder.EnsureBuiltInsAsync(db);
        db.Organizations.AddRange(
            new Organization { Id = Msp, Name = "MSP" },
            new Organization { Id = ManagedCustomer, Name = "Managed Customer", ItSupportOrganizationId = Msp },
            new Organization { Id = "foreign", Name = "Unrelated Tenant" });
        foreach (var id in new[] { "desktop", "wintel-l1", Wintel, "wintel-l3", VendorQueue })
        {
            db.SupportGroups.Add(new SupportGroup { Id = id, OwningOrganizationId = Msp, Name = id });
            db.OrganizationSupportCoverages.Add(new OrganizationSupportCoverage
            {
                CustomerOrganizationId = ManagedCustomer, ProviderOrganizationId = Msp, SupportGroupId = id
            });
        }
        db.Users.AddRange(new User { Id = Vendor, OrganizationId = Msp, Name = "Vendor Agent", Email = "vendor@example.test" },
            new User { Id = "operator", OrganizationId = Msp, Name = "MSP Operator" },
            new User { Id = "tenant-admin", OrganizationId = ManagedCustomer, Name = "Tenant Admin" });
        db.Customers.Add(new Customer { Id = "vendor-contact", OrganizationId = Msp, Email = "vendor@example.test" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink
        {
            CustomerId = "vendor-contact", DomainUserId = Vendor, OidcIssuer = "https://identity.example.test", OidcSubject = "vendor"
        });
        var appRead = RoleFor("custom.vendor-read", VendorQueue, HelpdeskPermissions.IncidentRead);
        var wintelWrite = RoleFor("custom.wintel-write", Wintel, HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite);
        db.Roles.AddRange(appRead, wintelWrite);
        db.ScopedRoleAssignments.AddRange(
            new ScopedRoleAssignment { UserId = Vendor, OrganizationId = Msp, RoleKey = appRead.Key },
            new ScopedRoleAssignment { UserId = Vendor, OrganizationId = Msp, RoleKey = wintelWrite.Key },
            new ScopedRoleAssignment { UserId = Vendor, OrganizationId = Msp, RoleKey = ScopedRoleCatalog.Technician },
            new ScopedRoleAssignment { UserId = "operator", OrganizationId = Msp, RoleKey = ScopedRoleCatalog.Technician },
            new ScopedRoleAssignment { UserId = "tenant-admin", OrganizationId = ManagedCustomer, RoleKey = ScopedRoleCatalog.TenantAdministrator },
            new ScopedRoleAssignment { UserId = "tenant-admin", OrganizationId = ManagedCustomer, RoleKey = ScopedRoleCatalog.Technician });
        var wintelMembership = new SupportGroupMember { UserId = Vendor, SupportGroupId = Wintel };
        db.SupportGroupMembers.AddRange(new SupportGroupMember { UserId = Vendor, SupportGroupId = VendorQueue },
            wintelMembership, new SupportGroupMember { UserId = Vendor, SupportGroupId = "desktop" });
        await TicketWorkforceRestrictions.EnsureAsync(db, Msp, [Vendor], "owner", default);

        var app = IncidentIn("app", ManagedCustomer, VendorQueue);
        var wintel = IncidentIn("wintel", ManagedCustomer, Wintel);
        var desktop = IncidentIn("desktop", ManagedCustomer, "desktop");
        var main = IncidentIn("main", ManagedCustomer, null);
        var sharedOrgMain = IncidentIn("own-main", Msp, null);
        sharedOrgMain.CustomerId = "vendor-contact";
        var localApp = IncidentIn("local-app", Msp, VendorQueue);
        var foreign = IncidentIn("foreign", "foreign", VendorQueue);
        db.Incidents.AddRange(app, wintel, desktop, main, sharedOrgMain, localApp, foreign);
        var request = new Request { Id = "request", OrganizationId = ManagedCustomer, AssignedSupportGroupId = VendorQueue };
        var change = new Change { Id = "change", OrganizationId = ManagedCustomer, AssignedSupportGroupId = VendorQueue };
        db.AddRange(request, change);
        var credentialId = Guid.NewGuid();
        db.Set<McpAccessPolicy>().Add(new McpAccessPolicy
        {
            CredentialId = credentialId, OrganizationId = ManagedCustomer,
            PermissionsJson = """["Incident.Read"]""", ExpiresUtc = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();

        var resolver = new CurrentUserAccessService(db);
        var vendor = ExternalVendor();
        var access = await resolver.ResolveAsync(vendor);
        Assert.True(access.HasRestrictedWorkforceAccess);
        Assert.Equal(new[] { "app", "local-app", "wintel" }, await TicketAccessPolicy.Readable(db.Incidents, access, TicketType.Incident)
            .OrderBy(ticket => ticket.Id).Take(4).Select(ticket => ticket.Id).ToArrayAsync());
        Assert.True(TicketAccessPolicy.CanRead(access, app));
        Assert.True(TicketAccessPolicy.CanWrite(access, wintel));
        Assert.False(TicketAccessPolicy.CanWrite(access, app));
        Assert.False(TicketAccessPolicy.CanRead(access, desktop));
        Assert.False(TicketAccessPolicy.CanRead(access, main));
        Assert.False(TicketAccessPolicy.CanRead(access, sharedOrgMain));
        Assert.False(TicketAccessPolicy.CanRead(access, request));
        Assert.False(TicketAccessPolicy.CanRead(access, change));
        Assert.False(TicketAccessPolicy.CanRead(access, foreign));
        Assert.False(TicketAccessPolicy.CanTransfer(access, TicketType.Incident, ManagedCustomer, Wintel));

        var credential = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Vendor), new Claim("auth_mode", "integration"),
            new Claim("integration_organization_id", ManagedCustomer), new Claim("integration_credential_id", credentialId.ToString()),
            new Claim("integration_permission", HelpdeskPermissions.IncidentRead),
            new Claim("integration_permission", HelpdeskPermissions.IncidentWrite)
        ], "test"));
        var delegated = await resolver.ResolveAsync(credential);
        Assert.True(TicketAccessPolicy.CanRead(delegated, app));
        Assert.False(TicketAccessPolicy.CanRead(delegated, localApp));
        Assert.False(TicketAccessPolicy.CanRead(delegated, main));
        Assert.False(TicketAccessPolicy.CanWrite(delegated, wintel));

        var operatorAccess = await resolver.ResolveAsync(Local("operator"));
        Assert.True(TicketAccessPolicy.CanRead(operatorAccess, desktop));
        Assert.True(TicketAccessPolicy.CanRead(operatorAccess, main));
        Assert.False(TicketAccessPolicy.CanRead(operatorAccess, foreign));
        var administrator = await resolver.ResolveAsync(Local("tenant-admin"));
        Assert.True(TicketAccessPolicy.CanRead(administrator, desktop));
        Assert.True(TicketAccessPolicy.CanRead(administrator, main));
        Assert.False(TicketAccessPolicy.CanRead(administrator, foreign));

        wintelMembership.IsEnabled = false;
        await db.SaveChangesAsync();
        Assert.False(TicketAccessPolicy.CanRead(await resolver.ResolveAsync(vendor), wintel));
        Assert.False(TicketAccessPolicy.CanRead(await resolver.ResolveAsync(credential), wintel));

        var appCoverage = await db.OrganizationSupportCoverages.SingleAsync(coverage => coverage.SupportGroupId == VendorQueue);
        appCoverage.IsEnabled = false;
        await db.SaveChangesAsync();
        Assert.False(TicketAccessPolicy.CanRead(await resolver.ResolveAsync(vendor), app));
        Assert.True(TicketAccessPolicy.CanRead(await resolver.ResolveAsync(vendor), localApp));
        db.ScopedRoleAssignments.RemoveRange(await db.ScopedRoleAssignments
            .Where(assignment => assignment.UserId == Vendor && assignment.RoleKey.StartsWith("custom.")).ToArrayAsync());
        await db.SaveChangesAsync();
        var afterRoleRemoval = await resolver.ResolveAsync(vendor);
        Assert.True(afterRoleRemoval.HasRestrictedWorkforceAccess);
        Assert.Empty(await TicketAccessPolicy.Readable(db.Incidents, afterRoleRemoval, TicketType.Incident).ToArrayAsync());
    }

    private static Role RoleFor(string key, string groupId, params string[] permissions)
    {
        var role = new Role
        {
            Key = key, Name = key, OwnerOrganizationId = Msp, TicketAccessScope = TicketAccessScope.SelectedSupportGroups,
            Permissions = permissions.Select(permission => new RolePermission { Permission = permission }).ToList()
        };
        role.SupportGroups.Add(new RoleSupportGroup { RoleId = role.Id, SupportGroupId = groupId });
        return role;
    }

    private static Incident IncidentIn(string id, string organizationId, string? groupId) =>
        new() { Id = id, OrganizationId = organizationId, AssignedSupportGroupId = groupId, Title = id, TrackingId = $"INC-{id}" };

    private static ClaimsPrincipal ExternalVendor() => new(new ClaimsIdentity([
        new Claim("iss", "https://identity.example.test"), new Claim("sub", "vendor"),
        new Claim("groups", AuthentikRbacGroups.Technical)
    ], "oidc"));

    private static ClaimsPrincipal Local(string userId) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, userId), new Claim("auth_mode", "local")
    ], "test"));
}
