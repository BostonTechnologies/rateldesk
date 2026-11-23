using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Infrastructure.Security;

internal sealed class PublicLinkCleanup(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PublicLinkCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                var cutoff = clock.GetUtcNow().AddDays(-30).ToUnixTimeSeconds();
                var links = db.Set<PublicLinkCapability>();
                var expired = links.Where(link => link.ExpiresAt < cutoff).OrderBy(link => link.ExpiresAt).Take(1000).Select(link => link.Id);
                await links.Where(link => expired.Contains(link.Id)).ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Expired public-link cleanup failed. ErrorType={Type}", error.GetType().Name);
            }
        }
    }
}
