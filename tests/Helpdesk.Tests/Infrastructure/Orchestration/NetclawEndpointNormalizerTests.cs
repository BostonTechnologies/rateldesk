using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Connectivity;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class NetclawEndpointNormalizerTests
{
    private static readonly string[] AcceptedPaths = ["", "/", "/hub/session", "/hub/session/"];

    public static IEnumerable<object[]> AcceptedEndpointVariants()
    {
        var authorities = new (string Authority, bool AllowPrivateHttp)[]
        {
            ("http://10.23.45.67:5199", true),
            ("http://[fd12:3456:789a::42]:5199", true),
            ("https://netclaw.example.test:7443", false)
        };

        foreach (var (authority, allowPrivateHttp) in authorities)
        foreach (var path in AcceptedPaths)
            yield return [authority + path, allowPrivateHttp, authority + NetclawEndpointNormalizer.SessionPath];
    }

    [Theory]
    [MemberData(nameof(AcceptedEndpointVariants))]
    public void Accepted_daemon_routes_share_the_canonical_session_and_pairing_endpoints(
        string daemonAddress,
        bool allowPrivateHttp,
        string expectedSessionEndpoint)
    {
        var normalized = NetclawEndpointNormalizer.Normalize(daemonAddress, "Endpoint", allowPrivateHttp);
        Assert.NotNull(normalized);

        Assert.Equal(expectedSessionEndpoint, normalized);
        Assert.Equal(new Uri(expectedSessionEndpoint).GetLeftPart(UriPartial.Authority),
            NetclawEndpointNormalizer.ToDaemonAddress(normalized));
        Assert.Equal(
            new Uri(new Uri(expectedSessionEndpoint).GetLeftPart(UriPartial.Authority) + NetclawEndpointNormalizer.PairingExchangePath),
            NetclawEndpointNormalizer.BuildPairingExchangeEndpoint(new Uri(normalized!), allowPrivateHttp));
    }

    [Theory]
    [InlineData("http://8.8.8.8:5199", true)]
    [InlineData("http://netclaw.example.test:5199", true)]
    [InlineData("http://10.23.45.67:5199", false)]
    [InlineData("https://user:password@netclaw.example.test", false)]
    [InlineData("https://netclaw.example.test/?unexpected=value", false)]
    [InlineData("https://netclaw.example.test/#fragment", false)]
    [InlineData("https://netclaw.example.test/another-service", false)]
    [InlineData("https://metadata.google.internal", false)]
    [InlineData("http://169.254.169.254", true)]
    [InlineData("https://[fe80::1]", false)]
    [InlineData("https://224.0.0.1", false)]
    [InlineData("https://[ff02::1]", false)]
    [InlineData("https://0.0.0.0", false)]
    [InlineData("https://[::]", false)]
    public void Unsafe_or_noncanonical_addresses_are_rejected(string address, bool allowPrivateHttp)
    {
        Assert.Throws<ArgumentException>(() =>
            NetclawEndpointNormalizer.Normalize(address, "Endpoint", allowPrivateHttp));
    }

    [Fact]
    public async Task Runtime_snapshot_fingerprints_match_across_equivalent_routes()
    {
        await using var fixture = await SettingsFixture.CreateAsync();
        var service = fixture.CreateService();
        var authorities = new (string Authority, bool AllowPrivateHttp)[]
        {
            ("http://10.23.45.67:5199", true),
            ("http://[fd12:3456:789a::42]:5199", true),
            ("https://netclaw.example.test:7443", false)
        };

        var expectedRevision = 0;
        string? previousAuthorityFingerprint = null;
        foreach (var (authority, allowPrivateHttp) in authorities)
        {
            string? expectedFingerprint = null;
            foreach (var path in AcceptedPaths)
            {
                var draft = await service.ResolveNetclawDraftAsync(new UpdateNetclawConnectivitySettingsDto
                {
                    ExpectedRevision = expectedRevision,
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = authority + path,
                    DeviceToken = "synthetic-runtime-token",
                    AllowPrivateHttp = allowPrivateHttp
                });
                var runtime = AiAssistantChatRuntimeSnapshot.From(draft);
                var initialRuntime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions
                {
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = authority + path,
                    DeviceToken = "synthetic-runtime-token",
                    AllowPrivateHttp = allowPrivateHttp
                })).Current;

                Assert.Equal(authority + NetclawEndpointNormalizer.SessionPath, runtime.Endpoint);
                Assert.Equal(draft.ProfileFingerprint, runtime.ProfileFingerprint);
                Assert.True(initialRuntime.Enabled);
                Assert.Equal(runtime.Endpoint, initialRuntime.Endpoint);
                Assert.Equal(runtime.ProfileFingerprint, initialRuntime.ProfileFingerprint);
                if (expectedFingerprint is null)
                    expectedFingerprint = runtime.ProfileFingerprint;
                else
                    Assert.Equal(expectedFingerprint, runtime.ProfileFingerprint);

                var saved = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
                {
                    ExpectedRevision = expectedRevision,
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = authority + path,
                    DeviceToken = "synthetic-runtime-token",
                    AllowPrivateHttp = allowPrivateHttp
                });
                expectedRevision = saved.Revision;
                var canonicalEndpoint = authority + NetclawEndpointNormalizer.SessionPath;
                Assert.Equal(canonicalEndpoint, saved.Endpoint);
                Assert.Equal(expectedFingerprint, saved.ProfileFingerprint);

                var persisted = await fixture.Db.NetclawConnectivitySettings.AsNoTracking().SingleAsync();
                Assert.Equal(expectedRevision, persisted.Revision);
                Assert.Equal(canonicalEndpoint, persisted.Endpoint);
                Assert.Equal(saved.ProfileFingerprint, persisted.ProfileFingerprint);

                var savedRuntime = AiAssistantChatRuntimeSnapshot.From(await service.GetResolvedNetclawSettingsAsync());
                Assert.Equal(canonicalEndpoint, savedRuntime.Endpoint);
                Assert.Equal(saved.ProfileFingerprint, savedRuntime.ProfileFingerprint);
            }

            if (previousAuthorityFingerprint is null)
                previousAuthorityFingerprint = expectedFingerprint;
            else
                Assert.NotEqual(previousAuthorityFingerprint, expectedFingerprint);
        }
    }

    private sealed class SettingsFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly HelpdeskDbContext db;

        private SettingsFixture(SqliteConnection connection, HelpdeskDbContext db)
        {
            this.connection = connection;
            this.db = db;
        }

        public static async Task<SettingsFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
            var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await db.Database.EnsureCreatedAsync();
            return new SettingsFixture(connection, db);
        }

        public IntegrationProviderSettingsService CreateService()
            => new(
                db,
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                Options.Create(new AiAssistantChatOptions()),
                new DatabaseOptions { Provider = "Sqlite" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance);

        public HelpdeskDbContext Db => db;

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
