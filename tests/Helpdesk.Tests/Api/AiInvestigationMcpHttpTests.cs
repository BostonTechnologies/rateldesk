using System.IdentityModel.Tokens.Jwt;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.API.Endpoints.AiAssistant;
using Helpdesk.API.Endpoints.Ops;
using Helpdesk.API.Ops;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class AiInvestigationMcpHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_ai_identity_reports_to_a_real_dispatched_invocation_with_binding_and_duplicate_readbacks(bool postgres)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new ECDsaSecurityKey(signingKey);
        var capture = new WebhookCapture();
        var logs = new AiAgentOpsLogBuffer();
        string logTrace;
        using (var activity = new Activity("Synthetic matching trace").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            logTrace = activity.TraceId.ToString();
            logs.Add(LogLevel.Information, "Synthetic", new EventId(1), "Selected synthetic diagnostic", null);
        }
        using (new Activity("Synthetic other trace").SetIdFormat(ActivityIdFormat.W3C).Start())
            logs.Add(LogLevel.Information, "Synthetic", new EventId(2), "Other synthetic diagnostic", null);
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddSingleton<IHttpClientFactory>(capture);
                services.AddSingleton(logs);
                services.AddAuthentication().AddJwtBearer("AuthentikAiAgent", options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true, ValidIssuer = "https://ai-identity.example.test",
                        ValidateAudience = true, ValidAudience = "synthetic-ai-api",
                        ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                        ValidateLifetime = true, ClockSkew = TimeSpan.Zero
                    };
                });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("AuthentikAiAgentApi", policy => policy.AddAuthenticationSchemes("AuthentikAiAgent").RequireAuthenticatedUser());
                    options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
                    options.AddPolicy("HelpdeskStaff", policy => policy.RequireAuthenticatedUser().RequireRole(
                        "HelpdeskAdmin", "Incident.Manager", "Request.Manager", "Change.Manager", "Incident.Write", "Request.Write", "Change.Write"));
                });
            }, configureApp: app => { app.MapAiAssistantAiAssistantEndpoints(); app.MapAiAgentOpsEndpoints(); });

        const string ticketId = "synthetic-ai-incident";
        await h.EditAsync(db =>
        {
            db.Incidents.Add(new Incident { Id = ticketId, OrganizationId = h.OrganizationId, CustomerId = h.CustomerId, Title = "Synthetic AI fixture", Description = "Captured investigation" });
            return Task.CompletedTask;
        });
        using var configResponse = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/ai-assistant-webhooks/",
            new UpsertAiAssistantWebhookConfigurationDto(h.OrganizationId, "Captured synthetic AI", [AiAssistantTicketArea.Incidents],
                "https://ai-capture.example.test/webhook", null, null, null, SigningSecret: "synthetic-ai-signing-secret"));
        Assert.Equal(HttpStatusCode.Created, configResponse.StatusCode);
        var config = (await configResponse.Content.ReadFromJsonAsync<AiAssistantWebhookConfigurationDto>())!;
        var dispatch = new DispatchAiInvestigationDto(config.Id, "Inspect the synthetic fixture and report findings.", "synthetic-ai-dispatch");
        using var dispatched = await h.AdminAsync(HttpMethod.Post, $"/api/v1/incidents/{ticketId}/ai-assistant/investigations", dispatch);
        Assert.Equal(HttpStatusCode.Accepted, dispatched.StatusCode);
        var invocation = (await dispatched.Content.ReadFromJsonAsync<AiInvestigationInvocationDto>())!;
        Assert.Single(capture.Deliveries);
        Assert.Equal(invocation.Id.ToString(), capture.Deliveries[0].GetProperty("invocationId").GetString());
        Assert.Equal(h.OrganizationId, capture.Deliveries[0].GetProperty("tenantId").GetString());
        Assert.Equal(invocation.CorrelationId, capture.Deliveries[0].GetProperty("mcpContext").GetProperty("correlationId").GetString());

        using var dispatchAgain = await h.AdminAsync(HttpMethod.Post, $"/api/v1/incidents/{ticketId}/ai-assistant/investigations", dispatch);
        Assert.Equal(HttpStatusCode.Accepted, dispatchAgain.StatusCode);
        Assert.Single(capture.Deliveries);

        string Token(SecurityKey tokenKey) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "https://ai-identity.example.test", "synthetic-ai-api", [new Claim("sub", "synthetic-ai-reporter")],
            DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(tokenKey, SecurityAlgorithms.EcdsaSha256)));
        HelpdeskTools Tools(string token)
        {
            var client = new HelpdeskAgentClient(AgentClientConfiguration.Empty with
            {
                ApiBaseUrl = "https://receiver.example.test", CredentialMode = "gateway"
            }, new ServerClientFactory(h.App.GetTestServer()), new TestLifetime(), new FixedToken(token));
            return new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine("work", "beta4", "unused-ai-profile.json")));
        }
        var tools = Tools(Token(key));
        var correlatedLogs = await tools.helpdesk_logs("search", JsonSerializer.SerializeToElement(new { correlationId = logTrace, limit = 10 }));
        Assert.True(correlatedLogs.Success, correlatedLogs.Summary);
        Assert.Equal("Selected synthetic diagnostic", Assert.Single(correlatedLogs.Data!["items"]!.AsArray())!["message"]!.GetValue<string>());
        var cursorLogs = await tools.helpdesk_logs("search", JsonSerializer.SerializeToElement(new { since = 1, limit = 10 }));
        Assert.True(cursorLogs.Success, cursorLogs.Summary);
        Assert.Equal("Other synthetic diagnostic", Assert.Single(cursorLogs.Data!["items"]!.AsArray())!["message"]!.GetValue<string>());
        var absentLogs = await tools.helpdesk_logs("search", JsonSerializer.SerializeToElement(new { contains = "absent-synthetic-diagnostic" }));
        Assert.True(absentLogs.Success, absentLogs.Summary);
        Assert.Empty(absentLogs.Data!["items"]!.AsArray());
        object Report(string eventId, string status, string? ticket = null, string? correlation = null) => new
        {
            invocationId = invocation.Id, eventId, ticketId = ticket ?? ticketId, ticketType = "incidents",
            correlationId = correlation ?? invocation.CorrelationId, status, message = "Synthetic investigation evidence.", runReference = "synthetic-ai-run"
        };
        async Task<HelpdeskToolResponse> ReportAsync(object payload) => await tools.helpdesk_ai_assistant("report", JsonSerializer.SerializeToElement(payload));

        var progress = await ReportAsync(Report("synthetic-progress", "Progress"));
        Assert.True(progress.Success, progress.Summary);
        var completed = await ReportAsync(Report("synthetic-completed", "Completed"));
        Assert.True(completed.Success, completed.Summary);
        // Duplicate callbacks retain their existing event and do not move the invocation backwards.
        var duplicate = await ReportAsync(Report("synthetic-progress", "Progress"));
        Assert.True(duplicate.Success, duplicate.Summary);
        await h.EditAsync(async db =>
        {
            var stored = await db.AiInvestigationInvocations.SingleAsync();
            Assert.Equal(AiInvestigationStatus.Completed, stored.Status);
            Assert.Equal("synthetic-ai-run", stored.AiAssistantRunReference);
            Assert.NotNull(stored.DispatchedUtc);
            var callbacks = await db.AiInvestigationWorklogEntries.Where(entry => entry.Source == AiInvestigationSource.AiAssistant).ToListAsync();
            Assert.Equal(2, callbacks.Count);
            Assert.All(callbacks, entry => { Assert.Equal(h.OrganizationId, entry.OrganizationId); Assert.Equal(ticketId, entry.TicketId); Assert.Equal(invocation.CorrelationId, entry.CorrelationId); });
            Assert.Equal(TicketState.New, (await db.Incidents.SingleAsync()).State);
            Assert.Equal(0, await db.WorkLogs.CountAsync());
        });

        foreach (var invalidBinding in new[] { Report("wrong-ticket", "Progress", ticket: "foreign-ticket"), Report("wrong-correlation", "Progress", correlation: "foreign-correlation") })
        {
            var rejected = await ReportAsync(invalidBinding);
            Assert.False(rejected.Success);
            Assert.Equal(400, rejected.Failure!.UpstreamStatus);
        }
        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var denied = await Tools(Token(new ECDsaSecurityKey(foreignKey))).helpdesk_ai_assistant("report", JsonSerializer.SerializeToElement(Report("unauthorized", "Progress")));
        Assert.False(denied.Success);
        Assert.Equal(401, denied.Failure!.UpstreamStatus);
        var invalidStatus = await ReportAsync(Report("invalid-status", "999"));
        Assert.False(invalidStatus.Success);
        Assert.Equal("validation_failed", invalidStatus.Status);
        await h.EditAsync(async db =>
        {
            Assert.Equal(2, await db.AiInvestigationWorklogEntries.CountAsync(entry => entry.Source == AiInvestigationSource.AiAssistant));
            Assert.Equal(AiInvestigationStatus.Completed, (await db.AiInvestigationInvocations.SingleAsync()).Status);
        });
    }

    private sealed class WebhookCapture : IHttpClientFactory
    {
        public List<JsonElement> Deliveries { get; } = [];
        public HttpClient CreateClient(string name) => new(new CaptureHandler(this));
        private sealed class CaptureHandler(WebhookCapture owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("https://ai-capture.example.test/webhook", request.RequestUri!.AbsoluteUri);
                Assert.True(request.Headers.Contains("X-Webhook-Signature"));
                owner.Deliveries.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
        }
    }
    private sealed class ServerClientFactory(TestServer server) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(server.CreateHandler()) { BaseAddress = new Uri("https://receiver.example.test") };
    }
    private sealed class FixedToken(string token) : IAgentAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }
    private sealed class TestLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
