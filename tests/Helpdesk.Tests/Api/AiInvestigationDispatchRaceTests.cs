using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.AiAssistant;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class AiInvestigationDispatchRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_callback_during_webhook_dispatch_survives_exhausted_transport_failure(bool postgres)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new ECDsaSecurityKey(signingKey);
        var capture = new RejectingWebhook();
        await using var h = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddSingleton<IHttpClientFactory>(capture);
                services.AddAiAgentOrOwnerMcp();
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
                    options.AddPolicy("HelpdeskStaff", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
                });
            }, configureApp: app => app.MapAiAssistantAiAssistantEndpoints());
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "https://ai-identity.example.test", "synthetic-ai-api", [new Claim("sub", "synthetic-ai-reporter")],
            DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256)));
        using var configResponse = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/ai-assistant-webhooks/",
            new UpsertAiAssistantWebhookConfigurationDto(h.OrganizationId, "Synthetic terminal race", [AiAssistantTicketArea.Incidents],
                "https://ai-capture.example.test/webhook", null, null, null, SigningSecret: "synthetic-ai-signing-secret"));
        Assert.Equal(HttpStatusCode.Created, configResponse.StatusCode);
        var config = (await configResponse.Content.ReadFromJsonAsync<AiAssistantWebhookConfigurationDto>())!;

        foreach (var terminal in new[] { AiInvestigationStatus.Completed, AiInvestigationStatus.Cancelled })
        {
            var ticketId = $"synthetic-dispatch-{terminal}";
            var eventId = $"synthetic-terminal-{terminal}";
            var runReference = $"synthetic-run-{terminal}";
            await h.EditAsync(db =>
            {
                db.Incidents.Add(new Incident
                {
                    Id = ticketId, OrganizationId = h.OrganizationId, CustomerId = h.CustomerId,
                    Title = "Synthetic dispatch ordering fixture"
                });
                return Task.CompletedTask;
            });
            Guid? callbackHistoryId = null;
            capture.Callback = async (payload, ct) =>
            {
                Assert.Equal(ticketId, payload.GetProperty("ticket").GetProperty("id").GetString());
                Assert.Equal(h.OrganizationId, payload.GetProperty("tenantId").GetString());
                var invocationId = payload.GetProperty("invocationId").GetGuid();
                var correlationId = payload.GetProperty("correlationId").GetString()!;
                using var callback = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/ai-assistant/investigations/{invocationId}/worklog")
                {
                    Content = JsonContent.Create(new AppendAiInvestigationWorklogDto(eventId, ticketId, "incidents", correlationId,
                        terminal, "info", "Synthetic terminal evidence before webhook response.", null, null, runReference,
                        DateTimeOffset.UtcNow))
                };
                callback.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var reported = await h.Client.SendAsync(callback, ct);
                Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);
                await h.EditAsync(async db =>
                {
                    var entry = await db.AiInvestigationWorklogEntries.SingleAsync(item => item.CallbackEventId == eventId);
                    callbackHistoryId ??= entry.Id;
                    Assert.Equal(callbackHistoryId, entry.Id);
                    Assert.Equal(invocationId, entry.InvocationId);
                    Assert.Equal(terminal, (await db.AiInvestigationInvocations.SingleAsync(item => item.Id == invocationId)).Status);
                });
            };
            var previousAttempts = capture.Attempts;
            using var dispatched = await h.AdminAsync(HttpMethod.Post, $"/api/v1/incidents/{ticketId}/ai-assistant/investigations",
                new DispatchAiInvestigationDto(config.Id, "Inspect the synthetic dispatch race.", $"synthetic-race-{terminal}"));
            Assert.Equal(HttpStatusCode.Accepted, dispatched.StatusCode);
            var invocation = (await dispatched.Content.ReadFromJsonAsync<AiInvestigationInvocationDto>())!;
            Assert.Equal(terminal, invocation.Status);
            Assert.Equal(runReference, invocation.AiAssistantRunReference);
            Assert.Equal(3, capture.Attempts - previousAttempts);
            await h.EditAsync(async db =>
            {
                var stored = await db.AiInvestigationInvocations.SingleAsync(item => item.Id == invocation.Id);
                Assert.Equal(terminal, stored.Status);
                Assert.Equal(runReference, stored.AiAssistantRunReference);
                Assert.Equal(3, stored.DispatchAttempts);
                Assert.Null(stored.DispatchedUtc);
                var history = await db.AiInvestigationWorklogEntries.SingleAsync(item => item.InvocationId == invocation.Id && item.Source == AiInvestigationSource.AiAssistant);
                Assert.Equal(callbackHistoryId, history.Id);
                Assert.Equal(eventId, history.CallbackEventId);
                Assert.Equal(terminal, history.Status);
                Assert.Equal(h.OrganizationId, history.OrganizationId);
                Assert.Equal(ticketId, history.TicketId);
                Assert.Equal(invocation.CorrelationId, history.CorrelationId);
                Assert.Equal("AiAssistant returned 503.", (await db.AiAssistantWebhookConfigurations.SingleAsync(item => item.Id == config.Id)).LastHealthMessage);
                Assert.Equal(TicketState.New, (await db.Incidents.SingleAsync(item => item.Id == ticketId)).State);
                Assert.False(await db.WorkLogs.AnyAsync(item => item.TicketId == ticketId));
            });
        }
    }

    private sealed class RejectingWebhook : IHttpClientFactory
    {
        public Func<JsonElement, CancellationToken, Task>? Callback { get; set; }
        public int Attempts { get; private set; }
        public HttpClient CreateClient(string name) => new(new CallbackHandler(this));

        private sealed class CallbackHandler(RejectingWebhook owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("https://ai-capture.example.test/webhook", request.RequestUri!.AbsoluteUri);
                Assert.True(request.Headers.Contains("X-Webhook-Signature"));
                owner.Attempts++;
                var callback = owner.Callback ?? throw new InvalidOperationException("Synthetic callback must be configured before dispatch.");
                var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
                await callback(payload, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
        }
    }
}
