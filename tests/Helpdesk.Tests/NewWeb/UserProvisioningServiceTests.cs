extern alias NewWeb;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Helpdesk.Shared.DTOs.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NewWeb::HelpDesk.NewWeb.Services;
using NSubstitute;

namespace Helpdesk.Tests.NewWeb;

public sealed class UserProvisioningServiceTests
{
    [Fact]
    public async Task Provisioning_sends_the_verified_user_access_token_without_a_claims_body_or_system_credentials()
    {
        string? authorization = null;
        HttpContent? body = null;
        using var client = new HttpClient(new CaptureHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = request.Content;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CurrentUserAccessDto(true, "Customer", "customer@example.test", "tenant", "Tenant", "contact", false, [], [], [], []))
            };
        })) { BaseAddress = new Uri("https://api.example.test") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("SystemApiNoAuth").Returns(client);
        var service = new UserProvisioningService(factory, NullLogger<UserProvisioningService>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "customer@example.test")], "oidc"));

        var access = await service.EnsureUserAccessAsync(principal, "verified-user-access-token", CancellationToken.None);

        Assert.Equal("Bearer verified-user-access-token", authorization);
        Assert.Null(body);
        Assert.Equal("contact", access!.CustomerId);
        factory.DidNotReceive().CreateClient("SystemApi");
    }

    [Fact]
    public async Task Provisioning_never_substitutes_a_system_token_when_the_user_token_is_missing()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var service = new UserProvisioningService(factory, NullLogger<UserProvisioningService>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "customer@example.test")], "oidc"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureUserAccessAsync(principal, "", CancellationToken.None));
        factory.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid-provisioning-identity")]
    [InlineData(HttpStatusCode.Unauthorized, "api-token-rejected")]
    [InlineData(HttpStatusCode.Forbidden, "account-access-denied")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "provisioning-api-unavailable")]
    public async Task Failure_logs_only_fixed_categories_and_status_without_identity_token_body_or_exception(HttpStatusCode status, string category)
    {
        using var client = new HttpClient(new CaptureHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("private-response-body-marker")
        })) { BaseAddress = new Uri("https://api.example.test") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("SystemApiNoAuth").Returns(client);
        var logger = new RecordingLogger();
        var service = new UserProvisioningService(factory, logger);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "private-identity-marker@example.test")], "oidc"));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.EnsureUserAccessAsync(principal, "private-token-marker", CancellationToken.None));
        var message = Assert.Single(logger.Messages);
        Assert.Contains(category, message, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", message, StringComparison.Ordinal);
        Assert.All(logger.Exceptions, Assert.Null);
    }

    private sealed class RecordingLogger : ILogger<UserProvisioningService>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
