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

        public static async Task<Harness> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            var databaseRoot = new InMemoryDatabaseRoot();
            var databaseName = $"inbound-rules-{Guid.NewGuid():N}";
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseInMemoryDatabase(databaseName, databaseRoot));
            builder.Services.AddScoped<ITenantContext>(_ => Substitute.For<ITenantContext>());
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
            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", HelpdeskPermissions.HelpdeskAdmin);
            return new Harness(app, client);
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
