using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Api;

public class UserEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public UserEndpointsTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseIsolatedTestStorage();
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Development");
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((context, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "test",
                    ["Jwt:Audience"] = "test",
                    ["Jwt:Key"] = "test-key-123456789012345678901234"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<User>>(new InMemoryRepository<User>());
                services.AddSingleton<IRepository<Role>>(new InMemoryRepository<Role>());

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        });
    }

    [Fact]
    public async Task Create_And_Update_RoundTrip_OrganizationId()
    {
        var client = GetAuthenticatedClient();

        var create = new CreateUserRequest(
            "Assigned User",
            "assigned@example.test",
            null,
            "Technician",
            false,
            "org-1");
        var createResp = await client.PostAsJsonAsync("/api/v1/users", create);
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal("org-1", created!.OrganizationId);

        var update = new UpdateUserRequest(
            created.Name,
            created.Email,
            null,
            created.Role,
            created.IsTestUser,
            "org-2");
        var updateResp = await client.PutAsJsonAsync($"/api/v1/users/{created.Id}", update);
        updateResp.EnsureSuccessStatusCode();
        var updated = await updateResp.Content.ReadFromJsonAsync<UserDto>();
        Assert.Equal("org-2", updated!.OrganizationId);
    }

    [Fact]
    public async Task Oidc_only_mode_does_not_map_local_authentication_routes()
    {
        await using var oidc = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Mode"] = "Oidc" })));
        using var client = oidc.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/local-auth/login", new { email = "synthetic@example.invalid", password = "synthetic passphrase" })).StatusCode);
    }

    [Fact]
    public async Task Domain_user_crud_rejects_password_writes()
    {
        var client = GetAuthenticatedClient();

        var create = await client.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(
            "Legacy Password", "legacy.password@example.test", "not-a-local-account-password", "Technician"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, create.StatusCode);
    }

    private HttpClient GetAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");
        return client;
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
            {
                return Task.FromResult(AuthenticateResult.Fail("No authorization header"));
            }

            var parts = header.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var role = parts.Length > 1 ? parts[1] : string.Empty;
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "Test"),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
