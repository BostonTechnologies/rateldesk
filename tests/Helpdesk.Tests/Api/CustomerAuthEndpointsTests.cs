using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Helpdesk.API;
using Helpdesk.Application.Services.Email;
using Helpdesk.Tests.Mcp;
using NSubstitute;
using Helpdesk.Infrastructure.Auth.Authentik;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class CustomerAuthEndpointsTests
{
    [Fact]
    public async Task ResendInvite_ReturnsServiceUnavailable_WhenAuthentikAdminIsNotConfigured()
    {
        using var factory = CreateFactory(new ThrowingInvitationService(
            new AuthentikAdminConfigurationException("Customer invitation is not configured. Set Authentication:AuthentikAdmin:ApiToken for this environment.")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.PostAsync("/api/v1/customers/customer-1/auth/resend-invite", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Customer invitation is not configured", body, StringComparison.Ordinal);
        Assert.Contains("Authentication:AuthentikAdmin:ApiToken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invite_ForUnlinkedCustomer_WhenProviderIsUnconfigured_LeavesNoProvisionalLink()
    {
        var invitationService = new ThrowingInvitationService(new InvalidOperationException("The invitation service must not be called."));
        using var factory = CreateFactory(invitationService, authentikConfigured: false);
        await SeedCustomerAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var status = await client.GetFromJsonAsync<CustomerAuthStatusDto>("/api/v1/customers/customer-1/auth/status");
        var response = await client.PostAsync("/api/v1/customers/customer-1/auth/invite", null);

        Assert.NotNull(status);
        Assert.False(status.CanInvite);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, invitationService.InvocationCount);
        await AssertSeedVisibleFromNewScopeAsync(factory, 0);
    }

    [Fact]
    public async Task Invite_ReturnsBadRequest_WhenAuthentikRejectsClientRequest()
    {
        using var factory = CreateFactory(new ThrowingInvitationService(
            new AuthentikAdminRequestException(
                HttpStatusCode.BadRequest,
                "create user",
                "api/v3/core/users/",
                "username already exists")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.PostAsync("/api/v1/customers/customer-1/auth/invite", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Customer invitation request could not be completed", body, StringComparison.Ordinal);
        Assert.Contains("Authentik rejected create user (400 BadRequest): username already exists", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invite_ReturnsBadGateway_WhenAuthentikFailsServerRequest()
    {
        using var factory = CreateFactory(new ThrowingInvitationService(
            new AuthentikAdminRequestException(
                HttpStatusCode.InternalServerError,
                "create recovery link",
                "api/v3/core/users/123/recovery/",
                "upstream failure")));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.PostAsync("/api/v1/customers/customer-1/auth/invite", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Customer invitation request could not be completed", body, StringComparison.Ordinal);
        Assert.Contains("Authentik rejected create recovery link (500 InternalServerError): upstream failure", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Local", 1, "local account")]
    [InlineData("Authentik", 2, "Multiple linked identities")]
    public async Task ExternalInviteAction_RejectsLocalOrAmbiguousLinks_BeforeCallingInvitationService(string provider, int linkCount, string expectedMessage)
    {
        var invitationService = new ThrowingInvitationService(new InvalidOperationException("The invitation service must not be called."));
        using var factory = CreateFactory(invitationService);
        await SeedLinksAsync(factory, provider, linkCount);
        await AssertSeedVisibleFromNewScopeAsync(factory, linkCount);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");

        var response = await client.PostAsync("/api/v1/customers/customer-1/auth/resend-invite", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(expectedMessage, (await response.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, invitationService.InvocationCount);
        await AssertSeedVisibleFromNewScopeAsync(factory, linkCount);
    }

    [Fact]
    public async Task CreateUserAsync_ThrowsAuthentikRequestException_WithSanitizedResponseBody()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"username\":[\"already exists\"]}\n", Encoding.UTF8, "application/json")
            }));
        var client = new AuthentikAdminClient(
            httpClient,
            Options.Create(new AuthentikOptions
            {
                BaseUrl = "https://auth.example.test/",
                ApiToken = "test-token"
            }));

        var ex = await Assert.ThrowsAsync<AuthentikAdminRequestException>(
            () => client.CreateUserAsync("Test User", "test@example.test"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("create user", ex.Operation);
        Assert.Equal("api/v3/core/users/", ex.Path);
        Assert.Equal("{\"username\":[\"already exists\"]}", ex.ResponseBody);
        Assert.Contains("Authentik rejected create user (400 BadRequest): {\"username\":[\"already exists\"]}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateUserAsync_SendsEmailAsUsername()
    {
        string? requestBody = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"pk\":123,\"username\":\"security@example.com\",\"name\":\"Example User\",\"email\":\"security@example.com\",\"is_active\":true}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var client = new AuthentikAdminClient(
            httpClient,
            Options.Create(new AuthentikOptions
            {
                BaseUrl = "https://auth.example.test/",
                ApiToken = "test-token"
            }));

        await client.CreateUserAsync(" Example User ", " security@example.com ");

        Assert.NotNull(requestBody);
        var payload = JsonNode.Parse(requestBody!)!;
        Assert.Equal("security@example.com", payload["username"]?.GetValue<string>());
        Assert.Equal("Example User", payload["name"]?.GetValue<string>());
        Assert.Equal("security@example.com", payload["email"]?.GetValue<string>());
        Assert.Equal(true, payload["is_active"]?.GetValue<bool>());
        Assert.Equal("users", payload["path"]?.GetValue<string>());
        Assert.Equal("internal", payload["type"]?.GetValue<string>());
    }

    [Fact]
    public async Task MCP_modern_routes_execute_supported_customer_identity_actions_with_isolated_invitation_capture()
    {
        var authentik = Substitute.For<IAuthentikAdminClient>();
        var external = new AuthentikUser { Id = 42, Name = "Campaign customer", Username = "campaign-customer@example.test", Email = "campaign-customer@example.test", IsActive = true };
        authentik.FindUserByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AuthentikUser?)null);
        authentik.CreateUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(external);
        authentik.GetUserAsync("42", Arg.Any<CancellationToken>()).Returns(_ => external);
        var issued = 0;
        authentik.CreateRecoveryLinkAsync("42", Arg.Any<CancellationToken>()).Returns(_ => "https://capture.example.test/activate/" + ++issued);
        var providerGroups = new HashSet<string>(StringComparer.Ordinal);
        authentik.AssignUserToGroupAsync("42", "campaign-customers", Arg.Any<CancellationToken>()).Returns(_ => { providerGroups.Add("campaign-customers"); return Task.CompletedTask; });
        authentik.RemoveUserFromGroupAsync("42", "campaign-customers", Arg.Any<CancellationToken>()).Returns(_ => { providerGroups.Remove("campaign-customers"); return Task.CompletedTask; });
        var capture = new CapturingInvitationEmail();
        using var factory = CreateFactory(null, authentik: authentik, capture: capture);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Organizations.Add(new Organization { Id = "campaign-org", Name = "Campaign organization", IsEnabled = true });
            db.Customers.Add(new Customer { Id = "campaign-customer", OrganizationId = "campaign-org", Name = "Campaign customer", Email = "campaign-customer@example.test", IsEnabled = true });
            await db.SaveChangesAsync();
            Assert.Equal(CustomerInviteStatus.NotInvited, (await scope.ServiceProvider.GetRequiredService<ICustomerInvitationService>().GetStatusAsync("campaign-customer")).InviteStatus);
        }
        using var http = factory.CreateClient();
        var tools = TicketAdapterHttpTests.Tools(http, "HelpdeskAdmin");
        var request = System.Text.Json.JsonSerializer.SerializeToElement(new { customerId = "campaign-customer" });
        var status = await tools.helpdesk_customers("auth_status", request);
        Assert.True(status.Success, status.Summary);
        Assert.Equal((int)CustomerInviteStatus.NotInvited, status.Data!["inviteStatus"]!.GetValue<int>());
        var preview = await tools.helpdesk_admin_mutations("customer_invite", request);
        Assert.Equal("confirmation_required", preview.Status);
        Assert.Empty(capture.Messages);
        var invite = await tools.helpdesk_admin_mutations("customer_invite", request, true);
        Assert.True(invite.Success, invite.Summary);
        Assert.Equal((int)CustomerInviteStatus.Pending, invite.Data!["inviteStatus"]!.GetValue<int>());
        Assert.Equal("campaign-customers", Assert.Single(providerGroups));
        Assert.Equal("campaign-customer@example.test", Assert.Single(capture.Messages).Recipient);
        Assert.Contains("https://capture.example.test/activate/1", capture.Messages[0].Html, StringComparison.Ordinal);
        await AssertLink(CustomerInviteStatus.Pending, link => { Assert.Equal("42", link.AuthentikUserId); Assert.NotNull(link.InviteSentAtUtc); Assert.NotNull(link.InviteLinkExpiresAtUtc); });
        // Repeating invite is idempotent while pending. Resend intentionally issues a fresh link.
        Assert.True((await tools.helpdesk_admin_mutations("customer_invite", request, true)).Success);
        Assert.Single(capture.Messages);
        var resend = await tools.helpdesk_admin_mutations("customer_resend_invite", request, true);
        Assert.True(resend.Success, resend.Summary);
        Assert.Equal(2, capture.Messages.Count);
        Assert.Contains("https://capture.example.test/activate/2", capture.Messages[1].Html, StringComparison.Ordinal);
        external.LastLogin = new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);
        external.Username = "synced-customer";
        var sync = await tools.helpdesk_admin_mutations("customer_sync_authentik", request, true);
        Assert.True(sync.Success, sync.Summary);
        await AssertLink(CustomerInviteStatus.Active, link => { Assert.Equal("synced-customer", link.AuthentikUsername); Assert.Equal(external.LastLogin, link.LastLoginAtUtc); Assert.NotNull(link.LastAuthSyncAtUtc); Assert.NotNull(link.InviteAcceptedAtUtc); });
        var disable = await tools.helpdesk_admin_mutations("customer_disable_login", request, true);
        Assert.True(disable.Success, disable.Summary);
        await AssertLink(CustomerInviteStatus.Disabled, link => { Assert.NotNull(link.DisabledAtUtc); Assert.Equal("Test", link.DisabledByUserId); });
        Assert.Empty(providerGroups);
        await authentik.Received(1).RemoveUserFromGroupAsync("42", "campaign-customers", Arg.Any<CancellationToken>());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal(3, await db.ActivityLogs.CountAsync(log => log.RelatedEntityId == "campaign-customer"));
        }
        Assert.Equal(2, capture.Messages.Count);

        async Task AssertLink(CustomerInviteStatus expected, Action<CustomerAuthLink> verify)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var link = await db.CustomerAuthLinks.AsNoTracking().SingleAsync(link => link.CustomerId == "campaign-customer");
            Assert.Equal(expected, link.InviteStatus);
            verify(link);
            var readback = await tools.helpdesk_customers("auth_status", request);
            Assert.True(readback.Success, readback.Summary);
            Assert.Equal((int)expected, readback.Data!["inviteStatus"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task Modern_and_legacy_customer_auth_routes_retain_administrator_authority()
    {
        var invitation = new ThrowingInvitationService(new InvalidOperationException("Unauthorized action must not execute."));
        using var factory = CreateFactory(invitation);
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "Request.Manager");
        foreach (var legacy in new[] { false, true })
        {
            var prefix = legacy ? "/api/admin/customers/customer-1" : "/api/v1/customers/customer-1/auth";
            using var status = await http.GetAsync(prefix + (legacy ? "/auth-status" : "/status"));
            Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
            foreach (var action in new[] { "invite", "resend-invite", "disable-login", "sync-authentik" })
            {
                using var denied = await http.PostAsync(prefix + "/" + action, null);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }
        }
        Assert.Equal(0, invitation.InvocationCount);
    }

    private sealed class CapturingInvitationEmail : IEmailService
    {
        public List<(string Recipient, string Subject, string Html)> Messages { get; } = [];
        public Task<bool> SendEmailAsync(string recipient, string subject, string htmlMessage, IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null, IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null, string? replyTo = null, bool suppressTimeline = false)
        {
            Assert.Equal("campaign-customer@example.test", recipient);
            Assert.True(suppressTimeline);
            Assert.Null(ticketId);
            Assert.True(cc is null || !cc.Any());
            Messages.Add((recipient, subject, htmlMessage));
            return Task.FromResult(true);
        }
        public Task<bool> SendEmailAsync(IEnumerable<string> recipients, string subject, string htmlMessage, IEnumerable<string>? cc = null, CancellationToken ct = default, string? ticketId = null, IEnumerable<EmailAttachmentData>? attachments = null, string? fromName = null, string? replyTo = null, bool suppressTimeline = false) => throw new InvalidOperationException("Only isolated single-recipient invitations are supported by this capture fixture.");
        public Task<bool> TestApiConnectionAsync() => Task.FromResult(true);
    }

    private static WebApplicationFactory<Program> CreateFactory(ICustomerInvitationService? invitationService, bool authentikConfigured = true, IAuthentikAdminClient? authentik = null, IEmailService? capture = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Helpdesk:TestDatabaseName", $"customer-auth-{Guid.NewGuid():N}");
            builder.UseSetting("Authentication:AuthentikAdmin:BaseUrl", "https://auth.example.test/");
            builder.UseSetting("Authentication:AuthentikAdmin:ApiToken", authentikConfigured ? "test-token" : string.Empty);
            builder.UseSetting("Authentication:AuthentikAdmin:CustomerDefaultGroups:0", "campaign-customers");
            builder.UseSetting("StorageOptions:ImageSigningSecret", "synthetic-customer-auth-signing-key-32-characters");
            builder.UseIsolatedTestStorage();
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Development");
            builder.UseEnvironment("Development");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICustomerInvitationService>();
                if (invitationService is not null) services.AddSingleton(invitationService);
                else services.AddScoped<ICustomerInvitationService, CustomerInvitationService>();
                if (authentik is not null) { services.RemoveAll<IAuthentikAdminClient>(); services.AddSingleton(authentik); }
                if (capture is not null) { services.RemoveAll<IEmailService>(); services.AddSingleton(capture); }

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        });

    private static async Task SeedLinksAsync(WebApplicationFactory<Program> factory, string provider, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        db.Customers.Add(new Customer { Id = "customer-1", Name = "Customer", Email = "customer@example.test", OrganizationId = "organization-1" });
        for (var index = 0; index < count; index++)
        {
            db.CustomerAuthLinks.Add(new CustomerAuthLink
            {
                CustomerId = "customer-1",
                AuthProviderType = index == 0 ? provider : "Authentik",
                LocalAccountId = index == 0 && string.Equals(provider, "Local", StringComparison.OrdinalIgnoreCase) ? "local-account" : null
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task SeedCustomerAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        db.Customers.Add(new Customer { Id = "customer-1", Name = "Customer", Email = "customer@example.test", OrganizationId = "organization-1" });
        await db.SaveChangesAsync();
    }

    private static async Task AssertSeedVisibleFromNewScopeAsync(WebApplicationFactory<Program> factory, int expectedLinkCount)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(expectedLinkCount, await db.CustomerAuthLinks.CountAsync(link => link.CustomerId == "customer-1"));
    }

    private sealed class ThrowingInvitationService(Exception exception) : ICustomerInvitationService
    {
        public int InvocationCount { get; private set; }
        public Task<CustomerAuthStatusDto> GetStatusAsync(string customerId, CancellationToken ct = default)
            => Throw();

        public Task<CustomerAuthStatusDto> InviteAsync(string customerId, string invitedByUserId, CancellationToken ct = default)
            => Throw();

        public Task<CustomerAuthStatusDto> ResendInviteAsync(string customerId, string invitedByUserId, CancellationToken ct = default)
            => Throw();

        public Task<CustomerAuthStatusDto> DisableLoginAsync(string customerId, string disabledByUserId, CancellationToken ct = default)
            => Throw();

        public Task<CustomerAuthStatusDto> SyncAuthentikAsync(string customerId, CancellationToken ct = default)
            => Throw();

        private Task<CustomerAuthStatusDto> Throw()
        {
            InvocationCount++;
            return Task.FromException<CustomerAuthStatusDto>(exception);
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
