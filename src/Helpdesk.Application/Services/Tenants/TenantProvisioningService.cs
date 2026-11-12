using Helpdesk.Application.Events;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Application.Services.Tenants;

public class TenantProvisioningService(
    IRepository<Organization> organizationRepository,
    IRepository<Customer> customerRepository,
    IDomainEventPublisher? domainEvents = null,
    ICorrelationContext? correlationContext = null)
    : ITenantProvisioningService
{
    private readonly IDomainEventPublisher _domainEvents = domainEvents ?? NoopDomainEventPublisher.Instance;
    private readonly ICorrelationContext? _correlationContext = correlationContext;

    public async Task<Organization> GetOrCreateOrganizationByDomainAsync(string domain)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var existingOrganization = await FindOrganizationAsync(normalizedDomain);
        if (existingOrganization is not null)
        {
            if (string.IsNullOrWhiteSpace(existingOrganization.DnsName))
            {
                existingOrganization.DnsName = normalizedDomain;
                await organizationRepository.UpdateAsync(existingOrganization);
            }

            return existingOrganization;
        }

        try
        {
            var createdOrganization = await organizationRepository.CreateAsync(new Organization
            {
                Name = normalizedDomain,
                DnsName = normalizedDomain,
                State = EntityState.Enabled
            });

            await _domainEvents.PublishAsync(
                new TenantCreatedEvent(
                    normalizedDomain,
                    createdOrganization.Id,
                    GetCorrelationId()),
                CancellationToken.None);
            return createdOrganization;
        }
        catch
        {
            // Duplicate-key races can happen under concurrent first-contact flows.
            var raceWinner = await FindOrganizationAsync(normalizedDomain);
            if (raceWinner is not null)
            {
                return raceWinner;
            }

            throw;
        }
    }

    public async Task<(Customer customer, bool isNew)> GetOrCreateCustomerAsync(
        string email,
        string name,
        string domain)
    {
        var normalizedEmail = NormalizeEmail(email);
        var organization = await GetOrCreateOrganizationByDomainAsync(domain);

        var existingCustomer = await FindCustomerAsync(normalizedEmail, organization.Id);
        if (existingCustomer is not null)
        {
            var didChange = false;
            var normalizedName = NormalizeCustomerName(name, normalizedEmail);
            if (!string.Equals(existingCustomer.Name, normalizedName, StringComparison.Ordinal))
            {
                existingCustomer.Name = normalizedName;
                didChange = true;
            }

            if (didChange)
            {
                await customerRepository.UpdateAsync(existingCustomer);
            }

            return (existingCustomer, false);
        }

        var newCustomer = new Customer
        {
            Email = normalizedEmail,
            Name = NormalizeCustomerName(name, normalizedEmail),
            OrganizationId = organization.Id,
            State = EntityState.Enabled
        };

        try
        {
            var createdCustomer = await customerRepository.CreateAsync(newCustomer);
            await _domainEvents.PublishAsync(
                new CustomerCreatedEvent(
                    createdCustomer.Email,
                    createdCustomer.Id,
                    createdCustomer.OrganizationId,
                    GetCorrelationId()),
                CancellationToken.None);
            return (createdCustomer, true);
        }
        catch
        {
            var raceWinner = await FindCustomerAsync(normalizedEmail, organization.Id);
            if (raceWinner is not null)
            {
                return (raceWinner, false);
            }

            throw;
        }
    }

    private async Task<Organization?> FindOrganizationAsync(string normalizedDomain)
    {
        var organizations = await organizationRepository.GetAllAsync();
        return organizations.FirstOrDefault(o =>
            (!string.IsNullOrWhiteSpace(o.DnsName) && o.DnsName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase))
            || o.Name.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Customer?> FindCustomerAsync(string normalizedEmail, string organizationId)
    {
        var customers = await customerRepository.GetAllAsync();
        return customers.FirstOrDefault(c => c.IdentityUserId is null && c.OrganizationId == organizationId &&
            c.Email.Equals(normalizedEmail, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDomain(string domain)
    {
        var normalized = (domain ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains('@'))
        {
            normalized = normalized.Split('@', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        }

        normalized = normalized.TrimStart('@').Trim('.');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Domain cannot be empty.", nameof(domain));
        }

        return normalized;
    }

    private static string NormalizeEmail(string email)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Contains('@'))
        {
            throw new ArgumentException("Email must contain a domain.", nameof(email));
        }

        return normalized;
    }

    private static string NormalizeCustomerName(string name, string fallback)
    {
        var normalized = (name ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private string GetCorrelationId()
    {
        return _correlationContext?.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }
}
