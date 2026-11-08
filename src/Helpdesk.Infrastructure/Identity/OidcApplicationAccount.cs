using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Identity;

/// <summary>Application ownership for a verified external subject, independent of email and customer records.</summary>
public static class OidcApplicationAccount
{
    public static async Task<ApplicationUser?> FindAsync(RatelDeskIdentityDbContext db,
        string issuer, string subject, CancellationToken ct = default)
    {
        var provider = Provider(issuer);
        return await (from login in db.UserLogins
                      join account in db.Users on login.UserId equals account.Id
                      where login.LoginProvider == provider && login.ProviderKey == subject
                      select account).SingleOrDefaultAsync(ct);
    }

    public static async Task<ApplicationUser> ProvisionAdministratorAsync(RatelDeskIdentityDbContext db,
        string issuer, string subject, string displayName, CancellationToken ct = default)
    {
        var existing = await FindAsync(db, issuer, subject, ct);
        if (existing is not null) return existing;

        // No password, email lookup or customer-contact fabrication. The standard
        // Identity login key provides uniqueness across concurrent sign-ins.
        var provider = Provider(issuer);
        var userName = "oidc-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider + "\n" + subject)));
        var account = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(), UserName = userName, NormalizedUserName = userName.ToUpperInvariant(),
            DisplayName = displayName[..Math.Min(displayName.Length, 256)], IsEnabled = true, IsInstanceAdministrator = true,
            SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString()
        };
        var login = new IdentityUserLogin<string>
        {
            LoginProvider = provider, ProviderKey = subject,
            ProviderDisplayName = issuer.TrimEnd('/'), UserId = account.Id
        };
        db.Users.Add(account);
        db.UserLogins.Add(login);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(login).State = EntityState.Detached;
            db.Entry(account).State = EntityState.Detached;
            var concurrent = await FindAsync(db, issuer, subject, ct);
            if (concurrent is not null) return concurrent;
            throw;
        }
        return account;
    }

    private static string Provider(string issuer) => "oidc:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issuer.TrimEnd('/'))));
}
