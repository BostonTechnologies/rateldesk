using System.Security.Claims;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

internal static class InboundForwarderAuthorization
{
    public static async Task<CurrentUserAccessProfile?> ResolveAsync(
        HelpdeskDbContext db, RatelDeskIdentityDbContext? identityDb, User user, CancellationToken cancellationToken)
    {
        if (user.IsDeleted) return null;
        var account = identityDb is null ? null : await identityDb.Users.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == user.Id, cancellationToken);
        var localLink = await db.CustomerAuthLinks.AsNoTracking()
            .AnyAsync(link => link.LocalAccountId == user.Id, cancellationToken);
        ClaimsPrincipal principal;
        if (account is not null || localLink)
        {
            if (account?.IsEnabled != true || account.IsDeleted) return null;
            principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim("auth_mode", "local")
            ], "local-email-forwarder"));
        }
        else
        {
            // Offline forwarding can use legacy external role metadata only
            // when an administrator/provisioning linked that exact domain user
            // to verified provider identifiers. An email match is insufficient.
            var link = await db.CustomerAuthLinks.AsNoTracking()
                .FirstOrDefaultAsync(link => link.DomainUserId == user.Id &&
                    ((link.OidcIssuer != null && link.OidcSubject != null) || link.AuthentikUserId != null), cancellationToken);
            if (link is null) return null;
            var claims = new List<Claim>();
            if (!string.IsNullOrWhiteSpace(link.OidcIssuer)) claims.Add(new Claim("iss", link.OidcIssuer));
            if (!string.IsNullOrWhiteSpace(link.OidcSubject)) claims.Add(new Claim("sub", link.OidcSubject));
            if (!string.IsNullOrWhiteSpace(link.AuthentikUserId)) claims.Add(new Claim("authentik_user_id", link.AuthentikUserId));
            var legacyRole = user.Role is "Technician" or HelpdeskRoleBundles.Technical
                ? AuthentikRbacGroups.Technical
                : user.Role;
            if (legacyRole == AuthentikRbacGroups.Technical || legacyRole == HelpdeskPermissions.HelpdeskAdmin ||
                legacyRole == HelpdeskPermissions.IncidentManager)
                claims.Add(new Claim("groups", legacyRole));
            principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "linked-external-email-forwarder"));
        }
        return await new CurrentUserAccessService(db, identityDb).ResolveAsync(principal, cancellationToken);
    }

    public static bool CanForward(CurrentUserAccessProfile? access) => access is not null &&
        (access.IsHelpdeskAdmin || access.OrganizationIdsForAny(HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite,
            HelpdeskPermissions.IncidentManager).Any(access.CanManageIncident));
}
