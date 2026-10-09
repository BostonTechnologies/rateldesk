using System.Security.Claims;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services;

public enum IncidentReceiverOperation { Create, ReadReceipt, ValidateTarget }
public sealed record AuthorizedIncidentSource(IncidentReceiverSource Source, Customer Customer, Organization Organization);

/// <summary>Resolves current, explicitly approved receiver membership without granting a machine human authority.</summary>
public interface IIncidentReceiverAuthorization
{
    bool RequiresKey(ClaimsPrincipal principal);
    Task<AuthorizedIncidentSource?> ResolveAsync(ClaimsPrincipal principal, Guid sourceInstanceId,
        IncidentReceiverOperation operation, CancellationToken ct);
}

public sealed class IncidentReceiverAuthorization(HelpdeskDbContext db, RatelDeskIdentityDbContext identity,
    ICurrentUserAccessService accessService, TimeProvider time, IServicePrincipalRegistry? services = null) : IIncidentReceiverAuthorization
{
    public bool RequiresKey(ClaimsPrincipal principal) => principal.FindFirstValue("auth_mode") is "machine" or "service";

    public async Task<AuthorizedIncidentSource?> ResolveAsync(ClaimsPrincipal principal, Guid sourceInstanceId,
        IncidentReceiverOperation operation, CancellationToken ct)
    {
        if (RequiresKey(principal))
        {
            if (services is null) return null;
            var scope = operation switch
            {
                IncidentReceiverOperation.Create => ServiceIdentityScopes.IncidentCreate,
                IncidentReceiverOperation.ReadReceipt => ServiceIdentityScopes.IncidentReceipts,
                IncidentReceiverOperation.ValidateTarget => ServiceIdentityScopes.IncidentTargets,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            var registration = await services.ResolvePrincipalAsync(principal, scope, ct);
            if (registration is null || registration.SourceInstanceId != sourceInstanceId || registration.SourceNamespaceId is null)
                return null;
            var principalId = registration.Id.ToString("N");
            var serviceSource = await (from sourceRow in db.IncidentReceiverSources.AsNoTracking()
                                join binding in db.IncidentReceiverPrincipalBindings.AsNoTracking()
                                    on sourceRow.SourceNamespaceId equals binding.SourceNamespaceId
                                where sourceRow.SourceNamespaceId == registration.SourceNamespaceId &&
                                      sourceRow.SourceInstanceId == sourceInstanceId && sourceRow.IsEnabled && binding.IsEnabled &&
                                      binding.PrincipalKind == "service_principal" && binding.PrincipalId == principalId
                                select sourceRow).SingleOrDefaultAsync(ct);
            if (serviceSource is null || registration.OrganizationId != serviceSource.OrganizationId) return null;
            string[] customers;
            try { customers = JsonSerializer.Deserialize<string[]>(registration.CustomerIdsJson) ?? []; }
            catch (JsonException) { return null; }
            if (!customers.Contains(serviceSource.CustomerId, StringComparer.Ordinal)) return null;
            return await ResolveEnabledMappingAsync(serviceSource, ct);
        }
        if (principal.Identity?.IsAuthenticated != true || principal.FindFirstValue("auth_mode") != "integration" ||
            !Guid.TryParseExact(principal.FindFirstValue("integration_credential_id"), "N", out var id)) return null;
        var credential = await identity.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (credential is null || credential.Purpose != IntegrationCredentialAuthenticationHandler.ApiPurpose ||
            credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= time.GetUtcNow() ||
            !await identity.Users.AnyAsync(x => x.Id == credential.OwnerUserId && x.IsEnabled, ct)) return null;

        var candidates = await (from sourceRow in db.IncidentReceiverSources.AsNoTracking()
                            join binding in db.IncidentReceiverPrincipalBindings.AsNoTracking()
                                on sourceRow.SourceNamespaceId equals binding.SourceNamespaceId
                            where sourceRow.SourceInstanceId == sourceInstanceId && sourceRow.IsEnabled && binding.IsEnabled &&
                                  binding.PrincipalKind == "api_credential" && binding.PrincipalId == id.ToString("N")
                            select sourceRow).Take(2).ToListAsync(ct);
        var source = candidates.Count == 1 ? candidates[0] : null;
        if (source is null || credential.OrganizationId != source.OrganizationId) return null;
        var approved = await ResolveEnabledMappingAsync(source, ct);
        if (approved is null) return null;
        var customer = approved.Customer;
        var organization = approved.Organization;
        var access = await accessService.ResolveAsync(principal, ct);
        var allowed = operation == IncidentReceiverOperation.ReadReceipt
            ? access.CanViewIncident(organization.Id, customer.Id, customer.Email)
            : access.CanCreateIncident(organization.Id, customer.Id, customer.Email);
        return allowed ? new(source, customer, organization) : null;
    }

    private async Task<AuthorizedIncidentSource?> ResolveEnabledMappingAsync(IncidentReceiverSource source, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.CustomerId && x.IsEnabled, ct);
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == source.OrganizationId && x.IsEnabled, ct);
        return customer is not null && organization is not null && customer.OrganizationId == organization.Id
            ? new(source, customer, organization) : null;
    }
}
