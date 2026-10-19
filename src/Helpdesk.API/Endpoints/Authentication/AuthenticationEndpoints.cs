using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Auth;
using Helpdesk.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.API.Endpoints.Authentication;

public static class AuthenticationEndpoints
{
    public static void MapCurrentUserAccessEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/auth/me", async (
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(user, ct);
            var grantKind = user.HasClaim("auth_mode", "integration") ? user.FindFirstValue("integration_purpose") switch
            {
                IntegrationCredentialGrantKinds.OwnerAdministrationPurpose => IntegrationCredentialGrantKinds.OwnerAdministration,
                "mcp-setup" => "setup-verification",
                "api" or "mcp" => IntegrationCredentialGrantKinds.Ordinary,
                _ => null
            } : null;
            return Results.Ok(ToDto(access, user.FindFirstValue(ClaimTypes.NameIdentifier)) with { GrantKind = grantKind });
        })
        .RequireAuthorization()
        .WithTags("Authentication")
        .WithName("GetCurrentUserAccess")
        .WithSummary("Gets the current user's resolved Helpdesk authorization profile.");
    }

    /// <summary>
    /// Maps the authentication-related endpoints for the application.
    /// </summary>
    /// <remarks>This method defines and configures the authentication endpoints under the route group
    /// <c>/api/v1/auth</c>.  It includes an endpoint for user login that validates credentials and issues a JSON Web
    /// Token (JWT) for  authenticated access to subsequent requests.</remarks>
    /// <param name="app">The <see cref="IEndpointRouteBuilder"/> used to define the application's routing.</param>
    public static void MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", async ([FromBody] LoginRequest request, [FromServices] IRepository<User> users, [FromServices] IConfiguration config) =>
        {
            var allUsers = await users.GetAllAsync();
            var user = allUsers.FirstOrDefault(u => u.Email == request.Email);
            if (user is null || string.IsNullOrEmpty(user.HashedPassword) || !BCrypt.Net.BCrypt.Verify(request.Password, user.HashedPassword))
            {
                return Results.Unauthorized();
            }

            var tokenString = CreateJwt(user, config);
            return Results.Ok(new LoginResponse(tokenString));
        })
        .AllowAnonymous()
        .WithName("Login")
        .WithSummary("Authenticates a user and returns a JWT.")
        .WithDescription("Validates credentials and issues a JWT for subsequent requests.");

        static string CreateJwt(User user, IConfiguration config)
        {
            var jwtSection = config.GetSection("Jwt");
            var issuer = jwtSection["Issuer"] ?? "test";
            var audience = jwtSection["Audience"] ?? "test";
            var keyString = jwtSection["Key"] ?? "test-key";
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyString));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: new[]
                {
                    new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                    new Claim(JwtRegisteredClaimNames.Name, user.Name),
                    new Claim(ClaimTypes.Role, user.Role),
                    new Claim("tid", user.OrganizationId ?? string.Empty)
                },
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    private static CurrentUserAccessDto ToDto(CurrentUserAccessProfile access, string? userId) => new(
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
            UserId = userId,
            UsesScopedPermissions = access.UsesScopedPermissions,
            ScopedPermissionGrants = access.ScopedPermissionGrants
                .OrderBy(grant => grant.OrganizationId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(grant => grant.Permission, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
}

public record LoginRequest(string Email, string Password);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);
public record TwoFactorInitiateRequest(string Email);
public record TwoFactorVerifyRequest(string Email, string TwoFactorCode);
public record LoginResponse(string Token);
