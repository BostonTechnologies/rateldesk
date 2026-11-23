using System.Net.Mail;
using Helpdesk.Application.Events;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Tenants;

public sealed class TenantProvisioningService(HelpdeskDbContext db, IDomainEventPublisher events,
    ICorrelationContext correlation, IIngressEffectContext effects, MailboxOutboxStore outbox) : ITenantProvisioningService
{
    public async Task<T> ProcessAsync<T>(string email, string? submittedName,
        Func<Customer, bool, CancellationToken, Task<T>> process, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        if (normalized.Length > 254 || !MailAddress.TryCreate(normalized, out var address) || address.Address != normalized)
            throw new RequesterIntakeRejectedException("InvalidRequesterAddress");
        var domain = address.Host.TrimEnd('.');
        if (domain.Length == 0)
            throw new RequesterIntakeRejectedException("InvalidRequesterDomain");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await InboundTenantRouter.LockProvisioningDomainAsync(db, domain, ct);
        var requester = await db.Customers.AsNoTracking()
            .SingleOrDefaultAsync(row => row.IdentityUserId == null && row.Email.ToLower() == normalized, ct);
        var matches = await db.Organizations.AsNoTracking()
            .Where(row => row.DnsName != null && row.DnsName.ToLower() == domain || row.Name.ToLower() == domain)
            .Select(row => row.Id).Take(2).ToListAsync(ct);
        if (matches.Count > 1 || (requester is not null && matches.Count == 1 && matches[0] != requester.OrganizationId))
            throw new RequesterIntakeRejectedException("RequesterOwnershipConflict");

        var organizationId = requester?.OrganizationId ?? matches.SingleOrDefault();
        Organization? organization = null;
        if (organizationId is not null)
        {
            await InboundTenantRouter.LockOrganizationAsync(db, organizationId, ct);
            organization = await db.Organizations.SingleOrDefaultAsync(row => row.Id == organizationId, ct);
            if (organization?.State != Helpdesk.Shared.Models.EntityState.Enabled)
                throw new RequesterIntakeRejectedException("OrganizationUnavailable");
        }
        if (requester is not null)
        {
            await InboundTenantRouter.LockCustomerAsync(db, requester.Id, ct);
            requester = await db.Customers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == requester.Id, ct);
            if (requester is null || requester.IdentityUserId is not null || requester.Email.ToLowerInvariant() != normalized ||
                requester.OrganizationId != organizationId || requester.State != Helpdesk.Shared.Models.EntityState.Enabled)
                throw new RequesterIntakeRejectedException("RequesterUnavailable");
        }

        var newOrganization = organization is null;
        if (organization is null)
        {
            organization = new Organization { Name = domain, DnsName = domain };
            db.Organizations.Add(organization);
        }
        else if (matches.Count == 1 && string.IsNullOrWhiteSpace(organization.DnsName))
        {
            organization.DnsName = domain;
        }

        var newRequester = requester is null;
        if (requester is null)
        {
            var name = submittedName?.Trim();
            requester = new Customer
            {
                Email = normalized, Name = string.IsNullOrEmpty(name) ? normalized : name,
                OrganizationId = organization.Id
            };
            db.Customers.Add(requester);
        }
        await db.SaveChangesAsync(ct);

        using var captured = effects.Begin(Guid.NewGuid());
        effects.OrganizationId = organization.Id;
        var correlationId = correlation.GetCorrelationId();
        if (newOrganization)
            await events.PublishAsync(new TenantCreatedEvent(domain, organization.Id, correlationId), ct);
        if (newRequester)
            await events.PublishAsync(new CustomerCreatedEvent(requester.Email, requester.Id, organization.Id, correlationId), ct);
        var result = await process(requester, newRequester, ct);
        await outbox.FlushAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }
}
