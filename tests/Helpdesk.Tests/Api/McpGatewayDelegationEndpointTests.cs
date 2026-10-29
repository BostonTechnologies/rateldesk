using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class McpGatewayDelegationEndpointTests
{
    [Fact]
    public async Task Paired_mcp_credential_delegates_only_to_its_resource_and_revocation_invalidates_execution()
    {
        await using var harness = await Harness.CreateAsync();
        var credential = await harness.CreateCredentialAsync("https://helpdesk.example/mcp");
        using var delegation = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
        delegation.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
        delegation.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, "https://helpdesk.example/mcp");

        using var delegatedResponse = await harness.Client.SendAsync(delegation);
        Assert.True(delegatedResponse.IsSuccessStatusCode, $"Delegation status {delegatedResponse.StatusCode}; challenge {string.Join("; ", delegatedResponse.Headers.WwwAuthenticate)}.");
        var delegated = await delegatedResponse.Content.ReadFromJsonAsync<McpGatewayDelegationEndpoints.McpExecutionTokenResponse>();
        Assert.NotNull(delegated);
        Assert.StartsWith(McpExecutionTokenService.TokenPrefix, delegated.AccessToken, StringComparison.Ordinal);

        using var directMcpRequest = new HttpRequestMessage(HttpMethod.Get, "/business");
        directMcpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
        using var directMcpResponse = await harness.Client.SendAsync(directMcpRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, directMcpResponse.StatusCode);

        using var businessRequest = new HttpRequestMessage(HttpMethod.Get, "/business");
        businessRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegated.AccessToken);
        using var businessResponse = await harness.Client.SendAsync(businessRequest);
        Assert.Equal(HttpStatusCode.OK, businessResponse.StatusCode);

        await harness.RevokeAsync(credential.Id);
        using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/business");
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegated.AccessToken);
        using var revokedResponse = await harness.Client.SendAsync(revokedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
    }

    [Fact]
    public async Task Paired_mcp_credential_cannot_delegate_to_another_gateway_resource()
    {
        await using var harness = await Harness.CreateAsync();
        var credential = await harness.CreateCredentialAsync("https://helpdesk.example/mcp");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
        request.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, "https://other.example/mcp");

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Temporary_verifier_execution_permits_only_status_even_for_other_authenticated_none_operations()
    {
        await using var harness = await Harness.CreateAsync();
        var credential = await harness.CreateCredentialAsync("https://helpdesk.example/mcp", setup: true);
        using var delegation = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
        delegation.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
        delegation.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, "https://helpdesk.example/mcp");
        using var response = await harness.Client.SendAsync(delegation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delegated = (await response.Content.ReadFromJsonAsync<McpGatewayDelegationEndpoints.McpExecutionTokenResponse>())!;
        Assert.True(delegated.SetupVerification); Assert.Empty(delegated.Permissions);
        foreach (var path in new[] { "/business", "/api/v1/system/version", "/api/v1/auth/me?broader=true" })
        {
            using var forbidden = new HttpRequestMessage(HttpMethod.Get, path);
            forbidden.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegated.AccessToken);
            using var denied = await harness.Client.SendAsync(forbidden);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        status.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegated.AccessToken);
        using var success = await harness.Client.SendAsync(status);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        await harness.RevokeAsync(credential.Id);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        stale.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegated.AccessToken);
        using var revoked = await harness.Client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_source_deletion_invalidates_already_issued_ordinary_and_temporary_execution_tokens(bool setup)
    {
        await using var harness = await Harness.CreateAsync();
        var credential = await harness.CreateCredentialAsync("https://helpdesk.example/mcp", setup);
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
        exchange.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
        exchange.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, "https://helpdesk.example/mcp");
        using var response = await harness.Client.SendAsync(exchange);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delegated = (await response.Content.ReadFromJsonAsync<McpGatewayDelegationEndpoints.McpExecutionTokenResponse>())!;
        var path = setup ? "/api/v1/auth/me" : "/business";
        using (var allowed = new HttpRequestMessage(HttpMethod.Get, path))
        {
            allowed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegated.AccessToken);
            using var before = await harness.Client.SendAsync(allowed);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }
        await harness.DeleteAsync(credential.Id);
        using var stale = new HttpRequestMessage(HttpMethod.Get, path);
        stale.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegated.AccessToken);
        using var after = await harness.Client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    private sealed class Harness(WebApplication application, SqliteConnection businessConnection) : IAsyncDisposable
    {
        public HttpClient Client { get; } = application.GetTestClient();

        public static async Task<Harness> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddDataProtection();
            var identityConnection = new SqliteConnection("Data Source=:memory:");
            await identityConnection.OpenAsync();
            builder.Services.AddSingleton(identityConnection);
            builder.Services.AddDbContext<RatelDeskIdentityDbContext>(options => options.UseSqlite(identityConnection));
            var businessConnection = new SqliteConnection("Data Source=:memory:");
            await businessConnection.OpenAsync();
            builder.Services.AddHttpContextAccessor();
            var tenant = Substitute.For<ITenantContext>();
            tenant.IsHelpdeskAdmin.Returns(true);
            builder.Services.AddSingleton(tenant);
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(businessConnection));
            builder.Services.AddSingleton<McpExecutionTokenService>();
            builder.Services.AddSingleton<ICurrentUserAccessService>(new TestAccessService());
            builder.Services.AddAuthentication(IntegrationCredentialAuthenticationHandler.McpSchemeName)
                .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.McpSchemeName, options => options.Purpose = IntegrationCredentialAuthenticationHandler.McpPurpose)
                .AddScheme<AuthenticationSchemeOptions, McpExecutionAuthenticationHandler>(McpExecutionAuthenticationHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy(McpGatewayDelegationEndpoints.DelegationPolicy, policy =>
                {
                    policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.McpSchemeName);
                    policy.RequireAuthenticatedUser();
                });
                options.AddPolicy("ExecutionOnly", policy =>
                {
                    policy.AddAuthenticationSchemes(McpExecutionAuthenticationHandler.SchemeName);
                    policy.RequireAuthenticatedUser();
                });
            });
            var application = builder.Build();
            application.UseAuthentication();
            application.UseMiddleware<SetupVerificationExecutionBoundaryMiddleware>();
            application.UseAuthorization();
            application.MapMcpGatewayDelegationEndpoints();
            application.MapGet("/business", () => Results.Ok()).RequireAuthorization("ExecutionOnly");
            application.MapGet("/api/v1/system/version", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/api/v1/auth/me", () => Results.Ok()).RequireAuthorization("ExecutionOnly");
            await application.StartAsync();
            await using (var scope = application.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                await identity.Database.EnsureCreatedAsync();
                identity.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", IsEnabled = true });
                await identity.SaveChangesAsync();
                var business = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await business.Database.EnsureCreatedAsync();
            }

            return new Harness(application, businessConnection);
        }

        public async Task<(Guid Id, string Bearer)> CreateCredentialAsync(string resourceUri, bool setup = false)
        {
            var id = Guid.NewGuid();
            const string secret = "test-secret";
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.IntegrationCredentials.Add(new IntegrationCredential
            {
                Id = id,
                OwnerUserId = "owner",
                Name = "MCP test",
                Prefix = "rdk_test",
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
                Purpose = setup ? IntegrationCredentialIssuer.SetupVerificationPurpose : IntegrationCredentialAuthenticationHandler.McpPurpose,
                McpResourceUri = resourceUri,
                OrganizationId = "org-a",
                Permissions = setup ? "" : "Incident.Read",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExpiresAtUtc = setup ? DateTimeOffset.UtcNow.AddMinutes(4) : DateTimeOffset.UtcNow.AddDays(1)
            });
            await identity.SaveChangesAsync();
            return (id, $"rdk_{id:N}_{secret}");
        }

        public async Task DeleteAsync(Guid id)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            Assert.Equal(1, await identity.IntegrationCredentials.Where(credential => credential.Id == id).ExecuteDeleteAsync());
        }

        public async Task RevokeAsync(Guid id)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var credential = await identity.IntegrationCredentials.SingleAsync(candidate => candidate.Id == id);
            credential.RevokedAtUtc = DateTimeOffset.UtcNow;
            await identity.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
            await businessConnection.DisposeAsync();
        }
    }

    private sealed class TestAccessService : ICurrentUserAccessService
    {
        private static readonly CurrentUserAccessProfile Profile = new(
            true, "owner", null, "org-a", null, null, false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["Incident.Read"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["org-a"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<CurrentUserAccessProfile> ResolveAsync(System.Security.Claims.ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(user.HasClaim("setup_verification", "true") ? Profile with { Permissions = new HashSet<string>() } : Profile);
    }
}

