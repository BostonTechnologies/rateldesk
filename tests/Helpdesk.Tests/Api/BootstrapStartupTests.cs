using Helpdesk.API.Bootstrap;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Helpdesk.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;

namespace Helpdesk.Tests.Api;

public sealed class BootstrapStartupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"rateldesk-startup-{Guid.NewGuid():N}");

    [Fact]
    public async Task Setup_unlock_rejects_bad_codes_with_401_and_the_sixth_attempt_with_429()
    {
        var (_, store, options) = Create();
        await store.LoadOrCreateAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IBootstrapStateStore>(store);
        builder.Services.AddSingleton<BootstrapSessionService>();
        builder.Services.AddSingleton<BootstrapInitializationService>();
        builder.Services.AddSingleton<PostgreSqlSetupPreflightService>();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddRateLimiter(BootstrapEndpoints.ConfigureRateLimiting);
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.MapBootstrapEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var rejected = await client.PostAsJsonAsync("/api/v1/setup/session", new { setupCode = "incorrect-test-code" });
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync("/api/v1/setup/session", new { setupCode = "incorrect-test-code" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(BootstrapState.Unconfigured, (await store.LoadOrCreateAsync()).State);
    }

    [Theory]
    [InlineData(BootstrapState.Unconfigured, "Unconfigured")]
    [InlineData(BootstrapState.Configuring, "Configuring")]
    [InlineData(BootstrapState.Ready, "Restarting")]
    [InlineData(BootstrapState.RecoveryRequired, "RecoveryRequired")]
    public async Task Bootstrap_only_status_does_not_advertise_application_readiness(BootstrapState state, string expected)
    {
        var (_, store, options) = Create();
        await store.LoadOrCreateAsync();
        await store.UpdateAsync(current => current with { State = state });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IBootstrapStateStore>(store);
        builder.Services.AddSingleton<BootstrapSessionService>();
        builder.Services.AddSingleton<BootstrapInitializationService>();
        builder.Services.AddSingleton<PostgreSqlSetupPreflightService>();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        await using var app = builder.Build();
        app.MapBootstrapEndpoints();
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/api/v1/setup/status");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("state").GetString());
        Assert.Equal(state, (await store.LoadOrCreateAsync()).State);
    }

    [Fact]
    public async Task Fresh_sqlite_configuration_requires_setup_without_creating_database()
    {
        var (service, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "configured.db");
        var result = await service.ResolveAsync(Config(("Database:Sqlite:Path", path)));
        Assert.Equal(BootstrapState.Configuring, result.State);
        Assert.Equal(path, result.SqlitePath);
        Assert.False(File.Exists(path));
        Assert.Null(result.CompletedAtUtc);
        Assert.Equal(result, await store.LoadOrCreateAsync());
    }

    [Fact]
    public async Task Missing_completed_sqlite_requires_recovery_and_is_not_recreated()
    {
        var (service, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "missing.db");
        await ConfigureAsync(store, path, BootstrapState.Ready);
        Assert.Equal(BootstrapState.RecoveryRequired, (await service.ResolveAsync(Config())).State);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(BootstrapState.Ready)]
    [InlineData(BootstrapState.Configuring)]
    [InlineData(BootstrapState.RecoveryRequired)]
    public async Task Matching_marker_restores_ready_including_interrupted_commit(BootstrapState state)
    {
        var (service, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "rateldesk.db");
        var configured = await ConfigureAsync(store, path, state);
        await WriteMarkerAsync(path, configured.InstanceId, configured.OperationId!.Value);
        var result = await service.ResolveAsync(Config());
        Assert.Equal(BootstrapState.Ready, result.State);
        Assert.Equal(configured.InstanceId, result.InstanceId);
        Assert.NotNull(result.CompletedAtUtc);
    }

    [Fact]
    public async Task Wrong_database_or_lost_descriptor_does_not_reopen_setup()
    {
        var (service, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "rateldesk.db");
        await ConfigureAsync(store, path, BootstrapState.Ready);
        await WriteMarkerAsync(path, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(BootstrapState.RecoveryRequired, (await service.ResolveAsync(Config())).State);
        File.Delete(Path.Combine(options.StateDirectory, "descriptor.json"));
        Assert.Equal(BootstrapState.RecoveryRequired, (await service.ResolveAsync(Config())).State);
    }

    [Fact]
    public async Task Empty_schema_is_not_readiness_evidence_for_completed_descriptor()
    {
        var (service, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "rateldesk.db");
        await ConfigureAsync(store, path, BootstrapState.Ready);
        Directory.CreateDirectory(options.DataDirectory);
        await using (var db = new SqliteConnection($"Data Source={path}")) await db.OpenAsync();
        Assert.Equal(BootstrapState.RecoveryRequired, (await service.ResolveAsync(Config())).State);
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Fresh_environment_postgresql_requires_setup_and_restarts_with_exact_committed_identity()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16").Build();
        await postgres.StartAsync();
        var (service, store, options) = Create();
        var config = Config(("ConnectionStrings:HelpdeskDb", postgres.GetConnectionString()));
        var configured = await service.ResolveAsync(config);
        Assert.Equal(BootstrapState.Configuring, configured.State);
        Assert.Equal("PostgreSql", configured.Provider);
        Assert.Null(configured.CompletedAtUtc);
        Assert.Equal(configured, await service.ResolveAsync(config));
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(options.StateDirectory, "keys")));
        var initialized = await new BootstrapInitializationService(store, options, protection)
            .InitializeAsync(configured, ValidAdministrator(), CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Error);
        Assert.Equal(initialized.Descriptor, await service.ResolveAsync(config));
        Assert.Equal(initialized.Descriptor, await service.ResolveAsync(Config()));
        await store.UpdateAsync(current => current with { State = BootstrapState.Configuring, CompletedAtUtc = null });
        Assert.Equal(initialized.Descriptor, await service.ResolveAsync(Config()));
        await using var identity = new RatelDeskIdentityDbContext(
            new DbContextOptionsBuilder<RatelDeskIdentityDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        Assert.Single(await identity.Users.Where(user => user.IsInstanceAdministrator).ToListAsync());

        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE wrong_target";
            await command.ExecuteNonQueryAsync();
        }
        var wrongTarget = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "wrong_target" }.ToString();
        Assert.Equal(BootstrapState.RecoveryRequired,
            (await service.ResolveAsync(Config(("ConnectionStrings:HelpdeskDb", wrongTarget)))).State);
        await using (var connection = new NpgsqlConnection(wrongTarget))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task Missing_key_ring_enters_recovery_even_when_the_database_marker_matches()
    {
        var (service, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "rateldesk.db");
        var configured = await service.ResolveAsync(Config(("Database:Sqlite:Path", path)));
        await WriteMarkerAsync(path, configured.InstanceId, configured.OperationId!.Value);
        var ready = await service.ResolveAsync(Config());
        Assert.Equal(BootstrapState.Ready, ready.State);
        Assert.NotNull(ready.ProtectedKeyRingProof);

        var replacementKeys = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "replacement-keys")));
        var restarted = new BootstrapStartupService(new FileBootstrapStateStore(options), options, replacementKeys);
        Assert.Equal(BootstrapState.RecoveryRequired, (await restarted.ResolveAsync(Config())).State);
        Assert.Equal(ready, await service.ResolveAsync(Config()));
    }

    [Fact]
    public async Task Existing_descriptor_without_proof_requires_a_surviving_key_ring()
    {
        var (service, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "rateldesk.db");
        var configured = await ConfigureAsync(store, path, BootstrapState.Ready);
        await WriteMarkerAsync(path, configured.InstanceId, configured.OperationId!.Value);
        Directory.Delete(Path.Combine(options.StateDirectory, "keys"), recursive: true);
        Assert.Equal(BootstrapState.RecoveryRequired, (await service.ResolveAsync(Config())).State);
        Assert.Null((await store.LoadOrCreateAsync()).ProtectedKeyRingProof);
    }

    [Fact]
    public async Task Verified_sqlite_relocation_updates_the_descriptor_used_by_runtime()
    {
        var (service, store, options) = Create();
        var firstPath = Path.Combine(options.DataDirectory, "first.db");
        var secondPath = Path.Combine(options.DataDirectory, "second.db");
        var configured = await ConfigureAsync(store, firstPath, BootstrapState.Ready);
        await WriteMarkerAsync(firstPath, configured.InstanceId, configured.OperationId!.Value);
        File.Copy(firstPath, secondPath);
        var resolved = await service.ResolveAsync(Config(("Database:Sqlite:Path", secondPath)));
        Assert.Equal(BootstrapState.Ready, resolved.State);
        Assert.Equal(secondPath, resolved.SqlitePath);
        Assert.Equal(resolved, await service.ResolveAsync(Config()));
    }

    [Fact]
    public async Task Stale_initialization_cannot_resume_recovery_or_a_reselected_target()
    {
        var (_, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "original.db");
        var configured = await ConfigureAsync(store, path, BootstrapState.Configuring);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(options.StateDirectory, "keys")));
        var initializer = new BootstrapInitializationService(store, options, protection);
        await store.UpdateAsync(current => current with { State = BootstrapState.RecoveryRequired });
        Assert.Equal(BootstrapInitializationResult.InvalidState,
            await initializer.InitializeAsync(configured, ValidAdministrator(), CancellationToken.None));
        await store.UpdateAsync(current => current with { State = BootstrapState.Configuring, OperationId = Guid.NewGuid(), SqlitePath = Path.Combine(options.DataDirectory, "other.db") });
        Assert.Equal(BootstrapInitializationResult.InvalidState,
            await initializer.InitializeAsync(configured, ValidAdministrator(), CancellationToken.None));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(options.DataDirectory, "other.db")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://admin:password@desk.example.test")]
    [InlineData("https://desk.example.test/?access_token=value")]
    [InlineData("https://desk.example.test/#section")]
    public async Task Canonical_public_url_is_required_and_cannot_contain_credentials_or_navigation_suffixes(string? url)
    {
        var (_, store, options) = Create();
        var path = Path.Combine(options.DataDirectory, "invalid-url.db");
        var configured = await ConfigureAsync(store, path, BootstrapState.Configuring);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(options.StateDirectory, "keys")));
        var result = await new BootstrapInitializationService(store, options, protection)
            .InitializeAsync(configured, ValidAdministrator() with { ApplicationUrl = url }, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("setup_validation_failed", result.Code);
        Assert.Null(result.Descriptor);
        var error = Assert.Single(result.Errors!);
        Assert.Equal("applicationUrl", error.Key);
        Assert.NotEmpty(Assert.Single(error.Value));
        Assert.Equal(BootstrapState.Configuring, (await store.LoadOrCreateAsync()).State);
        Assert.False(File.Exists(path));
    }

    private static FirstAdministratorRequest ValidAdministrator() =>
        new("admin@example.test", "Administrator", "correct horse battery staple", "Example", "Example Desk", "https://desk.example.test");

    private (BootstrapStartupService Service, FileBootstrapStateStore Store, BootstrapOptions Options) Create()
    {
        var options = new BootstrapOptions { StateDirectory = Path.Combine(directory, "state"), DataDirectory = Path.Combine(directory, "data") };
        var store = new FileBootstrapStateStore(options);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(options.StateDirectory, "keys")));
        _ = protection.CreateProtector("startup-test-key").Protect("existing key ring");
        return (new BootstrapStartupService(store, options, protection), store, options);
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    private static async Task<BootstrapDescriptor> ConfigureAsync(FileBootstrapStateStore store, string path, BootstrapState state)
    {
        await store.LoadOrCreateAsync();
        return await store.UpdateAsync(current => current with
        {
            State = state, Provider = "Sqlite", SqlitePath = path, OperationId = Guid.NewGuid()
        });
    }

    private static async Task WriteMarkerAsync(string path, Guid instance, Guid operation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var db = new SqliteConnection($"Data Source={path}");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE InstanceInitializations (InstanceId TEXT, OperationId TEXT, CompletedAtUtc TEXT); INSERT INTO InstanceInitializations VALUES ($instance, $operation, $date)";
        command.Parameters.AddWithValue("$instance", instance.ToString());
        command.Parameters.AddWithValue("$operation", operation.ToString());
        command.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
