using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Services.Tenants;

public interface ITenantProvisioningService
{
    Task<T> ProcessAsync<T>(string email, string? submittedName,
        Func<Customer, bool, CancellationToken, Task<T>> process, CancellationToken cancellationToken);
}

public sealed class RequesterIntakeRejectedException(string reason) : Exception("Ticket submission could not be accepted.")
{
    public string Reason { get; } = reason;
}
