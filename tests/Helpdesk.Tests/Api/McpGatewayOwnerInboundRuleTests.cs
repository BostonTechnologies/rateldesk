using System.Net;
using System.Net.Http.Json;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.EmailRules;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class McpGatewayRealAccessPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_can_delete_selected_inbound_rule_preserving_audit_while_ordinary_grants_stay_denied(bool postgreSql)
    {
        await using var harness = postgreSql
            ? await GatewayAccessHarness.CreatePostgreSqlAsync(unscopedData: true)
            : await GatewayAccessHarness.CreateAsync(unscopedData: true);
        await harness.SetOwnerAdministratorAsync();
        var owner = await harness.IssueCredentialAsync(OwnerGrantRequest());
        var ownerExecution = await harness.DelegateAsync(owner);
        var ordinary = await harness.IssueCredentialAsync(OwnerGrantRequest() with
        {
            GrantKind = IntegrationCredentialGrantKinds.Ordinary,
            OwnerAdministrationConsent = false,
            Permissions = [HelpdeskPermissions.IncidentRead]
        });
        var ordinaryExecution = await harness.DelegateAsync(ordinary);

        // The rule belongs to the organization outside the ordinary credential's ceiling.
        var create = new CreateInboundEmailRuleRequest(InboundEmailRuleScopeType.Tenant, "org-b", null,
            "Owned inbound rule", null, false, 9000,
            [new(InboundEmailRuleConditionType.IsForwardedEmail)],
            [new(InboundEmailRuleActionType.CreateIncidentForOriginalForwardedSender)], true);
        using var createdResponse = await harness.SendAsync(ownerExecution, HttpMethod.Post,
            "/api/v1/inbound-email-rules", create);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<InboundEmailRuleDto>();
        Assert.NotNull(created);
        var path = $"/api/v1/inbound-email-rules/{Uri.EscapeDataString(created.Id)}";
        await harness.SeedInboundRuleAuditAsync(created.Id);

        using var denied = await harness.SendAsync(ordinaryExecution, HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var unchanged = await harness.GetAsync(ownerExecution, path);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        using var deleted = await harness.SendAsync(ownerExecution, HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await harness.GetAsync(ownerExecution, path);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var list = await harness.GetAsync<List<InboundEmailRuleDto>>(ownerExecution,
            "/api/v1/inbound-email-rules?tenantId=org-b");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(list.Body!, rule => rule.Id == created.Id);
        var audit = await harness.GetAsync<List<InboundEmailRuleAuditDto>>(ownerExecution,
            "/api/v1/inbound-email-rules/audit?tenantId=org-b&messageId=owned-rule-message");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var retained = Assert.Single(audit.Body!);
        Assert.Equal(created.Id, retained.RuleId);
        Assert.Equal(InboundEmailProcessingStatus.Succeeded, retained.Status);
        Assert.Equal("owned-original-ticket", retained.TicketId);

        using var repeated = await harness.SendAsync(ownerExecution, HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        var auditAfterReplay = await harness.GetAsync<List<InboundEmailRuleAuditDto>>(ownerExecution,
            "/api/v1/inbound-email-rules/audit?tenantId=org-b&messageId=owned-rule-message");
        Assert.Equal(retained, Assert.Single(auditAfterReplay.Body!));
    }

    internal sealed partial class GatewayAccessHarness
    {
        public async Task SeedInboundRuleAuditAsync(string ruleId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.InboundEmailProcessingLogs.Add(new InboundEmailProcessingLog
            {
                MessageId = "owned-rule-message", TenantId = "org-b", RuleId = ruleId,
                MailboxKey = "default", ActionKey = "CreateIncidentForOriginalForwardedSender",
                Matched = true, Status = InboundEmailProcessingStatus.Succeeded,
                TicketId = "owned-original-ticket"
            });
            await db.SaveChangesAsync();
        }
    }
}
