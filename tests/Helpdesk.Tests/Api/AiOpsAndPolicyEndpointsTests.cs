using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.AiOps;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.AiOps;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.AiOps;
using Helpdesk.Shared.DTOs.McpPolicies;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed partial class AiOpsAndPolicyEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_preview_confirm_replay_stale_owner_review_and_revocation_are_enforced(bool postgres)
    {
        await using var h = await Harness.Create(postgres);
        var draft = new McpPolicyDraft(h.Credential, "a", [HelpdeskPermissions.IncidentRead], DateTime.UtcNow.AddHours(1), 0, "Active");
        var preview = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", draft);
        preview.EnsureSuccessStatusCode();
        await h.WithDb(async db => Assert.False(await db.Set<McpAccessPolicy>().AnyAsync()));
        var receipt = (await preview.Content.ReadFromJsonAsync<McpPolicyPreview>())!;
        var confirm = new ConfirmMcpPolicy(receipt.Confirmation);
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", confirm)).EnsureSuccessStatusCode();
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", confirm)).EnsureSuccessStatusCode();
        await h.WithDb(async db =>
        {
            Assert.Equal(1, await db.Set<McpAccessPolicyAudit>().CountAsync());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_mode", "integration"), new Claim("integration_credential_id", h.Credential.ToString("N"))], "test"));
            var bounded = await McpAccessPolicyEnforcement.ApplyAsync(db, Profile(true), principal, default);
            Assert.False(bounded.IsHelpdeskAdmin);
            Assert.True(bounded.HasPermission(HelpdeskPermissions.IncidentRead, "a"));
            Assert.False(bounded.CanManageIncident("a"));
            Assert.False(bounded.HasPermission(HelpdeskPermissions.IncidentRead, "b"));
        });
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", draft)).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new("Test", "owner");
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", draft with { ExpectedRevision = 1 })).EnsureSuccessStatusCode();
        h.Client.DefaultRequestHeaders.Authorization = new("Test", "admin");
        var revoked = await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", draft with { ExpectedRevision = 1, State = "Revoked" });
        revoked.EnsureSuccessStatusCode();
        (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/confirm", new ConfirmMcpPolicy((await revoked.Content.ReadFromJsonAsync<McpPolicyPreview>())!.Confirmation))).EnsureSuccessStatusCode();
        await h.WithDb(async db =>
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_mode", "integration"), new Claim("integration_credential_id", h.Credential.ToString())], "test"));
            Assert.Empty((await McpAccessPolicyEnforcement.ApplyAsync(db, Profile(true), principal, default)).Permissions);
        });
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync("/api/v1/admin/mcp-policies/preview", draft with { ExpectedRevision = 2 })).StatusCode);
        h.Client.DefaultRequestHeaders.Authorization = new("Test", "integration");
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.GetAsync("/api/v1/admin/mcp-policies")).StatusCode);
    }

    [Theory]
    [InlineData("Disabled", false)]
    [InlineData("Active", true)]
    public async Task Inactive_or_expired_policy_never_restores_credential_authority(string state, bool expired)
    {
        await using var h = await Harness.Create(false);
        await h.WithDb(async db =>
        {
            db.Add(new McpAccessPolicy { CredentialId = h.Credential, OrganizationId = "a", PermissionsJson = "[\"Incident.Read\"]", State = state, ExpiresUtc = DateTime.UtcNow.AddHours(expired ? -1 : 1), Revision = 1 });
            await db.SaveChangesAsync();
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_mode", "integration"), new Claim("integration_credential_id", h.Credential.ToString())], "test"));
            var profile = await McpAccessPolicyEnforcement.ApplyAsync(db, Profile(true), principal, default);
            Assert.Empty(profile.Permissions);
            Assert.Empty(profile.AllowedOrganizationIds);
            Assert.False(profile.IsHelpdeskAdmin);
        });
    }

    [Fact]
    public async Task Investigation_pins_guidance_deduplicates_context_and_rechecks_all_ticket_access()
    {
        await using var h = await Harness.Create(true);
        var promptId = Guid.NewGuid();
        var prompt = new SaveAiPromptDto("a", "Default guidance", "Synthetic", "Inspect before proposing changes", 0, true, false);
        (await h.Client.PutAsJsonAsync($"/api/v1/ai/ops/prompts/{promptId}", prompt)).EnsureSuccessStatusCode();
        var request = new CreateAiInvestigationDto(Guid.NewGuid(), "a", "Investigate synthetic tickets", ["incident", "task", "incident"], [promptId]);
        var response = await h.Client.PostAsJsonAsync("/api/v1/ai/ops/investigations", request);
        response.EnsureSuccessStatusCode();
        var investigation = (await response.Content.ReadFromJsonAsync<AiInvestigationDto>())!;
        Assert.Contains("Inspect before proposing changes", investigation.ContextSnapshot);
        Assert.Contains("tasks", investigation.ContextSnapshot);
        await h.WithDb(async db => Assert.False(await db.Set<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatEvent>().AnyAsync()));
        (await h.Client.PutAsJsonAsync($"/api/v1/ai/ops/prompts/{promptId}", prompt with { ExpectedRevision = 1, Content = "Changed guidance" })).EnsureSuccessStatusCode();
        var replay = await h.Client.PostAsJsonAsync("/api/v1/ai/ops/investigations", request);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(investigation.ContextSnapshot, (await replay.Content.ReadFromJsonAsync<AiInvestigationDto>())!.ContextSnapshot);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Client.PostAsJsonAsync("/api/v1/ai/ops/investigations", request with { Title = "Different" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.PostAsJsonAsync("/api/v1/ai/ops/investigations", request with { Id = Guid.NewGuid(), TicketIds = ["foreign"] })).StatusCode);
        h.Access.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(Profile(false));
        await h.WithDb(async db => { (await db.Tickets.IgnoreQueryFilters().SingleAsync(x => x.Id == "task")).OrganizationId = "b"; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync($"/api/v1/ai/ops/investigations/{request.Id}")).StatusCode);
        Assert.Empty((await h.Client.GetFromJsonAsync<List<AiInvestigationDto>>("/api/v1/ai/ops/investigations"))!);
    }

    [Fact]
    public void Serialized_utf8_payload_limit_rejects_oversized_guidance_without_truncation()
    {
        var snapshot = "Guidance " + new string('界', 12000);
        Assert.Throws<ArgumentException>(() => AiOpsContext.ValidatePayload(AiOpsContext.Compose(snapshot, "Question")));
        AiOpsContext.ValidatePayload(AiOpsContext.Compose("bounded context", "Question"));
    }

    private static CurrentUserAccessProfile Profile(bool admin) => new(true, "Synthetic", null, "a", "A", null, admin, new HashSet<string>(), new HashSet<string> { HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.RequestWrite }, new HashSet<string> { "a" }, new HashSet<string>())
    { UsesScopedPermissions = true, ScopedPermissionGrants = new HashSet<ScopedPermissionGrant> { new(HelpdeskPermissions.IncidentWrite, "a"), new(HelpdeskPermissions.RequestWrite, "a") } };
    private sealed class Harness(WebApplication app, HttpClient client, PostgreSqlContainer? postgres, string directory, ICurrentUserAccessService access, Guid credential) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public ICurrentUserAccessService Access => access;
        public Guid Credential => credential;
        public static async Task<Harness> Create(bool pg)
        {
            var directory = Path.Combine(Path.GetTempPath(), "aiops-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            PostgreSqlContainer? postgres = null;
            if (pg) { postgres = new PostgreSqlBuilder("postgres:16").Build(); await postgres.StartAsync(); }
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseTestServer(); builder.Services.AddHttpContextAccessor(); builder.Services.AddDataProtection();
            builder.Services.AddSingleton(Substitute.For<Helpdesk.Application.AiAssistant.Chat.IAiAssistantChatTransport>());
            var tenant = Substitute.For<ITenantContext>(); tenant.IsHelpdeskAdmin.Returns(true); builder.Services.AddSingleton(tenant);
            var access = Substitute.For<ICurrentUserAccessService>(); access.ResolveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(Profile(true)); builder.Services.AddSingleton(access);
            builder.Services.AddDbContext<HelpdeskDbContext>(o => { if (postgres is null) o.UseSqlite($"Data Source={directory}/business.db"); else o.UseNpgsql(postgres.GetConnectionString()); });
            builder.Services.AddDbContext<RatelDeskIdentityDbContext>(o => o.UseSqlite($"Data Source={directory}/identity.db"));
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
            builder.Services.AddAuthorization(o => { o.AddPolicy("HelpdeskAdmin", p => p.RequireRole("HelpdeskAdmin")); o.AddPolicy("HelpdeskStaff", p => p.RequireAuthenticatedUser()); });
            var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapAiOpsEndpoints(); app.MapMcpPolicyEndpoints();
            var credential = Guid.NewGuid();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>(); await db.Database.EnsureCreatedAsync();
                db.Organizations.AddRange(new Organization { Id = "a", Name = "A" }, new Organization { Id = "b", Name = "B" });
                db.Tickets.AddRange(new Incident { Id = "incident", OrganizationId = "a", Title = "Synthetic incident" }, new Request { Id = "request", OrganizationId = "a", Title = "Parent" }, new RequestTask { Id = "task", OrganizationId = "a", RequestId = "request", Title = "Synthetic task" }, new Incident { Id = "foreign", OrganizationId = "b", Title = "Foreign" });
                await db.SaveChangesAsync();
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>(); await identity.Database.EnsureCreatedAsync();
                identity.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", IsEnabled = true });
                identity.IntegrationCredentials.Add(new IntegrationCredential { Id = credential, OwnerUserId = "owner", Name = "Synthetic credential", Purpose = "mcp", OrganizationId = "a", McpResourceUri = "https://helpdesk.example.test/mcp", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(2), Permissions = HelpdeskPermissions.IncidentRead, SecretHash = "synthetic-nonsecret" }); await identity.SaveChangesAsync();
            }
            await app.StartAsync(); var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Test", "admin");
            return new(app, client, postgres, directory, access, credential);
        }
        public async Task WithDb(Func<HelpdeskDbContext, Task> action) { await using var scope = app.Services.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>()); }
        public async Task WithIdentity(Func<RatelDeskIdentityDbContext, Task> action) { await using var scope = app.Services.CreateAsyncScope(); await action(scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>()); }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); if (postgres is not null) await postgres.DisposeAsync(); Directory.Delete(directory, true); }
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var actor = Request.Headers.Authorization.ToString().Split(' ').Last();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor), new(ClaimTypes.Role, "HelpdeskAdmin") };
            if (actor is "integration" or "mcp" or "gateway" or "service" or "machine") claims.Add(new("auth_mode", actor));
            if (actor == "service-purpose") claims.Add(new("token_use", "rateldesk_service"));
            if (actor == "execution-purpose") claims.Add(new("token_use", "mcp_execution"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}

