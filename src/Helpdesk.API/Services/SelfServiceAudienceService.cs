using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Pairing;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using EntityState = Helpdesk.Shared.Models.EntityState;

namespace Helpdesk.API.Services;

public sealed class SelfServiceAudienceService(
    HelpdeskDbContext db,
    ICurrentUserAccessService accessService,
    IRequestFormSchemaParser schemaParser,
    RatelDeskIdentityDbContext? identityDb = null)
{
    internal async Task<SelfServiceAuthority> ResolveAuthorityAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var access = await accessService.ResolveAsync(principal, ct);
        var grants = access.OrganizationIdsFor(HelpdeskPermissions.SelfServiceUser).ToArray();
        var organizations = await db.Organizations.AsNoTracking()
            .Where(org => org.State == EntityState.Enabled && (access.IsHelpdeskAdmin || grants.Contains(org.Id)))
            .Select(org => new SelfServiceTargetOrganizationDto(org.Id, org.Name))
            .ToDictionaryAsync(org => org.Id, StringComparer.Ordinal, ct);
        var identity = await SelfServiceRequesterResolver.ResolveAsync(db, identityDb, access, principal, ct);
        var customers = await SelfServiceRequesterResolver.LoadCustomersAsync(db, identity, organizations.Keys, ct);
        return new SelfServiceAuthority(access, identity, organizations, customers);
    }

    internal async Task<SelfServiceCatalogue> LoadCatalogueAsync(
        SelfServiceAuthority authority, CancellationToken ct, string? formId = null)
    {
        var folders = await db.Services.AsNoTracking().ToDictionaryAsync(folder => folder.Id, StringComparer.Ordinal, ct);
        var bindings = (await db.AutomationBindings.AsNoTracking()
            .Where(binding => binding.Enabled && (formId == null || binding.RequestFormId == formId))
            .Select(binding => new SelfServiceFormBinding(binding.RequestFormId, binding.OrganizationId, binding.SystemConnectionId))
            .ToListAsync(ct)).ToLookup(binding => binding.FormId, StringComparer.Ordinal);
        var connectionIds = bindings.SelectMany(group => group)
            .Select(binding => binding.ConnectionId).OfType<Guid>().Distinct().ToArray();
        var connections = await db.Set<SystemConnection>().AsNoTracking()
            .Where(connection => connectionIds.Contains(connection.Id) && connection.State == "connected")
            .Select(connection => new { connection.Id, connection.MappingJson }).ToListAsync(ct);
        var connectionOrganizations = new Dictionary<Guid, string>();
        foreach (var connection in connections)
        {
            try
            {
                var mapping = SystemPairingService.Read<PairingMapping>(connection.MappingJson);
                if (mapping.RunAutomation) connectionOrganizations[connection.Id] = mapping.RatelDeskOrganizationId;
            }
            catch (Exception exception) when (exception is JsonException or PairingFailure)
            {
                // An invalid saved mapping provides no eligible organization.
                continue;
            }
        }

        var organizationIds = authority.Organizations.Keys.ToArray();
        var datasets = await db.DatasetDefinitions.AsNoTracking()
            .Where(dataset => dataset.IsActive && organizationIds.Contains(dataset.OrganizationId))
            .ToDictionaryAsync(dataset => dataset.Id, dataset => dataset.OrganizationId, StringComparer.Ordinal, ct);
        return new SelfServiceCatalogue(authority, folders, bindings, connectionOrganizations, datasets, schemaParser);
    }

    // The organization predicate bounds this feature's use of the primary-tenant
    // filter override. Every candidate also passes the catalogue policy before
    // it is returned or used in a submission.
    internal IQueryable<RequestForm> CandidateForms(SelfServiceAuthority authority)
    {
        var forms = db.RequestForms.IgnoreQueryFilters().AsNoTracking();
        if (authority.Access.IsHelpdeskAdmin) return forms;
        var ids = authority.Organizations.Keys.ToArray();
        return forms.Where(form => string.IsNullOrWhiteSpace(form.OrganizationId) || ids.Contains(form.OrganizationId));
    }

    internal Task<RequestForm?> FindFormAsync(SelfServiceAuthority authority, string id, CancellationToken ct)
    {
        var normalized = id.Trim();
        var ids = Guid.TryParse(normalized, out var guid) ? new[] { normalized, guid.ToString("D"), guid.ToString("N") } : [normalized];
        return CandidateForms(authority).FirstOrDefaultAsync(form => ids.Contains(form.Id), ct);
    }

}

internal sealed record SelfServiceAuthority(
    CurrentUserAccessProfile Access,
    SelfServiceIdentity? Identity,
    IReadOnlyDictionary<string, SelfServiceTargetOrganizationDto> Organizations,
    IReadOnlyDictionary<string, Customer> Customers)
{
    public bool IsTestUser => Access.IsHelpdeskAdmin || Identity?.DomainUser?.IsTestUser == true;

    public Customer? CustomerFor(string organizationId) =>
        Customers.GetValueOrDefault(organizationId) is { IsEnabled: true } customer ? customer : null;

    public bool CanSubmitAsSelf(string organizationId) =>
        Identity is not null && (CustomerFor(organizationId) is not null ||
            !Customers.ContainsKey(organizationId) && Identity.CanCreateCustomer)
        && (!Access.IsHelpdeskAdmin ||
            organizationId == Identity.HomeOrganizationId ||
            Access.OrganizationIdsFor(HelpdeskPermissions.SelfServiceUser).Contains(organizationId));
}

internal sealed record SelfServiceFormBinding(string FormId, string OrganizationId, Guid? ConnectionId);

internal sealed class SelfServiceCatalogue(
    SelfServiceAuthority authority,
    IReadOnlyDictionary<string, Service> folders,
    ILookup<string, SelfServiceFormBinding> bindings,
    IReadOnlyDictionary<Guid, string> connectionOrganizations,
    IReadOnlyDictionary<string, string> datasets,
    IRequestFormSchemaParser schemaParser)
{
    public IReadOnlyDictionary<string, Service> Folders => folders;

    public bool CanViewFolder(string id) => folders.ContainsKey(id) &&
        (authority.Access.IsHelpdeskAdmin || authority.Organizations.Keys.Any(
            organizationId => FolderAllows(id, organizationId, authority.CustomerFor(organizationId)?.Id)));

    public IReadOnlyList<string>? ParentOrganizationIds(string? parentId) =>
        string.IsNullOrEmpty(parentId) ? null : authority.Organizations.Keys
            .Where(id => FolderAllows(parentId, id, customerId: null, checkCustomer: false)).ToList();

    public IReadOnlyList<SelfServiceTargetOrganizationDto> FormOrganizations(RequestForm form)
    {
        if (!authority.IsTestUser && form.ReleaseStatus != RequestFormReleaseStatus.Production) return [];

        var possibleOrganizations = authority.Organizations.Values
            .Where(org => FormAndBindingsAllow(form, org.Id) &&
                FolderAllows(form.ServiceId, org.Id, authority.CustomerFor(org.Id)?.Id))
            .ToList();
        if (possibleOrganizations.Count == 0) return [];

        var datasetIds = GetDatasetIds(form);
        return datasetIds is null ? [] : possibleOrganizations
            .Where(org => datasetIds.All(id => datasets.GetValueOrDefault(id) == org.Id))
            .OrderBy(org => org.Name, StringComparer.OrdinalIgnoreCase).ThenBy(org => org.Id, StringComparer.Ordinal)
            .ToList();
    }

    public bool AllowsRecipient(RequestForm form, string organizationId, string? customerId) =>
        authority.Organizations.ContainsKey(organizationId) && FormAndBindingsAllow(form, organizationId) &&
        (authority.IsTestUser || form.ReleaseStatus == RequestFormReleaseStatus.Production) &&
        FolderAllows(form.ServiceId, organizationId, customerId) &&
        GetDatasetIds(form) is { } ids && ids.All(id => datasets.GetValueOrDefault(id) == organizationId);

    private bool FormAndBindingsAllow(RequestForm form, string organizationId) =>
        (string.IsNullOrWhiteSpace(form.OrganizationId) || form.OrganizationId == organizationId) &&
        ExplicitlyAllows(form.AllowedOrganizationIds, organizationId) &&
        bindings[form.Id].All(binding => binding.OrganizationId == organizationId &&
            (binding.ConnectionId is null || connectionOrganizations.GetValueOrDefault(binding.ConnectionId.Value) == organizationId));

    private bool FolderAllows(string? folderId, string organizationId, string? customerId, bool checkCustomer = true)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (folderId is not null && folderId.Length != 0)
        {
            if (!visited.Add(folderId) || !folders.TryGetValue(folderId, out var folder) ||
                !ExplicitlyAllows(folder.AllowedOrganizationIds, organizationId) ||
                (checkCustomer && folder.AllowedCustomerIds is { Count: > 0 } &&
                    (customerId is null || !folder.AllowedCustomerIds.Contains(customerId, StringComparer.OrdinalIgnoreCase))))
                return false;
            folderId = folder.ParentServiceId;
        }
        return true;
    }

    private string[]? GetDatasetIds(RequestForm form)
    {
        try
        {
            return schemaParser.Parse(form.JsonSchema).Fields
                .Where(field => field.DataBinding is not null)
                .Select(field => field.DataBinding!.DatasetId).Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ExplicitlyAllows(IReadOnlyCollection<string>? allowed, string organizationId) =>
        allowed?.Contains(organizationId, StringComparer.OrdinalIgnoreCase) == true;
}
