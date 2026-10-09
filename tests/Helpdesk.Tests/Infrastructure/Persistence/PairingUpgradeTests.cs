using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Infrastructure.Persistence;

public sealed class PairingUpgradeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_install_has_only_pairing_setup_and_all_current_migrations(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        await using var db = fixture.Open();
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(await db.Set<SystemPair>().ToListAsync());
        Assert.Empty(await db.Set<SystemConnection>().ToListAsync());
        Assert.Empty(await db.Set<InstallationPairingCode>().ToListAsync());
        Assert.Empty(await db.Set<PairingRedemption>().ToListAsync());
        Assert.Empty(await db.Set<PairingCleanup>().ToListAsync());
        await AssertOldTablesAbsentAsync(db, postgres);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forward_cutover_retires_active_pending_failed_and_malformed_state_and_preserves_unrelated_authority_and_history(bool postgres)
    {
        await using var fixture = await ServiceIdentityMigrationTests.DatabaseFixture.CreateAsync(postgres);
        var now = DateTimeOffset.UtcNow;
        var installation = Guid.NewGuid();
        var publishedInstallation = Guid.NewGuid();
        var apiCredential = Guid.NewGuid();
        var apiNamespace = Guid.NewGuid();
        var apiProducer = Guid.NewGuid();
        var oldNamespace = Guid.NewGuid();
        var oldProducer = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        const string accepted = "{\"id\":\"preserved-incident\",\"trackingId\":\"INC-PRESERVED\"}";
        var protection = fixture.Protection();
        var protector = protection.CreateProtector("PairingUpgradePreservation");
        var netclawToken = protector.Protect("synthetic-unrelated-netclaw-token");
        var signingKey = protector.Protect("synthetic-preserved-signing-material");
        var oldPrincipals = new List<Guid>();
        string[] previousHistory;

        await using (var identity = fixture.Identity())
        {
            await identity.Database.MigrateAsync();
            identity.Users.Add(new() { Id = "preserved-owner", UserName = "preserved-owner", Email = "owner@example.test", IsEnabled = true, IsInstanceAdministrator = true });
            identity.IntegrationCredentials.Add(new() { Id = apiCredential, OwnerUserId = "preserved-owner", Name = "Unrelated API", Prefix = "rdk_preserved",
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-unrelated-api-secret"))), Purpose = "api", OrganizationId = "preserved-org",
                Permissions = "Incident.Read Incident.Write", CreatedAtUtc = now, CreatedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(), ExpiresAtUtc = now.AddDays(30) });
            await identity.SaveChangesAsync();
        }
        await using (var db = fixture.Open())
        {
            var baseline = db.Database.GetMigrations().Single(x => x.EndsWith("_AddManagedServiceIdentitySettings", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(baseline);
            db.Organizations.Add(new() { Id = "preserved-org", Name = "Existing organization" });
            db.Customers.Add(new() { Id = "preserved-customer", OrganizationId = "preserved-org", Name = "Existing customer", Email = "customer@example.test" });
            db.InstanceInitializations.Add(new() { InstanceId = installation, OperationId = Guid.NewGuid(), SetupVersion = "existing", CompletedAtUtc = now });
            db.Set<ServiceIdentityConfiguration>().Add(new() { InstanceId = publishedInstallation.ToString("D"), Enabled = true,
                WebBaseUrl = "https://desk.example.test", ApiBaseUrl = "https://api.example.test", Issuer = "https://existing.example/services" });
            db.Incidents.Add(new() { Id = "preserved-incident", TrackingId = "INC-PRESERVED", Title = "Existing incident", OrganizationId = "preserved-org", CustomerId = "preserved-customer" });
            db.Requests.Add(new() { Id = "preserved-request", TrackingId = "REQ-PRESERVED", Title = "Existing request", OrganizationId = "preserved-org", CustomerId = "preserved-customer" });
            db.RequestTasks.Add(new() { Id = "preserved-task", TrackingId = "TASK-PRESERVED", RequestId = "preserved-request", OrganizationId = "preserved-org", OrchestrationExternalRunId = "original-run" });
            db.Set<NetclawConnectivitySettings>().Add(new() { Enabled = true, ProtectedDeviceToken = netclawToken, ProfileFingerprint = "unchanged-netclaw", Revision = 7 });
            db.Set<ServiceSigningKey>().Add(new() { Kid = "preserved-signing-key", Issuer = "https://existing.example/services", ProtectedPrivateKey = signingKey,
                PublicModulus = "unchanged-modulus", PublicExponent = "AQAB", ActiveSlot = 1, CreatedAtUtc = now });
            foreach (var source in new[] { (apiNamespace, apiProducer), (oldNamespace, oldProducer) })
                db.IncidentReceiverSources.Add(new() { SourceNamespaceId = source.Item1, SourceInstanceId = source.Item2, OrganizationId = "preserved-org", CustomerId = "preserved-customer",
                    CreatedBy = "preserved-owner", UpdatedBy = "preserved-owner", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.IncidentReceiverPrincipalBindings.Add(new() { SourceNamespaceId = apiNamespace, PrincipalKind = "api_credential", PrincipalId = apiCredential.ToString("N"), IsEnabled = true });
            db.IncidentCreateReceipts.Add(new() { Id = receiptId, SourceNamespaceId = oldNamespace, Key = "original-key", Fingerprint = new string('a', 64), IncidentId = "preserved-incident",
                OrganizationId = "preserved-org", CustomerId = "preserved-customer", Location = "api/v1/incidents/preserved-incident", AcceptedJson = accepted, CommittedAtUtc = now });
            await db.SaveChangesAsync();

            // Seed the actual shipped schema. No discarded runtime or parser is needed to upgrade malformed state.
            foreach (var status in new[] { "active", "pending", "failed", "verified" })
            {
                var principal = Guid.NewGuid(); oldPrincipals.Add(principal);
                await InsertHistoricalAsync(db, "ServicePrincipalRegistrations", new()
                {
                    ["Id"] = principal, ["ClientId"] = "old-" + status, ["NormalizedClientId"] = ("old-" + status).ToUpperInvariant(),
                    ["Name"] = "Old " + status, ["OrganizationId"] = "preserved-org", ["PeerInstanceId"] = oldProducer.ToString("D"), ["PeerTenantId"] = "17",
                    ["LinkId"] = "legacy-" + status, ["DirectionId"] = "netratel-to-rateldesk",
                    ["Status"] = status, ["SourceInstanceId"] = oldProducer, ["SourceNamespaceId"] = oldNamespace, ["CreatedBy"] = "preserved-owner", ["ApprovedBy"] = "preserved-owner",
                    ["AllowedScopesJson"] = "[\"rateldesk.incidents.create\"]", ["CustomerIdsJson"] = "[\"preserved-customer\"]", ["ResourceConstraintsJson"] = status == "verified" ? "{malformed" : "{}",
                    ["Revision"] = 1L, ["Version"] = 1L, ["CurrentCredentialRevision"] = 1L
                });
                await InsertHistoricalAsync(db, "ServicePrincipalSecrets", new() { ["ServicePrincipalId"] = principal, ["CredentialRevision"] = 1L,
                    ["Status"] = status == "pending" ? "pending" : "active", ["Salt"] = new string('b', 64), ["SecretHash"] = new string('c', 64), ["ExpiresAtUtc"] = now.AddDays(90) });
                await InsertHistoricalAsync(db, "ServiceLinkAttempts", new() { ["AttemptId"] = "old-" + status, ["Role"] = "initiator",
                    ["LifecycleState"] = status == "verified" ? "failed" : status, ["GrantSummaryJson"] = status == "verified" ? "{malformed" : "{}",
                    ["ProtectedOutboundCredential"] = "opaque-retired-secret", ["InboundPrincipalId"] = principal });
            }
            db.IncidentReceiverPrincipalBindings.Add(new() { SourceNamespaceId = oldNamespace, PrincipalKind = "service_principal", PrincipalId = oldPrincipals[0].ToString("N"), IsEnabled = true });
            await db.SaveChangesAsync();
            await InsertHistoricalAsync(db, "M2MConnectivitySettings", new() { ["Id"] = Guid.NewGuid(), ["ProviderKey"] = "Orchestrator", ["Enabled"] = true,
                ["ManagedSenderEnabled"] = true, ["ProtectedClientSecret"] = "opaque-retired-outbound-secret" });
            await InsertHistoricalAsync(db, "AutomationBindings", new() { ["Id"] = "old-binding", ["OrganizationId"] = "preserved-org", ["TaskTemplateId"] = Guid.NewGuid(), ["RequestFormId"] = "existing-form", ["Enabled"] = true });
            previousHistory = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            db.ChangeTracker.Clear();
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
        }
        await using (var db = fixture.Open())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(previousHistory, (await db.Database.GetAppliedMigrationsAsync()).Take(previousHistory.Length));
            Assert.Empty(await db.Set<SystemPair>().ToListAsync());
            Assert.Empty(await db.Set<SystemConnection>().ToListAsync());
            Assert.Empty(await db.Set<PairingRedemption>().ToListAsync());
            Assert.All(await db.Set<ServicePrincipalRegistration>().ToListAsync(), row => { Assert.Equal("revoked", row.Status); Assert.NotNull(row.RevokedAtUtc); Assert.Null(row.TerminalControlUntilUtc); Assert.Null(row.MappingId); });
            Assert.All(await db.Set<ServicePrincipalSecret>().ToListAsync(), row => Assert.Equal("revoked", row.Status));
            Assert.False((await db.IncidentReceiverSources.SingleAsync(x => x.SourceNamespaceId == oldNamespace)).IsEnabled);
            Assert.True((await db.IncidentReceiverSources.SingleAsync(x => x.SourceNamespaceId == apiNamespace)).IsEnabled);
            Assert.All(await db.IncidentReceiverPrincipalBindings.Where(x => x.PrincipalKind == "service_principal").ToListAsync(), row => Assert.False(row.IsEnabled));
            Assert.True((await db.IncidentReceiverPrincipalBindings.SingleAsync(x => x.PrincipalKind == "api_credential")).IsEnabled);
            Assert.False((await db.AutomationBindings.SingleAsync()).Enabled);
            Assert.Equal(installation, (await db.InstanceInitializations.SingleAsync()).InstanceId);
            Assert.Equal(publishedInstallation.ToString("D"), (await db.Set<ServiceIdentityConfiguration>().SingleAsync()).InstanceId);
            var effective = await new ServicePublicSettingsResolver(db, new ConfigurationBuilder().Build(), new FixedOptions(new())).ResolveAsync();
            Assert.Equal(publishedInstallation.ToString("D"), effective.Identity.InstanceId);
            Assert.Equal(oldProducer, (await db.IncidentReceiverSources.SingleAsync(x => x.SourceNamespaceId == oldNamespace)).SourceInstanceId);
            var receipt = await db.IncidentCreateReceipts.SingleAsync(x => x.Id == receiptId);
            Assert.Equal("original-key", receipt.Key); Assert.Equal(accepted, receipt.AcceptedJson);
            Assert.Equal("Existing incident", (await db.Incidents.SingleAsync()).Title);
            Assert.Equal("original-run", (await db.RequestTasks.SingleAsync()).OrchestrationExternalRunId);
            Assert.Equal(netclawToken, (await db.Set<NetclawConnectivitySettings>().SingleAsync()).ProtectedDeviceToken);
            Assert.Equal(signingKey, (await db.Set<ServiceSigningKey>().SingleAsync()).ProtectedPrivateKey);
            Assert.Equal("synthetic-preserved-signing-material", fixture.Protection().CreateProtector("PairingUpgradePreservation").Unprotect(signingKey));
            Assert.Equal("synthetic-unrelated-netclaw-token", fixture.Protection().CreateProtector("PairingUpgradePreservation").Unprotect(netclawToken));
            await AssertOldTablesAbsentAsync(db, postgres);
            // Distinct namespaces may now share the preserved producer without changing old receipts.
            db.IncidentReceiverSources.Add(new() { SourceNamespaceId = Guid.NewGuid(), SourceInstanceId = oldProducer, OrganizationId = "preserved-org", CustomerId = "preserved-customer",
                CreatedBy = "preserved-owner", UpdatedBy = "preserved-owner", CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.IncidentReceiverSources.CountAsync(x => x.SourceInstanceId == oldProducer));
        }
        await using (var identity = fixture.Identity())
        {
            var credential = await identity.IntegrationCredentials.SingleAsync(x => x.Id == apiCredential);
            Assert.Equal("api", credential.Purpose); Assert.Null(credential.RevokedAtUtc);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-unrelated-api-secret"))), credential.SecretHash);
            Assert.True((await identity.Users.SingleAsync(x => x.Id == "preserved-owner")).IsEnabled);
        }
    }

    private sealed class FixedOptions(ServiceIdentityOptions value) : IOptionsMonitor<ServiceIdentityOptions>
    {
        public ServiceIdentityOptions CurrentValue => value;
        public ServiceIdentityOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ServiceIdentityOptions, string?> listener) => null;
    }

    private static async Task AssertOldTablesAbsentAsync(HelpdeskDbContext db, bool postgres)
    {
        var connection = db.Database.GetDbConnection(); await db.Database.OpenConnectionAsync();
        foreach (var table in new[] { "ServiceLinkAttempts", "ServiceLinkOperations", "ServiceLinkRotations", "ServiceLinkVerificationReceipts", "M2MConnectivitySettings" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = postgres ? "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = @name"
                : "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = @name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "name"; parameter.Value = table; command.Parameters.Add(parameter);
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
    }

    private static async Task InsertHistoricalAsync(HelpdeskDbContext db, string table, Dictionary<string, object?> values)
    {
        await db.Database.OpenConnectionAsync(); var connection = db.Database.GetDbConnection();
        await using var schema = connection.CreateCommand(); schema.CommandText = $"SELECT * FROM \"{table}\" LIMIT 0";
        IReadOnlyList<DbColumn> columns;
        await using (var reader = await schema.ExecuteReaderAsync()) columns = reader.GetColumnSchema();
        Assert.All(values.Keys, name => Assert.Contains(columns, column => column.ColumnName == name));
        await using var insert = connection.CreateCommand(); var names = new List<string>(); var parameters = new List<string>();
        foreach (var column in columns)
        {
            var name = column.ColumnName!;
            if (name.EndsWith("SortTicks", StringComparison.Ordinal)) continue;
            if (column.IsAutoIncrement == true && !values.ContainsKey(name)) continue;
            object? value;
            if (!values.TryGetValue(name, out value) && column.AllowDBNull != true)
                value = name.EndsWith("AtUtc", StringComparison.Ordinal) ? DateTimeOffset.UtcNow : column.DataType == typeof(Guid) ? Guid.NewGuid()
                    : column.DataType == typeof(bool) ? false : column.DataType == typeof(int) ? 0 : column.DataType == typeof(long) ? 0L
                    : column.DataType == typeof(DateTime) ? DateTime.UtcNow : column.DataType == typeof(DateTimeOffset) ? DateTimeOffset.UtcNow : "";
            var parameter = insert.CreateParameter(); parameter.ParameterName = "p" + parameters.Count; parameter.Value = value ?? DBNull.Value;
            insert.Parameters.Add(parameter); names.Add('"' + name + '"'); parameters.Add("@" + parameter.ParameterName);
        }
        insert.CommandText = $"INSERT INTO \"{table}\" ({string.Join(',', names)}) VALUES ({string.Join(',', parameters)})";
        await insert.ExecuteNonQueryAsync();
    }
}
