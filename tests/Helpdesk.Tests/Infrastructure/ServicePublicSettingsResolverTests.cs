using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.ServiceIdentity;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure;

public sealed class ServicePublicSettingsResolverTests
{
    [Fact]
    public async Task Managed_enable_is_durable_and_disable_stops_existing_scoped_signing_without_replacing_identity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var database = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
        var tenant = Substitute.For<ITenantContext>(); tenant.IsHelpdeskAdmin.Returns(true);
        await using var first = new HelpdeskDbContext(database, tenant, new HttpContextAccessor());
        await first.Database.EnsureCreatedAsync();
        var installedInstance = Guid.NewGuid();
        first.InstanceInitializations.Add(new() { InstanceId = installedInstance, OperationId = Guid.NewGuid(), CompletedAtUtc = DateTimeOffset.UtcNow });
        await first.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicWebAppUrl"] = "https://desk.example.test", ["StorageOptions:PublicApiBaseUrl"] = "https://desk-api.example.test"
        }).Build();
        var identity = Substitute.For<IOptionsMonitor<ServiceIdentityOptions>>(); identity.CurrentValue.Returns(new ServiceIdentityOptions());
        var linking = Substitute.For<IOptionsMonitor<ServiceLinkOptions>>(); linking.CurrentValue.Returns(new ServiceLinkOptions());
        var settings = new ServicePublicSettingsResolver(first, configuration, identity, linking, TimeProvider.System);
        var initial = await settings.ResolveAsync();
        Assert.False(initial.Identity.Enabled); Assert.Equal(0, initial.Revision);
        var enabled = await settings.UpdateAsync(Update(initial, true), "authenticated-administrator");
        Assert.True(enabled.Identity.Enabled); Assert.True(enabled.Linking.Enabled);
        Assert.True(Guid.TryParse(enabled.Identity.InstanceId, out _));
        Assert.Equal(installedInstance.ToString("D"), enabled.Identity.InstanceId);
        Assert.Equal("https://desk-api.example.test/services", enabled.Identity.Issuer);
        var protection = new EphemeralDataProtectionProvider();
        var signing = new ServiceSigningKeyStore(first, protection, identity, TimeProvider.System, settings);
        string originalKid;
        using (var key = await signing.GetSigningKeyAsync()) originalKid = key.Key.KeyId;

        await using var second = new HelpdeskDbContext(database, tenant, new HttpContextAccessor());
        var otherReplica = new ServicePublicSettingsResolver(second, configuration, identity, linking, TimeProvider.System);
        var durable = await otherReplica.ResolveAsync();
        Assert.Equal(enabled.Identity.InstanceId, durable.Identity.InstanceId);
        await otherReplica.UpdateAsync(Update(durable, false), "authenticated-administrator");
        Assert.Empty(await signing.GetValidationKeysAsync());
        await Assert.ThrowsAsync<ServiceSigningKeyUnavailableException>(() => signing.GetSigningKeyAsync());
        var registry = new ServicePrincipalRegistry(first, identity, TimeProvider.System, linking, settings);
        Assert.Null(await registry.AuthenticateClientAsync("unavailable-client", new string('x', 32)));
        var disabled = await otherReplica.ResolveAsync();
        var restored = await otherReplica.UpdateAsync(Update(disabled, true), "authenticated-administrator");
        Assert.Equal(enabled.Identity.InstanceId, restored.Identity.InstanceId);
        using var resumed = await signing.GetSigningKeyAsync();
        Assert.Equal(originalKid, resumed.Key.KeyId);
        await Assert.ThrowsAsync<ServiceClientConflictException>(() => otherReplica.UpdateAsync(
            Update(restored, true) with { Issuer = "https://replacement.example.test/services" }, "authenticated-administrator"));
    }

    [Fact]
    public async Task Deployment_locks_and_stale_setup_revision_are_rejected_with_actionable_results()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var database = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
        var tenant = Substitute.For<ITenantContext>(); tenant.IsHelpdeskAdmin.Returns(true);
        await using var db = new HelpdeskDbContext(database, tenant, new HttpContextAccessor()); await db.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceIdentity:Enabled"] = "false", ["PublicWebAppUrl"] = "https://desk.example.test",
            ["StorageOptions:PublicApiBaseUrl"] = "https://desk-api.example.test"
        }).Build();
        var identity = Substitute.For<IOptionsMonitor<ServiceIdentityOptions>>(); identity.CurrentValue.Returns(new ServiceIdentityOptions());
        var linking = Substitute.For<IOptionsMonitor<ServiceLinkOptions>>(); linking.CurrentValue.Returns(new ServiceLinkOptions());
        var settings = new ServicePublicSettingsResolver(db, configuration, identity, linking, TimeProvider.System);
        var locked = await settings.ResolveAsync();
        Assert.Contains("enabled", locked.LockedFields);
        var rejected = await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(Update(locked, true), "administrator"));
        Assert.Contains("deployment", rejected.Message);
        Assert.Contains(locked.ToDto().Readiness, message => message.Contains("ServiceIdentity__Enabled", StringComparison.Ordinal));
        configuration["ServiceIdentity:Enabled"] = null;
        var enabled = await settings.UpdateAsync(Update(await settings.ResolveAsync(), true), "administrator");
        await Assert.ThrowsAsync<ServiceClientConflictException>(() => settings.UpdateAsync(Update(enabled, false) with { ExpectedRevision = 0 }, "administrator"));
        Assert.True((await settings.ResolveAsync()).Identity.Enabled);
    }

    private static ServicePublicSettingsUpdate Update(ServicePublicSettingsEffective settings, bool enabled) =>
        new(settings.Revision, enabled, settings.Identity.WebBaseUrl, settings.Identity.ApiBaseUrl, settings.Identity.Issuer, settings.Identity.Audience);
}
