using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Helpdesk.API.Endpoints.Email;
using Helpdesk.Application.Services.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public sealed class OperatorMailSendTruthTests
{
    [Theory]
    [InlineData(false, false, HttpStatusCode.BadGateway, "Not confirmed")]
    [InlineData(false, true, HttpStatusCode.BadGateway, "Not confirmed")]
    [InlineData(true, false, HttpStatusCode.OK, "Accepted by provider")]
    [InlineData(true, true, HttpStatusCode.Accepted, "Queued")]
    public async Task Sending_preserves_failure_provider_acceptance_and_durable_queue_truth(
        bool accepted, bool durable, HttpStatusCode expectedStatus, string expectedState)
    {
        var email = Substitute.For<IEmailService, IDurableEmailService>();
        ((IDurableEmailService)email).QueuesDelivery.Returns(durable);
        email.SendEmailAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<string>(), Arg.Any<string>(),
            cc: null, ct: Arg.Any<CancellationToken>()).Returns(accepted);
        await using var app = await CreateAsync(email, "Admin");
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/email/send",
            new SendEmailRequest(["capture@example.test"], "Isolated fixture", "<p>Isolated fixture</p>"));

        Assert.Equal(expectedStatus, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedState, data.RootElement.GetProperty("status").GetString());
        Assert.False(data.RootElement.GetProperty("deliveryConfirmed").GetBoolean());
        if (accepted) Assert.Equal(durable, data.RootElement.GetProperty("queued").GetBoolean());
        else Assert.Contains("before retrying", data.RootElement.GetProperty("nextAction").GetString());
        await email.Received(1).SendEmailAsync(Arg.Is<IEnumerable<string>>(recipients => recipients.SequenceEqual(new[] { "capture@example.test" })),
            "Isolated fixture", "<p>Isolated fixture</p>", cc: null, ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_recipient_input_does_not_call_the_email_transport()
    {
        var email = Substitute.For<IEmailService>();
        await using var app = await CreateAsync(email, "Admin");
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/email/send",
            new SendEmailRequest(["bad\r\nheader@example.test"], "Isolated fixture", "<p>Isolated fixture</p>"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(email.ReceivedCalls());
    }

    [Fact]
    public async Task Ordinary_account_cannot_use_instance_mail_send()
    {
        var email = Substitute.For<IEmailService>();
        await using var app = await CreateAsync(email, "User");
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/v1/email/send",
            new SendEmailRequest(["capture@example.test"], "Isolated fixture", "<p>Isolated fixture</p>"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(email.ReceivedCalls());
    }

    private static async Task<WebApplication> CreateAsync(IEmailService email, string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(email);
        builder.Services.AddSingleton(new TestActor(role));
        builder.Services.AddAuthentication("operator-fixture")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("operator-fixture", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("HelpdeskAdmin", policy => policy.RequireRole("Admin")));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEmailEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed record TestActor(string Role);

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, TestActor actor) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "mail-fixture"), new Claim(ClaimTypes.Role, actor.Role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
