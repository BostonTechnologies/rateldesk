using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Middleware;
using Helpdesk.API.Services;
using Helpdesk.Application.Events;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

/// <summary>
/// Exercises the persisted credential-to-execution-token path against the
/// self-service endpoint. This deliberately does not replace the component
/// tests for the HTTP MCP host: it proves that the API authority reloads the
/// owner, scoped assignments, and source credential for every request.
/// </summary>
public sealed class McpGatewayRealAccessPipelineTests
{
    [Fact]
    public async Task Same_owner_credentials_are_isolated_by_organization_through_execution_tokens()
    {
        await using var harness = await GatewayAccessHarness.CreateAsync();
        await AssertOrganizationScopeAsync(harness);
    }

    [Fact]
    public async Task Same_owner_credentials_are_isolated_by_organization_through_execution_tokens_on_postgresql()
    {
        await using var harness = await GatewayAccessHarness.CreatePostgreSqlAsync();
        await AssertOrganizationScopeAsync(harness);
        await AssertVerifierScopeAsync(harness);
    }

    [Fact]
    public async Task Setup_verifier_projects_only_its_authorized_organization_and_no_business_grants()
    {
        await using var harness = await GatewayAccessHarness.CreateAsync();
        await AssertVerifierScopeAsync(harness);
    }

    private static async Task AssertVerifierScopeAsync(GatewayAccessHarness harness)
    {
        var verifier = await harness.CreateCredentialAsync("org-b", "mcp-setup");
        var execution = await harness.DelegateAsync(verifier);
        var status = await harness.GetAsync<Helpdesk.Shared.DTOs.Auth.CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("org-b", status.Body!.PrimaryOrganizationId);
        Assert.Equal(["org-b"], status.Body.AllowedOrganizationIds);
        Assert.Empty(status.Body.Permissions); Assert.False(status.Body.IsHelpdeskAdmin);
        using var denied = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await harness.RemoveRoleAsync("org-b");
        using var lostScopeStatus = await harness.GetAsync(execution, "/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, lostScopeStatus.StatusCode);
        await harness.AssertDelegationStatusAsync(verifier, GatewayAccessHarness.ResourceUri, HttpStatusCode.Forbidden);
    }

    private static async Task AssertOrganizationScopeAsync(GatewayAccessHarness harness)
    {
        var credentialA = await harness.CreateCredentialAsync("org-a");
        var credentialB = await harness.CreateCredentialAsync("org-b");

        var executionB = await harness.DelegateAsync(credentialB);
        var deniedList = await harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executionB, "/api/v1/self-service/requests");
        var deniedDetail = await harness.GetAsync(executionB, "/api/v1/self-service/requests/request-a");
        var deniedTasks = await harness.GetAsync(executionB, "/api/v1/self-service/requests/request-a/tasks");

        Assert.Equal(HttpStatusCode.OK, deniedList.StatusCode);
        Assert.NotNull(deniedList.Body);
        Assert.Equal(0, deniedList.Body!.TotalCount);
        Assert.Empty(deniedList.Body.Items);
        Assert.Equal(HttpStatusCode.NotFound, deniedDetail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedTasks.StatusCode);

        var executionA = await harness.DelegateAsync(credentialA);
        var allowedList = await harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executionA, "/api/v1/self-service/requests");

        Assert.Equal(HttpStatusCode.OK, allowedList.StatusCode);
        Assert.NotNull(allowedList.Body);
        Assert.Equal(1, allowedList.Body!.TotalCount);
        Assert.Equal("request-a", Assert.Single(allowedList.Body.Items).Id);
    }

    [Fact]
    public async Task Execution_token_rechecks_resource_role_revocation_and_account_state()
    {
        await using var harness = await GatewayAccessHarness.CreateAsync();
        var credential = await harness.CreateCredentialAsync("org-b");

        await harness.AssertDelegationStatusAsync(credential, "https://wrong.example/mcp", HttpStatusCode.Forbidden);
        var execution = await harness.DelegateAsync(credential);

        await harness.RemoveRoleAsync("org-b");
        var removedRole = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Forbidden, removedRole.StatusCode);

        await harness.RestoreRoleAsync("org-b");
        await harness.SetCredentialRevokedAsync(credential.Id);
        var revoked = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        var enabledCredential = await harness.CreateCredentialAsync("org-b");
        var enabledExecution = await harness.DelegateAsync(enabledCredential);
        await harness.SetOwnerEnabledAsync(false);
        var disabledOwner = await harness.GetAsync(enabledExecution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Unauthorized, disabledOwner.StatusCode);
    }

    [Fact]
    public async Task Simultaneous_gateway_credentials_do_not_share_owner_scope_or_cached_authorization()
    {
        await using var harness = await GatewayAccessHarness.CreatePostgreSqlAsync();
        var credentialA = await harness.CreateCredentialAsync("org-a");
        var credentialB = await harness.CreateCredentialAsync("org-b");

        var executions = await Task.WhenAll(harness.DelegateAsync(credentialA), harness.DelegateAsync(credentialB));
        var reads = await Task.WhenAll(
            harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executions[0], "/api/v1/self-service/requests"),
            harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executions[1], "/api/v1/self-service/requests"));

        Assert.Equal(HttpStatusCode.OK, reads[0].StatusCode);
        Assert.Equal("request-a", Assert.Single(reads[0].Body!.Items).Id);
        Assert.Equal(HttpStatusCode.OK, reads[1].StatusCode);
        Assert.Empty(reads[1].Body!.Items);

        var apiPurposeCredential = await harness.CreateCredentialAsync("org-a", purpose: "api");
        await harness.AssertDelegationStatusAsync(apiPurposeCredential, GatewayAccessHarness.ResourceUri, HttpStatusCode.Unauthorized);
    }

    internal sealed class GatewayAccessHarness(WebApplication application, IAsyncDisposable database) : IAsyncDisposable
    {
        public const string ResourceUri = "https://helpdesk.example/mcp";

        public HttpClient Client { get; } = application.GetTestClient();

        public static async Task<GatewayAccessHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            return await CreateAsync(options => options.UseSqlite(connection), connection);
        }

        public static async Task<GatewayAccessHarness> CreatePostgreSqlAsync()
        {
            var postgres = new PostgreSqlBuilder("postgres:16").Build();
            await postgres.StartAsync();
            try
            {
                return await CreateAsync(options => options.UseNpgsql(postgres.GetConnectionString()), postgres);
            }
            catch
            {
                await postgres.DisposeAsync();
                throw;
            }
        }

        private static async Task<GatewayAccessHarness> CreateAsync(Action<DbContextOptionsBuilder> configureDatabase, IAsyncDisposable database)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDataProtection();
            builder.Services.AddDbContext<HelpdeskDbContext>(configureDatabase);
            builder.Services.AddDbContext<RatelDeskIdentityDbContext>(configureDatabase);
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("org-a", "owner"));
            builder.Services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
            builder.Services.AddScoped<ISelfServiceAudienceService, SelfServiceAudienceService>();
            builder.Services.AddSingleton<IDomainEventPublisher, NoOpDomainEventPublisher>();
            builder.Services.AddScoped<ICorrelationContext, TestCorrelationContext>();
            builder.Services.AddSingleton<McpExecutionTokenService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = McpExecutionAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = McpExecutionAuthenticationHandler.SchemeName;
            })
            .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
                IntegrationCredentialAuthenticationHandler.McpSchemeName,
                options => options.Purpose = IntegrationCredentialAuthenticationHandler.McpPurpose)
            .AddScheme<AuthenticationSchemeOptions, McpExecutionAuthenticationHandler>(
                McpExecutionAuthenticationHandler.SchemeName,
                _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(McpGatewayDelegationEndpoints.DelegationPolicy, policy =>
                {
                    policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.McpSchemeName);
                    policy.RequireAuthenticatedUser();
                });
                options.AddPolicy("SelfService.User", policy =>
                {
                    policy.AddAuthenticationSchemes(McpExecutionAuthenticationHandler.SchemeName);
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.SelfServiceUser, HelpdeskPermissions.HelpdeskAdmin);
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseMiddleware<SetupVerificationExecutionBoundaryMiddleware>();
            app.UseMiddleware<UserAccessClaimsMiddleware>();
            app.UseAuthorization();
            app.MapMcpGatewayDelegationEndpoints();
            app.MapCurrentUserAccessEndpoint();
            app.MapSelfServiceMyRequestsEndpoints();

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                await identity.Database.EnsureCreatedAsync();
                identity.Users.Add(new ApplicationUser
                {
                    Id = "owner",
                    UserName = "owner@example.test",
                    Email = "owner@example.test",
                    DisplayName = "Owner",
                    IsEnabled = true
                });
                await identity.SaveChangesAsync();

                var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await domain.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
                domain.Organizations.AddRange(
                    new Organization { Id = "org-a", Name = "Organization A" },
                    new Organization { Id = "org-b", Name = "Organization B" });
                domain.Users.Add(new User
                {
                    Id = "owner",
                    Name = "Owner",
                    Email = "owner@example.test",
                    OrganizationId = "org-a",
                    Role = "Customer"
                });
                domain.Customers.Add(new Customer
                {
                    Id = "customer-a",
                    Name = "Owner",
                    Email = "owner@example.test",
                    OrganizationId = "org-a"
                });
                domain.CustomerAuthLinks.Add(new CustomerAuthLink
                {
                    CustomerId = "customer-a",
                    LocalAccountId = "owner",
                    DomainUserId = "owner",
                    InviteStatus = CustomerInviteStatus.Active
                });
                domain.ScopedRoleAssignments.AddRange(
                    new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-a", RoleKey = ScopedRoleCatalog.SelfServiceUser },
                    new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-b", RoleKey = ScopedRoleCatalog.SelfServiceUser });
                domain.RequestForms.Add(new RequestForm
                {
                    Id = "form-a",
                    Title = "Request form",
                    ServiceId = "service-a",
                    OrganizationId = "org-a",
                    ReleaseStatus = RequestFormReleaseStatus.Production
                });
                domain.Requests.Add(new Request
                {
                    Id = "request-a",
                    TrackingId = "REQ-A",
                    Title = "Organization A request",
                    CustomerId = "customer-a",
                    OrganizationId = "org-a",
                    RequestFormId = "form-a",
                    State = TicketState.New,
                    CreatedAt = DateTime.UtcNow
                });
                await domain.SaveChangesAsync();
            }

            await app.StartAsync();
            return new GatewayAccessHarness(app, database);
        }

        public async Task<Credential> CreateCredentialAsync(string organizationId, string? purpose = null)
        {
            var id = Guid.NewGuid();
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.IntegrationCredentials.Add(new IntegrationCredential
            {
                Id = id,
                OwnerUserId = "owner",
                Name = $"MCP {organizationId}",
                Prefix = $"rdk_{id:N}"[..16],
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
                Purpose = purpose ?? IntegrationCredentialAuthenticationHandler.McpPurpose,
                McpResourceUri = ResourceUri,
                OrganizationId = organizationId,
                Permissions = purpose == "mcp-setup" ? "" : HelpdeskPermissions.SelfServiceUser,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExpiresAtUtc = purpose == "mcp-setup" ? DateTimeOffset.UtcNow.AddMinutes(4) : DateTimeOffset.UtcNow.AddDays(1)
            });
            await identity.SaveChangesAsync();
            return new Credential(id, $"rdk_{id:N}_{secret}");
        }

        public HttpMessageHandler CreateHandler() => application.GetTestServer().CreateHandler();

        public async Task<string> DelegateAsync(Credential credential)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
            request.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, ResourceUri);
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var delegated = await response.Content.ReadFromJsonAsync<McpGatewayDelegationEndpoints.McpExecutionTokenResponse>();
            return Assert.IsType<string>(delegated?.AccessToken);
        }

        public async Task AssertDelegationStatusAsync(Credential credential, string resource, HttpStatusCode expectedStatus)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
            request.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, resource);
            using var response = await Client.SendAsync(request);
            Assert.Equal(expectedStatus, response.StatusCode);
        }

        public async Task<(HttpStatusCode StatusCode, T? Body)> GetAsync<T>(string executionToken, string path)
        {
            using var response = await GetAsync(executionToken, path);
            return (response.StatusCode, response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<T>()
                : default);
        }

        public async Task<HttpResponseMessage> GetAsync(string executionToken, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", executionToken);
            return await Client.SendAsync(request);
        }

        public async Task RemoveRoleAsync(string organizationId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var assignment = await domain.ScopedRoleAssignments.SingleAsync(candidate =>
                candidate.UserId == "owner" && candidate.OrganizationId == organizationId);
            domain.ScopedRoleAssignments.Remove(assignment);
            await domain.SaveChangesAsync();
        }

        public async Task RestoreRoleAsync(string organizationId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            domain.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "owner",
                OrganizationId = organizationId,
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            await domain.SaveChangesAsync();
        }

        public async Task SetCredentialRevokedAsync(Guid credentialId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var credential = await identity.IntegrationCredentials.SingleAsync(candidate => candidate.Id == credentialId);
            credential.RevokedAtUtc = DateTimeOffset.UtcNow;
            await identity.SaveChangesAsync();
        }

        public async Task SetOwnerEnabledAsync(bool isEnabled)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var owner = await identity.Users.SingleAsync(candidate => candidate.Id == "owner");
            owner.IsEnabled = isEnabled;
            await identity.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    internal sealed record Credential(Guid Id, string Bearer);

    private sealed class TestTenantContext(string? tenantId, string? userId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin => false;
    }

    private sealed class NoOpDomainEventPublisher : IDomainEventPublisher
    {
        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string GetCorrelationId() => "corr-mcp-real-pipeline";
    }
}
