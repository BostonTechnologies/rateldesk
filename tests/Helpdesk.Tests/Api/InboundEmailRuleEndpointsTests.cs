using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public class InboundEmailRuleEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paged_audit_finds_older_status_date_and_rule_matches_and_preserves_deleted_history(bool postgres)
    {
        await using var harness = await Harness.CreateAsync(postgres);
        await harness.SeedFullAuditAsync();
        var first = await harness.Client.GetFromJsonAsync<InboundEmailAuditPage>("/api/v1/inbound-email-rules/audit/page");
        Assert.Equal(305, first!.Total); Assert.Equal(25, first.Items.Count);
        var older = await harness.Client.GetFromJsonAsync<InboundEmailAuditPage>(
            "/api/v1/inbound-email-rules/audit/page?status=Failed&ruleId=deleted-rule&from=2026-11-01T00:00:00Z&to=2026-11-01T00:01:00Z");
        var match = Assert.Single(older!.Items);
        Assert.Equal(1, older.Total); Assert.Equal("Unknown rule", match.RuleName);
        Assert.Equal("old-message", match.Event.MessageId); Assert.Equal("Retained complete diagnostic", match.Event.Error);
        Assert.Equal("Unknown organization", match.OrganizationName); Assert.Null(match.TicketRoute);
        var search = await harness.Client.GetFromJsonAsync<InboundEmailAuditPage>("/api/v1/inbound-email-rules/audit/page?search=old-message");
        Assert.Equal(match.Event.Id, Assert.Single(search!.Items).Event.Id);
        var tail = await harness.Client.GetFromJsonAsync<InboundEmailAuditPage>("/api/v1/inbound-email-rules/audit/page?page=3&pageSize=100&descending=false");
        Assert.Equal(305, tail!.Total); Assert.Equal(5, tail.Items.Count);
        Assert.Equal("audit-300", tail.Items[0].Event.Id);
        var seen = new HashSet<string>();
        for (var page = 0; page < 4; page++)
            foreach (var row in (await harness.Client.GetFromJsonAsync<InboundEmailAuditPage>($"/api/v1/inbound-email-rules/audit/page?page={page}&pageSize=100&sort=status"))!.Items)
                Assert.True(seen.Add(row.Event.Id));
        Assert.Equal(305, seen.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await harness.Client.GetAsync("/api/v1/inbound-email-rules/audit/page?pageSize=200")).StatusCode);
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "Incident.Read");
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.GetAsync("/api/v1/inbound-email-rules/audit/page")).StatusCode);
    }

    [Fact]
    public async Task Crud_EnableDisable_Reorder_AndAudit_WorkForHelpdeskAdmin()
    {
        await using var harness = await Harness.CreateAsync();
        var create = new CreateInboundEmailRuleRequest(
            InboundEmailRuleScopeType.Global,
            null,
            null,
            "Forwarded support rule",
            "Test",
            false,
            50,
            [new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.IsForwardedEmail)],
            [new InboundEmailRuleActionConfig(InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender)],
            true);

        var createResponse = await harness.Client.PostAsJsonAsync("/api/v1/inbound-email-rules", create);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<InboundEmailRuleDto>();
        Assert.NotNull(created);
        Assert.False(string.IsNullOrWhiteSpace(created!.Id));
        Assert.False(created.Enabled);
        Assert.Contains(created.Id, await harness.RuleIdsAsync());

        var rulePathId = Uri.EscapeDataString(created.Id);
        var enableResponse = await harness.Client.PostAsync($"/api/v1/inbound-email-rules/{rulePathId}/enable", null);
        Assert.True(enableResponse.IsSuccessStatusCode, $"{enableResponse.StatusCode} id={created.Id} body={await enableResponse.Content.ReadAsStringAsync()}");
        var enabled = await enableResponse.Content.ReadFromJsonAsync<InboundEmailRuleDto>();
        Assert.True(enabled!.Enabled);

        var reorderResponse = await harness.Client.PostAsJsonAsync("/api/v1/inbound-email-rules/reorder", new ReorderInboundEmailRulesRequest([new(created.Id, 10)]));
        reorderResponse.EnsureSuccessStatusCode();
        var list = await harness.Client.GetFromJsonAsync<List<InboundEmailRuleDto>>("/api/v1/inbound-email-rules");
        Assert.Single(list!);
        Assert.Equal(10, list![0].Priority);

        await harness.SeedAuditAsync(created.Id);
        var audit = await harness.Client.GetFromJsonAsync<List<InboundEmailRuleAuditDto>>("/api/v1/inbound-email-rules/audit?messageId=message-1");
        Assert.Single(audit!);
        Assert.Equal(InboundEmailProcessingStatus.Succeeded, audit![0].Status);

        var disableResponse = await harness.Client.PostAsync($"/api/v1/inbound-email-rules/{rulePathId}/disable", null);
        disableResponse.EnsureSuccessStatusCode();
        var disabled = await disableResponse.Content.ReadFromJsonAsync<InboundEmailRuleDto>();
        Assert.False(disabled!.Enabled);

        var deleteResponse = await harness.Client.DeleteAsync($"/api/v1/inbound-email-rules/{rulePathId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.DoesNotContain(created.Id, await harness.RuleIdsAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.GetAsync($"/api/v1/inbound-email-rules/{rulePathId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await harness.Client.DeleteAsync($"/api/v1/inbound-email-rules/{rulePathId}")).StatusCode);
        var retainedAudit = await harness.Client.GetFromJsonAsync<List<InboundEmailRuleAuditDto>>("/api/v1/inbound-email-rules/audit?messageId=message-1");
        Assert.Equal(created.Id, Assert.Single(retainedAudit!).RuleId);
    }

    [Fact]
    public async Task Create_RejectsTenantScopeWithoutTenantId()
    {
        await using var harness = await Harness.CreateAsync();
        var create = new CreateInboundEmailRuleRequest(
            InboundEmailRuleScopeType.Tenant,
            null,
            null,
            "Tenant rule",
            null,
            false,
            1,
            [new InboundEmailRuleConditionConfig(InboundEmailRuleConditionType.IsForwardedEmail)],
            [new InboundEmailRuleActionConfig(InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender)],
            true);

        var response = await harness.Client.PostAsJsonAsync("/api/v1/inbound-email-rules", create);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Harness(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        private Testcontainers.PostgreSql.PostgreSqlContainer? container;
        private string? sqlitePath;

        public static async Task<Harness> CreateAsync(bool? postgres = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            var databaseRoot = new InMemoryDatabaseRoot();
            var databaseName = $"inbound-rules-{Guid.NewGuid():N}";
            Testcontainers.PostgreSql.PostgreSqlContainer? container = null;
            string? sqlitePath = null;
            if (postgres == true) { container = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:16").Build(); await container.StartAsync(); }
            else if (postgres == false) sqlitePath = Path.Combine(Path.GetTempPath(), $"inbound-audit-{Guid.NewGuid():N}.db");
            builder.Services.AddDbContext<HelpdeskDbContext>(options =>
            {
                if (container is not null) options.UseNpgsql(container.GetConnectionString());
                else if (sqlitePath is not null) options.UseSqlite($"Data Source={sqlitePath};Pooling=False", sqlite => sqlite.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"));
                else options.UseInMemoryDatabase(databaseName, databaseRoot);
            });
            builder.Services.AddScoped<ITenantContext>(_ => { var tenant = Substitute.For<ITenantContext>(); tenant.IsHelpdeskAdmin.Returns(true); return tenant; });
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("HelpdeskAdmin", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin);
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapInboundEmailRuleEndpoints();
            if (postgres is not null)
            {
                await using var scope = app.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.MigrateAsync();
            }
            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", HelpdeskPermissions.HelpdeskAdmin);
            return new Harness(app, client) { container = container, sqlitePath = sqlitePath };
        }

        public async Task SeedFullAuditAsync()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var first = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            db.InboundEmailProcessingLogs.AddRange(Enumerable.Range(0, 305).Select(i => new InboundEmailProcessingLog
            {
                Id = $"audit-{i:D3}", MessageId = i == 0 ? "old-message" : $"message-{i}",
                TenantId = "missing-org", RuleId = i == 0 ? "deleted-rule" : null,
                ActionKey = "SyntheticAction", Status = i == 0 ? InboundEmailProcessingStatus.Failed : InboundEmailProcessingStatus.Succeeded,
                Error = i == 0 ? "Retained complete diagnostic" : null, CreatedAtUtc = first.AddMinutes(i)
            }));
            await db.SaveChangesAsync();
        }

        public async Task SeedAuditAsync(string ruleId)
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.InboundEmailProcessingLogs.Add(new InboundEmailProcessingLog
            {
                MessageId = "message-1",
                MailboxKey = "default",
                RuleId = ruleId,
                ActionKey = "CreateIncidentForOriginalForwardedSender",
                Matched = true,
                Status = InboundEmailProcessingStatus.Succeeded,
                TicketId = "inc-1"
            });
            await db.SaveChangesAsync();
        }

        public async Task<List<string>> RuleIdsAsync()
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            return await db.InboundEmailRules.Select(x => x.Id).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            if (container is not null) await container.DisposeAsync();
            if (sqlitePath is not null) File.Delete(sqlitePath);
        }
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
