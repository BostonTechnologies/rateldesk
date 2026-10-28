using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.AgentClient;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Authentication;
using Helpdesk.API.Middleware;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class IntegrationCredentialSqliteTests
{
    [Fact]
    public async Task Pairing_http_protocol_secure_profile_save_and_ordinary_revocation_work_together()
    {
        await using var harness = await Harness.CreateAsync();
        using var device = harness.DeviceClient();
        using var pairing = new IntegrationCredentialPairingClient("https://desk.example", device);
        var capabilities = await pairing.DiscoverAsync();
        Assert.Equal("RatelDesk", capabilities!.Product); Assert.True(capabilities.PairingSupported);
        var started = await pairing.StartAsync(new("integration test client", "api"));
        Assert.Equal("https://desk.example/account/integration-credentials/approve", started.VerificationUri);
        using var reviewResponse = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/pairing/review",
            new IntegrationPairingReviewRequest(started.HumanCode));
        var review = await reviewResponse.Content.ReadFromJsonAsync<IntegrationPairingReview>();
        Assert.Equal("org-a", Assert.Single(review!.Organizations).Id);
        using var approval = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/pairing/approve",
            new IntegrationPairingApprovalRequest(started.HumanCode, "org-a", ["Incident.Read"], 30, true));
        Assert.Equal(HttpStatusCode.NoContent, approval.StatusCode);
        var polled = await pairing.PollAsync(started.DeviceCode);
        Assert.Equal("issued", polled.Status); Assert.NotNull(polled.DeliveryExpiresAtUtc);
        var granted = polled.Credential!;
        var privateRoot = Path.Combine(Path.GetTempPath(), "rdk-paired-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profiles = new ConfigurationProfiles(privateRoot);
            var saved = profiles.Save("paired", AgentClientConfiguration.Empty with
            {
                ApiBaseUrl = "https://desk.example", CredentialMode = "integration", CredentialPurpose = "api",
                ClientTransport = "cli", CredentialTarget = "https://desk.example", IntegrationCredential = granted.Secret,
                CredentialOrganizationId = granted.OrganizationId, CredentialPermissions = granted.Permissions,
                CredentialExpiresAtUtc = granted.ExpiresAtUtc
            }, expectedRevision: "");
            var profileBytes = await File.ReadAllTextAsync(saved.Path);
            Assert.False(profileBytes.Contains(granted.Secret, StringComparison.Ordinal), "Profiles must reference a private secret file.");
            await pairing.AcknowledgeAsync(started.DeviceCode, granted.Id);
            await pairing.AcknowledgeAsync(started.DeviceCode, granted.Id);
            var resolved = profiles.Load("paired").Resolve();
            using var read = new HttpRequestMessage(HttpMethod.Get, "/api/v1/pairing-read-only-check");
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolved.IntegrationCredential);
            using var validated = await device.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
            var metadata = await harness.Client.GetFromJsonAsync<List<IntegrationCredentialEndpoints.IntegrationCredentialMetadata>>("/api/v1/integration-credentials/");
            Assert.Equal(granted.Id, Assert.Single(metadata!).Id);
            using var revoked = await harness.Client.DeleteAsync($"/api/v1/integration-credentials/{granted.Id}");
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            using var deniedRead = new HttpRequestMessage(HttpMethod.Get, "/api/v1/pairing-read-only-check");
            deniedRead.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolved.IntegrationCredential);
            using var denied = await device.SendAsync(deniedRead);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        finally { if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, true); }
    }

    [Fact]
    public async Task List_endpoint_uses_provider_supported_chronological_ordering_with_a_stable_tie_breaker()
    {
        await using var harness = await Harness.CreateAsync();
        var older = Credential("owner", "older", DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"));
        var firstAtSameInstant = Credential("owner", "first", DateTimeOffset.Parse("2026-01-02T00:00:00+00:00"));
        var secondAtSameInstant = Credential("owner", "second", firstAtSameInstant.CreatedAtUtc);
        secondAtSameInstant.Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.IntegrationCredentials.AddRange(older, firstAtSameInstant, secondAtSameInstant);
            await identity.SaveChangesAsync();
        }

        var response = await harness.Client.GetAsync("/api/v1/integration-credentials/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var credentials = await response.Content.ReadFromJsonAsync<IntegrationCredentialEndpoints.IntegrationCredentialMetadata[]>();
        Assert.NotNull(credentials);
        Assert.Equal(["second", "first", "older"], credentials.Select(credential => credential.Name));
    }

    [Fact]
    public async Task Forward_sqlite_migration_backfills_the_sort_key_for_existing_credentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RatelDeskIdentityDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"))
            .Options;
        var createdAtUtc = DateTimeOffset.Parse("2026-01-01T12:34:56.789+00:00");
        var credentialId = Guid.NewGuid();

        await using (var beforeUpgrade = new RatelDeskIdentityDbContext(options))
        {
            await beforeUpgrade.GetService<IMigrator>().MigrateAsync("20260917095318_AddIntegrationCredentials");
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO "IntegrationCredentials" (
                    "Id", "OwnerUserId", "Name", "Prefix", "SecretHash", "Purpose", "OrganizationId", "Permissions",
                    "ExpiresAtUtc", "CreatedAtUtc", "LastUsedAtUtc", "RevokedAtUtc")
                VALUES ($id, 'owner', 'existing', 'rdk_existing', 'hash', 'api', 'org-a', 'Incident.Read',
                    $expiresAt, $createdAt, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$id", credentialId.ToString());
            insert.Parameters.AddWithValue("$createdAt", createdAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$expiresAt", createdAtUtc.AddDays(30).ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        await using (var afterUpgrade = new RatelDeskIdentityDbContext(options))
            await afterUpgrade.Database.MigrateAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT \"CreatedAtUnixMilliseconds\" FROM \"IntegrationCredentials\" WHERE \"Name\" = 'existing';";
        Assert.Equal(createdAtUtc.ToUnixTimeMilliseconds(), Convert.ToInt64(await verify.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Create_endpoint_pairs_an_mcp_credential_to_its_canonical_resource()
    {
        await using var harness = await Harness.CreateAsync();
        var response = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/", new
        {
            name = "MCP client",
            purpose = "mcp",
            organizationId = "org-a",
            permissions = new[] { "Incident.Read" },
            lifetimeDays = 30,
            mcpResourceUri = "https://helpdesk.example/mcp/"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<IntegrationCredentialEndpoints.CreatedIntegrationCredential>();
        Assert.NotNull(created);
        Assert.Equal("https://helpdesk.example/mcp", created.McpResourceUri);
        Assert.StartsWith("rdk_", created.Secret, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        await using var scope = harness.Services.CreateAsyncScope();
        var credential = await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>()
            .IntegrationCredentials.SingleAsync();
        Assert.Equal("mcp", credential.Purpose);
        Assert.Equal("https://helpdesk.example/mcp", credential.McpResourceUri);
    }

    [Fact]
    public async Task Create_endpoint_rejects_a_permission_from_another_organization()
    {
        await using var harness = await Harness.CreateAsync();

        var response = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/", new
        {
            name = "Wrong organization",
            purpose = "api",
            organizationId = "org-b",
            permissions = new[] { "Incident.Write" },
            lifetimeDays = 30
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = harness.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>()
            .IntegrationCredentials.ToListAsync());
    }

    [Theory]
    [InlineData("integration")]
    [InlineData("mcp")]
    [InlineData("gateway")]
    public async Task Non_interactive_credentials_cannot_revoke_a_same_owner_sibling(string authenticationMode)
    {
        await using var harness = await Harness.CreateAsync();
        var first = Credential("owner", "first", DateTimeOffset.UtcNow.AddMinutes(-1));
        var sibling = Credential("owner", "sibling", DateTimeOffset.UtcNow);
        await harness.AddCredentialsAsync(first, sibling);

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/integration-credentials/{sibling.Id:N}");
        request.Headers.Add("X-Test-Auth-Mode", authenticationMode);
        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await harness.IsRevokedAsync(first.Id));
        Assert.False(await harness.IsRevokedAsync(sibling.Id));
    }

    [Fact]
    public async Task Anonymous_and_wrong_owner_requests_cannot_revoke_a_credential()
    {
        await using var harness = await Harness.CreateAsync();
        var credential = Credential("owner", "owner credential", DateTimeOffset.UtcNow);
        await harness.AddCredentialsAsync(credential);

        using var anonymous = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/integration-credentials/{credential.Id:N}");
        anonymous.Headers.Add("X-Test-Auth-Mode", "anonymous");
        using var anonymousResponse = await harness.Client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var wrongOwner = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/integration-credentials/{credential.Id:N}");
        wrongOwner.Headers.Add("X-Test-User", "other");
        using var wrongOwnerResponse = await harness.Client.SendAsync(wrongOwner);
        Assert.Equal(HttpStatusCode.NotFound, wrongOwnerResponse.StatusCode);
        Assert.False(await harness.IsRevokedAsync(credential.Id));
    }

    [Fact]
    public async Task Interactive_owner_can_revoke_its_own_credential()
    {
        await using var harness = await Harness.CreateAsync();
        var credential = Credential("owner", "owner credential", DateTimeOffset.UtcNow);
        await harness.AddCredentialsAsync(credential);

        using var response = await harness.Client.DeleteAsync($"/api/v1/integration-credentials/{credential.Id:N}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await harness.IsRevokedAsync(credential.Id));
    }

    [Fact]
    public async Task Permanent_delete_removes_live_authentication_material_and_cancels_pending_delivery()
    {
        await using var harness = await Harness.CreateAsync();
        using var device = harness.DeviceClient();
        using var pairing = new IntegrationCredentialPairingClient("https://desk.example", device);
        var started = await pairing.StartAsync(new("pending delivery", "api"));
        using var approval = await harness.Client.PostAsJsonAsync("/api/v1/integration-credentials/pairing/approve",
            new IntegrationPairingApprovalRequest(started.HumanCode, "org-a", ["Incident.Read"], 30, true));
        Assert.Equal(HttpStatusCode.NoContent, approval.StatusCode);
        var issued = (await pairing.PollAsync(started.DeviceCode)).Credential!;
        Assert.NotNull(issued);
        using var before = new HttpRequestMessage(HttpMethod.Get, "/api/v1/pairing-read-only-check");
        before.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
        using var beforeResponse = await device.SendAsync(before);
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);

        using var deleted = await harness.Client.DeleteAsync($"/api/v1/integration-credentials/{issued.Id:N}/record");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var after = new HttpRequestMessage(HttpMethod.Get, "/api/v1/pairing-read-only-check");
        after.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);
        using var afterResponse = await device.SendAsync(after);
        Assert.Equal(HttpStatusCode.Unauthorized, afterResponse.StatusCode);
        var retry = await pairing.PollAsync(started.DeviceCode);
        Assert.Equal("cancelled", retry.Status);
        Assert.Null(retry.Credential);
        using var repeated = await harness.Client.DeleteAsync($"/api/v1/integration-credentials/{issued.Id:N}/record");
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        await using var scope = harness.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
        Assert.False(await identity.IntegrationCredentials.AnyAsync(c => c.Id == issued.Id));
        var tombstone = await identity.IntegrationCredentialPairings.SingleAsync();
        Assert.Equal("cancelled", tombstone.State);
        Assert.Null(tombstone.ProtectedDelivery);
        Assert.Null(tombstone.ProtectedApproval);
        var audit = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().ActivityLogs
            .Where(a => a.RelatedEntityId == issued.Id.ToString("N")).ToListAsync();
        Assert.Contains(audit, a => a.Message.Contains("revoked and deleted", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, a => a.Message.Contains(issued.Secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("integration", "owner", HttpStatusCode.Forbidden)]
    [InlineData("mcp", "owner", HttpStatusCode.Forbidden)]
    [InlineData("gateway", "owner", HttpStatusCode.Forbidden)]
    [InlineData("anonymous", "owner", HttpStatusCode.Unauthorized)]
    [InlineData("local", "other", HttpStatusCode.NotFound)]
    public async Task Permanent_delete_retains_interactive_owner_boundary(string mode, string user, HttpStatusCode expected)
    {
        await using var harness = await Harness.CreateAsync();
        var credential = Credential("owner", "protected credential", DateTimeOffset.UtcNow);
        await harness.AddCredentialsAsync(credential);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/integration-credentials/{credential.Id:N}/record");
        request.Headers.Add("X-Test-Auth-Mode", mode);
        request.Headers.Add("X-Test-User", user);
        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        await using var scope = harness.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>().IntegrationCredentials.AnyAsync(c => c.Id == credential.Id));
    }

    [Fact]
    public async Task Revoke_preserves_history_then_delete_removes_only_the_named_record()
    {
        await using var harness = await Harness.CreateAsync();
        var selected = Credential("owner", "selected", DateTimeOffset.UtcNow);
        var untouched = Credential("owner", "unrelated", DateTimeOffset.UtcNow);
        await harness.AddCredentialsAsync(selected, untouched);
        using var revoke = await harness.Client.DeleteAsync($"/api/v1/integration-credentials/{selected.Id:N}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.True(await harness.IsRevokedAsync(selected.Id));
        using var deleted = await harness.Client.DeleteAsync($"/api/v1/integration-credentials/{selected.Id:N}/record");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var list = await harness.Client.GetFromJsonAsync<List<IntegrationCredentialEndpoints.IntegrationCredentialMetadata>>("/api/v1/integration-credentials/");
        Assert.Equal(untouched.Id, Assert.Single(list!).Id);
    }

    [Fact]
    public async Task Permanent_delete_rejects_a_local_cookie_mutation_without_the_existing_csrf_protection()
    {
        await using var harness = await Harness.CreateAsync();
        var credential = Credential("owner", "protected mutation", DateTimeOffset.UtcNow);
        await harness.AddCredentialsAsync(credential);
        harness.Client.DefaultRequestHeaders.Remove("X-Requested-With");
        using var response = await harness.Client.DeleteAsync($"/api/v1/integration-credentials/{credential.Id:N}/record");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = harness.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>().IntegrationCredentials.AnyAsync(c => c.Id == credential.Id));
    }

    private static IntegrationCredential Credential(string ownerId, string name, DateTimeOffset createdAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerId,
        Name = name,
        Prefix = $"rdk_{name}",
        SecretHash = "hash",
        Purpose = "api",
        OrganizationId = "org-a",
        Permissions = "Incident.Read",
        CreatedAtUtc = createdAtUtc,
        CreatedAtUnixMilliseconds = createdAtUtc.ToUnixTimeMilliseconds(),
        ExpiresAtUtc = createdAtUtc.AddDays(30)
    };

    private sealed class Harness(WebApplication application, SqliteConnection connection) : IAsyncDisposable
    {
        public IServiceProvider Services => application.Services;

        public HttpClient Client { get; } = InteractiveClient(application);
        private static HttpClient InteractiveClient(WebApplication application)
        { var client = application.GetTestClient(); client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest"); return client; }

        public HttpClient DeviceClient()
        {
            var device = application.GetTestClient(); device.BaseAddress = new Uri("https://desk.example");
            device.DefaultRequestHeaders.Add("X-Test-Auth-Mode", "anonymous"); return device;
        }

        public static async Task<Harness> CreateAsync(TimeProvider? clock = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Configuration["IntegrationCredentials:Pairing:PublicOrigin"] = "https://desk.example";
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddSingleton(clock ?? TimeProvider.System);
            builder.Services.AddScoped<IntegrationCredentialIssuer>();
            builder.Services.AddScoped<IntegrationCredentialPairingService>();
            builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("IntegrationPairing", limiter =>
            { limiter.PermitLimit = 3000; limiter.Window = TimeSpan.FromMinutes(1); }));
            builder.Services.AddDbContext<RatelDeskIdentityDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddScoped<ITenantContext>(_ =>
            {
                var tenant = Substitute.For<ITenantContext>();
                tenant.IsHelpdeskAdmin.Returns(true);
                return tenant;
            });
            builder.Services.AddSingleton<ICurrentUserAccessService>(new TestAccessService());
            builder.Services.AddScoped<IIntegrationCredentialOwnerResolver, IntegrationCredentialOwnerResolver>();
            builder.Services.AddScoped<IAuthorizationHandler, IntegrationCredentialManagementSessionHandler>();
            builder.Services.AddScoped<IAuthorizationHandler, IntegrationCredentialOwnerManagementHandler>();
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { })
                .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
                    IntegrationCredentialAuthenticationHandler.SchemeName, _ => { })
                .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
                    IntegrationCredentialAuthenticationHandler.McpSchemeName, options => options.Purpose = "mcp");
            builder.Services.AddAuthorization(options => options.AddPolicy("PairingReadOnly", policy =>
            { policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName); policy.RequireAuthenticatedUser(); }));
            builder.Services.AddAuthorization(options => options.AddPolicy("McpReadOnly", policy =>
            { policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.McpSchemeName); policy.RequireAuthenticatedUser(); }));
            builder.Services.AddAuthorization(options => options.AddPolicy(IntegrationCredentialEndpoints.CredentialManagementPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new IntegrationCredentialManagementSessionRequirement());
            }));
            builder.Services.AddAuthorization(options => options.AddPolicy(IntegrationCredentialEndpoints.OwnerManagementPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new IntegrationCredentialOwnerManagementRequirement());
            }));
            var application = builder.Build();
            application.UseAuthentication();
            application.UseMiddleware<LocalCookieCsrfMiddleware>();
            application.UseRateLimiter();
            application.UseAuthorization();
            application.MapIntegrationCredentialEndpoints();
            application.MapIntegrationCredentialPairingEndpoints();
            application.MapGet("/api/v1/pairing-read-only-check", (ClaimsPrincipal principal) =>
                principal.HasClaim("integration_permission", "Incident.Read") && principal.HasClaim("integration_organization_id", "org-a")
                    ? Results.Ok(new { authenticated = true, readOnly = true }) : Results.Forbid()).RequireAuthorization("PairingReadOnly");
            application.MapGet("/api/v1/mcp-read-only-check", (ClaimsPrincipal principal) =>
                principal.HasClaim("integration_permission", "Incident.Read") && principal.HasClaim("integration_organization_id", "org-a")
                    ? Results.Ok() : Results.Forbid()).RequireAuthorization("McpReadOnly");
            await application.StartAsync();
            await using (var scope = application.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                await identity.Database.EnsureCreatedAsync();
                identity.Users.AddRange(
                    new ApplicationUser { Id = "owner", UserName = "owner", Email = "owner@example.test", IsEnabled = true },
                    new ApplicationUser { Id = "other", UserName = "other", Email = "other@example.test", IsEnabled = true });
                await identity.SaveChangesAsync();
                var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await domain.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
                domain.Organizations.AddRange(
                    new Organization { Id = "org-a", Name = "Organization A", IsEnabled = true },
                    new Organization { Id = "org-b", Name = "Organization B", IsEnabled = true });
                await domain.SaveChangesAsync();
            }

            return new Harness(application, connection);
        }

        public async Task AddCredentialsAsync(params IntegrationCredential[] credentials)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.IntegrationCredentials.AddRange(credentials);
            await identity.SaveChangesAsync();
        }

        public async Task<bool> IsRevokedAsync(Guid credentialId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>()
                .IntegrationCredentials
                .Where(credential => credential.Id == credentialId)
                .Select(credential => credential.RevokedAtUtc != null)
                .SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var authenticationMode = Request.Headers["X-Test-Auth-Mode"].ToString();
            if (string.Equals(authenticationMode, "anonymous", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].FirstOrDefault() ?? "owner"),
                    new Claim("auth_mode", string.IsNullOrWhiteSpace(authenticationMode) ? "local" : authenticationMode)
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class TestAccessService : ICurrentUserAccessService
    {
        public CurrentUserAccessProfile Profile { get; set; } = new(
            true, "owner", "owner@example.test", "org-a", null, null, false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["Incident.Read", "Incident.Write"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["org-a", "org-b"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        {
            UsesScopedPermissions = true,
            ScopedPermissionGrants = new HashSet<ScopedPermissionGrant>
            {
                new("Incident.Read", "org-a"),
                new("Incident.Write", "org-a")
            }
        };

        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(Profile);
    }
}
