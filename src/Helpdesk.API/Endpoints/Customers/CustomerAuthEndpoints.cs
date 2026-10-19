using System.Security.Claims;
using Helpdesk.Infrastructure.Auth.Authentik;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Endpoints.Customers;

public static class CustomerAuthEndpoints
{
    public static void MapCustomerAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/customers/{customerId}/auth")
            .WithTags("Customer Authentication")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/status", async (
            [FromRoute] string customerId,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            Results.Ok(await GetStatusAsync(customerId, invitationService, db, users, authentikOptions.Value, ct)));

        group.MapPost("/invite", async (
            [FromRoute] string customerId,
            ClaimsPrincipal user,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.InviteAsync(customerId, ResolveUserId(user), ct), ct));

        group.MapPost("/resend-invite", async (
            [FromRoute] string customerId,
            ClaimsPrincipal user,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.ResendInviteAsync(customerId, ResolveUserId(user), ct), ct));

        group.MapPost("/disable-login", async (
            [FromRoute] string customerId,
            ClaimsPrincipal user,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.DisableLoginAsync(customerId, ResolveUserId(user), ct), ct));

        group.MapPost("/sync-authentik", async (
            [FromRoute] string customerId,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.SyncAuthentikAsync(customerId, ct), ct));

        group.MapPost("/local-activation-token", async (
            [FromRoute] string customerId,
            HttpContext context,
            [FromServices] HelpdeskDbContext db,
            [FromServices] UserManager<ApplicationUser> users,
            CancellationToken ct) =>
        {
            var links = await db.CustomerAuthLinks.AsNoTracking().Where(link => link.CustomerId == customerId).ToArrayAsync(ct);
            if (links.Length != 1 || !string.Equals(links[0].AuthProviderType, "Local", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(links[0].LocalAccountId))
                return Results.Conflict(new { message = "A single linked local account is required to generate an activation token." });
            var account = await users.FindByIdAsync(links[0].LocalAccountId);
            if (account is null || !account.IsEnabled) return Results.Conflict(new { message = "The linked local account is unavailable." });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new LocalActivationTokenResponse(account.Id, account.Email ?? string.Empty, await users.GeneratePasswordResetTokenAsync(account)));
        });

        var adminGroup = app.MapGroup("/api/admin/customers/{customerId}")
            .WithTags("Customer Authentication")
            .RequireAuthorization("HelpdeskAdmin");

        adminGroup.MapGet("/auth-status", async (
            [FromRoute] string customerId,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] UserManager<ApplicationUser> users,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            Results.Ok(await GetStatusAsync(customerId, invitationService, db, users, authentikOptions.Value, ct)));

        adminGroup.MapPost("/invite", async (
            [FromRoute] string customerId,
            ClaimsPrincipal user,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.InviteAsync(customerId, ResolveUserId(user), ct), ct));

        adminGroup.MapPost("/resend-invite", async (
            [FromRoute] string customerId,
            ClaimsPrincipal user,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.ResendInviteAsync(customerId, ResolveUserId(user), ct), ct));

        adminGroup.MapPost("/disable-login", async (
            [FromRoute] string customerId,
            ClaimsPrincipal user,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.DisableLoginAsync(customerId, ResolveUserId(user), ct), ct));

        adminGroup.MapPost("/sync-authentik", async (
            [FromRoute] string customerId,
            [FromServices] ICustomerInvitationService invitationService,
            [FromServices] HelpdeskDbContext db,
            [FromServices] IOptions<AuthentikOptions> authentikOptions,
            CancellationToken ct) =>
            await RunExternalCustomerAuthActionAsync(customerId, db, authentikOptions.Value, () => invitationService.SyncAuthentikAsync(customerId, ct), ct));
    }

    private static async Task<IResult> RunCustomerAuthActionAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (AuthentikAdminConfigurationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Customer invitation is not configured");
        }
        catch (AuthentikAdminRequestException ex)
        {
            var statusCode = (int)ex.StatusCode is >= 400 and <= 499
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status502BadGateway;
            return Results.Problem(
                detail: ex.Message,
                statusCode: statusCode,
                title: "Customer invitation request could not be completed");
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Customer invitation request could not be completed");
        }
    }

    private static async Task<IResult> RunExternalCustomerAuthActionAsync<T>(
        string customerId,
        HelpdeskDbContext db,
        AuthentikOptions authentikOptions,
        Func<Task<T>> action,
        CancellationToken ct)
    {
        var links = await db.CustomerAuthLinks.AsNoTracking()
            .Where(link => link.CustomerId == customerId)
            .Select(link => new { link.AuthProviderType, link.LocalAccountId })
            .ToArrayAsync(ct);
        if (links.Length > 1)
        {
            return Results.Conflict(new { message = "Multiple linked identities must be reviewed before an external access action can run." });
        }
        if (links.SingleOrDefault() is { } link &&
            (string.Equals(link.AuthProviderType, "Local", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(link.LocalAccountId)))
        {
            return Results.Conflict(new { message = "This customer has a local account. Use the local activation workflow; external invitation actions are unavailable." });
        }
        if (!authentikOptions.IsConfigured)
        {
            return Results.Problem(
                detail: authentikOptions.ConfigurationError,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Customer invitation is not configured");
        }
        return await RunCustomerAuthActionAsync(action);
    }

    private static async Task<CustomerAuthStatusDto> GetStatusAsync(
        string customerId,
        ICustomerInvitationService invitationService,
        HelpdeskDbContext db,
        UserManager<ApplicationUser> users,
        AuthentikOptions authentikOptions,
        CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == customerId, ct)
            ?? throw new InvalidOperationException("Customer not found.");
        var links = await db.CustomerAuthLinks.AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderBy(x => x.AuthProviderType)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

        if (links.Count == 0)
        {
            return NoLinkedLogin(customer.Id, authentikOptions.IsConfigured);
        }

        if (links.Count > 1)
        {
            return new CustomerAuthStatusDto
            {
                CustomerId = customer.Id,
                InviteStatus = CustomerInviteStatus.Failed,
                StatusText = "Multiple linked identities",
                IdentitySummary = "Multiple provider links require instance-administrator review.",
                HasMultipleIdentityLinks = true,
                IsLinkedLogin = true,
                LastAuthError = "Access actions are unavailable until the linked identities are reviewed.",
                AuthProviderType = string.Join(", ", links.Select(link => link.AuthProviderType).Distinct(StringComparer.OrdinalIgnoreCase))
            };
        }

        var link = links[0];
        if (string.Equals(link.AuthProviderType, "Local", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(link.LocalAccountId))
        {
            return await GetLocalAccountStatusAsync(customer.Id, link, users);
        }

        // Existing external flows remain owned by the invitation provider. The
        // caller receives its exact provider/link status rather than an inferred
        // identity from a matching customer email address.
        return await invitationService.GetStatusAsync(customerId, ct);
    }

    private static async Task<CustomerAuthStatusDto> GetLocalAccountStatusAsync(
        string customerId,
        CustomerAuthLink link,
        UserManager<ApplicationUser> users)
    {
        var account = await users.FindByIdAsync(link.LocalAccountId!);
        var status = account switch
        {
            null => CustomerInviteStatus.Failed,
            { IsEnabled: false } => CustomerInviteStatus.Disabled,
            { EmailConfirmed: false } or { PasswordHash: null or "" } => CustomerInviteStatus.Pending,
            _ => CustomerInviteStatus.Active
        };

        return new CustomerAuthStatusDto
        {
            CustomerId = customerId,
            InviteStatus = status,
            StatusText = status switch
            {
                CustomerInviteStatus.Pending => "Activation pending",
                CustomerInviteStatus.Active => "Login active",
                CustomerInviteStatus.Disabled => "Login disabled",
                _ => "Linked local account unavailable"
            },
            AuthProviderType = "Local",
            LocalAccountId = link.LocalAccountId,
            DomainUserId = link.DomainUserId,
            IdentitySummary = account is null ? "The linked local account no longer exists." : account.Email,
            IsLinkedLogin = true,
            DisabledAtUtc = account?.DisabledAtUtc,
            LastAuthError = account is null ? "The linked local account could not be found." : null,
            CanInvite = false,
            CanResend = false,
            CanDisableLogin = false
        };
    }

    private static CustomerAuthStatusDto NoLinkedLogin(string customerId, bool canInvite) => new()
    {
        CustomerId = customerId,
        InviteStatus = CustomerInviteStatus.NotInvited,
        StatusText = "No linked login",
        IdentitySummary = canInvite
            ? "This contact has no login link."
            : "This contact has no login link. External invitations are not configured.",
        CanInvite = canInvite
    };

    public sealed record LocalActivationTokenResponse(string UserId, string Email, string ActivationToken)
    {
        public IReadOnlyList<string> SensitiveFields { get; init; } = ["activationToken"];
        public override string ToString() => $"LocalActivationTokenResponse {{ UserId = {UserId}, ActivationToken = [redacted] }}";
    }

    private static string ResolveUserId(ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? user.FindFirstValue("sub")
           ?? user.FindFirstValue("preferred_username")
           ?? user.Identity?.Name
           ?? "unknown";
}
