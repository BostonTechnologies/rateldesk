using Helpdesk.Shared.Models;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.DTOs.User;
using Dodo.Primitives;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Users;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users")
            .WithTags("Users")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async ([FromServices] IRepository<User> repo) =>
            (await repo.GetAllAsync()).Select(ToDto));

        group.MapGet("/{id}", async ([FromRoute] string id, [FromServices] IRepository<User> repo) =>
            await repo.GetAsync(id) is User user
                ? Results.Ok(ToDto(user))
                : Results.Problem("User not found", statusCode: 404));

        group.MapGet("/by-email/{email}", async ([FromRoute] string email, [FromServices] IRepository<User> repo) =>
        {
            var all = await repo.GetAllAsync();
            var user = all.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
            return user is null ? Results.Problem("User not found", statusCode: 404) : Results.Ok(ToDto(user));
        });

        group.MapPost("/", async ([FromBody] CreateUserRequest request, [FromServices] IRepository<User> repo) =>
        {
            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["password"] = ["Create local accounts through /api/v1/local-auth/users; domain user records do not accept credentials."]
                });
            }

            var user = new User
            {
                Id = Uuid.CreateVersion7().ToString(),
                Name = request.Name,
                Email = request.Email,
                Role = request.Role,
                IsTestUser = request.IsTestUser,
                OrganizationId = string.IsNullOrWhiteSpace(request.OrganizationId) ? null : request.OrganizationId
            };
            var created = await repo.CreateAsync(user);
            return Results.Created($"/api/v1/users/{created.Id}", ToDto(created));
        });

        app.MapPost("/api/v1/users/provision", async (
            ClaimsPrincipal principal,
            [FromServices] HelpdeskDbContext db,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] Helpdesk.Infrastructure.Identity.RatelDeskIdentityDbContext identityDb,
            CancellationToken ct) =>
        {
            var email = FirstClaim(principal, ClaimTypes.Email, "email", "preferred_username");
            var issuer = FirstClaim(principal, "iss")?.TrimEnd('/');
            var subject = FirstClaim(principal, "sub");
            var authentikUserId = FirstClaim(principal, "authentik_user_id", "ak_user_id");
            var preferredUsername = FirstClaim(principal, "preferred_username");

            if (string.IsNullOrWhiteSpace(email) ||
                (string.IsNullOrWhiteSpace(authentikUserId) &&
                 (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["identity"] = ["A verified issuer and subject, or provider user identifier, is required."]
                });
            }

            var link = await FindCustomerLoginAsync(issuer, subject, authentikUserId, db);
            if (link is null)
            {
                // An explicitly authorized external instance administrator does not
                // require a customer-contact link. Other users need an established identity link.
                var unlinkedAccess = await accessService.ResolveAsync(principal);
                if (!unlinkedAccess.IsHelpdeskAdmin) return Results.Forbid();
                if (principal.HasClaim("auth_mode", "local")) return Results.Ok(ToAccessDto(unlinkedAccess));
                if (principal.FindFirstValue("auth_mode") is "integration" or "mcp" or "gateway" or "service" or "machine" or "ai_agent" ||
                    principal.HasClaim("token_use", "rateldesk_service") ||
                    string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
                    return Results.Forbid();
                var account = await Helpdesk.Infrastructure.Identity.OidcApplicationAccount.ProvisionAdministratorAsync(
                    identityDb, issuer, subject, principal.Identity?.Name ?? email, ct);
                return account.IsEnabled ? Results.Ok(ToAccessDto(unlinkedAccess)) : Results.Forbid();
            }

            var linkedCustomer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == link.CustomerId);
            if (linkedCustomer?.IsEnabled != true ||
                !await db.Organizations.AnyAsync(organization => organization.Id == linkedCustomer.OrganizationId && organization.IsEnabled))
                return Results.Forbid();

            var hasLinkedDomainUser = !string.IsNullOrWhiteSpace(link.DomainUserId) &&
                                      await db.Users.AnyAsync(user => user.Id == link.DomainUserId);
            if (!hasLinkedDomainUser && !string.IsNullOrWhiteSpace(link.DomainUserId))
                return Results.Forbid();

            if (!hasLinkedDomainUser)
            {
                var user = new User
                {
                    Id = Uuid.CreateVersion7().ToString(),
                    Name = principal.Identity?.Name ?? FirstClaim(principal, "name") ?? email,
                    Email = email,
                    Role = "Customer",
                    OrganizationId = linkedCustomer.OrganizationId
                };
                db.Users.Add(user);
                link.DomainUserId = user.Id;
                db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
                {
                    UserId = user.Id,
                    OrganizationId = linkedCustomer.OrganizationId,
                    RoleKey = ScopedRoleCatalog.SelfServiceUser
                });
            }

            UpdateCustomerLogin(link, issuer, subject, authentikUserId, preferredUsername, email);
            await db.SaveChangesAsync();
            return Results.Ok(ToAccessDto(await accessService.ResolveAsync(principal)));
        })
        .RequireAuthorization()
        .WithTags("Users");

        group.MapPut("/{id}", async ([FromRoute] string id, [FromBody] UpdateUserRequest request, [FromServices] IRepository<User> repo) =>
        {
            var existing = await repo.GetAsync(id);
            if (existing is null)
            {
                return Results.Problem("User not found", statusCode: 404);
            }

            if (!string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["password"] = ["Change local credentials through /api/v1/local-auth/change-password."]
                });
            }

            existing.Name = request.Name;
            existing.Email = request.Email;
            existing.Role = request.Role;
            existing.IsTestUser = request.IsTestUser;
            existing.OrganizationId = string.IsNullOrWhiteSpace(request.OrganizationId) ? null : request.OrganizationId;
            var updated = await repo.UpdateAsync(existing);
            return updated is null
                ? Results.Problem("User not found", statusCode: 404)
                : Results.Ok(ToDto(updated));
        });

        group.MapDelete("/{id}", async (
            [FromRoute] string id,
            [FromServices] IRepository<User> repo,
            [FromServices] UserManager<ApplicationUser> users) =>
        {
            if (await users.FindByIdAsync(id) is not null)
                return Results.Conflict(new { error = "local_account_requires_disable", message = "Disable this local account through account management; deleting its application profile does not revoke login." });
            return await repo.DeleteAsync(id)
                ? Results.NoContent()
                : Results.Problem("User not found", statusCode: 404);
        });
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Name, user.Email, user.Role, user.IsTestUser, user.OrganizationId);

    private static CurrentUserAccessDto ToAccessDto(CurrentUserAccessProfile access) => new(
            access.IsAuthenticated,
            access.Name,
            access.Email,
            access.PrimaryOrganizationId,
            access.PrimaryOrganizationName,
            access.CustomerId,
            access.IsHelpdeskAdmin,
            access.RoleBundles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            access.Permissions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            access.AllowedOrganizationIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            access.ManagedOrganizationIds.Order(StringComparer.OrdinalIgnoreCase).ToArray())
    {
        UsesScopedPermissions = access.UsesScopedPermissions,
        ScopedPermissionGrants = access.ScopedPermissionGrants
                .OrderBy(grant => grant.OrganizationId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(grant => grant.Permission, StringComparer.OrdinalIgnoreCase)
                .ToArray()
    };

    private static string? FirstClaim(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static async Task<CustomerAuthLink?> FindCustomerLoginAsync(
        string? issuer,
        string? subject,
        string? authentikUserId,
        HelpdeskDbContext db)
    {
        CustomerAuthLink? link = null;
        if (!string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject))
        {
            link = await db.CustomerAuthLinks.FirstOrDefaultAsync(x => x.OidcIssuer == issuer && x.OidcSubject == subject);
        }

        if (link is null && !string.IsNullOrWhiteSpace(authentikUserId))
        {
            link = await db.CustomerAuthLinks.FirstOrDefaultAsync(x => x.AuthentikUserId == authentikUserId);
        }

        if (link is null)
        {
            return null;
        }

        return link;
    }

    private static void UpdateCustomerLogin(
        CustomerAuthLink link,
        string? issuer,
        string? subject,
        string? authentikUserId,
        string? preferredUsername,
        string email)
    {
        link.OidcIssuer ??= issuer;
        link.OidcSubject ??= subject;
        link.AuthentikUserId ??= authentikUserId;
        link.AuthentikUsername = preferredUsername ?? email;
        link.AuthentikEmail = email;
        link.LastLoginAtUtc = DateTimeOffset.UtcNow;
        if (link.InviteStatus == CustomerInviteStatus.Pending)
        {
            link.InviteStatus = CustomerInviteStatus.Active;
            link.InviteAcceptedAtUtc ??= DateTimeOffset.UtcNow;
        }

    }
}
