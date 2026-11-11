using System.Data;
using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Helpdesk.API.Services.Users;

/// <summary>Irreversible application retirement, retaining attribution and provider subject tombstones.</summary>
public static class UserRetirement
{
    public sealed record Outcome(bool Found, string? Error = null, string? Message = null);

    public static async Task<Outcome> RetireAsync(string userId, ClaimsPrincipal actor,
        HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb, CancellationToken ct = default)
    {
        var relational = ValidateSharedStores(db, identityDb);
        // A separate enlisted context preserves the caller's EF-owned connection.
        await using var coordinatedIdentity = relational ? CoordinateIdentity(db, identityDb) : null;
        identityDb = coordinatedIdentity ?? identityDb;
        IDbContextTransaction? transaction = null;
        try
        {
            if (relational)
            {
                transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await identityDb.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);
            }
            var outcome = await RetireWithinTransactionAsync(userId, actor, db, identityDb, ct);
            if (outcome.Found && outcome.Error is null && transaction is not null) await transaction.CommitAsync(ct);
            return outcome;
        }
        finally
        {
            if (relational)
            {
                await identityDb.Database.UseTransactionAsync(null, CancellationToken.None);
                if (transaction is not null) await transaction.DisposeAsync();
            }
        }
    }

    private static bool ValidateSharedStores(HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb)
    {
        // Test hosts can retain relational extensions when selecting InMemory.
        var relational = !db.Database.IsInMemory() && db.Database.IsRelational();
        var identityRelational = !identityDb.Database.IsInMemory() && identityDb.Database.IsRelational();
        if (relational != identityRelational)
            throw new InvalidOperationException("User retirement requires matching application and identity database providers.");
        if (relational && (db.Database.ProviderName != identityDb.Database.ProviderName ||
            db.Database.GetConnectionString() != identityDb.Database.GetConnectionString()))
            throw new InvalidOperationException("User retirement requires the configured shared identity and application database.");
        return relational;
    }

    private static async Task<Outcome> RetireWithinTransactionAsync(string userId, ClaimsPrincipal actor,
        HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb, CancellationToken ct)
    {
        var target = await db.Users.SingleOrDefaultAsync(user => user.Id == userId, ct);
        if (target is null) return new(false);
        var links = await db.CustomerAuthLinks.Where(link => link.DomainUserId == userId || link.LocalAccountId == userId).ToListAsync(ct);
        var accountIds = await ResolveAccountIdsAsync(userId, links, identityDb, ct);
        if (await IsActorAsync(actor, accountIds, links, identityDb, ct))
            return new(true, "cannot_delete_self", "You cannot delete your own application access. Ask another instance administrator.");
        if (await HasSharedIdentityAsync(userId, accountIds, links, db, ct))
            return new(true, "shared_identity_dependency", "This sign-in identity is linked to another active application user. An instance administrator must resolve that identity link before deleting either user's access.");
        if (links.Any(link => string.IsNullOrWhiteSpace(link.LocalAccountId) && !string.IsNullOrWhiteSpace(link.AuthentikUserId) &&
            (string.IsNullOrWhiteSpace(link.OidcIssuer) || string.IsNullOrWhiteSpace(link.OidcSubject))))
            return new(true, "external_identity_link_incomplete", "The external identity link needs its verified sign-in subject before all application credentials can be retired. Have the user sign in once through the configured provider, then retry Delete.");
        var accounts = await identityDb.Users.Where(account => accountIds.Contains(account.Id)).ToListAsync(ct);
        var retiringAdministrators = accounts.Count(account => account.IsInstanceAdministrator && account.IsEnabled && !account.IsDeleted);
        if (retiringAdministrators > 0 && await identityDb.Users.CountAsync(account => account.IsInstanceAdministrator && account.IsEnabled && !account.IsDeleted, ct) <= retiringAdministrators)
            return new(true, "last_instance_administrator", "The last enabled instance administrator must remain active. Add another administrator before deleting this user.");

        await ApplyRetirementAsync(target, actor, links, accountIds, accounts, db, identityDb, ct);
        return new(true);
    }

    private static async Task<List<string>> ResolveAccountIdsAsync(string userId, List<CustomerAuthLink> links,
        RatelDeskIdentityDbContext identityDb, CancellationToken ct)
    {
        var accountIds = links.Where(link => link.LocalAccountId != null).Select(link => link.LocalAccountId!).Append(userId).Distinct().ToList();
        foreach (var link in links.Where(link => !string.IsNullOrWhiteSpace(link.OidcIssuer) && !string.IsNullOrWhiteSpace(link.OidcSubject)))
        {
            var external = await OidcApplicationAccount.FindAsync(identityDb, link.OidcIssuer!, link.OidcSubject!, ct);
            if (external is not null && !accountIds.Contains(external.Id)) accountIds.Add(external.Id);
        }
        return accountIds;
    }

    private static async Task<bool> IsActorAsync(ClaimsPrincipal actor, List<string> accountIds, List<CustomerAuthLink> links,
        RatelDeskIdentityDbContext identityDb, CancellationToken ct)
    {
        var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorIssuer = actor.FindFirstValue("iss")?.TrimEnd('/');
        var actorSubject = actor.FindFirstValue("sub");
        var actorProviderId = actor.FindFirstValue("authentik_user_id") ?? actor.FindFirstValue("ak_user_id");
        var actorExternalAccount = !string.IsNullOrWhiteSpace(actorIssuer) && !string.IsNullOrWhiteSpace(actorSubject)
            ? await OidcApplicationAccount.FindAsync(identityDb, actorIssuer, actorSubject, ct) : null;
        return (!string.IsNullOrWhiteSpace(actorId) && accountIds.Contains(actorId)) ||
            (actorExternalAccount is not null && accountIds.Contains(actorExternalAccount.Id)) ||
            links.Any(link => (!string.IsNullOrWhiteSpace(actorSubject) && link.OidcSubject == actorSubject && link.OidcIssuer?.TrimEnd('/') == actorIssuer) ||
                (!string.IsNullOrWhiteSpace(actorProviderId) && link.AuthentikUserId == actorProviderId));
    }

    private static async Task ApplyRetirementAsync(User target, ClaimsPrincipal actor, List<CustomerAuthLink> links,
        List<string> accountIds, List<ApplicationUser> accounts, HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await RetireAccountsAsync(accounts, accountIds, identityDb, now, ct);
        await CancelDeliveriesAsync(accountIds, identityDb, ct);
        await RetireDomainAccessAsync(target, actor, links, accountIds, db, now, ct);
        await identityDb.SaveChangesAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task RetireAccountsAsync(List<ApplicationUser> accounts, List<string> accountIds,
        RatelDeskIdentityDbContext identityDb, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var account in accounts)
        {
            if (!account.IsDeleted)
            {
                account.IsDeleted = true;
                account.DeletedAtUtc = now;
                account.AuthorizationRevision++;
                account.SecurityStamp = Guid.NewGuid().ToString("N");
                account.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            }
            account.IsEnabled = false;
            account.DisabledAtUtc ??= now;
            account.IsInstanceAdministrator = false;
            account.PasswordHash = null;
            account.TwoFactorEnabled = false;
        }

        // Keep UserLogins and normalized identity names as durable identity tombstones:
        // automatic OIDC provisioning and recreation cannot silently restore grants.
        identityDb.UserTokens.RemoveRange(await identityDb.UserTokens.Where(token => accountIds.Contains(token.UserId)).ToListAsync(ct));
        identityDb.UserClaims.RemoveRange(await identityDb.UserClaims.Where(claim => accountIds.Contains(claim.UserId)).ToListAsync(ct));
        identityDb.UserRoles.RemoveRange(await identityDb.UserRoles.Where(role => accountIds.Contains(role.UserId)).ToListAsync(ct));
        foreach (var credential in await identityDb.IntegrationCredentials.Where(credential => accountIds.Contains(credential.OwnerUserId) && credential.RevokedAtUtc == null).ToListAsync(ct))
            credential.RevokedAtUtc = now;
    }

    private static async Task CancelDeliveriesAsync(List<string> accountIds, RatelDeskIdentityDbContext identityDb, CancellationToken ct)
    {
        foreach (var pairing in await identityDb.IntegrationCredentialPairings.Where(pairing => pairing.OwnerUserId != null && accountIds.Contains(pairing.OwnerUserId)).ToListAsync(ct))
        {
            if (pairing.State == "cancelled" && pairing.ProtectedApproval is null && pairing.ProtectedDelivery is null) continue;
            pairing.State = "cancelled";
            pairing.ProtectedApproval = null;
            pairing.ProtectedDelivery = null;
            pairing.Revision++;
        }
        foreach (var setup in await identityDb.IntegrationCredentialSetupAttempts.Where(setup => accountIds.Contains(setup.OwnerUserId)).ToListAsync(ct))
        {
            if (setup.State == "Revoked" && setup.ProtectedDelivery is null) continue;
            setup.State = "Revoked";
            setup.ProtectedDelivery = null;
            setup.Revision++;
        }
    }

    private static async Task RetireDomainAccessAsync(User target, ClaimsPrincipal actor, List<CustomerAuthLink> links,
        List<string> accountIds, HelpdeskDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var userId = target.Id;
        var actorId = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        var actorSubject = actor.FindFirstValue("sub");
        foreach (var link in links)
        {
            link.InviteStatus = CustomerInviteStatus.Disabled;
            link.DisabledAtUtc ??= now;
            link.DisabledByUserId ??= actorId;
        }
        db.ScopedRoleAssignments.RemoveRange(await db.ScopedRoleAssignments.Where(assignment => assignment.UserId == userId || accountIds.Contains(assignment.UserId)).ToListAsync(ct));
        foreach (var membership in await db.SupportGroupMembers.Where(member => member.UserId == userId).ToListAsync(ct))
        {
            membership.IsEnabled = false;
            membership.UpdatedUtc = now;
        }
        foreach (var preference in await db.UserSupportNotificationPreferences.Where(preference => preference.UserId == userId).ToListAsync(ct))
        {
            preference.IsEnabled = false;
            preference.UpdatedUtc = now;
        }
        foreach (var subscription in await db.SupportNotificationSubscriptions.Where(subscription => subscription.RecipientType == SupportNotificationRecipientType.User && subscription.RecipientId == userId).ToListAsync(ct))
        {
            subscription.IsEnabled = false;
            subscription.UpdatedUtc = now;
        }
        if (!target.IsDeleted)
        {
            target.IsDeleted = true;
            target.DeletedAtUtc = now;
            target.HashedPassword = null;
            db.ActivityLogs.Add(new ActivityLog
            {
                UserId = actorId ?? actorSubject ?? "administrator",
                RelatedEntityId = userId,
                Message = "Application user deleted: access, sessions, credentials and grants retired; historical attribution retained. External identity provider account unchanged."
            });
        }
    }

    private static async Task<bool> HasSharedIdentityAsync(string userId, List<string> accountIds, List<CustomerAuthLink> links,
        HelpdeskDbContext db, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(user => user.Id != userId && !user.IsDeleted && accountIds.Contains(user.Id), ct)) return true;
        if (await db.CustomerAuthLinks.AnyAsync(link => link.LocalAccountId != null && accountIds.Contains(link.LocalAccountId) &&
            link.DomainUserId != null && link.DomainUserId != userId && db.Users.Any(user => user.Id == link.DomainUserId && !user.IsDeleted), ct)) return true;

        var providerIds = links.Where(link => !string.IsNullOrWhiteSpace(link.AuthentikUserId)).Select(link => link.AuthentikUserId!).Distinct().ToList();
        if (providerIds.Count != 0 && await db.CustomerAuthLinks.AnyAsync(link => providerIds.Contains(link.AuthentikUserId!) &&
            link.DomainUserId != null && link.DomainUserId != userId && db.Users.Any(user => user.Id == link.DomainUserId && !user.IsDeleted), ct)) return true;
        foreach (var external in links.Where(link => !string.IsNullOrWhiteSpace(link.OidcIssuer) && !string.IsNullOrWhiteSpace(link.OidcSubject)))
        {
            var issuer = external.OidcIssuer!.TrimEnd('/');
            if (await db.CustomerAuthLinks.AnyAsync(link => link.OidcSubject == external.OidcSubject &&
                (link.OidcIssuer == issuer || link.OidcIssuer == issuer + "/") && link.DomainUserId != null && link.DomainUserId != userId &&
                db.Users.Any(user => user.Id == link.DomainUserId && !user.IsDeleted), ct)) return true;
        }
        return false;
    }

    private static RatelDeskIdentityDbContext CoordinateIdentity(HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb)
    {
        var options = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>(
            (DbContextOptions<RatelDeskIdentityDbContext>)identityDb.GetService<IDbContextOptions>());
        if (db.Database.IsSqlite()) options.UseSqlite(db.Database.GetDbConnection());
        else options.UseNpgsql(db.Database.GetDbConnection());
        return new(options.Options);
    }
}
