using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services;

public enum IncidentReceiverOperation { Create, ReadReceipt, ValidateTarget }
public sealed record AuthorizedIncidentSource(IncidentReceiverSource Source, Customer Customer, Organization Organization);

/// <summary>The bounded authorization seam for a future approved machine-principal implementation.</summary>
public interface IIncidentReceiverAuthorization
{
    bool RequiresKey(ClaimsPrincipal principal);
    Task<AuthorizedIncidentSource?> ResolveAsync(ClaimsPrincipal principal, Guid sourceInstanceId,
        IncidentReceiverOperation operation, CancellationToken ct);
}

public sealed class IncidentReceiverAuthorization(HelpdeskDbContext db, RatelDeskIdentityDbContext identity,
    ICurrentUserAccessService accessService, TimeProvider time) : IIncidentReceiverAuthorization
{
    public bool RequiresKey(ClaimsPrincipal principal) => principal.FindFirstValue("auth_mode") is "machine" or "service";

    public async Task<AuthorizedIncidentSource?> ResolveAsync(ClaimsPrincipal principal, Guid sourceInstanceId,
        IncidentReceiverOperation operation, CancellationToken ct)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.FindFirstValue("auth_mode") != "integration" ||
            !Guid.TryParseExact(principal.FindFirstValue("integration_credential_id"), "N", out var id)) return null;
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (credential is null || credential.Purpose != IntegrationCredentialAuthenticationHandler.ApiPurpose ||
            credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= time.GetUtcNow() ||
            !await identity.Users.AnyAsync(x => x.Id == credential.OwnerUserId && x.IsEnabled, ct)) return null;

        var source = await (from sourceRow in db.IncidentReceiverSources.AsNoTracking()
                            join binding in db.IncidentReceiverPrincipalBindings.AsNoTracking()
                                on sourceRow.SourceNamespaceId equals binding.SourceNamespaceId
                            where sourceRow.SourceInstanceId == sourceInstanceId && sourceRow.IsEnabled && binding.IsEnabled &&
                                  binding.PrincipalKind == "api_credential" && binding.PrincipalId == id.ToString("N")
                            select sourceRow).SingleOrDefaultAsync(ct);
        if (source is null || credential.OrganizationId != source.OrganizationId) return null;
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.CustomerId && x.IsEnabled, ct);
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.OrganizationId && x.IsEnabled, ct);
        if (customer is null || organization is null || customer.OrganizationId != organization.Id) return null;
        var access = await accessService.ResolveAsync(principal, ct);
        var allowed = operation == IncidentReceiverOperation.ReadReceipt
            ? access.CanViewIncident(organization.Id, customer.Id, customer.Email)
            : access.CanCreateIncident(organization.Id, customer.Id, customer.Email);
        return allowed ? new(source, customer, organization) : null;
    }
}
