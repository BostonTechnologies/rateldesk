using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Pairing;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Pairing;

/// <summary>Retained setup ownership is a current account lookup, never a human token or frozen administrator grant.</summary>
public sealed class PairingAuthority(HelpdeskDbContext db, RatelDeskIdentityDbContext identity, ICurrentUserAccessService access)
{
    public async Task<CurrentUserAccessProfile> OwnerAsync(string ownerId, CancellationToken ct)
    {
        if (!await identity.Users.AsNoTracking().AnyAsync(x => x.Id == ownerId && x.IsEnabled, ct))
            throw new PairingFailure("owner_unavailable", "The administrator who authorized this pairing is disabled or unavailable. Pair again with a current administrator.", 403);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerId), new Claim("auth_mode", "local")], "retained-setup-owner"));
        var current = await access.ResolveAsync(principal, ct);
        if (!current.IsHelpdeskAdmin) throw new PairingFailure("setup_authority_removed", "The administrator who authorized this pairing no longer has system connection permission.", 403);
        return current;
    }
    public async Task ValidateAsync(string ownerId, PairingMapping mapping, CancellationToken ct)
    {
        var current = await OwnerAsync(ownerId, ct);
        if (mapping is null || !Guid.TryParse(mapping.Id, out var id) || id == Guid.Empty || string.IsNullOrWhiteSpace(mapping.Name) || mapping.Name.Length > 128 ||
            mapping.Name.Any(char.IsControl) || !int.TryParse(mapping.NetRatelTenantId, out var tenant) || tenant <= 0 ||
            string.IsNullOrWhiteSpace(mapping.RatelDeskOrganizationId) || !mapping.CreateIncidents && !mapping.RunAutomation)
            throw new PairingFailure("invalid_mapping", "Choose both tenants, enter a connection name and select at least one capability.");
        var org = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mapping.RatelDeskOrganizationId && x.IsEnabled, ct);
        if (org is null || !current.IsHelpdeskAdmin && !current.AllowedOrganizationIds.Contains(org.Id))
            throw new PairingFailure("organization_not_authorized", "The selected RatelDesk organization is unavailable or no longer authorized.", 403);
        if (mapping.CreateIncidents)
        {
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == mapping.RatelDeskCustomerId && x.OrganizationId == org.Id && x.IsEnabled, ct);
            if (customer is null || !current.CanCreateIncident(org.Id, customer.Id, customer.Email))
                throw new PairingFailure("customer_not_authorized", "Choose an enabled incident customer belonging to the selected RatelDesk organization.", 403);
        }
        else if (mapping.RatelDeskCustomerId is not null)
            throw new PairingFailure("unexpected_customer", "Customer selection is only used for Create incidents.");
    }
    public async Task<bool> BusinessUsableAsync(Guid mappingId, string requiredScope, CancellationToken ct)
    {
        var connection = await db.Set<SystemConnection>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == mappingId && x.State == "connected", ct);
        if (connection is null) return false;
        var pair = await db.Set<SystemPair>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == connection.PairId && x.State == "paired" && x.Generation == connection.PairGeneration, ct);
        if (pair is null) return false;
        try
        {
            await OwnerAsync(pair.OwnerId, ct);
            var mapping = SystemPairingService.Read<PairingMapping>(connection.MappingJson);
            await ValidateAsync(connection.OwnerId, mapping, ct);
            return requiredScope == "rateldesk.orchestration.callback" ? mapping.RunAutomation : mapping.CreateIncidents;
        }
        catch (Exception ex) when (ex is PairingFailure or System.Text.Json.JsonException) { return false; }
    }
}
