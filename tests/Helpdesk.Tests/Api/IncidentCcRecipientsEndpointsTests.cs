using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Linq;
using System.Collections.Generic;
using Helpdesk.API;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using NSubstitute;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace Helpdesk.Tests.Api;

public partial class IncidentCcRecipientsEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InMemoryRepository<Incident> _incidentRepo = new();
    private readonly InMemoryRepository<Customer> _customerRepo = new();
    private readonly InMemoryRepository<User> _userRepo = new();
    private readonly IRequestSender _sender = Substitute.For<IRequestSender>();
    private readonly ITicketNotificationService _ticketNotificationService = Substitute.For<ITicketNotificationService>();
    private readonly ISupportAccessService _supportAccessService = Substitute.For<ISupportAccessService>();
    private readonly ISupportNotificationService _supportNotificationService = Substitute.For<ISupportNotificationService>();

    public IncidentCcRecipientsEndpointsTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseIsolatedTestStorage();
            builder.UseEnvironment("Development");

            builder.ConfigureAppConfiguration((context, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "test",
                    ["Jwt:Audience"] = "test",
                    ["Jwt:Key"] = "test-key-123456789012345678901234",
                    ["StorageOptions:ImageSigningSecret"] = "synthetic-incident-preview-test-signing-secret"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IRepository<Incident>>(_incidentRepo);
                services.AddSingleton<IRepository<Customer>>(_customerRepo);
                services.AddSingleton<IRepository<User>>(_userRepo);
                services.AddSingleton(_sender);
                services.AddSingleton(_ticketNotificationService);
                services.AddSingleton(_supportAccessService);
                services.AddSingleton(_supportNotificationService);

                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

                services.AddAuthorization(opts =>
                {
                    opts.AddPolicy("HelpdeskAdmin", p =>
                    {
                        p.AddAuthenticationSchemes("Test");
                        p.RequireAuthenticatedUser();
                        p.RequireRole("HelpdeskAdmin");
                    });
                    opts.AddPolicy("IncidentAccess", p =>
                    {
                        p.AddAuthenticationSchemes("Test");
                        p.RequireAuthenticatedUser();
                        p.RequireRole("Incident.User", "Incident.Manager", "HelpdeskAdmin");
                    });
                    opts.AddPolicy("IncidentManager", p =>
                    {
                        p.AddAuthenticationSchemes("Test");
                        p.RequireAuthenticatedUser();
                        p.RequireRole("Incident.Manager", "HelpdeskAdmin");
                    });
                });
            });
        });
    }

    [Fact]
    public async Task CcRecipients_RoundTrip_Via_Get_And_Put()
    {
        var incident = new Incident
        {
            Id = "inc1",
            Title = "Test",
            TrackingId = "INC-1",
            Priority = TicketPriority.Medium,
            State = TicketState.New,
            CcRecipients = new List<string> { "first@example.com" }
        };
        await _incidentRepo.CreateAsync(incident);

        var client = GetAuthenticatedClient();

        var get = await client.GetFromJsonAsync<IncidentDto>($"/api/v1/incidents/{incident.Id}");
        Assert.Contains("first@example.com", get!.CcRecipients);

        var updateDto = new UpdateIncidentDto
        {
            State = incident.State,
            Priority = incident.Priority,
            CcRecipients = new List<string> { "second@example.com", "third@example.com" }
        };
        var putResp = await client.PutAsJsonAsync($"/api/v1/incidents/{incident.Id}", updateDto);
        putResp.EnsureSuccessStatusCode();
        var updated = await putResp.Content.ReadFromJsonAsync<IncidentDto>();
        Assert.Contains("second@example.com", updated!.CcRecipients);
        Assert.Contains("third@example.com", updated.CcRecipients);
        Assert.DoesNotContain("first@example.com", updated.CcRecipients);

        var get2 = await client.GetFromJsonAsync<IncidentDto>($"/api/v1/incidents/{incident.Id}");
        Assert.Contains("second@example.com", get2!.CcRecipients);
        Assert.Contains("third@example.com", get2.CcRecipients);
        Assert.DoesNotContain("first@example.com", get2.CcRecipients);
    }

    [Fact]
    public async Task Put_Resolved_Sends_Incident_Resolved_Email()
    {
        var incident = new Incident
        {
            Id = "inc-resolved",
            Title = "Broken thing",
            TrackingId = "INC-RESOLVED",
            Priority = TicketPriority.Medium,
            State = TicketState.InProgress,
            RequesterEmail = "customer@example.com",
            CcRecipients = new List<string> { "watcher@example.com" }
        };
        await _incidentRepo.CreateAsync(incident);

        var client = GetAuthenticatedClient();
        var updateDto = new UpdateIncidentDto
        {
            State = TicketState.Resolved,
            Priority = incident.Priority,
            CcRecipients = incident.CcRecipients
        };

        var response = await client.PutAsJsonAsync($"/api/v1/incidents/{incident.Id}", updateDto);

        response.EnsureSuccessStatusCode();
        await _ticketNotificationService.Received(1).SendTicketResolvedAsync(
            Arg.Is<Incident>(x => x.TrackingId == "INC-RESOLVED"),
            "customer@example.com",
            "customer@example.com",
            Arg.Is<IEnumerable<string>>(x => x.SequenceEqual(new[] { "watcher@example.com" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Put_AssignmentChanged_SendsSupportAssignmentNotification()
    {
        var incident = new Incident
        {
            Id = "inc-assign",
            Title = "Broken thing",
            TrackingId = "INC-ASSIGN",
            Priority = TicketPriority.Medium,
            State = TicketState.New,
            OrganizationId = "org-1"
        };
        await _incidentRepo.CreateAsync(incident);
        _supportAccessService.CanUserSupportOrganizationAsync("user-1", "org-1", Arg.Any<CancellationToken>())
            .Returns(true);

        var client = GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"/api/v1/incidents/{incident.Id}", new UpdateIncidentDto
        {
            State = TicketState.New,
            Priority = TicketPriority.Medium,
            AssignedToId = "user-1"
        });

        response.EnsureSuccessStatusCode();
        await _supportNotificationService.Received(1).NotifyTicketAssignedAsync(
            Arg.Is<Incident>(x => x.Id == "inc-assign"),
            null,
            "user-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Put_AssignmentUnchanged_DoesNotSendSupportAssignmentNotification()
    {
        var incident = new Incident
        {
            Id = "inc-assign-same",
            Title = "Broken thing",
            TrackingId = "INC-ASSIGN-SAME",
            Priority = TicketPriority.Medium,
            State = TicketState.New,
            OrganizationId = "org-1",
            AssignedToId = "user-1"
        };
        await _incidentRepo.CreateAsync(incident);
        _supportAccessService.CanUserSupportOrganizationAsync("user-1", "org-1", Arg.Any<CancellationToken>())
            .Returns(true);

        var client = GetAuthenticatedClient();
        var response = await client.PutAsJsonAsync($"/api/v1/incidents/{incident.Id}", new UpdateIncidentDto
        {
            State = TicketState.New,
            Priority = TicketPriority.Medium,
            AssignedToId = "user-1"
        });

        response.EnsureSuccessStatusCode();
        await _supportNotificationService.DidNotReceive().NotifyTicketAssignedAsync(
            Arg.Any<Incident>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkAssign_SendsSupportAssignmentNotificationForChangedTickets()
    {
        await _incidentRepo.CreateAsync(new Incident
        {
            Id = "inc-bulk-1",
            Title = "One",
            TrackingId = "INC-BULK-1",
            OrganizationId = "org-1"
        });
        await _incidentRepo.CreateAsync(new Incident
        {
            Id = "inc-bulk-2",
            Title = "Two",
            TrackingId = "INC-BULK-2",
            OrganizationId = "org-1",
            AssignedToId = "user-1"
        });
        _supportAccessService.CanUserSupportOrganizationAsync("user-1", "org-1", Arg.Any<CancellationToken>())
            .Returns(true);

        var client = GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/v1/incidents/bulk/assign", new
        {
            Ids = new[] { "inc-bulk-1", "inc-bulk-2" },
            AssignedToId = "user-1"
        });

        response.EnsureSuccessStatusCode();
        await _supportNotificationService.Received(1).NotifyTicketAssignedAsync(
            Arg.Is<Incident>(x => x.Id == "inc-bulk-1"),
            null,
            "user-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDelete_RemovesSelectedIncidents_WithoutRequesterNotification()
    {
        await _incidentRepo.CreateAsync(new Incident
        {
            Id = "inc-delete-1",
            Title = "Delete one",
            TrackingId = "INC-DELETE-1",
            RequesterEmail = "sender@example.com"
        });
        await _incidentRepo.CreateAsync(new Incident
        {
            Id = "inc-delete-2",
            Title = "Delete two",
            TrackingId = "INC-DELETE-2",
            RequesterEmail = "sender@example.com"
        });

        var client = GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/v1/incidents/bulk/delete", new
        {
            Ids = new[] { "inc-delete-1", "inc-delete-2" }
        });

        response.EnsureSuccessStatusCode();
        Assert.Null(await _incidentRepo.GetAsync("inc-delete-1"));
        Assert.Null(await _incidentRepo.GetAsync("inc-delete-2"));
        await _ticketNotificationService.DidNotReceive().SendTicketResolvedAsync(
            Arg.Any<Incident>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDelete_WithoutIds_ReturnsBadRequest()
    {
        var client = GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/v1/incidents/bulk/delete", new
        {
            Ids = Array.Empty<string>()
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkMarketingSpam_SilentlyClosesAndExcludesIncidents()
    {
        await _incidentRepo.CreateAsync(new Incident
        {
            Id = "inc-spam-1",
            Title = "Spam one",
            TrackingId = "INC-SPAM-1",
            State = TicketState.InProgress,
            RequesterEmail = "vendor@example.com"
        });
        await _incidentRepo.CreateAsync(new Incident
        {
            Id = "inc-spam-2",
            Title = "Spam two",
            TrackingId = "INC-SPAM-2",
            State = TicketState.New,
            RequesterEmail = "vendor@example.com"
        });

        var client = GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/v1/incidents/bulk/marketing-spam", new
        {
            Ids = new[] { "inc-spam-1", "inc-spam-2" }
        });

        response.EnsureSuccessStatusCode();
        var first = await _incidentRepo.GetAsync("inc-spam-1");
        var second = await _incidentRepo.GetAsync("inc-spam-2");
        Assert.Equal(TicketState.Resolved, first!.State);
        Assert.Equal(TicketEmailExclusionReason.MarketingSpam, first.EmailExclusionReason);
        Assert.NotNull(first.ClosedAt);
        Assert.Equal(TicketState.Resolved, second!.State);
        Assert.Equal(TicketEmailExclusionReason.MarketingSpam, second.EmailExclusionReason);
        await _ticketNotificationService.DidNotReceive().SendTicketResolvedAsync(
            Arg.Any<Incident>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkStateResolved_StillSendsRequesterNotification()
    {
        await _incidentRepo.CreateAsync(new Incident
        {
            Id = "inc-bulk-resolve",
            Title = "Resolve normally",
            TrackingId = "INC-BULK-RESOLVE",
            State = TicketState.InProgress,
            RequesterEmail = "customer@example.com"
        });

        var client = GetAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/v1/incidents/bulk/state", new
        {
            Ids = new[] { "inc-bulk-resolve" },
            NewState = TicketState.Resolved,
            Comment = (string?)null
        });

        response.EnsureSuccessStatusCode();
        await _ticketNotificationService.Received(1).SendTicketResolvedAsync(
            Arg.Is<Incident>(x => x.Id == "inc-bulk-resolve"),
            "customer@example.com",
            "customer@example.com",
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_Sets_Requester_And_CcRecipients()
    {
        await SeedCustomerAsync("org-1", "customer-1", "customer@example.com");

        CreateIncidentCommand? sentCommand = null;
        _sender.Send(Arg.Do<CreateIncidentCommand>(c => sentCommand = c), Arg.Any<CancellationToken>())
            .Returns(ci => new Incident
            {
                Id = "new1",
                Title = ci.Arg<CreateIncidentCommand>().Title,
                TrackingId = "INC-NEW1",
                CustomerId = ci.Arg<CreateIncidentCommand>().CustomerId,
                OrganizationId = ci.Arg<CreateIncidentCommand>().OrganizationId,
                CcRecipients = ci.Arg<CreateIncidentCommand>().CcRecipients?.ToList() ?? new(),
                RequesterEmail = ci.Arg<CreateIncidentCommand>().RequesterEmail
            });

        var client = GetAuthenticatedClient();
        var createDto = new CreateIncidentDto
        {
            Title = "T",
            Description = "D",
            Priority = TicketPriority.Low,
            CustomerId = "customer-1",
            OrganizationId = "org-1",
            RequesterEmail = "spoofed@example.com",
            CcRecipients = new List<string> { "a@example.com", "customer@example.com", "A@EXAMPLE.com" }
        };
        var resp = await client.PostAsJsonAsync("/api/v1/incidents", createDto);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<IncidentDto>();

        Assert.Equal("customer@example.com", sentCommand!.RequesterEmail);
        Assert.Equal("customer-1", sentCommand.CustomerId);
        Assert.Contains("a@example.com", sentCommand.CcRecipients!);
        Assert.DoesNotContain("customer@example.com", sentCommand.CcRecipients!);
        Assert.Equal("customer@example.com", dto!.RequesterEmail);
        Assert.Single(dto.CcRecipients);
        Assert.Contains("a@example.com", dto.CcRecipients);
        await _ticketNotificationService.Received(1).SendNewTicketConfirmationAsync(
            Arg.Is<Incident>(x => x.TrackingId == "INC-NEW1"),
            "customer@example.com",
            "customer-1",
            Arg.Is<IEnumerable<string>>(x => x.SequenceEqual(new[] { "a@example.com" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_Rejects_Missing_Customer()
    {
        var client = GetAuthenticatedClient();
        var createDto = new CreateIncidentDto
        {
            Title = "T",
            Description = "D",
            Priority = TicketPriority.Low,
            OrganizationId = "org-1"
        };

        var resp = await client.PostAsJsonAsync("/api/v1/incidents", createDto);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Rejects_Customer_From_Different_Organization()
    {
        var client = GetAuthenticatedClient();
        await SeedCustomerAsync("org-2", "customer-2", "customer2@example.com");

        var createDto = new CreateIncidentDto
        {
            Title = "T",
            Description = "D",
            Priority = TicketPriority.Low,
            CustomerId = "customer-2",
            OrganizationId = "org-1"
        };

        var resp = await client.PostAsJsonAsync("/api/v1/incidents", createDto);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Post_Sets_AssignedToId_WhenUserBelongsToOrganization()
    {
        var client = GetAuthenticatedClient();
        await SeedUserAsync("org-1", "user-1");
        await SeedCustomerAsync("org-1", "customer-1", "customer@example.com");
        _supportAccessService.CanUserSupportOrganizationAsync("user-1", "org-1", Arg.Any<CancellationToken>())
            .Returns(true);

        CreateIncidentCommand? sentCommand = null;
        _sender.Send(Arg.Do<CreateIncidentCommand>(c => sentCommand = c), Arg.Any<CancellationToken>())
            .Returns(ci => new Incident
            {
                Id = "assigned-new",
                Title = ci.Arg<CreateIncidentCommand>().Title,
                TrackingId = "INC-ASSIGNED",
                CustomerId = ci.Arg<CreateIncidentCommand>().CustomerId,
                OrganizationId = ci.Arg<CreateIncidentCommand>().OrganizationId,
                RequesterEmail = ci.Arg<CreateIncidentCommand>().RequesterEmail,
                AssignedToId = ci.Arg<CreateIncidentCommand>().AssignedToId
            });

        var createDto = new CreateIncidentDto
        {
            Title = "T",
            Description = "<p>Rich description</p>",
            Priority = TicketPriority.Low,
            CustomerId = "customer-1",
            OrganizationId = "org-1",
            AssignedToId = "user-1"
        };

        var resp = await client.PostAsJsonAsync("/api/v1/incidents", createDto);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, body);
        var dto = await resp.Content.ReadFromJsonAsync<IncidentDto>();

        Assert.Equal("user-1", sentCommand!.AssignedToId);
        Assert.Equal("customer-1", sentCommand.CustomerId);
        Assert.Equal("user-1", dto!.AssignedToId);
    }

    [Fact]
    public async Task Post_Rejects_AssignedToId_WhenUserIsNotConfiguredToSupportOrganization()
    {
        var client = GetAuthenticatedClient();
        await SeedUserAsync("org-2", "user-2");
        await SeedCustomerAsync("org-1", "customer-1", "customer@example.com");
        _supportAccessService.CanUserSupportOrganizationAsync("user-2", "org-1", Arg.Any<CancellationToken>())
            .Returns(false);

        var createDto = new CreateIncidentDto
        {
            Title = "T",
            Description = "D",
            Priority = TicketPriority.Low,
            CustomerId = "customer-1",
            OrganizationId = "org-1",
            AssignedToId = "user-2"
        };

        var resp = await client.PostAsJsonAsync("/api/v1/incidents", createDto);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private HttpClient GetAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");
        return client;
    }

    private async Task SeedUserAsync(string organizationId, string userId)
    {
        await _userRepo.CreateAsync(new User
        {
            Id = userId,
            Name = userId,
            Email = $"{userId}@example.test",
            Role = "Technician",
            OrganizationId = organizationId
        });
    }

    private async Task SeedCustomerAsync(string organizationId, string customerId, string email, bool enabled = true)
    {
        await _customerRepo.CreateAsync(new Customer
        {
            Id = customerId,
            Name = customerId,
            Email = email,
            OrganizationId = organizationId,
            IsEnabled = enabled
        });
    }

    private class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
                return Task.FromResult(AuthenticateResult.Fail("No authorization header"));

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
