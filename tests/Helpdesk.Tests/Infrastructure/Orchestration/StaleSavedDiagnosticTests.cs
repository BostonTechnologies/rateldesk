using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class StaleSavedDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Superseded_test_does_not_contaminate_the_following_audit_save(bool netclaw)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-stale-test-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
                .UseSqlite($"Data Source={databasePath}").Options;
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            await using (var initializer = new HelpdeskDbContext(options, tenant, new HttpContextAccessor()))
            {
                await initializer.Database.EnsureCreatedAsync();
                if (netclaw)
                    initializer.NetclawConnectivitySettings.Add(new NetclawConnectivitySettings { Revision = 1, ProfileFingerprint = "profile-a" });
                else
                    initializer.M2MConnectivitySettings.Add(new M2MConnectivitySettings { Revision = 1, ProfileFingerprint = "profile-a" });
                await initializer.SaveChangesAsync();
            }

            await using var diagnosticDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await using var editorDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
            var service = new IntegrationProviderSettingsService(
                diagnosticDb,
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                new IntegrationProviderSecretProtector(protection),
                Options.Create(new AiAssistantChatOptions()),
                new DatabaseOptions { Provider = "Sqlite" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance);

            if (netclaw)
                _ = await service.GetResolvedNetclawSettingsAsync();
            else
                _ = await service.GetResolvedOrchestratorSettingsAsync();

            if (netclaw)
            {
                var current = await editorDb.NetclawConnectivitySettings.SingleAsync();
                current.Revision = 2;
                current.ProfileFingerprint = "profile-b";
            }
            else
            {
                var current = await editorDb.M2MConnectivitySettings.SingleAsync();
                current.Revision = 2;
                current.ProfileFingerprint = "profile-b";
            }
            await editorDb.SaveChangesAsync();

            var recorded = netclaw
                ? await service.RecordNetclawTestAsync(1, "profile-a", succeeded: true)
                : await service.RecordOrchestratorTestAsync(1, "profile-a", succeeded: true);
            Assert.False(recorded);
            diagnosticDb.ActivityLogs.Add(new ActivityLog { UserId = "test-admin", Message = "Superseded diagnostic" });
            await diagnosticDb.SaveChangesAsync();

            await using var verification = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            Assert.Single(await verification.ActivityLogs.ToListAsync());
            if (netclaw)
            {
                var current = await verification.NetclawConnectivitySettings.SingleAsync();
                Assert.Equal(2, current.Revision);
                Assert.Equal("profile-b", current.ProfileFingerprint);
                Assert.Null(current.LastTestedAtUtc);
                Assert.Null(current.LastTestSucceeded);
            }
            else
            {
                var current = await verification.M2MConnectivitySettings.SingleAsync();
                Assert.Equal(2, current.Revision);
                Assert.Equal("profile-b", current.ProfileFingerprint);
                Assert.Null(current.LastTestedAtUtc);
                Assert.Null(current.LastTestSucceeded);
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
}
