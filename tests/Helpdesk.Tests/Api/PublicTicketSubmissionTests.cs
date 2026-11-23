using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Api;

public sealed class PublicTicketSubmissionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Eligibility_precedes_provisioning_and_retains_the_saved_customer_profile(bool postgres, bool system)
    {
        await using var host = await CreateAsync(postgres);
        await SetStateAsync(host, organizationEnabled: false, customerEnabled: true);
        var baseline = await host.CountsAsync();

        foreach (var (email, organizationEnabled, customerEnabled) in new[]
        {
            ("customer@example.test", false, true), ("customer@example.test", false, false),
            ("new@example.test", false, true), ("customer@example.test", true, false)
        })
        {
            await SetStateAsync(host, organizationEnabled, customerEnabled);
            using var response = await SubmitAsync(host, system, email, "Unverified display name");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Ticket submission could not be accepted.", await response.Content.ReadAsStringAsync());
            Assert.Equal(baseline, await host.CountsAsync());
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Single(await db.Customers.ToListAsync());
            Assert.Equal("Synthetic customer", (await db.Customers.SingleAsync()).Name);
            Assert.Null((await db.Organizations.SingleAsync()).DnsName);
        }

        await SetStateAsync(host, organizationEnabled: true, customerEnabled: true);
        using var accepted = await SubmitAsync(host, system, "  CUSTOMER@example.test ", "Another display name");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.Equal("Synthetic customer", (await db.Customers.SingleAsync()).Name);
            Assert.Equal(host.CustomerId, (await db.Incidents.SingleAsync()).CustomerId);
            Assert.Equal("example.test", (await db.Organizations.SingleAsync()).DnsName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_first_contact_creates_one_canonical_customer_and_admits_both_tickets(bool postgres)
    {
        await using var host = await CreateAsync(postgres);
        await SetStateAsync(host, organizationEnabled: true, customerEnabled: true);
        var responses = await Task.WhenAll(
            Task.Run(() => SubmitAsync(host, system: false, " NEW@example.test ", "First submitted name")),
            Task.Run(() => SubmitAsync(host, system: true, "new@example.test", "Second submitted name")));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var customer = await db.Customers.SingleAsync(row => row.Email == "new@example.test");
            Assert.Equal(host.OrganizationId, customer.OrganizationId);
            Assert.Contains(customer.Name, new[] { "First submitted name", "Second submitted name" });
            Assert.Equal(2, await db.Incidents.CountAsync(row => row.CustomerId == customer.Id));
            Assert.Equal(2, (await host.CountsAsync()).Confirmation);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Intake_observes_a_concurrent_disable_before_ticket_commit(bool postgres, bool disableCustomer)
    {
        var barrier = new IntakeBarrier();
        await using var host = await CreateAsync(postgres, barrier);
        await SetStateAsync(host, organizationEnabled: true, customerEnabled: true);
        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (disableCustomer)
            (await db.Customers.SingleAsync()).IsEnabled = false;
        else
            (await db.Organizations.SingleAsync()).IsEnabled = false;
        await db.SaveChangesAsync();

        barrier.Arm();
        var submission = Task.Run(() => SubmitAsync(host, system: false, "customer@example.test", "Fixture name"));
        await barrier.TransactionStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await transaction.CommitAsync();
        using var rejected = await submission.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var counts = await host.CountsAsync();
        Assert.Equal(0, counts.Incidents);
        Assert.Equal(0, counts.Confirmation);
    }

    private static Task<IncidentReceiverTests.Harness> CreateAsync(bool postgres, IntakeBarrier? barrier = null) =>
        IncidentReceiverTests.Harness.CreateAsync(postgres,
            configureServices: services =>
            {
                services.AddSingleton<PublicAdmission>();
                if (barrier is not null)
                    services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(barrier));
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SystemFixture>("System", _ => { });
            },
            configureApp: app => app.MapTicketSubmissionEndpoints());

    private static Task SetStateAsync(IncidentReceiverTests.Harness host, bool organizationEnabled, bool customerEnabled) =>
        host.EditAsync(async db =>
        {
            var organization = await db.Organizations.SingleAsync();
            organization.Name = "example.test";
            organization.IsEnabled = organizationEnabled;
            var customer = await db.Customers.SingleAsync();
            customer.IsEnabled = customerEnabled;
        });

    private static async Task<HttpResponseMessage> SubmitAsync(IncidentReceiverTests.Harness host, bool system, string email, string name)
    {
        var service = Assert.IsType<CaptchaService>(host.App.Services.GetRequiredService<ICaptchaService>());
        var answer = "";
        var challenge = service.TryGenerate("test-fixture", default, digits =>
        {
            answer = digits;
            return CaptchaService.RenderCaptchaImage(digits);
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, system ? "/api/v1/tickets/submit" : "/api/v1/public/tickets/submit")
        {
            Content = JsonContent.Create(new SubmitTicketRequest
            {
                Name = name, Email = email, Subject = "Synthetic intake", Message = "Synthetic issue description",
                CaptchaId = Assert.IsType<CaptchaChallenge>(challenge).Id, CaptchaAnswer = answer
            })
        };
        if (system)
            request.Headers.Add("X-Submission-Fixture", "system");
        return await host.Client.SendAsync(request);
    }

    private sealed class IntakeBarrier : DbTransactionInterceptor
    {
        private int _armed;
        public TaskCompletionSource TransactionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                TransactionStarted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SystemFixture(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggers, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggers, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Submission-Fixture"] != "system")
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "synthetic-web"), new(ClaimTypes.Role, "system.blazor-web")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
