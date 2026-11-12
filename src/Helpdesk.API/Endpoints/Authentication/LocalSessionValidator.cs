using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

/// <summary>Rechecks long-lived requests after the initial cookie middleware validation.</summary>
public static class LocalSessionValidator
{
    public static async Task<bool> IsValidAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (!context.User.HasClaim("auth_mode", "local"))
        {
            var expiration = context.User.FindFirstValue("exp");
            return expiration is null ||
                (long.TryParse(expiration, global::System.Globalization.NumberStyles.None,
                    global::System.Globalization.CultureInfo.InvariantCulture, out var expiresAt) &&
                 expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        var ticket = await context.AuthenticateAsync(LocalAuthenticationOptions.Scheme);
        if (!ticket.Succeeded || ticket.Properties?.ExpiresUtc is not { } expires || expires <= DateTimeOffset.UtcNow)
            return false;

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        var db = context.RequestServices.GetRequiredService<RatelDeskIdentityDbContext>();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(account => account.Id == userId, cancellationToken);
        return user is { IsEnabled: true, IsDeleted: false }
            && string.Equals(context.User.FindFirstValue("security_stamp"), user.SecurityStamp, StringComparison.Ordinal)
            && string.Equals(context.User.FindFirstValue("authorization_revision"),
                user.AuthorizationRevision.ToString(global::System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && context.User.IsInRole("HelpdeskAdmin") == user.IsInstanceAdministrator;
    }
}
