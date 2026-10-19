using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.AiAssistant;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.AgentClient;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

public sealed partial class AiInvestigationMcpHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_recovers_a_simulated_postcommit_presubmit_queue_without_remote_cancel_or_late_reopening(bool postgres)
    {
        var capture = new WebhookCapture();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddSingleton<IHttpClientFactory>(capture);
                services.AddAiAgentOrOwnerMcp();
                services.AddSingleton<McpExecutionTokenService>();
                services.AddAuthentication()
                    .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
                        IntegrationCredentialAuthenticationHandler.McpSchemeName, options => options.Purpose = IntegrationCredentialAuthenticationHandler.McpPurpose)
                    .AddScheme<AuthenticationSchemeOptions, McpExecutionAuthenticationHandler>(McpExecutionAuthenticationHandler.SchemeName, _ => { });
                services.Configure<PolicySchemeOptions>("ReceiverTest", options => options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey("X-Synthetic-Admin") ? "SyntheticAdmin" :
                    context.Request.Headers.Authorization.ToString().StartsWith("Bearer " + McpExecutionTokenService.TokenPrefix, StringComparison.Ordinal)
                        ? McpExecutionAuthenticationHandler.SchemeName : IntegrationCredentialAuthenticationHandler.SchemeName);
                services.AddAuthorization(options =>
                {
                    options.AddPolicy(McpGatewayDelegationEndpoints.DelegationPolicy, policy =>
                        policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.McpSchemeName).RequireAuthenticatedUser());
                    options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
                    options.AddPolicy("HelpdeskStaff", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
                });
            }, configureApp: app => { app.MapAiAssistantAiAssistantEndpoints(); app.MapMcpGatewayDelegationEndpoints(); });

        const string ticketId = "interrupted-ai-ticket";
        const string resource = "https://receiver.example.test/mcp";
        await h.EditAsync(db =>
        {
            db.Incidents.Add(new Incident { Id = ticketId, OrganizationId = h.OrganizationId,
                CustomerId = h.CustomerId, Title = "Synthetic interrupted investigation" });
            return Task.CompletedTask;
        });
        using var configurationResponse = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/ai-assistant-webhooks/",
            new UpsertAiAssistantWebhookConfigurationDto(h.OrganizationId, "Captured recovery fixture", [AiAssistantTicketArea.Incidents],
                "https://ai-capture.example.test/webhook", null, null, null, SigningSecret: "synthetic-recovery-signing-secret"));
        Assert.Equal(HttpStatusCode.Created, configurationResponse.StatusCode);
        var configuration = (await configurationResponse.Content.ReadFromJsonAsync<AiAssistantWebhookConfigurationDto>())!;

        // This fixture represents the exact durable checkpoint after queue commit
        // and before any provider submit. It does not claim to kill a real process.
        var interrupted = new AiInvestigationInvocation { OrganizationId = h.OrganizationId, TicketId = ticketId,
            TicketArea = AiAssistantTicketArea.Incidents, ConfigurationId = configuration.Id, RequestedByUserId = "owner",
            OperatorAssistanceRequest = "Inspect the synthetic interruption.", IdempotencyKey = "interrupted-dispatch",
            CorrelationId = "interrupted-correlation" };
        await h.EditAsync(db =>
        {
            db.AiInvestigationInvocations.Add(interrupted);
            db.AiInvestigationWorklogEntries.Add(new AiInvestigationWorklogEntry { InvocationId = interrupted.Id,
                OrganizationId = h.OrganizationId, TicketId = ticketId, TicketArea = interrupted.TicketArea,
                Source = AiInvestigationSource.Operator, Status = AiInvestigationStatus.Queued,
                Message = "AI assistance requested by operator.", CorrelationId = interrupted.CorrelationId });
            db.AiAssistantWebhookAuditRecords.Add(new AiAssistantWebhookAuditRecord { OrganizationId = h.OrganizationId,
                ConfigurationId = configuration.Id, InvocationId = interrupted.Id, Action = "dispatch-queued", Actor = "owner" });
            return Task.CompletedTask;
        });

        string ingress;
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var issuer = new IntegrationCredentialIssuer(scope.ServiceProvider.GetRequiredService<IIntegrationCredentialOwnerResolver>(),
                scope.ServiceProvider.GetRequiredService<ICurrentUserAccessService>(), scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>(), identity);
            var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"),
                new Claim("auth_mode", "local")], "Synthetic owner consent"));
            var grant = await issuer.ValidateAsync(owner, new IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest(
                "Recovery owner", "mcp", h.OrganizationId, [HelpdeskPermissions.HelpdeskAdmin], 1)
            {
                McpResourceUri = resource, GrantKind = IntegrationCredentialGrantKinds.OwnerAdministration, OwnerAdministrationConsent = true
            }, CancellationToken.None);
            var (record, delivery) = issuer.Create(grant, DateTimeOffset.UtcNow);
            issuer.Track(record);
            await identity.SaveChangesAsync();
            ingress = delivery.Secret;
        }
        using var exchange = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
        exchange.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingress);
        exchange.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, resource);
        using var exchangeResponse = await h.Client.SendAsync(exchange);
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);
        var execution = (await exchangeResponse.Content.ReadFromJsonAsync<McpGatewayDelegationEndpoints.McpExecutionTokenResponse>())!.AccessToken;
        var client = new HelpdeskAgentClient(AgentClientConfiguration.Empty with { ApiBaseUrl = "https://receiver.example.test", CredentialMode = "gateway" },
            new ServerClientFactory(h.App.GetTestServer()), new TestLifetime(), new FixedToken(execution));
        var tools = new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine("work", "beta8", "unused-recovery-profile.json")));
        var selector = JsonSerializer.SerializeToElement(new { ticketType = "incidents", ticketId });

        var read = await tools.helpdesk_ai_assistant("investigation_worklog", selector);
        Assert.True(read.Success, read.Summary);
        var before = Assert.Single(read.Data!.Deserialize<AiInvestigationWorklogEntryDto[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Equal(interrupted.Id, before.InvocationId);
        Assert.Equal(interrupted.CorrelationId, before.CorrelationId);
        Assert.Equal(AiInvestigationStatus.Queued, before.Status);
        Assert.Empty(capture.Deliveries);

        object Dispatch(string key) => new { ticketType = "incidents", ticketId,
            body = new { configurationId = configuration.Id, operatorAssistanceRequest = interrupted.OperatorAssistanceRequest, idempotencyKey = key } };
        var sameKey = await tools.helpdesk_ai_assistant("dispatch_investigation", JsonSerializer.SerializeToElement(Dispatch(interrupted.IdempotencyKey)), true);
        Assert.True(sameKey.Success, sameKey.Summary);
        Assert.Equal(interrupted.Id, Guid.Parse(sameKey.Data!["id"]!.GetValue<string>()));
        Assert.Empty(capture.Deliveries);
        object Report(string eventId, string status) => new { invocationId = interrupted.Id, eventId, ticketId, ticketType = "incidents",
            correlationId = interrupted.CorrelationId, status, message = "Closed local tracking after captured evidence proved that this fixture had no provider submission." };
        var cancelled = await tools.helpdesk_ai_assistant("report", JsonSerializer.SerializeToElement(Report("owner-recovery-cancel", "Cancelled")));
        Assert.True(cancelled.Success, cancelled.Summary);
        Assert.Empty(capture.Deliveries); // The report operation sends no remote cancellation request.
        var duplicate = await tools.helpdesk_ai_assistant("report", JsonSerializer.SerializeToElement(Report("owner-recovery-cancel", "Cancelled")));
        Assert.True(duplicate.Success, duplicate.Summary);
        var late = await tools.helpdesk_ai_assistant("report", JsonSerializer.SerializeToElement(Report("late-provider-start", "Started")));
        Assert.False(late.Success);
        Assert.Equal(400, late.Failure!.UpstreamStatus);
        var after = await tools.helpdesk_ai_assistant("investigation_worklog", selector);
        Assert.True(after.Success, after.Summary);
        var terminal = after.Data!.Deserialize<AiInvestigationWorklogEntryDto[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, terminal.Length);
        Assert.Single(terminal, entry => entry.Status == AiInvestigationStatus.Cancelled && entry.InvocationId == interrupted.Id);
        Assert.Empty(capture.Deliveries);

        // A new key is authorized only after the above independent fixture capture
        // establishes zero submission for the old operation.
        var fresh = await tools.helpdesk_ai_assistant("dispatch_investigation", JsonSerializer.SerializeToElement(Dispatch("reviewed-replacement-dispatch")), true);
        Assert.True(fresh.Success, fresh.Summary);
        Assert.NotEqual(interrupted.Id, Guid.Parse(fresh.Data!["id"]!.GetValue<string>()));
        var submitted = Assert.Single(capture.Deliveries);
        Assert.Equal(fresh.Data["id"]!.ToString(), submitted.GetProperty("invocationId").GetString());
        await h.EditAsync(async db =>
        {
            var stored = await db.AiInvestigationInvocations.AsNoTracking().SingleAsync(invocation => invocation.Id == interrupted.Id);
            Assert.Equal(AiInvestigationStatus.Cancelled, stored.Status);
            Assert.Null(stored.DispatchedUtc);
            Assert.Equal(0, stored.DispatchAttempts);
            Assert.Equal(2, await db.AiInvestigationInvocations.CountAsync());
            Assert.Equal(2, await db.AiInvestigationWorklogEntries.CountAsync(entry => entry.InvocationId == interrupted.Id));
        });
    }
}
