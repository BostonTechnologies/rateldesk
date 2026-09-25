using Helpdesk.Application.Services.Email;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Helpdesk.Infrastructure.Health;

public sealed class GraphEmailHealthCheck : IHealthCheck
{
    private readonly IEmailService _email;

    public GraphEmailHealthCheck(IEmailService email)
    {
        _email = email;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var ok = await _email.TestApiConnectionAsync();

        return ok
            ? HealthCheckResult.Healthy("A mailbox sender is configured; delivery is verified separately.")
            : HealthCheckResult.Degraded("No enabled mailbox sender is configured.");
    }
}
