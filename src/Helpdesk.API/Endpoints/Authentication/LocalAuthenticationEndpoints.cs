using System.Security.Claims;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

public static class LocalAuthenticationEndpoints
{
    public static void MapLocalAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/local-auth").WithTags("Local authentication");

        group.MapPost("/login", async (
            [FromBody] LocalLoginRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IDataProtectionProvider protection,
            [FromServices] IConfiguration configuration,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ClearLoginChallenge(context, configuration);
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
                return Results.Unauthorized();

            var user = await users.FindByEmailAsync(request.Email.Trim());
            if (user is null || !user.IsEnabled || await users.IsLockedOutAsync(user))
                return Results.Unauthorized();

            if (!await users.CheckPasswordAsync(user, request.Password))
            {
                await users.AccessFailedAsync(user);
                return Results.Unauthorized();
            }

            if (await users.GetTwoFactorEnabledAsync(user))
            {
                if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                {
                    var challenge = new LocalSignInChallenge(user.Id, user.SecurityStamp ?? string.Empty,
                        user.AuthorizationRevision, request.RememberMe);
                    var protectedChallenge = protection.CreateProtector(LocalSignInChallenge.ProtectionPurpose)
                        .ToTimeLimitedDataProtector().Protect(JsonSerializer.Serialize(challenge), LocalSignInChallenge.Lifetime);
                    context.Response.Cookies.Append(LoginChallengeCookieName(configuration), protectedChallenge,
                        LoginChallengeCookieOptions(configuration, LocalSignInChallenge.Lifetime));
                    return Results.Accepted(value: new { requiresTwoFactor = true });
                }

                // Preserve credential-plus-code API clients; the browser uses the challenge step.
                if (!await IsSecondFactorValidAsync(users, user, request.TwoFactorCode))
                {
                    await users.AccessFailedAsync(user);
                    return Results.Unauthorized();
                }
            }

            await CompleteLoginAsync(users, user, accessService, context, request.RememberMe);
            return Results.NoContent();
        })
        .AllowAnonymous()
        .RequireRateLimiting("LocalLogin");

        group.MapPost("/login/two-factor", async (
            [FromBody] CompleteTwoFactorLoginRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IDataProtectionProvider protection,
            [FromServices] IConfiguration configuration,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var challenge = ReadLoginChallenge(context, configuration, protection);
            var user = challenge is null ? null : await users.FindByIdAsync(challenge.UserId);
            if (user is null || !user.IsEnabled || await users.IsLockedOutAsync(user) ||
                !await users.GetTwoFactorEnabledAsync(user) ||
                !string.Equals(user.SecurityStamp, challenge!.SecurityStamp, StringComparison.Ordinal) ||
                user.AuthorizationRevision != challenge.AuthorizationRevision)
            {
                ClearLoginChallenge(context, configuration);
                return Results.Unauthorized();
            }

            if (!await IsSecondFactorValidAsync(users, user, request.Code))
            {
                await users.AccessFailedAsync(user);
                if (await users.IsLockedOutAsync(user))
                    ClearLoginChallenge(context, configuration);
                return Results.Unauthorized();
            }

            ClearLoginChallenge(context, configuration);
            await CompleteLoginAsync(users, user, accessService, context, challenge!.RememberMe);
            return Results.NoContent();
        })
        .AllowAnonymous()
        .RequireRateLimiting("LocalLogin");

        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(LocalAuthenticationOptions.Scheme);
            return Results.NoContent();
        })
        .RequireAuthorization();

        group.MapPost("/two-factor/setup", async (
            [FromBody] StartAuthenticatorSetupRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            HttpContext context) =>
        {
            var user = await GetLocalUserAsync(users, context.User);
            if (user is null || !user.IsEnabled)
            {
                return Results.Unauthorized();
            }

            if (await users.GetTwoFactorEnabledAsync(user))
                return Results.Conflict(new { error = "authenticator_already_enabled" });

            if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
                return Results.BadRequest(new { error = "authenticator_setup_failed" });

            var reset = await users.ResetAuthenticatorKeyAsync(user);
            if (!reset.Succeeded)
            {
                return Results.Problem("The authenticator setup could not be started.", statusCode: StatusCodes.Status409Conflict);
            }

            user.AuthorizationRevision++;
            await users.UpdateAsync(user);
            await RenewCurrentSessionAsync(context, user);
            var sharedKey = await users.GetAuthenticatorKeyAsync(user);
            var accountName = user.Email ?? user.UserName ?? user.Id;
            var issuer = "RatelDesk";
            var uri = $"otpauth://totp/{Uri.EscapeDataString($"{issuer}:{accountName}")}?secret={Uri.EscapeDataString(sharedKey!)}&issuer={Uri.EscapeDataString(issuer)}&digits=6";
            return Results.Ok(new AuthenticatorSetupResponse(sharedKey!, uri));
        })
        .RequireAuthorization();

        group.MapPost("/two-factor/enable", async (
            [FromBody] EnableTwoFactorRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            HttpContext context) =>
        {
            var user = await GetLocalUserAsync(users, context.User);
            if (user is null || !user.IsEnabled ||
                !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeAuthenticatorCode(request.Code)))
            {
                return Results.BadRequest(new { error = "invalid_authenticator_code" });
            }

            var enabled = await users.SetTwoFactorEnabledAsync(user, true);
            if (!enabled.Succeeded)
            {
                return Results.Problem("Two-factor authentication could not be enabled.", statusCode: StatusCodes.Status409Conflict);
            }

            var recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            user.AuthorizationRevision++;
            await users.UpdateAsync(user);
            await RenewCurrentSessionAsync(context, user);
            return Results.Ok(new TwoFactorRecoveryCodesResponse(recoveryCodes?.ToArray() ?? []));
        })
        .RequireAuthorization();

        group.MapPost("/two-factor/disable", async (
            [FromBody] DisableTwoFactorRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            HttpContext context) =>
        {
            var user = await GetLocalUserAsync(users, context.User);
            if (user is null || !user.IsEnabled ||
                !await users.CheckPasswordAsync(user, request.CurrentPassword) ||
                !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeAuthenticatorCode(request.Code)))
            {
                return Results.BadRequest(new { error = "two_factor_disable_failed" });
            }

            var disabled = await users.SetTwoFactorEnabledAsync(user, false);
            if (!disabled.Succeeded)
            {
                return Results.Problem("Two-factor authentication could not be disabled.", statusCode: StatusCodes.Status409Conflict);
            }

            user.AuthorizationRevision++;
            await users.UpdateAsync(user);
            await context.SignOutAsync(LocalAuthenticationOptions.Scheme);
            return Results.NoContent();
        })
        .RequireAuthorization();

        group.MapPost("/change-password", async (
            [FromBody] ChangeLocalPasswordRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            HttpContext context) =>
        {
            var user = await GetLocalUserAsync(users, context.User);
            if (user is null || !user.IsEnabled)
            {
                return Results.Unauthorized();
            }

            var change = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!change.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["password"] = ["The current password or new password was not accepted."]
                });
            }

            user.AuthorizationRevision++;
            await users.UpdateAsync(user);
            await context.SignOutAsync(LocalAuthenticationOptions.Scheme);
            return Results.NoContent();
        })
        .RequireAuthorization();

        group.MapPost("/users/{userId}/disable", async (
            string userId,
            [FromServices] RatelDeskIdentityDbContext identityDb,
            CancellationToken cancellationToken) =>
        {
            await using var transaction = identityDb.Database.IsInMemory()
                ? null
                : await identityDb.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var target = await identityDb.Users.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
            if (target is null)
            {
                return Results.NotFound();
            }

            if (target.IsInstanceAdministrator && target.IsEnabled)
            {
                var enabledAdministrators = await identityDb.Users.CountAsync(
                    user => user.IsInstanceAdministrator && user.IsEnabled,
                    cancellationToken);
                if (enabledAdministrators <= 1)
                {
                    return Results.Conflict(new { error = "last_instance_administrator" });
                }
            }

            target.IsEnabled = false;
            target.DisabledAtUtc = DateTimeOffset.UtcNow;
            target.AuthorizationRevision++;
            target.SecurityStamp = Guid.NewGuid().ToString("N");
            await identityDb.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return Results.NoContent();
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapPost("/users/{userId}/enable", async (
            string userId,
            [FromServices] UserManager<ApplicationUser> users) =>
        {
            var target = await users.FindByIdAsync(userId);
            if (target is null)
            {
                return Results.NotFound();
            }

            if (target.IsEnabled)
            {
                return Results.NoContent();
            }

            target.IsEnabled = true;
            target.DisabledAtUtc = null;
            target.AuthorizationRevision++;
            target.SecurityStamp = Guid.NewGuid().ToString("N");
            var update = await users.UpdateAsync(target);
            return update.Succeeded
                ? Results.NoContent()
                : Results.Problem("The local account could not be enabled.", statusCode: StatusCodes.Status409Conflict);
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/users/{userId}/status", async (
            string userId,
            [FromServices] UserManager<ApplicationUser> users) =>
        {
            var target = await users.FindByIdAsync(userId);
            return target is null
                ? Results.NotFound()
                : Results.Ok(new LocalAccountStatusResponse(target.IsEnabled));
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/users/{userId}/assignments", async (
            string userId,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var account = await users.FindByIdAsync(userId);
            if (!await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
            {
                return Results.NotFound();
            }

            var assignments = await db.ScopedRoleAssignments.AsNoTracking()
                .Where(assignment => assignment.UserId == userId)
                .OrderBy(assignment => assignment.OrganizationId)
                .ThenBy(assignment => assignment.RoleKey)
                .Select(assignment => new LocalScopedRoleAssignment(assignment.RoleKey, assignment.OrganizationId))
                .ToListAsync(cancellationToken);
            return Results.Ok(new LocalScopedRoleAssignmentsResponse(
                assignments,
                account?.IsInstanceAdministrator == true,
                account is not null));
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapPut("/users/{userId}/assignments", async (
            string userId,
            [FromBody] ReplaceLocalScopedRoleAssignmentsRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var account = await users.FindByIdAsync(userId);
            var domainUser = await db.Users.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
            if (domainUser is null)
            {
                return Results.NotFound();
            }

            if (account?.IsInstanceAdministrator == true)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignments"] = ["Instance administrator access is managed separately from tenant-scoped role assignments."]
                });
            }

            var requestedAssignments = (request.Assignments ?? [])
                .Select(assignment => new LocalScopedRoleAssignment(
                    assignment.RoleKey?.Trim() ?? string.Empty,
                    assignment.OrganizationId?.Trim() ?? string.Empty))
                .ToArray();
            if (requestedAssignments.Any(assignment => string.IsNullOrWhiteSpace(assignment.OrganizationId) || string.IsNullOrWhiteSpace(assignment.RoleKey)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignments"] = ["Each role assignment must name a role and an enabled organization."]
                });
            }

            if (requestedAssignments
                .GroupBy(assignment => $"{assignment.RoleKey}\u001f{assignment.OrganizationId}", StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignments"] = ["Each local role can be assigned to an organization only once."]
                });
            }

            var organizationIds = requestedAssignments
                .Select(assignment => assignment.OrganizationId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var enabledOrganizationIds = await db.Organizations.AsNoTracking()
                .Where(organization => organizationIds.Contains(organization.Id) && organization.IsEnabled)
                .Select(organization => organization.Id)
                .ToListAsync(cancellationToken);
            if (enabledOrganizationIds.Count != organizationIds.Length)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignments"] = ["Every local role assignment must reference an enabled organization."]
                });
            }

            var requestedRoleKeys = requestedAssignments
                .Select(assignment => assignment.RoleKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var persistedRoles = await db.Roles.AsNoTracking()
                .Where(role => requestedRoleKeys.Contains(role.Key))
                .ToListAsync(cancellationToken);
            var rolesByKey = persistedRoles.ToDictionary(role => role.Key, StringComparer.OrdinalIgnoreCase);
            if (requestedAssignments.Any(assignment =>
                    (!rolesByKey.TryGetValue(assignment.RoleKey, out var role) &&
                     !ScopedRoleCatalog.IsSupported(assignment.RoleKey)) ||
                    (role is not null &&
                     (role.Scope == RoleScopeKind.Instance ||
                      (role.OwnerOrganizationId is not null &&
                       !string.Equals(role.OwnerOrganizationId, assignment.OrganizationId, StringComparison.OrdinalIgnoreCase))))))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignments"] = ["Each role must exist, support tenant or self-service scope, and be owned by the assigned organization when it is tenant-specific."]
                });
            }

            var existingAssignments = await db.ScopedRoleAssignments
                .Where(assignment => assignment.UserId == userId)
                .ToListAsync(cancellationToken);
            db.ScopedRoleAssignments.RemoveRange(existingAssignments);
            db.ScopedRoleAssignments.AddRange(requestedAssignments.Select(assignment => new ScopedRoleAssignment
            {
                UserId = userId,
                RoleKey = assignment.RoleKey,
                OrganizationId = assignment.OrganizationId
            }));
            await db.SaveChangesAsync(cancellationToken);

            if (account is not null)
            {
                account.AuthorizationRevision++;
                var update = await users.UpdateAsync(account);
                if (!update.Succeeded)
                {
                    return Results.Problem("The local role assignments were saved, but the account authorization revision could not be updated.", statusCode: StatusCodes.Status409Conflict);
                }
            }

            return Results.NoContent();
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapPost("/users", async (
            [FromBody] CreateLocalAccountRequest request,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] HelpdeskDbContext db,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["Display name and email are required."]
                });
            }

            var normalizedEmail = request.Email.Trim();
            var role = request.IsInstanceAdministrator
                ? "HelpdeskAdmin"
                : request.Role?.Trim() ?? string.Empty;
            if (!request.IsInstanceAdministrator && role is not ("User" or "Technician"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["role"] = ["Local accounts can be assigned the User or Technician access bundle."]
                });
            }

            var organizationId = string.IsNullOrWhiteSpace(request.OrganizationId)
                ? null
                : request.OrganizationId.Trim();
            if (!request.IsInstanceAdministrator && organizationId is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["organizationId"] = ["A local User or Technician account must be assigned to an enabled organization."]
                });
            }

            if (organizationId is not null && !await db.Organizations.AnyAsync(organization => organization.Id == organizationId && organization.IsEnabled))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["organizationId"] = ["The organization does not exist or is disabled."]
                });
            }

            if (await db.Users.AnyAsync(domainUser => domainUser.Email == normalizedEmail))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["The email is already linked to an application user."]
                });
            }

            var user = new ApplicationUser
            {
                UserName = normalizedEmail,
                Email = normalizedEmail,
                DisplayName = request.DisplayName.Trim(),
                IsInstanceAdministrator = request.IsInstanceAdministrator,
                EmailConfirmed = false
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["The local account could not be created. The email may already be in use."]
                });
            }

            try
            {
                if (organizationId is not null && role == "User")
                {
                    var linkError = await LocalCustomerAccessLinker.LinkAsync(
                        db,
                        user.Id,
                        user.DisplayName,
                        user.Email!,
                        organizationId);
                    if (linkError is not null)
                    {
                        await users.DeleteAsync(user);
                        return Results.Conflict(new { error = "local_customer_link_conflict", message = linkError });
                    }
                }

                db.Users.Add(new User
                {
                    Id = user.Id,
                    Name = user.DisplayName,
                    Email = user.Email!,
                    Role = role,
                    OrganizationId = organizationId,
                    IsTestUser = request.IsTestUser
                });
                if (organizationId is not null && !user.IsInstanceAdministrator)
                {
                    db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
                    {
                        UserId = user.Id,
                        OrganizationId = organizationId,
                        RoleKey = role == "Technician"
                            ? ScopedRoleCatalog.Technician
                            : ScopedRoleCatalog.SelfServiceUser
                    });

                }
                await db.SaveChangesAsync();
            }
            catch (Exception)
            {
                await users.DeleteAsync(user);
                return Results.Problem("The local account could not be linked to the application user record.", statusCode: StatusCodes.Status409Conflict);
            }

            var activationToken = await users.GeneratePasswordResetTokenAsync(user);
            return Results.Created($"/api/v1/local-auth/users/{user.Id}", new LocalAccountActivationResponse(user.Id, user.Email!, activationToken));
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapPost("/users/{userId}/activation-token", async (
            string userId,
            [FromServices] UserManager<ApplicationUser> users,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var user = await users.FindByIdAsync(userId);
            if (user is null)
            {
                return Results.NotFound();
            }

            var activationToken = await users.GeneratePasswordResetTokenAsync(user);
            return Results.Ok(new LocalAccountActivationResponse(user.Id, user.Email!, activationToken));
        })
        .RequireAuthorization("HelpdeskAdmin");

        group.MapPost("/activate", async (
            [FromBody] ActivateLocalAccountRequest request,
            [FromServices] UserManager<ApplicationUser> users) =>
        {
            var user = await users.FindByEmailAsync(request.Email);
            if (user is null || !user.IsEnabled)
            {
                return Results.BadRequest(new { error = "activation_failed" });
            }

            var reset = await users.ResetPasswordAsync(user, request.ActivationToken, request.NewPassword);
            if (!reset.Succeeded)
            {
                return Results.BadRequest(new { error = "activation_failed" });
            }

            user.EmailConfirmed = true;
            user.AuthorizationRevision++;
            await users.UpdateAsync(user);
            return Results.NoContent();
        })
        .AllowAnonymous()
        .RequireRateLimiting("LocalLogin");
    }

    public sealed record LocalLoginRequest(string Email, string Password, bool RememberMe = false, string? TwoFactorCode = null);

    public sealed record CompleteTwoFactorLoginRequest(string Code);

    public sealed record ChangeLocalPasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record CreateLocalAccountRequest
    {
        public CreateLocalAccountRequest(string displayName, string email, bool isInstanceAdministrator = false)
        {
            DisplayName = displayName;
            Email = email;
            IsInstanceAdministrator = isInstanceAdministrator;
        }

        public string DisplayName { get; init; }

        public string Email { get; init; }

        public bool IsInstanceAdministrator { get; init; }

        public string Role { get; init; } = "User";

        public string? OrganizationId { get; init; }

        public bool IsTestUser { get; init; }
    }

    public sealed record ActivateLocalAccountRequest(string Email, string ActivationToken, string NewPassword);

    public sealed record LocalAccountActivationResponse(string UserId, string Email, string ActivationToken)
    {
        public IReadOnlyList<string> SensitiveFields { get; init; } = ["activationToken"];
        public override string ToString() => $"LocalAccountActivationResponse {{ UserId = {UserId}, ActivationToken = [redacted] }}";
    }

    public sealed record LocalAccountStatusResponse(bool IsEnabled);

    public sealed record LocalScopedRoleAssignment(string RoleKey, string OrganizationId);

    public sealed record LocalScopedRoleAssignmentsResponse(
        IReadOnlyList<LocalScopedRoleAssignment> Assignments,
        bool IsInstanceAdministrator = false,
        bool IsLocalAccount = false);

    public sealed record ReplaceLocalScopedRoleAssignmentsRequest(IReadOnlyList<LocalScopedRoleAssignment> Assignments);

    public sealed record StartAuthenticatorSetupRequest(string CurrentPassword);

    public sealed record EnableTwoFactorRequest(string Code);

    public sealed record DisableTwoFactorRequest(string CurrentPassword, string Code);

    public sealed record AuthenticatorSetupResponse(string SharedKey, string AuthenticatorUri);

    public sealed record TwoFactorRecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

    private static async Task CompleteLoginAsync(
        UserManager<ApplicationUser> users,
        ApplicationUser user,
        ICurrentUserAccessService accessService,
        HttpContext context,
        bool rememberMe)
    {
        await users.ResetAccessFailedCountAsync(user);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? user.Email! : user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
            new Claim("auth_mode", "local"),
            new Claim("security_stamp", user.SecurityStamp ?? string.Empty),
            new Claim("authorization_revision", user.AuthorizationRevision.ToString(global::System.Globalization.CultureInfo.InvariantCulture))
        }.Concat(user.IsInstanceAdministrator
            ? [new Claim(ClaimTypes.Role, "HelpdeskAdmin"), new Claim("roles", "HelpdeskAdmin")]
            : []);
        var identity = new ClaimsIdentity(
            claims,
            LocalAuthenticationOptions.Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var access = await accessService.ResolveAsync(principal, context.RequestAborted);
        foreach (var role in access.RoleBundles.Concat(access.Permissions).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }

            if (!identity.HasClaim("roles", role))
            {
                identity.AddClaim(new Claim("roles", role));
            }
        }
        if (access.UsesScopedPermissions)
            identity.AddClaim(new Claim("permission_scope_mode", "scoped"));
        foreach (var grant in access.ScopedPermissionGrants)
            identity.AddClaim(new Claim("scoped_permission", grant.ToString()));
        await context.SignInAsync(LocalAuthenticationOptions.Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(rememberMe ? 24 : 8)
        });
    }

    private static string LoginChallengeCookieName(IConfiguration configuration) =>
        LocalSignInChallenge.CookieName(configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost"));

    private static CookieOptions LoginChallengeCookieOptions(IConfiguration configuration, TimeSpan? lifetime = null) => new()
    {
        HttpOnly = true,
        Secure = !configuration.GetValue<bool>("Authentication:AllowInsecureLocalhost"),
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        MaxAge = lifetime
    };

    private static void ClearLoginChallenge(HttpContext context, IConfiguration configuration) =>
        context.Response.Cookies.Delete(LoginChallengeCookieName(configuration), LoginChallengeCookieOptions(configuration));

    private static LocalSignInChallenge? ReadLoginChallenge(
        HttpContext context, IConfiguration configuration, IDataProtectionProvider protection)
    {
        var value = context.Request.Cookies[LoginChallengeCookieName(configuration)];
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var payload = protection.CreateProtector(LocalSignInChallenge.ProtectionPurpose)
                .ToTimeLimitedDataProtector().Unprotect(value);
            return JsonSerializer.Deserialize<LocalSignInChallenge>(payload);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static Task<ApplicationUser?> GetLocalUserAsync(UserManager<ApplicationUser> users, ClaimsPrincipal principal) =>
        principal.HasClaim("auth_mode", "local") ? users.GetUserAsync(principal) : Task.FromResult<ApplicationUser?>(null);

    private static async Task RenewCurrentSessionAsync(HttpContext context, ApplicationUser user)
    {
        var authentication = await context.AuthenticateAsync(LocalAuthenticationOptions.Scheme);
        var identity = new ClaimsIdentity(context.User.Identity as ClaimsIdentity
            ?? throw new InvalidOperationException("A local session is required."));
        foreach (var claim in identity.FindAll("security_stamp").Concat(identity.FindAll("authorization_revision")).ToArray())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim("security_stamp", user.SecurityStamp ?? string.Empty));
        identity.AddClaim(new Claim("authorization_revision", user.AuthorizationRevision.ToString(global::System.Globalization.CultureInfo.InvariantCulture)));
        await context.SignInAsync(LocalAuthenticationOptions.Scheme, new ClaimsPrincipal(identity),
            authentication.Properties ?? new AuthenticationProperties());
    }

    private static async Task<bool> IsSecondFactorValidAsync(UserManager<ApplicationUser> users, ApplicationUser user, string? code)
    {
        if (!await users.GetTwoFactorEnabledAsync(user))
        {
            return true;
        }

        var suppliedCode = code?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(suppliedCode))
        {
            return false;
        }

        return await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeAuthenticatorCode(suppliedCode)) ||
               (await users.RedeemTwoFactorRecoveryCodeAsync(user, suppliedCode)).Succeeded;
    }

    private static string NormalizeAuthenticatorCode(string? code) =>
        code?.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal) ?? string.Empty;
}
