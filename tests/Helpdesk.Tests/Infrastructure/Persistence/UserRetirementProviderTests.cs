using System.Security.Claims;
using Helpdesk.API.Endpoints.Users;
using Helpdesk.API.Services.Users;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class UserRetirementProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_migrations_preserve_existing_long_identity_data_and_default_to_active(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        var previousDomain = db.Database.GetMigrations().TakeWhile(migration => !migration.EndsWith("_RetireApplicationUsers", StringComparison.Ordinal)).Last();
        await db.GetService<IMigrator>().MigrateAsync(previousDomain);
        var longName = new string('x', 10_000);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "Name", "Email", "Role", "IsTestUser")
            VALUES ({"legacy-user"}, {longName}, {"legacy@example.invalid"}, {"User"}, {false})
            """);
        await using var identity = fixture.Identity();
        var previousIdentity = identity.Database.GetMigrations().TakeWhile(migration => !migration.EndsWith("_RetireApplicationAccounts", StringComparison.Ordinal)).Last();
        await identity.GetService<IMigrator>().MigrateAsync(previousIdentity);
        await identity.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers" ("Id", "UserName", "DisplayName", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "IsEnabled", "IsInstanceAdministrator", "AuthorizationRevision")
            VALUES ({"legacy-user"}, {"legacy@example.invalid"}, {"Legacy identity"}, {true}, {false}, {false}, {true}, {0}, {true}, {false}, {0L})
            """);
        await db.Database.MigrateAsync(); await identity.Database.MigrateAsync();
        var domain = await db.Users.SingleAsync(); var account = await identity.Users.SingleAsync();
        Assert.Equal(longName, domain.Name); Assert.False(domain.IsDeleted); Assert.Null(domain.DeletedAtUtc);
        Assert.True(account.IsEnabled); Assert.False(account.IsDeleted); Assert.Null(account.DeletedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_identity_dependency_is_rejected_and_unlinked_domain_profile_has_supported_retirement(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open(); await db.Database.MigrateAsync();
        await using var identity = fixture.Identity(); await identity.Database.MigrateAsync();
        db.Organizations.Add(new Organization { Id = "organization", Name = "Synthetic team" });
        db.Users.AddRange(new User { Id = "target", Name = "Target" }, new User { Id = "other", Name = "Other" }, new User { Id = "profile", Name = "Unlinked profile" }, new User { Id = "legacy", Name = "Legacy provider profile" });
        db.Customers.AddRange(new Customer { Id = "one", Name = "One", Email = "one@example.invalid", OrganizationId = "organization" }, new Customer { Id = "two", Name = "Two", Email = "two@example.invalid", OrganizationId = "organization" }, new Customer { Id = "legacy", Name = "Legacy", Email = "legacy@example.invalid", OrganizationId = "organization" });
        db.CustomerAuthLinks.AddRange(new CustomerAuthLink { Id = "one", CustomerId = "one", DomainUserId = "target" },
            new CustomerAuthLink { Id = "two", CustomerId = "two", DomainUserId = "other", LocalAccountId = "target" },
            new CustomerAuthLink { Id = "legacy", CustomerId = "legacy", DomainUserId = "legacy", AuthentikUserId = "provider-only-id" });
        identity.Users.Add(new ApplicationUser { Id = "target", UserName = "target" });
        await db.SaveChangesAsync(); await identity.SaveChangesAsync();
        Assert.Equal("shared_identity_dependency", (await UserRetirement.RetireAsync("target", Actor("administrator"), db, identity)).Error);
        Assert.Equal("shared_identity_dependency", (await UserRetirement.RetireAsync("other", Actor("administrator"), db, identity)).Error);
        Assert.False((await db.Users.SingleAsync(user => user.Id == "target")).IsDeleted);
        Assert.True((await identity.Users.SingleAsync()).IsEnabled);
        Assert.Null((await UserRetirement.RetireAsync("profile", Actor("administrator"), db, identity)).Error);
        Assert.True((await db.Users.SingleAsync(user => user.Id == "profile")).IsDeleted);
        var otherLink = await db.CustomerAuthLinks.SingleAsync(link => link.Id == "two");
        otherLink.AuthentikUserId = "provider-only-id";
        await db.SaveChangesAsync();
        Assert.Equal("shared_identity_dependency", (await UserRetirement.RetireAsync("legacy", Actor("administrator"), db, identity)).Error);
        otherLink.AuthentikUserId = null;
        await db.SaveChangesAsync();
        Assert.Equal("external_identity_link_incomplete", (await UserRetirement.RetireAsync("legacy", Actor("administrator"), db, identity)).Error);
        Assert.False((await db.Users.SingleAsync(user => user.Id == "legacy")).IsDeleted);
        Assert.Equal(3, await db.Customers.CountAsync());
        Assert.Single(await identity.Users.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_atomically_retires_access_and_delivery_material_preserves_history_and_is_idempotent(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var db = fixture.Open())
        {
            await db.Database.MigrateAsync();
            db.Organizations.Add(new Organization { Id = "organization", Name = "Synthetic team" });
            db.Users.Add(new User { Id = "target", Name = "Historic Person", Email = "person@example.invalid", OrganizationId = "organization", Role = "User" });
            db.Customers.Add(new Customer { Id = "contact", Name = "Retained contact", Email = "contact@example.invalid", OrganizationId = "organization" });
            db.CustomerAuthLinks.Add(new CustomerAuthLink { Id = "link", CustomerId = "contact", DomainUserId = "target", LocalAccountId = "target", AuthProviderType = "Local", InviteStatus = CustomerInviteStatus.Active });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "target", OrganizationId = "organization", RoleKey = "self-service-user" });
            db.ActivityLogs.Add(new ActivityLog { Id = "history", RelatedEntityId = "prior-work", UserId = "target", Message = "Synthetic historical attribution" });
            await db.SaveChangesAsync();
        }
        var credentialId = Guid.NewGuid();
        var pairId = Guid.NewGuid();
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.Users.Add(new ApplicationUser { Id = "target", UserName = "person@example.invalid", NormalizedUserName = "PERSON@EXAMPLE.INVALID", IsEnabled = true, SecurityStamp = "original-stamp", PasswordHash = "synthetic-verifier", AuthorizationRevision = 4 });
            identity.UserTokens.Add(new IdentityUserToken<string> { UserId = "target", LoginProvider = "Default", Name = "AuthenticatorKey", Value = "synthetic" });
            identity.IntegrationCredentials.Add(new IntegrationCredential { Id = credentialId, OwnerUserId = "target", Name = "Synthetic credential", Prefix = "rdk_synthetic", SecretHash = "synthetic-verifier", Purpose = "api", Permissions = "Request.User", OrganizationId = "organization", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) });
            identity.IntegrationCredentialPairings.Add(new IntegrationCredentialPairing { Id = pairId, DeviceHash = new string('a', 64), HumanHash = new string('b', 64), Name = "Synthetic pairing", OwnerUserId = "target", State = "issued", CredentialId = credentialId, ProtectedApproval = "synthetic-protected-approval", ProtectedDelivery = "synthetic-protected-delivery" });
            identity.IntegrationCredentialSetupAttempts.Add(new IntegrationCredentialSetupAttempt { RequestId = Guid.NewGuid(), OwnerUserId = "target", CredentialId = credentialId, RequestHash = new string('c', 64), ProtectedDelivery = "synthetic-protected-delivery" });
            await identity.SaveChangesAsync();
        }
        await using var stale = fixture.Identity();
        var stalePair = await stale.IntegrationCredentialPairings.SingleAsync();
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            var outcome = await UserRetirement.RetireAsync("target", Actor("administrator"), db, identity);
            Assert.True(outcome.Found); Assert.Null(outcome.Error);
        }
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            var target = await db.Users.SingleAsync();
            Assert.True(target.IsDeleted); Assert.NotNull(target.DeletedAtUtc); Assert.Equal("Historic Person", target.Name);
            Assert.Equal("Retained contact", (await db.Customers.SingleAsync()).Name);
            Assert.Equal("target", (await db.ActivityLogs.SingleAsync(log => log.Id == "history")).UserId);
            Assert.Empty(await db.ScopedRoleAssignments.ToListAsync());
            var link = await db.CustomerAuthLinks.SingleAsync();
            Assert.Equal("target", link.DomainUserId); Assert.Equal(CustomerInviteStatus.Disabled, link.InviteStatus); Assert.NotNull(link.DisabledAtUtc);
            var account = await identity.Users.SingleAsync();
            Assert.True(account.IsDeleted); Assert.False(account.IsEnabled); Assert.Null(account.PasswordHash); Assert.Equal(5, account.AuthorizationRevision); Assert.NotEqual("original-stamp", account.SecurityStamp);
            Assert.Empty(await identity.UserTokens.ToListAsync());
            Assert.NotNull((await identity.IntegrationCredentials.SingleAsync()).RevokedAtUtc);
            var pair = await identity.IntegrationCredentialPairings.SingleAsync();
            Assert.Equal("cancelled", pair.State); Assert.Null(pair.ProtectedDelivery); Assert.Null(pair.ProtectedApproval);
            var setup = await identity.IntegrationCredentialSetupAttempts.SingleAsync();
            Assert.Equal("Revoked", setup.State); Assert.Null(setup.ProtectedDelivery);
            var access = await new CurrentUserAccessService(db, identity).ResolveAsync(Actor("target", local: true));
            Assert.False(access.IsAuthenticated);
            Assert.False(access.IsHelpdeskAdmin); Assert.Empty(access.Permissions); Assert.Empty(access.AllowedOrganizationIds);
            var repeated = await UserRetirement.RetireAsync("target", Actor("administrator"), db, identity);
            Assert.Null(repeated.Error);
            Assert.Single(await db.ActivityLogs.Where(log => log.RelatedEntityId == "target").ToListAsync());
        }
        stalePair.State = "issued"; stalePair.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_subject_tombstone_blocks_claims_and_automatic_reprovisioning_without_deleting_customer(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        const string issuer = "https://identity.example.invalid/application";
        await using (var db = fixture.Open())
        {
            await db.Database.MigrateAsync();
            db.Organizations.Add(new Organization { Id = "organization", Name = "Synthetic team" });
            db.Users.AddRange(new User { Id = "external-domain", Name = "External Person", OrganizationId = "organization" }, new User { Id = "alias-domain", Name = "Existing alias", OrganizationId = "organization" });
            db.Customers.AddRange(new Customer { Id = "contact", Name = "Retained", Email = "external@example.invalid", OrganizationId = "organization" },
                new Customer { Id = "alias-contact", Name = "Retained alias", Email = "alias@example.invalid", OrganizationId = "organization" });
            db.CustomerAuthLinks.AddRange(new CustomerAuthLink { CustomerId = "contact", DomainUserId = "external-domain", OidcIssuer = issuer, OidcSubject = "verified-subject", InviteStatus = CustomerInviteStatus.Active },
                new CustomerAuthLink { CustomerId = "alias-contact", DomainUserId = "alias-domain", OidcIssuer = issuer + "/", OidcSubject = "verified-subject", InviteStatus = CustomerInviteStatus.Active });
            await db.SaveChangesAsync();
        }
        string accountId;
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            accountId = (await OidcApplicationAccount.ProvisionAdministratorAsync(identity, issuer, "verified-subject", "External Person")).Id;
            identity.Users.Add(new ApplicationUser { Id = "remaining-admin", UserName = "remaining", IsEnabled = true, IsInstanceAdministrator = true });
            await identity.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            Assert.Equal("shared_identity_dependency", (await UserRetirement.RetireAsync("external-domain", Actor("remaining-admin"), db, identity)).Error);
            (await db.Users.SingleAsync(user => user.Id == "alias-domain")).IsDeleted = true;
            await db.SaveChangesAsync();
            Assert.Null((await UserRetirement.RetireAsync("external-domain", Actor("remaining-admin"), db, identity)).Error);
        }
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", issuer), new Claim("sub", "verified-subject"), new Claim("groups", "HelpdeskAdmin")], "Oidc"));
            var access = await new CurrentUserAccessService(db, identity).ResolveAsync(principal);
            Assert.False(access.IsAuthenticated);
            Assert.False(access.IsHelpdeskAdmin); Assert.Empty(access.Permissions);
            var reprovisioned = await OidcApplicationAccount.ProvisionAdministratorAsync(identity, issuer, "verified-subject", "Changed profile");
            Assert.Equal(accountId, reprovisioned.Id); Assert.True(reprovisioned.IsDeleted); Assert.False(reprovisioned.IsEnabled);
            Assert.Single(await identity.UserLogins.ToListAsync()); Assert.Equal(2, await db.Customers.CountAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Self_last_admin_and_failed_audit_do_not_commit_partial_retirement(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using (var db = fixture.Open())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(new User { Id = "administrator", Name = "Admin" }, new User { Id = "ordinary", Name = "Ordinary" });
            await db.SaveChangesAsync();
        }
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.Users.AddRange(new ApplicationUser { Id = "administrator", UserName = "administrator", IsInstanceAdministrator = true }, new ApplicationUser { Id = "ordinary", UserName = "ordinary", SecurityStamp = "retained-stamp" });
            await identity.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            Assert.Equal("cannot_delete_self", (await UserRetirement.RetireAsync("administrator", Actor("administrator"), db, identity)).Error);
            Assert.Equal("last_instance_administrator", (await UserRetirement.RetireAsync("administrator", Actor("another-admin"), db, identity)).Error);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ActivityLogs\" RENAME TO \"UnavailableActivityLogs\"");
            await Assert.ThrowsAsync<DbUpdateException>(() => UserRetirement.RetireAsync("ordinary", Actor("administrator"), db, identity));
        }
        await using (var db = fixture.Open())
        await using (var identity = fixture.Identity())
        {
            Assert.False((await db.Users.SingleAsync(user => user.Id == "ordinary")).IsDeleted);
            var account = await identity.Users.SingleAsync(user => user.Id == "ordinary");
            Assert.True(account.IsEnabled); Assert.False(account.IsDeleted); Assert.Equal("retained-stamp", account.SecurityStamp);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"UnavailableActivityLogs\" RENAME TO \"ActivityLogs\"");
            Assert.Null((await UserRetirement.RetireAsync("ordinary", Actor("administrator"), db, identity)).Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Directory_search_filter_sort_count_and_clamp_cover_more_than_one_hundred_rows(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open(); await db.Database.MigrateAsync();
        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.Users.AddRange(Enumerable.Range(0, 133).Select(index => new ApplicationUser { Id = $"user-{index:D3}", UserName = $"user-{index:D3}", IsEnabled = index % 2 == 0 }));
            identity.UserLogins.Add(new IdentityUserLogin<string> { UserId = "user-128", LoginProvider = "synthetic-external", ProviderKey = "verified-subject" });
            await identity.SaveChangesAsync();
        }
        db.Organizations.Add(new Organization { Id = "organization", Name = "Synthetic organization" });
        db.Users.AddRange(Enumerable.Range(0, 133).Select(index => new User { Id = $"user-{index:D3}", Name = "Duplicate Name", Email = $"user{index:D3}@example.invalid", OrganizationId = "organization", Role = index % 3 == 0 ? "Technician" : "User", IsTestUser = index % 2 == 0 }));
        db.Users.Add(new User { Id = "deleted", Name = "Deleted profile", IsDeleted = true });
        db.Customers.Add(new Customer { Id = "disabled-link-contact", Name = "Synthetic contact", Email = "linked@example.invalid", OrganizationId = "organization" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "disabled-link-contact", DomainUserId = "user-130", LocalAccountId = "user-130", InviteStatus = CustomerInviteStatus.Disabled, DisabledAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var parameters = new UserDirectoryQuery.Parameters { Page = 6, PageSize = 25 };
        var page = Assert.IsType<Ok<PagedResult<UserDirectoryDto>>>(await UserDirectoryQuery.ReadAsync(parameters, db, Actor("administrator"), default)).Value!;
        Assert.Equal(133, page.Total); Assert.Equal(6, page.Page); Assert.Equal(8, page.Items.Count); Assert.Equal("user-125", page.Items[0].Id);
        var oidcOnly = Assert.IsType<Ok<PagedResult<UserDirectoryDto>>>(await UserDirectoryQuery.ReadAsync(new(), db, Actor("administrator"), false, default)).Value!;
        Assert.All(oidcOnly.Items, user => { Assert.False(user.CanManageAccount); Assert.False(user.CanChangeSignIn); Assert.True(user.CanDelete); });
        parameters.Search = "user132"; parameters.State = "Enabled"; parameters.IsTestUser = true;
        var filtered = Assert.IsType<Ok<PagedResult<UserDirectoryDto>>>(await UserDirectoryQuery.ReadAsync(parameters, db, Actor("administrator"), default)).Value!;
        Assert.Equal(1, filtered.Total); Assert.Equal(1, filtered.Page); Assert.Equal("user-132", Assert.Single(filtered.Items).Id);
        parameters.Search = null; parameters.State = "Disabled"; parameters.IsTestUser = null; parameters.SortBy = "email"; parameters.Descending = true;
        var disabled = Assert.IsType<Ok<PagedResult<UserDirectoryDto>>>(await UserDirectoryQuery.ReadAsync(parameters, db, Actor("administrator"), default)).Value!;
        Assert.Equal(67, disabled.Total); Assert.Equal(3, disabled.Page); Assert.All(disabled.Items, user => Assert.Equal("Disabled", user.AccountState));
        var blockedLink = Assert.IsType<Ok<PagedResult<UserDirectoryDto>>>(await UserDirectoryQuery.ReadAsync(new() { Search = "user130", State = "Disabled" }, db, Actor("administrator"), default)).Value!;
        var blockedUser = Assert.Single(blockedLink.Items);
        Assert.True(blockedUser.AccountSignInEnabled); Assert.Contains("Identity link disabled", blockedUser.IdentitySummary);
        var external = Assert.IsType<Ok<PagedResult<UserDirectoryDto>>>(await UserDirectoryQuery.ReadAsync(new() { Search = "user128", State = "Enabled" }, db, Actor("administrator"), default)).Value!;
        var externalUser = Assert.Single(external.Items);
        Assert.False(externalUser.CanManageAccount); Assert.Equal("External application account", externalUser.IdentitySummary);
        parameters.PageSize = 100; parameters.Page = 1; parameters.Role = "Technician";
        var technicians = Assert.IsType<Ok<PagedResult<UserDirectoryDto>>>(await UserDirectoryQuery.ReadAsync(parameters, db, Actor("administrator"), default)).Value!;
        Assert.Equal(22, technicians.Total); Assert.Equal("user-129", technicians.Items[0].Id);
        var invalid = Assert.IsType<ProblemHttpResult>(await UserDirectoryQuery.ReadAsync(new() { PageSize = 1000 }, db, Actor("administrator"), default));
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        Assert.NotEmpty(Assert.IsType<HttpValidationProblemDetails>(invalid.ProblemDetails).Errors["query"]);
    }

    private static ClaimsPrincipal Actor(string id, bool local = false) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, "HelpdeskAdmin"),
        new Claim("auth_mode", local ? "local" : "test")
    }, "Test"));
}
