using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Helpdesk.API.Endpoints.ServiceLink;
using Helpdesk.Application.Notifications;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.ServiceLink;
using Helpdesk.Shared.DTOs.Notification;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class ServiceLinkFailureReportingTests
{
    [Theory]
    [InlineData("organization-disabled", "invalid-organization")]
    [InlineData("unsupported-peer", "unsupported-peer")]
    [InlineData("invalid-browser-session", "session-expired")]
    [InlineData("invalid-browser-state", "invalid-proof")]
    [InlineData("invalid-local-consent", "invalid-proof")]
    [InlineData("https://secret.invalid/proof", "not-authorized")]
    public async Task Handled_failure_has_safe_diagnostics_and_personal_notification_in_an_independent_scope(string code, string expected)
    {
        var captured = new Captured();
        var services = new ServiceCollection().AddLogging().AddScoped<ScopeMarker>();
        services.AddScoped<INotificationService>(provider =>
        {
            var marker = provider.GetRequiredService<ScopeMarker>();
            var notifications = Substitute.For<INotificationService>();
            notifications.CreateNotificationAsync(Arg.Any<CreateNotificationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var value = call.Arg<CreateNotificationRequest>();
                captured.Actor = value.UserId; captured.Tenant = value.TenantId; captured.Scope = marker.Id;
                captured.Link = value.Link;
                captured.Code = value.Reference;
                return Task.CompletedTask;
            });
            return notifications;
        });
        await using var provider = services.BuildServiceProvider();
        await using var requestScope = provider.CreateAsyncScope();
        var marker = requestScope.ServiceProvider.GetRequiredService<ScopeMarker>();
        var context = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/admin/service-links/start";
        context.Request.Headers["X-Correlation-Id"] = "https://secret.invalid/proof";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture-actor")], "interactive"));
        var result = await ServiceLinkEndpoints.Respond<string>(context,
            () => throw new ServiceLinkProtocolException(403, code, "https://secret.invalid/proof"));
        var problem = Assert.IsType<ProblemDetails>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(403, problem.Status);
        Assert.Equal("start", problem.Extensions["stage"]);
        Assert.NotNull(ServiceLinkFailure.NormalizeCorrelation(problem.Extensions["correlationId"] as string));
        Assert.DoesNotContain("secret.invalid", problem.Title!);
        Assert.Equal(expected, captured.Code);
        Assert.Equal("fixture-actor", captured.Actor);
        Assert.Null(captured.Tenant);
        Assert.NotEqual(marker.Id, captured.Scope);
        Assert.Equal("/account/integration-credentials", captured.Link);
    }

    [Theory]
    [InlineData("GET", null)]
    [InlineData("POST", "service_principal_id")]
    [InlineData("POST", "integration_credential_id")]
    [InlineData("POST", "netratel_integration_credential_id")]
    public async Task Reads_and_service_credentials_do_not_emit_operator_notifications(string method, string? credentialClaim)
    {
        var captured = new Captured();
        var services = new ServiceCollection().AddLogging().AddScoped<ScopeMarker>();
        services.AddScoped<INotificationService>(provider =>
        {
            var marker = provider.GetRequiredService<ScopeMarker>();
            var notifications = Substitute.For<INotificationService>();
            notifications.CreateNotificationAsync(Arg.Any<CreateNotificationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var value = call.Arg<CreateNotificationRequest>();
                captured.Actor = value.UserId; captured.Tenant = value.TenantId; captured.Scope = marker.Id;
                captured.Link = value.Link;
                captured.Code = value.Reference;
                return Task.CompletedTask;
            });
            return notifications;
        });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = method;
        context.Request.Path = "/api/v1/admin/service-links/start";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "fixture-actor") };
        if (credentialClaim is not null) claims.Add(new(credentialClaim, "fixture-credential"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture"));
        await ServiceLinkEndpoints.Respond<string>(context, () => throw new ServiceLinkProtocolException(403, "administrator-required", "raw-private-detail"));
        Assert.Null(captured.Actor);
    }

    [Fact]
    public void Unknown_display_fields_are_discarded_and_network_marker_survives_transport_wrapping()
    {
        var failure = ServiceLinkFailure.From("arbitrary-peer-code", "https://secret.invalid/proof", "private-trace");
        Assert.Equal("invalid-request", failure.Code);
        Assert.Equal("request", failure.Stage);
        Assert.Null(failure.CorrelationId);
        var error = Assert.Throws<ArgumentException>(() => IntegrationEndpointPolicy.ValidateServiceLink(new Uri("https://127.0.0.1"), "fixture"));
        Assert.True(ServiceLinkFailureReporting.IsNetworkPolicyFailure(new HttpRequestException("not surfaced", error)));
        Assert.False(ServiceLinkFailureReporting.IsNetworkPolicyFailure(new HttpRequestException("network policy rejected")));
    }

    [Theory]
    [InlineData("organization-disabled", "organization-disabled")]
    [InlineData("unsupported-peer", "unsupported-peer")]
    [InlineData("unknown-secret-code", "peer-operation-failed")]
    public async Task Peer_error_propagates_only_bounded_allowlisted_code(string code, string expected)
    {
        using var client = new HttpClient(new FailureHandler(code));
        var transport = new ServiceLinkTransport(client, Microsoft.Extensions.Options.Options.Create(new ServiceLinkOptions()));
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() =>
            transport.GetAsync<object>("https://peer.example.invalid/protocol", CancellationToken.None));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("private-peer-proof", error.Message);
        Assert.DoesNotContain("secret.invalid", error.Message);
    }

    private sealed class FailureHandler(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { code,
                    title = "private-peer-proof", detail = "https://secret.invalid/private" }))
            });
    }

    private sealed class ScopeMarker { public Guid Id { get; } = Guid.NewGuid(); }
    private sealed class Captured
    {
        public string? Actor { get; set; }
        public string? Tenant { get; set; }
        public string? Code { get; set; }
        public string? Link { get; set; }
        public Guid Scope { get; set; }
    }

}
