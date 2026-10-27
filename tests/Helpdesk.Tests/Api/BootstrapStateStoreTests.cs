using Helpdesk.API.Bootstrap;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Testcontainers.PostgreSql;
using Helpdesk.Shared.Models;

namespace Helpdesk.Tests.Api;

public sealed class BootstrapStateStoreTests
{
    [Fact]
    public void Bootstrap_managed_image_signing_secret_is_protected_and_survives_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-image-signing-{Guid.NewGuid():N}");
        try
        {
            var options = new BootstrapOptions { StateDirectory = directory };
            var keyRingPath = Path.Combine(directory, "keys");

            var generated = BootstrapRuntimeSecretStore.GetOrCreateImageSigningSecret(
                options,
                keyRingPath,
                "RatelDesk-Test-Keyring");
            var persisted = File.ReadAllText(Path.Combine(directory, "image-signing-secret"));
            var restored = BootstrapRuntimeSecretStore.GetOrCreateImageSigningSecret(
                options,
                keyRingPath,
                "RatelDesk-Test-Keyring");

            Assert.NotEmpty(generated);
            Assert.NotEqual(generated, persisted);
            Assert.Equal(generated, restored);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PostgreSql_preflight_rejects_a_missing_connection_without_attempting_setup()
    {
        var preflight = new PostgreSqlSetupPreflightService();

        var result = await preflight.CheckAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("connection string", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, false, false, false, false, PostgreSqlTargetKind.Empty, true)]
    [InlineData(4, true, true, true, true, PostgreSqlTargetKind.EstablishedRatelDesk, false)]
    [InlineData(1, false, false, false, false, PostgreSqlTargetKind.UnrelatedOrUnrecognized, false)]
    public void PostgreSql_preflight_classifies_empty_established_and_unrelated_targets(
        long publicTableCount,
        bool hasMigrationHistory,
        bool hasOrganizationsTable,
        bool hasUsersTable,
        bool hasInitialRatelDeskMigration,
        PostgreSqlTargetKind expectedTarget,
        bool expectedSuccess)
    {
        var result = PostgreSqlSetupPreflightService.Classify(
            publicTableCount,
            hasMigrationHistory,
            hasOrganizationsTable,
            hasUsersTable,
            hasInitialRatelDeskMigration);

        Assert.Equal(expectedSuccess, result.Succeeded);
        Assert.Equal(expectedTarget, result.Target);
    }

    [Fact]
    public async Task Local_admin_recovery_does_not_create_a_missing_sqlite_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-recovery-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "missing.db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:Sqlite:Path"] = databasePath,
                ["DataProtection:KeyRingPath"] = Path.Combine(directory, "keys")
            }).Build();

            var token = await LocalAdminRecoveryCommand.GenerateActivationTokenAsync(configuration, "admin@example.test");

            Assert.Null(token);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Local_admin_recovery_reenables_an_instance_administrator_and_mints_a_single_use_activation_token()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-recovery-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:Sqlite:Path"] = Path.Combine(directory, "rateldesk.db"),
                ["DataProtection:KeyRingPath"] = Path.Combine(directory, "keys")
            }).Build();

            await using (var provider = CreateRecoveryServiceProvider(configuration))
            await using (var scope = provider.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                await identity.Database.MigrateAsync();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var result = await users.CreateAsync(new ApplicationUser
                {
                    UserName = "admin@example.test",
                    Email = "admin@example.test",
                    DisplayName = "Recovered administrator",
                    IsEnabled = false,
                    DisabledAtUtc = DateTimeOffset.UtcNow,
                    IsInstanceAdministrator = true,
                    AuthorizationRevision = 41
                }, "correct horse battery staple");
                Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
            }

            var token = await LocalAdminRecoveryCommand.GenerateActivationTokenAsync(configuration, "admin@example.test");

            Assert.False(string.IsNullOrWhiteSpace(token));
            await using var recoveryProvider = CreateRecoveryServiceProvider(configuration);
            await using var recoveryScope = recoveryProvider.CreateAsyncScope();
            var recoveryUsers = recoveryScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var administrator = await recoveryUsers.FindByEmailAsync("admin@example.test");
            Assert.NotNull(administrator);
            Assert.True(administrator.IsEnabled);
            Assert.Null(administrator.DisabledAtUtc);
            Assert.Equal(42, administrator.AuthorizationRevision);

            var reset = await recoveryUsers.ResetPasswordAsync(administrator, token!, "another secure passphrase");
            var replay = await recoveryUsers.ResetPasswordAsync(administrator, token, "a different secure passphrase");

            Assert.True(reset.Succeeded, string.Join(", ", reset.Errors.Select(error => error.Description)));
            Assert.False(replay.Succeeded);
            Assert.True(await recoveryUsers.CheckPasswordAsync(administrator, "another secure passphrase"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Local_admin_recovery_returns_no_token_when_a_postgresql_identity_store_is_unavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-recovery-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql",
                ["ConnectionStrings:HelpdeskDb"] = "Host=127.0.0.1;Port=1;Database=rateldesk;Username=operator;Password=not-used;Timeout=1;Command Timeout=1;Pooling=false",
                ["DataProtection:KeyRingPath"] = Path.Combine(directory, "keys")
            }).Build();

            Assert.Null(await LocalAdminRecoveryCommand.GenerateActivationTokenAsync(configuration, "admin@example.test"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Local_admin_recovery_reenables_an_instance_administrator_in_postgresql()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16")
            .Build();
        await postgres.StartAsync();

        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-recovery-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "PostgreSql",
                ["ConnectionStrings:HelpdeskDb"] = postgres.GetConnectionString(),
                ["DataProtection:KeyRingPath"] = Path.Combine(directory, "keys")
            }).Build();

            await using (var provider = CreateRecoveryServiceProvider(configuration))
            await using (var scope = provider.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                await identity.Database.MigrateAsync();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var result = await users.CreateAsync(new ApplicationUser
                {
                    UserName = "admin@example.test",
                    Email = "admin@example.test",
                    DisplayName = "Recovered administrator",
                    IsEnabled = false,
                    DisabledAtUtc = DateTimeOffset.UtcNow,
                    IsInstanceAdministrator = true,
                    AuthorizationRevision = 7
                }, "correct horse battery staple");
                Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
            }

            var token = await LocalAdminRecoveryCommand.GenerateActivationTokenAsync(configuration, "admin@example.test");

            Assert.False(string.IsNullOrWhiteSpace(token));
            await using var recoveryProvider = CreateRecoveryServiceProvider(configuration);
            await using var recoveryScope = recoveryProvider.CreateAsyncScope();
            var recoveryUsers = recoveryScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var administrator = await recoveryUsers.FindByEmailAsync("admin@example.test");
            Assert.NotNull(administrator);
            Assert.True(administrator.IsEnabled);
            Assert.Null(administrator.DisabledAtUtc);
            Assert.Equal(8, administrator.AuthorizationRevision);

            var reset = await recoveryUsers.ResetPasswordAsync(administrator, token!, "another secure passphrase");

            Assert.True(reset.Succeeded, string.Join(", ", reset.Errors.Select(error => error.Description)));
            Assert.True(await recoveryUsers.CheckPasswordAsync(administrator, "another secure passphrase"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Generated_operator_code_unlocks_a_new_unconfigured_descriptor()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        try
        {
            var store = new FileBootstrapStateStore(new BootstrapOptions { StateDirectory = directory });
            var descriptor = await store.LoadOrCreateAsync();
            var setupCode = await File.ReadAllTextAsync(Path.Combine(directory, "setup-code"));
            var sessions = new BootstrapSessionService();

            Assert.Equal(BootstrapState.Unconfigured, descriptor.State);
            Assert.NotEqual(setupCode.Trim(), descriptor.SetupCodeHash);
            Assert.False((await File.ReadAllBytesAsync(Path.Combine(directory, "setup-code"))).AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.False(sessions.TryCreate(descriptor, "incorrect", out _));
            Assert.True(sessions.TryCreate(descriptor, setupCode, out var session));
            Assert.True(sessions.TryCreate(descriptor, $"\uFEFF{setupCode}", out _));
            Assert.True(sessions.IsValid(session, descriptor));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Rotating_the_setup_code_invalidates_existing_setup_sessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        try
        {
            var store = new FileBootstrapStateStore(new BootstrapOptions { StateDirectory = directory });
            var descriptor = await store.LoadOrCreateAsync();
            var originalCode = await File.ReadAllTextAsync(Path.Combine(directory, "setup-code"));
            var sessions = new BootstrapSessionService();
            Assert.True(sessions.TryCreate(descriptor, originalCode, out var session));

            var rotatedCode = await store.RotateSetupCodeAsync();
            var rotatedDescriptor = await store.LoadOrCreateAsync();

            Assert.NotNull(rotatedCode);
            Assert.NotEqual(originalCode.Trim(), rotatedCode);
            Assert.False(sessions.IsValid(session, rotatedDescriptor));
            Assert.False(sessions.TryCreate(rotatedDescriptor, originalCode, out _));
            Assert.True(sessions.TryCreate(rotatedDescriptor, rotatedCode, out _));
            Assert.Equal(rotatedCode, (await File.ReadAllTextAsync(Path.Combine(directory, "setup-code"))).Trim());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Setup_code_cannot_be_rotated_after_setup_is_ready()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        try
        {
            var store = new FileBootstrapStateStore(new BootstrapOptions { StateDirectory = directory });
            await store.LoadOrCreateAsync();
            await store.UpdateAsync(current => current with { State = BootstrapState.Ready });

            Assert.Null(await store.RotateSetupCodeAsync());
            Assert.False(File.Exists(Path.Combine(directory, "setup-code")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Caller_supplied_code_is_hashed_without_creating_a_retrieval_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        try
        {
            var store = new FileBootstrapStateStore(new BootstrapOptions
            {
                StateDirectory = directory,
                SetupCode = "operator-provided-code"
            });

            var descriptor = await store.LoadOrCreateAsync();
            var updated = await store.UpdateAsync(current => current with
            {
                State = BootstrapState.Configuring,
                Provider = "Sqlite",
                OperationId = Guid.NewGuid()
            });

            Assert.Equal(BootstrapState.Configuring, updated.State);
            Assert.Equal("Sqlite", updated.Provider);
            Assert.NotEqual("operator-provided-code", descriptor.SetupCodeHash);
            Assert.False(File.Exists(Path.Combine(directory, "setup-code")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Sqlite_initialization_creates_the_first_instance_administrator_and_marks_ready()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(directory, "data");
        try
        {
            var options = new BootstrapOptions
            {
                StateDirectory = directory,
                DataDirectory = dataDirectory,
                SetupCode = "operator-provided-code",
                Interactive = new BootstrapInteractiveOptions
                {
                    OrganizationName = "Deployment Organization",
                    ApplicationName = "Deployment Desk",
                    ApplicationUrl = "https://deployment.example.test",
                    TimeZoneId = "Africa/Johannesburg"
                }
            };
            var store = new FileBootstrapStateStore(options);
            await store.LoadOrCreateAsync();
            var configured = await store.UpdateAsync(current => current with
            {
                State = BootstrapState.Configuring,
                Provider = "Sqlite",
                SqlitePath = Path.Combine(dataDirectory, "rateldesk.db"),
                OperationId = Guid.NewGuid()
            });
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys")),
                configuration => configuration.SetApplicationName("Helpdesk-Keyring"));
            var initializer = new BootstrapInitializationService(store, options, dataProtection);

            var request = new FirstAdministratorRequest(
                "admin@example.test",
                "Instance Admin",
                "correct horse battery staple",
                "Example Organization",
                "Example Desk",
                "https://desk.example.test",
                LogoUrl: "https://desk.example.test/logo.svg",
                CompactLogoUrl: "https://desk.example.test/compact.svg",
                SupportUrl: "https://support.example.test",
                SupportEmail: "support@example.test",
                EmailFromDisplayName: "Example Desk Support",
                Tagline: "The example service desk");
            var result = await initializer.InitializeAsync(configured, request, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(BootstrapState.Ready, result.Descriptor!.State);
            Assert.False(File.Exists(Path.Combine(directory, "setup-code")));

            var identityOptions = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataDirectory, "rateldesk.db")}")
                .Options;
            await using var identity = new RatelDeskIdentityDbContext(identityOptions);
            Assert.True(await identity.Users.AnyAsync(user => user.Email == "admin@example.test" && user.IsInstanceAdministrator));

            var applicationOptions = new DbContextOptionsBuilder<Helpdesk.Infrastructure.Persistence.HelpdeskDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataDirectory, "rateldesk.db")}")
                .Options;
            await using var application = new Helpdesk.Infrastructure.Persistence.HelpdeskDbContext(
                applicationOptions,
                new TestTenantContext(),
                new HttpContextAccessor());
            var initialization = await application.InstanceInitializations.SingleAsync();
            Assert.Equal(InstanceInitialization.SingletonId, initialization.Id);
            Assert.Equal(configured.InstanceId, initialization.InstanceId);
            Assert.Equal(configured.OperationId, initialization.OperationId);
            Assert.NotEqual("unknown", initialization.SetupVersion);
            Assert.Equal("Africa/Johannesburg", initialization.TimeZoneId);
            Assert.Equal("Deployment Organization", (await application.Organizations.SingleAsync()).Name);
            var branding = await application.InstanceBrandings.SingleAsync();
            Assert.Equal("Deployment Desk", branding.ApplicationName);
            Assert.Equal("https://deployment.example.test", branding.ApplicationUrl);
            Assert.Equal("https://desk.example.test/logo.svg", branding.LogoUrl);
            Assert.Equal("https://desk.example.test/compact.svg", branding.CompactLogoUrl);
            Assert.Equal("https://support.example.test", branding.SupportUrl);
            Assert.Equal("support@example.test", branding.SupportEmail);
            Assert.Equal("Example Desk Support", branding.EmailFromDisplayName);
            Assert.Equal("The example service desk", branding.Tagline);

            var interruptedDescriptor = await store.UpdateAsync(current => current with
            {
                State = BootstrapState.Configuring,
                CompletedAtUtc = null
            });
            var recovered = await initializer.InitializeAsync(interruptedDescriptor, request, CancellationToken.None);

            Assert.True(recovered.Succeeded, recovered.Error);
            Assert.Equal(BootstrapState.Ready, recovered.Descriptor!.State);
            Assert.Single(await identity.Users.Where(user => user.Email == "admin@example.test").ToListAsync());
            Assert.Single(await application.InstanceInitializations.ToListAsync());

            await using var connection = new SqliteConnection($"Data Source={Path.Combine(dataDirectory, "rateldesk.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
            await using var reader = await command.ExecuteReaderAsync();
            var appliedMigrations = new List<string>();
            while (await reader.ReadAsync())
            {
                appliedMigrations.Add(reader.GetString(0));
            }

            Assert.Contains(appliedMigrations, migration => migration.EndsWith("InitialSqliteApplication", StringComparison.Ordinal));
            Assert.Contains(appliedMigrations, migration => migration.EndsWith("AddInstanceInitialization", StringComparison.Ordinal));
            Assert.Contains(appliedMigrations, migration => migration.EndsWith("AddInitializationTimeZone", StringComparison.Ordinal));
            Assert.Contains(appliedMigrations, migration => migration.EndsWith("AddLocalCustomerAccountLinks", StringComparison.Ordinal));
            Assert.Contains(appliedMigrations, migration => migration.EndsWith("InitialSqliteIdentity", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Unattended_initialization_uses_the_same_sqlite_bootstrap_pipeline()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-unattended-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(directory, "data");
        try
        {
            var options = new BootstrapOptions
            {
                StateDirectory = directory,
                DataDirectory = dataDirectory,
                SetupCode = "operator-provided-code",
                Unattended = new BootstrapUnattendedOptions
                {
                    Provider = "Sqlite",
                    Email = "admin@example.test",
                    DisplayName = "Instance Admin",
                    Password = "correct horse battery staple",
                    OrganizationName = "Example Organization",
                    ApplicationName = "Example Desk",
                    ApplicationUrl = "https://desk.example.test"
                }
            };
            var store = new FileBootstrapStateStore(options);
            var descriptor = await store.LoadOrCreateAsync();
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys")),
                configuration => configuration.SetApplicationName("Helpdesk-Keyring"));
            var command = new UnattendedBootstrapCommand(
                store,
                options,
                dataProtection,
                new PostgreSqlSetupPreflightService());

            var result = await command.InitializeAsync(descriptor, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(BootstrapState.Ready, result.Descriptor!.State);
            var identityOptions = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dataDirectory, "rateldesk.db")}")
                .Options;
            await using var identity = new RatelDeskIdentityDbContext(identityOptions);
            Assert.True(await identity.Users.AnyAsync(user => user.Email == "admin@example.test" && user.IsInstanceAdministrator));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Unattended_initialization_uses_the_same_postgresql_bootstrap_pipeline()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16")
            .Build();
        await postgres.StartAsync();

        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-unattended-{Guid.NewGuid():N}");
        try
        {
            var options = new BootstrapOptions
            {
                StateDirectory = directory,
                DataDirectory = Path.Combine(directory, "data"),
                SetupCode = "operator-provided-code",
                Unattended = new BootstrapUnattendedOptions
                {
                    Provider = "PostgreSql",
                    PostgreSqlConnectionString = postgres.GetConnectionString(),
                    Email = "admin@example.test",
                    DisplayName = "Instance Admin",
                    Password = "correct horse battery staple",
                    OrganizationName = "Example Organization",
                    ApplicationName = "Example Desk",
                    ApplicationUrl = "https://desk.example.test"
                }
            };
            var store = new FileBootstrapStateStore(options);
            var descriptor = await store.LoadOrCreateAsync();
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys")),
                configuration => configuration.SetApplicationName("Helpdesk-Keyring"));
            var command = new UnattendedBootstrapCommand(
                store,
                options,
                dataProtection,
                new PostgreSqlSetupPreflightService());

            var result = await command.InitializeAsync(descriptor, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(BootstrapState.Ready, result.Descriptor!.State);

            var identityOptions = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseNpgsql(postgres.GetConnectionString())
                .Options;
            await using var identity = new RatelDeskIdentityDbContext(identityOptions);
            Assert.True(await identity.Users.AnyAsync(user => user.Email == "admin@example.test" && user.IsInstanceAdministrator));

            var applicationOptions = new DbContextOptionsBuilder<Helpdesk.Infrastructure.Persistence.HelpdeskDbContext>()
                .UseNpgsql(postgres.GetConnectionString())
                .Options;
            await using var application = new Helpdesk.Infrastructure.Persistence.HelpdeskDbContext(
                applicationOptions,
                new TestTenantContext(),
                new HttpContextAccessor());
            var initialization = await application.InstanceInitializations.SingleAsync();
            Assert.Equal(result.Descriptor.InstanceId, initialization.InstanceId);
            Assert.Equal(result.Descriptor.OperationId, initialization.OperationId);
            Assert.Equal("Example Organization", (await application.Organizations.SingleAsync()).Name);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Concurrent_postgresql_initialization_creates_one_instance_and_one_administrator()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16")
            .Build();
        await postgres.StartAsync();

        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-concurrent-bootstrap-{Guid.NewGuid():N}");
        try
        {
            var options = new BootstrapOptions
            {
                StateDirectory = directory,
                DataDirectory = Path.Combine(directory, "data"),
                SetupCode = "operator-provided-code"
            };
            var firstStore = new FileBootstrapStateStore(options);
            await firstStore.LoadOrCreateAsync();
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(directory, "keys")),
                configuration => configuration.SetApplicationName("Helpdesk-Keyring"));
            var configured = await firstStore.UpdateAsync(current => current with
            {
                State = BootstrapState.Configuring,
                Provider = "PostgreSql",
                ProtectedPostgreSqlConnection = dataProtection
                    .CreateProtector("RatelDesk.Bootstrap.PostgreSqlConnection.v1")
                    .Protect(postgres.GetConnectionString()),
                OperationId = Guid.NewGuid()
            });

            var secondStore = new FileBootstrapStateStore(options);
            var firstInitializer = new BootstrapInitializationService(firstStore, options, dataProtection);
            var secondInitializer = new BootstrapInitializationService(secondStore, options, dataProtection);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(async () =>
            {
                await start.Task;
                return await firstInitializer.InitializeAsync(configured, new FirstAdministratorRequest(
                    "first.admin@example.test",
                    "First Administrator",
                    "correct horse battery staple",
                    "Example Organization",
                    "Example Desk",
                    "https://desk.example.test"), CancellationToken.None);
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                return await secondInitializer.InitializeAsync(configured, new FirstAdministratorRequest(
                    "second.admin@example.test",
                    "Second Administrator",
                    "correct horse battery staple",
                    "Example Organization",
                    "Example Desk",
                    "https://desk.example.test"), CancellationToken.None);
            });

            start.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
            Assert.Equal(BootstrapState.Ready, (await firstStore.LoadOrCreateAsync()).State);

            var identityOptions = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseNpgsql(postgres.GetConnectionString())
                .Options;
            await using var identity = new RatelDeskIdentityDbContext(identityOptions);
            Assert.Single(await identity.Users.Where(user => user.IsInstanceAdministrator).ToListAsync());

            var applicationOptions = new DbContextOptionsBuilder<Helpdesk.Infrastructure.Persistence.HelpdeskDbContext>()
                .UseNpgsql(postgres.GetConnectionString())
                .Options;
            await using var application = new Helpdesk.Infrastructure.Persistence.HelpdeskDbContext(
                applicationOptions,
                new TestTenantContext(),
                new HttpContextAccessor());
            Assert.Single(await application.InstanceInitializations.ToListAsync());
            Assert.Single(await application.Organizations.ToListAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Legacy_adoption_marks_only_a_database_with_organization_and_user_evidence()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-adoption-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<Helpdesk.Infrastructure.Persistence.HelpdeskDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var database = new Helpdesk.Infrastructure.Persistence.HelpdeskDbContext(
                options,
                new TestTenantContext(),
                new HttpContextAccessor());
            await database.Database.EnsureCreatedAsync();

            var adoption = new LegacyInstallationAdoptionService();
            Assert.Equal(LegacyInstallationAdoptionResult.NotEstablished,
                await adoption.AdoptAsync(database, CancellationToken.None));

            var organization = new Organization { Name = "Established organization" };
            database.Organizations.Add(organization);
            database.Users.Add(new User
            {
                Name = "Established administrator",
                Email = "admin@example.test",
                OrganizationId = organization.Id,
                Role = "HelpdeskAdmin"
            });
            await database.SaveChangesAsync();

            Assert.Equal(LegacyInstallationAdoptionResult.Adopted,
                await adoption.AdoptAsync(database, CancellationToken.None));
            Assert.Equal(LegacyInstallationAdoptionResult.AlreadyMarked,
                await adoption.AdoptAsync(database, CancellationToken.None));
            Assert.Single(await database.InstanceInitializations.ToListAsync());
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task PostgreSql_migrations_adopt_established_legacy_data_but_leave_a_fresh_database_uninitialized()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16")
            .Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<Helpdesk.Infrastructure.Persistence.HelpdeskDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        var adoption = new LegacyInstallationAdoptionService();

        await using (var fresh = new Helpdesk.Infrastructure.Persistence.HelpdeskDbContext(
            options,
            new TestTenantContext(),
            new HttpContextAccessor()))
        {
            var migrator = fresh.GetService<IMigrator>();
            await migrator.MigrateAsync();

            Assert.Equal(LegacyInstallationAdoptionResult.NotEstablished,
                await adoption.AdoptAsync(fresh, CancellationToken.None));
            Assert.Empty(await fresh.InstanceInitializations.ToListAsync());
        }

        await using var established = new Helpdesk.Infrastructure.Persistence.HelpdeskDbContext(
            options,
            new TestTenantContext(),
            new HttpContextAccessor());
        await established.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");

        var establishedMigrator = established.GetService<IMigrator>();
        await establishedMigrator.MigrateAsync("20260912082029_AddRoleDefinitions");
        var organization = new Organization { Name = "Established organization" };
        // The historical target still requires EnableAiIntake, removed from
        // the rc.7 entity model. Seed that legacy shape before exercising the
        // upgrade path with current entities.
        await established.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Organizations" ("Id", "Name", "EnableAiIntake", "State")
            VALUES ({organization.Id}, {organization.Name}, {false}, {(int)organization.State});
            """);
        var customer = new Customer
        {
            Name = "Established OIDC customer",
            Email = "customer@example.test",
            OrganizationId = organization.Id
        };
        established.Customers.Add(customer);
        established.Users.Add(new User
        {
            Name = "Established administrator",
            Email = "admin@example.test",
            OrganizationId = organization.Id,
            Role = "HelpdeskAdmin"
        });
        await established.SaveChangesAsync();
        var legacyLinkId = Guid.NewGuid().ToString("N");
        var inviteAcceptedAtUtc = DateTimeOffset.UtcNow;
        await established.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CustomerAuthLinks" (
                "Id", "CustomerId", "AuthProviderType", "OidcIssuer", "OidcSubject",
                "AuthentikUserId", "AuthentikUsername", "AuthentikEmail", "InviteStatus", "InviteAcceptedAtUtc")
            VALUES (
                {legacyLinkId}, {customer.Id}, {"Oidc"}, {"https://id.example.test"}, {"established-subject"},
                {"established-provider-user"}, {"established.customer"}, {customer.Email}, {(int)CustomerInviteStatus.Active}, {inviteAcceptedAtUtc});
            """);

        await establishedMigrator.MigrateAsync();
        Assert.Equal(LegacyInstallationAdoptionResult.Adopted,
            await adoption.AdoptAsync(established, CancellationToken.None));
        Assert.Equal(LegacyInstallationAdoptionResult.AlreadyMarked,
            await adoption.AdoptAsync(established, CancellationToken.None));
        Assert.Single(await established.InstanceInitializations.ToListAsync());

        var preservedLink = await established.CustomerAuthLinks.AsNoTracking().SingleAsync();
        var preservedCustomer = await established.Customers.AsNoTracking().SingleAsync();
        Assert.Equal("Oidc", preservedLink.AuthProviderType);
        Assert.Equal("https://id.example.test", preservedLink.OidcIssuer);
        Assert.Equal("established-subject", preservedLink.OidcSubject);
        Assert.Equal("established-provider-user", preservedLink.AuthentikUserId);
        Assert.Equal("established.customer", preservedLink.AuthentikUsername);
        Assert.Equal(customer.Email, preservedLink.AuthentikEmail);
        Assert.Equal(CustomerInviteStatus.Active, preservedLink.InviteStatus);
        Assert.Equal(customer.Id, preservedLink.CustomerId);
        Assert.Equal(customer.Email, preservedCustomer.Email);
        Assert.Equal(organization.Id, preservedCustomer.OrganizationId);
    }

    [Fact]
    public async Task Initialization_rejects_an_external_http_application_url()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        try
        {
            var options = new BootstrapOptions
            {
                StateDirectory = directory,
                DataDirectory = Path.Combine(directory, "data"),
                SetupCode = "operator-provided-code"
            };
            var store = new FileBootstrapStateStore(options);
            await store.LoadOrCreateAsync();
            var configured = await store.UpdateAsync(current => current with
            {
                State = BootstrapState.Configuring,
                Provider = "Sqlite",
                SqlitePath = Path.Combine(options.DataDirectory, "rateldesk.db"),
                OperationId = Guid.NewGuid()
            });
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
            var initializer = new BootstrapInitializationService(store, options, dataProtection);

            var result = await initializer.InitializeAsync(configured, new FirstAdministratorRequest(
                "admin@example.test",
                "Instance Admin",
                "correct horse battery staple",
                "Example Organization",
                "Example Desk",
                "http://desk.example.test"), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("setup_validation_failed", result.Code);
            Assert.Null(result.Descriptor);
            var error = Assert.Single(result.Errors!);
            Assert.Equal("applicationUrl", error.Key);
            Assert.NotEmpty(Assert.Single(error.Value));
            Assert.Equal(BootstrapState.Configuring, (await store.LoadOrCreateAsync()).State);
            Assert.False(File.Exists(Path.Combine(options.DataDirectory, "rateldesk.db")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Initialization_rejects_an_unknown_time_zone_before_creating_the_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        try
        {
            var options = new BootstrapOptions
            {
                StateDirectory = directory,
                DataDirectory = Path.Combine(directory, "data"),
                SetupCode = "operator-provided-code"
            };
            var store = new FileBootstrapStateStore(options);
            await store.LoadOrCreateAsync();
            var configured = await store.UpdateAsync(current => current with
            {
                State = BootstrapState.Configuring,
                Provider = "Sqlite",
                SqlitePath = Path.Combine(options.DataDirectory, "rateldesk.db"),
                OperationId = Guid.NewGuid()
            });
            var initializer = new BootstrapInitializationService(
                store,
                options,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys"))));

            var result = await initializer.InitializeAsync(configured, new FirstAdministratorRequest(
                "admin@example.test",
                "Instance Admin",
                "correct horse battery staple",
                "Example Organization",
                "Example Desk",
                "https://desk.example.test",
                "Not/A-TimeZone"), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("setup_validation_failed", result.Code);
            Assert.Null(result.Descriptor);
            var error = Assert.Single(result.Errors!);
            Assert.Equal("timeZoneId", error.Key);
            Assert.NotEmpty(Assert.Single(error.Value));
            Assert.Equal(BootstrapState.Configuring, (await store.LoadOrCreateAsync()).State);
            Assert.False(File.Exists(Path.Combine(options.DataDirectory, "rateldesk.db")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Failed_application_initialization_rolls_back_the_first_identity_principal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rateldesk-bootstrap-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(directory, "data");
        try
        {
            var options = new BootstrapOptions
            {
                StateDirectory = directory,
                DataDirectory = dataDirectory,
                SetupCode = "operator-provided-code"
            };
            var store = new FileBootstrapStateStore(options);
            await store.LoadOrCreateAsync();
            var configured = await store.UpdateAsync(current => current with
            {
                State = BootstrapState.Configuring,
                Provider = "Sqlite",
                SqlitePath = Path.Combine(dataDirectory, "rateldesk.db"),
                OperationId = Guid.NewGuid()
            });
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
            var initializer = new BootstrapInitializationService(store, options, dataProtection);
            Directory.CreateDirectory(dataDirectory);

            // This existing row is harmless to an unconfigured database but
            // makes the initializer's branding insert fail after Identity has
            // attempted its insert.
            var applicationOptions = new DbContextOptionsBuilder<Helpdesk.Infrastructure.Persistence.HelpdeskDbContext>()
                .UseSqlite(
                    $"Data Source={configured.SqlitePath}",
                    sqlite => sqlite.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"))
                .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using (var application = new Helpdesk.Infrastructure.Persistence.HelpdeskDbContext(
                applicationOptions,
                new TestTenantContext(),
                new HttpContextAccessor()))
            {
                await application.Database.MigrateAsync();
                application.InstanceBrandings.Add(new InstanceBranding { Id = 1, ApplicationName = "Existing" });
                await application.SaveChangesAsync();
            }

            var result = await initializer.InitializeAsync(configured, new FirstAdministratorRequest(
                "admin@example.test",
                "Instance Admin",
                "correct horse battery staple",
                "Example Organization",
                "Example Desk",
                "https://desk.example.test"), CancellationToken.None);

            Assert.False(result.Succeeded);
            var identityOptions = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
                .UseSqlite($"Data Source={configured.SqlitePath}")
                .Options;
            await using var identity = new RatelDeskIdentityDbContext(identityOptions);
            Assert.False(await identity.Users.AnyAsync(user => user.Email == "admin@example.test"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class TestTenantContext : Helpdesk.Shared.Services.ITenantContext
    {
        public string? TenantId { get; set; }
        public string? UserId => null;
        public bool IsHelpdeskAdmin => true;
    }

    private static ServiceProvider CreateRecoveryServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["DataProtection:KeyRingPath"]!))
            .SetApplicationName(configuration["DataProtection:ApplicationName"] ?? "Helpdesk-Keyring");
        services.AddHelpdeskInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
