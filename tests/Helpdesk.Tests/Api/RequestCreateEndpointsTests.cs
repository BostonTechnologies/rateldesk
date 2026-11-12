using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Services;
using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Resources;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.Tenants;
using AppTicketServices = Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Workflow;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Api;

public sealed class RequestCreateEndpointsTests
{
    [Fact]
    public async Task Post_Rejects_Missing_Customer()
    {
        await using var harness = await RequestCreateTestHarness.CreateAsync();
        var dto = new CreateRequestDto
        {
            Title = "New request",
            Description = "Need help",
            OrganizationId = "org-1"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/requests", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Rejects_Customer_From_Different_Organization()
    {
        await using var harness = await RequestCreateTestHarness.CreateAsync();
        await harness.SeedCustomerAsync("org-2", "customer-2", "customer2@example.com");
        var dto = new CreateRequestDto
        {
            Title = "New request",
            Description = "Need help",
            CustomerId = "customer-2",
            OrganizationId = "org-1"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/requests", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_Persists_Customer_And_Uses_Customer_Email_As_Requester()
    {
        await using var harness = await RequestCreateTestHarness.CreateAsync();
        await harness.SeedCustomerAsync("org-1", "customer-1", "customer@example.com");
        var dto = new CreateRequestDto
        {
            Title = "New request",
            Description = "<p>Need help</p>",
            CustomerId = "customer-1",
            OrganizationId = "org-1",
            RequesterEmail = "spoofed@example.com"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/requests", dto);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.Single((await harness.Requests.GetAllAsync()).ToList());
        Assert.Equal("REQ-TEST-001", created.TrackingId);
        Assert.Equal("customer-1", created.CustomerId);
        Assert.Equal("customer@example.com", created.RequesterEmail);
        Assert.Equal("Need help", created.Description);
        Assert.Equal("REQ-TEST-001", harness.Notification.LastTicket?.TrackingId);
        Assert.Equal("customer@example.com", harness.Notification.LastRecipientEmail);
    }

    [Fact]
    public async Task Post_Accepts_Unassigned_Request_With_Customer()
    {
        await using var harness = await RequestCreateTestHarness.CreateAsync();
        await harness.SeedCustomerAsync("org-1", "customer-1", "customer@example.com");
        var dto = new CreateRequestDto
        {
            Title = "New request",
            Description = "Need help",
            CustomerId = "customer-1",
            OrganizationId = "org-1"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/requests", dto);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.Single((await harness.Requests.GetAllAsync()).ToList());
        Assert.Null(created.AssignedToId);
    }

    [Fact]
    public async Task SelfServicePost_Assigns_TrackingId_And_Sends_Confirmation()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync();
        await harness.SeedRequestFormAsync();
        var dto = new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            Title = "New laptop",
            Description = "Please approve a new laptop",
            PayloadJson = "{}"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SelfServiceRequestSubmittedDto>();
        Assert.Equal("REQ-TEST-001", result!.TrackingId);
        var created = Assert.Single((await harness.Requests.GetAllAsync()).ToList());
        Assert.Equal("REQ-TEST-001", created.TrackingId);
        Assert.Equal("customer-1", created.CustomerId);
        Assert.Equal("customer@example.com", created.RequesterEmail);
        Assert.Equal("org-1", created.OrganizationId);
        Assert.Empty(created.CcRecipients);
        Assert.Equal("SelfServiceRequestCreated", harness.Notification.LastNotificationKind);
        Assert.Equal("REQ-TEST-001", harness.Notification.LastTicket?.TrackingId);
        Assert.Equal("Laptop Request", harness.Notification.LastRequestForm?.Title);
        Assert.Equal("customer@example.com", harness.Notification.LastRecipientEmail);
        Assert.Equal("Example User", harness.Notification.LastRecipientName);
    }

    [Fact]
    public async Task SelfServicePost_Rejects_WhenAuthenticatedUserHasNoExplicitCustomerLink()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync(authHeader: "NoEmail");
        await harness.SeedRequestFormAsync();
        var dto = new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            Title = "New laptop",
            Description = "Please approve a new laptop",
            PayloadJson = "{}"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", dto);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await harness.Requests.GetAllAsync()).ToList());
        Assert.Equal(0, harness.Notification.SendCount);
    }

    [Fact]
    public async Task SelfServicePost_DoesNotUseBodyEmailToEstablishCustomerAccess()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync(authHeader: "NoEmail");
        await harness.SeedRequestFormAsync();
        var dto = new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            Title = "New laptop",
            Description = "Please approve a new laptop",
            PayloadJson = "{}",
            RequesterEmail = "requester@example.com",
            RequesterName = "Example User"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", dto);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await harness.Requests.GetAllAsync()).ToList());
        Assert.Equal(0, harness.Notification.SendCount);
    }

    [Fact]
    public async Task SelfServicePost_DoesNotProvisionACustomerFromAuthenticatedEmail()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync(authHeader: "DifferentDomain");
        await harness.SeedRequestFormAsync();
        var dto = new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            PayloadJson = "{}"
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", dto);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await harness.Requests.GetAllAsync()).ToList());
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.DoesNotContain(await db.Customers.ToListAsync(), customer => customer.Email == "local.user@unrelated.example");
    }

    [Fact]
    public async Task SelfServicePost_Does_Not_Claim_A_Customer_From_Another_Organization()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync(authHeader: "DifferentDomain");
        await harness.SeedRequestFormAsync();
        await harness.SeedCustomerAsync("other-customer", "Other Tenant Customer", "local.user@unrelated.example", "org-2");

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            PayloadJson = "{}"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await harness.Requests.GetAllAsync()).ToList());
    }

    [Fact]
    public async Task SelfServiceRequestUsers_Returns_Only_Visible_Regular_User_Organizations()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync();
        await harness.SeedOrganizationAsync("org-1", "Example Organization");
        await harness.SeedOrganizationAsync("org-2", "Other");
        await harness.SeedUserAsync("user-1", "Alice One", "alice@example.com", "org-1");
        await harness.SeedUserAsync("user-2", "Bob Two", "bob@example.com", "org-2");
        await harness.SeedCustomerAsync("customer-2", "Charlie Customer", "charlie@example.com", "org-1");
        await harness.SeedCustomerAsync("customer-3", "Dana Customer", "dana@example.com", "org-2");

        var people = await harness.Client.GetFromJsonAsync<List<SelfServiceRequestUserDto>>(
            "/api/v1/self-service/request-users?query=&pageSize=25");

        Assert.NotNull(people);
        Assert.Contains(people!, x => x.Id == "user-1" && x.Source == SelfServiceRequestPersonSource.User);
        Assert.Contains(people!, x => x.Id == "customer-2" && x.Source == SelfServiceRequestPersonSource.Customer);
        Assert.DoesNotContain(people!, x => x.Id == "user-2");
        Assert.DoesNotContain(people!, x => x.Id == "customer-3");
    }

    [Fact]
    public async Task SelfServiceRequestUsers_Returns_Managed_Organizations_For_Msp_User()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync(authHeader: "Msp");
        await harness.SeedOrganizationAsync("org-1", "MSP");
        await harness.SeedOrganizationAsync("org-managed", "Managed");
        await harness.SeedOrganizationAsync("org-other", "Other");
        await harness.SeedUserAsync("user-own", "Own User", "own@example.com", "org-1");
        await harness.SeedUserAsync("user-managed", "Managed User", "managed@example.com", "org-managed");
        await harness.SeedUserAsync("user-other", "Other User", "other@example.com", "org-other");

        var people = await harness.Client.GetFromJsonAsync<List<SelfServiceRequestUserDto>>(
            "/api/v1/self-service/request-users?query=user&pageSize=25");

        Assert.NotNull(people);
        Assert.Contains(people!, x => x.Id == "user-own");
        Assert.Contains(people!, x => x.Id == "user-managed");
        Assert.DoesNotContain(people!, x => x.Id == "user-other");
    }

    [Fact]
    public async Task SelfServicePost_SubmitOnBehalf_Uses_Selected_Customer_Context_And_Adds_Listeners()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync();
        await harness.SeedRequestFormAsync();
        await harness.SeedOrganizationAsync("org-1", "Example Organization");
        await harness.SeedCustomerAsync("requested-customer", "Requested User", "requested@example.com", "org-1");
        var dto = new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            Title = "New laptop",
            Description = "Please approve a new laptop",
            PayloadJson = "{}",
            RequestedForPersonId = "requested-customer",
            RequestedForPersonSource = SelfServiceRequestPersonSource.Customer
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = Assert.Single((await harness.Requests.GetAllAsync()).ToList());
        Assert.Equal("requested-customer", created.CustomerId);
        Assert.Equal("requested@example.com", created.RequesterEmail);
        Assert.Equal("org-1", created.OrganizationId);
        Assert.Equal(["requested@example.com", "customer@example.com"], created.CcRecipients);
        Assert.Equal("requested@example.com", harness.Notification.LastRecipientEmail);
        Assert.Equal("Requested User", harness.Notification.LastRecipientName);
        Assert.Equal(["requested@example.com", "customer@example.com"], harness.Notification.LastCc);
    }

    [Fact]
    public async Task SelfServicePost_SubmitOnBehalf_Creates_Customer_For_Selected_User()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync();
        await harness.SeedRequestFormAsync();
        await harness.SeedOrganizationAsync("org-1", "Example Organization");
        await harness.SeedUserAsync("requested-user", "Requested User", "requested-user@example.com", "org-1");
        var dto = new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            Title = "New laptop",
            Description = "Please approve a new laptop",
            PayloadJson = "{}",
            RequestedForPersonId = "requested-user",
            RequestedForPersonSource = SelfServiceRequestPersonSource.User
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = Assert.Single((await harness.Requests.GetAllAsync()).ToList());
        Assert.Equal("requested-user@example.com", created.RequesterEmail);
        Assert.Equal("org-1", created.OrganizationId);
        Assert.Equal(["requested-user@example.com", "customer@example.com"], created.CcRecipients);
    }

    [Fact]
    public async Task SelfServicePost_SubmitOnBehalf_Rejects_Out_Of_Scope_User()
    {
        await using var harness = await SelfServiceRequestTestHarness.CreateAsync();
        await harness.SeedRequestFormAsync();
        await harness.SeedOrganizationAsync("org-1", "Example Organization");
        await harness.SeedOrganizationAsync("org-2", "Other");
        await harness.SeedUserAsync("requested-user", "Requested User", "requested-user@example.com", "org-2");
        var dto = new SubmitSelfServiceRequestDto
        {
            RequestFormId = "form-1",
            Title = "New laptop",
            Description = "Please approve a new laptop",
            PayloadJson = "{}",
            RequestedForPersonId = "requested-user",
            RequestedForPersonSource = SelfServiceRequestPersonSource.User
        };

        var response = await harness.Client.PostAsJsonAsync("/api/v1/self-service/requests", dto);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await harness.Requests.GetAllAsync()).ToList());
    }

    [Fact]
    public async Task BulkState_Resolved_Sends_Generic_Request_Resolved_And_Skips_SelfService()
    {
        await using var harness = await RequestCreateTestHarness.CreateAsync();
        await harness.Requests.CreateAsync(new Request
        {
            Id = "request-1",
            Title = "Generic request",
            TrackingId = "REQ-GENERIC",
            State = TicketState.InProgress,
            Priority = TicketPriority.Low,
            RequesterEmail = "generic@example.com"
        });
        await harness.Requests.CreateAsync(new Request
        {
            Id = "request-2",
            Title = "Self-service request",
            TrackingId = "REQ-SELF",
            State = TicketState.InProgress,
            Priority = TicketPriority.Low,
            RequesterEmail = "self@example.com",
            RequestFormId = "form-1"
        });

        var response = await harness.Client.PostAsJsonAsync("/api/v1/requests/bulk/state", new
        {
            ids = new[] { "request-1", "request-2" },
            newState = TicketState.Resolved,
            comment = (string?)null
        });

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, harness.Notification.SendCount);
        Assert.Equal("TicketResolved", harness.Notification.LastNotificationKind);
        Assert.Equal("REQ-GENERIC", harness.Notification.LastTicket?.TrackingId);
        Assert.Equal("generic@example.com", harness.Notification.LastRecipientEmail);
        Assert.All(await harness.Requests.GetAllAsync(), request => Assert.Equal(TicketState.Resolved, request.State));
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var markers = await db.Notifications.AsNoTracking()
            .Where(marker => marker.Category == RequestSnapshotJournal.Category)
            .OrderBy(marker => marker.Reference).ToListAsync();
        Assert.Equal(["request-1", "request-2"], markers.Select(marker => marker.Reference));
        Assert.All(markers, marker =>
        {
            Assert.Equal("corr-test", marker.CorrelationId);
            Assert.Null(marker.UserId);
            Assert.False(marker.IsGlobal);
        });
    }

    private sealed class RequestCreateTestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly WebApplication app;
        private readonly InMemoryRepository<Customer> customers;

        private RequestCreateTestHarness(
            SqliteConnection connection,
            WebApplication app,
            HttpClient client,
            InMemoryRepository<Request> requests,
            InMemoryRepository<Customer> customers,
            CapturingTicketNotificationService notification)
        {
            this.connection = connection;
            this.app = app;
            this.customers = customers;
            Client = client;
            Requests = requests;
            Notification = notification;
        }

        public HttpClient Client { get; }
        public IServiceProvider Services => app.Services;
        public InMemoryRepository<Request> Requests { get; }
        public CapturingTicketNotificationService Notification { get; }

        public static async Task<RequestCreateTestHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var requests = new InMemoryRepository<Request>();
            var customers = new InMemoryRepository<Customer>();
            var notification = new CapturingTicketNotificationService();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("org-1", "admin-1", isHelpdeskAdmin: true));
            builder.Services.AddSingleton<IRepository<Request>>(requests);
            builder.Services.AddSingleton<IRepository<Customer>>(customers);
            builder.Services.AddSingleton<IRepository<User>>(new InMemoryRepository<User>());
            builder.Services.AddSingleton<ITicketSlaInitializer, NoopTicketSlaInitializer>();
            builder.Services.AddSingleton<ITicketSlaCompletionService, NoopTicketSlaCompletionService>();
            builder.Services.AddSingleton<AppTicketServices.ITicketRefGeneratorService>(new FixedTicketRefGenerator("REQ-TEST-001"));
            builder.Services.AddSingleton<IRequestTaskGenerationService, NoopRequestTaskGenerationService>();
            builder.Services.AddSingleton<IWorkflowEngine, NoopWorkflowEngine>();
            builder.Services.AddSingleton<ITicketNotificationService>(notification);
            builder.Services.AddSingleton<IDomainEventPublisher, NoopDomainEventPublisher>();
            builder.Services.AddSingleton<ICorrelationContext, TestCorrelationContext>();
            builder.Services.AddSingleton<IHtmlSanitizerService, HtmlSanitizerService>();
            builder.Services.AddSingleton<IHtmlToPlainTextConverter, HtmlToPlainTextConverter>();
            builder.Services.AddScoped<ICurrentUserAccessService, ClaimsCurrentUserAccessService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("HelpdeskAdmin", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("HelpdeskAdmin");
                });
                options.AddPolicy("RequestAccess", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Request.User", "Request.Manager", "HelpdeskAdmin");
                });
                options.AddPolicy("RequestManager", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("Request.Manager", "HelpdeskAdmin");
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRequestEndpoints();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", "HelpdeskAdmin");
            return new RequestCreateTestHarness(connection, app, client, requests, customers, notification);
        }

        public async Task SeedCustomerAsync(string organizationId, string customerId, string email, bool enabled = true)
        {
            await customers.CreateAsync(new Customer
            {
                Id = customerId,
                Name = customerId,
                Email = email,
                OrganizationId = organizationId,
                IsEnabled = enabled
            });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SelfServiceRequestTestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly WebApplication app;
        private readonly InMemoryRepository<RequestForm> requestForms;

        private SelfServiceRequestTestHarness(
            SqliteConnection connection,
            WebApplication app,
            HttpClient client,
            InMemoryRepository<Request> requests,
            InMemoryRepository<RequestForm> requestForms,
            CapturingTicketNotificationService notification)
        {
            this.connection = connection;
            this.app = app;
            this.requestForms = requestForms;
            Client = client;
            Requests = requests;
            Notification = notification;
        }

        public HttpClient Client { get; }
        public InMemoryRepository<Request> Requests { get; }
        public CapturingTicketNotificationService Notification { get; }
        public IServiceProvider Services => app.Services;

        public static async Task<SelfServiceRequestTestHarness> CreateAsync(string authHeader = "SelfService")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var requests = new InMemoryRepository<Request>();
            var requestForms = new InMemoryRepository<RequestForm>();
            var notification = new CapturingTicketNotificationService();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development"
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.UseSqlite(connection));
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext("org-1", "customer-1", isHelpdeskAdmin: false));
            builder.Services.AddSingleton<IRepository<Request>>(requests);
            builder.Services.AddSingleton<IRepository<RequestForm>>(requestForms);
            builder.Services.AddSingleton<IAutomationBindingPayloadContractService, PassingAutomationBindingPayloadContractService>();
            builder.Services.AddSingleton<IRequestFormSchemaParser, RequestFormSchemaParser>();
            builder.Services.AddSingleton<ISelfServiceDatasetBindingResolver, PassingSelfServiceDatasetBindingResolver>();
            builder.Services.AddSingleton<ISelfServiceAudienceService, AllowingSelfServiceAudienceService>();
            builder.Services.AddSingleton<IRequestTaskGenerationService, NoopRequestTaskGenerationService>();
            builder.Services.AddSingleton<IWorkflowEngine, NoopWorkflowEngine>();
            builder.Services.AddSingleton<ITicketSlaInitializer, NoopTicketSlaInitializer>();
            builder.Services.AddSingleton<AppTicketServices.ITicketRefGeneratorService>(new FixedTicketRefGenerator("REQ-TEST-001"));
            builder.Services.AddSingleton<ITicketNotificationService>(notification);
            builder.Services.AddSingleton<ITenantProvisioningService, TestTenantProvisioningService>();
            builder.Services.AddSingleton<IDomainEventPublisher, NoopDomainEventPublisher>();
            builder.Services.AddSingleton<ICorrelationContext, TestCorrelationContext>();
            builder.Services.AddScoped<ICurrentUserAccessService, ClaimsCurrentUserAccessService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("SelfService.User", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole("SelfService.User", "HelpdeskAdmin");
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapSelfServiceRequestEndpoints();

            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Organizations.Add(new Organization { Id = "org-1", Name = "Example Organization", IsEnabled = true });
                db.Customers.Add(new Customer
                {
                    Id = "customer-1",
                    Name = "Example User",
                    Email = "customer@example.com",
                    OrganizationId = "org-1",
                    State = Helpdesk.Shared.Models.EntityState.Enabled
                });
                await db.SaveChangesAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", authHeader);
            return new SelfServiceRequestTestHarness(connection, app, client, requests, requestForms, notification);
        }

        public async Task SeedRequestFormAsync()
        {
            await requestForms.CreateAsync(new RequestForm
            {
                Id = "form-1",
                Title = "Laptop Request",
                ServiceId = "service-1",
                OrganizationId = "org-1",
                JsonSchema = JsonDocument.Parse("""{"title":"Laptop Request","description":"Request hardware","fields":[]}""")
            });
        }

        public async Task SeedOrganizationAsync(string id, string name, string? itSupportOrganizationId = null)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var existing = await db.Organizations.FindAsync(id);
            if (existing is not null)
            {
                existing.Name = name;
                existing.ItSupportOrganizationId = itSupportOrganizationId;
                await db.SaveChangesAsync();
                return;
            }
            db.Organizations.Add(new Organization
            {
                Id = id,
                Name = name,
                ItSupportOrganizationId = itSupportOrganizationId,
                State = Helpdesk.Shared.Models.EntityState.Enabled
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedUserAsync(string id, string name, string email, string organizationId, string role = "User")
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Users.Add(new User
            {
                Id = id,
                Name = name,
                Email = email,
                OrganizationId = organizationId,
                Role = role
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedCustomerAsync(string id, string name, string email, string organizationId, bool enabled = true)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.Customers.Add(new Customer
            {
                Id = id,
                Name = name,
                Email = email,
                OrganizationId = organizationId,
                State = enabled ? Helpdesk.Shared.Models.EntityState.Enabled : Helpdesk.Shared.Models.EntityState.Blocked
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class NoopTicketSlaInitializer : ITicketSlaInitializer
    {
        public Task InitializeAsync(Ticket ticket) => Task.CompletedTask;
    }

    private sealed class NoopTicketSlaCompletionService : ITicketSlaCompletionService
    {
        public Task HandleTicketClosedAsync(string ticketId, string closedByUserId, DateTimeOffset nowUtc) =>
            Task.CompletedTask;
    }

    private sealed class NoopRequestTaskGenerationService : IRequestTaskGenerationService
    {
        public Task<int> GenerateForRequestAsync(Request request, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class NoopWorkflowEngine : IWorkflowEngine
    {
        public Task<WorkflowRunResult> RunAsync(string requestId, WorkflowRunReason reason, CancellationToken ct) =>
            Task.FromResult(new WorkflowRunResult { RequestId = requestId });
    }

    private sealed class TestTenantProvisioningService : ITenantProvisioningService
    {
        public Task<Organization> GetOrCreateOrganizationByDomainAsync(string domain) =>
            Task.FromResult(new Organization
            {
                Id = "org-1",
                Name = domain,
                DnsName = domain
            });

        public Task<(Customer customer, bool isNew)> GetOrCreateCustomerAsync(
            string email,
            string name,
            string domain)
        {
            var customer = new Customer
            {
                Id = "customer-1",
                Name = name,
                Email = email.Trim().ToLowerInvariant(),
                OrganizationId = "org-1"
            };

            return Task.FromResult((customer, true));
        }
    }

    private sealed class PassingAutomationBindingPayloadContractService : IAutomationBindingPayloadContractService
    {
        public Task<AutomationPayloadValidationResult> ValidateBoundRequestPayloadAsync(
            RequestForm requestForm,
            string payloadJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AutomationPayloadValidationResult.Passed());

        public AutomationTaskInputBuildResult BuildTaskInput(
            RequestForm requestForm,
            Helpdesk.Shared.DTOs.RequestForm.RequestTaskTemplateModel taskTemplate,
            string payloadJson) =>
            AutomationTaskInputBuildResult.Failed("Not used by this test.");
    }

    private sealed class PassingSelfServiceDatasetBindingResolver : ISelfServiceDatasetBindingResolver
    {
        public Task<(bool Success, string PayloadJson, IReadOnlyList<string> Errors)> NormalizePayloadAsync(
            string organizationId,
            IReadOnlyCollection<Helpdesk.Shared.DTOs.RequestForm.FormField> fields,
            string payloadJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<(bool, string, IReadOnlyList<string>)>((true, payloadJson, []));
    }

    private sealed class AllowingSelfServiceAudienceService : ISelfServiceAudienceService
    {
        public Task<bool> IsTestUserAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> CanAccessRequestFormAsync(RequestForm requestForm, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public IQueryable<RequestForm> ApplyAudienceFilter(
            IQueryable<RequestForm> query,
            bool isAdmin,
            bool isTestUser,
            string? tenantId) =>
            query;
    }

    private sealed class NoopDomainEventPublisher : IDomainEventPublisher
    {
        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string GetCorrelationId() => "corr-test";
    }

    private sealed class FixedTicketRefGenerator(string value) : AppTicketServices.ITicketRefGeneratorService
    {
        public Task<string> NextReferenceAsync(string prefix) => Task.FromResult(value);
    }

    public sealed class CapturingTicketNotificationService : ITicketNotificationService
    {
        public Ticket? LastTicket { get; private set; }
        public RequestForm? LastRequestForm { get; private set; }
        public string? LastNotificationKind { get; private set; }
        public string? LastRecipientEmail { get; private set; }
        public string? LastRecipientName { get; private set; }
        public IReadOnlyList<string>? LastCc { get; private set; }
        public int SendCount { get; private set; }

        public Task<bool> SendNewTicketConfirmationAsync(
            Ticket ticket,
            string recipientEmail,
            string recipientName,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            LastNotificationKind = "NewTicketConfirmation";
            LastTicket = ticket;
            LastRecipientEmail = recipientEmail;
            LastRecipientName = recipientName;
            LastCc = cc?.ToList();
            return Task.FromResult(true);
        }

        public Task<bool> SendSelfServiceRequestCreatedAsync(
            Request request,
            RequestForm requestForm,
            string recipientEmail,
            string recipientName,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            LastNotificationKind = "SelfServiceRequestCreated";
            LastTicket = request;
            LastRequestForm = requestForm;
            LastRecipientEmail = recipientEmail;
            LastRecipientName = recipientName;
            LastCc = cc?.ToList();
            return Task.FromResult(true);
        }

        public Task<bool> SendSelfServiceRequestCompletedAsync(
            Request request,
            string recipientEmail,
            string recipientName,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("SelfServiceRequestCompleted", request, recipientEmail, recipientName, cc);

        public Task<bool> SendSelfServiceRequestFailedAsync(
            Request request,
            Incident incident,
            string failureReason,
            string recipientEmail,
            string recipientName,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("SelfServiceRequestFailed", request, recipientEmail, recipientName, cc);

        public Task<bool> SendRequestApprovalRequiredAsync(
            Request request,
            RequestTask task,
            RequestTaskApproval approval,
            string approvers,
            string payloadHtml,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("RequestApprovalRequired", request, approval.ApproverEmail, approval.ApproverName, cc);

        public Task<bool> SendRequestApprovalDeclinedAsync(
            Request request,
            RequestTask task,
            RequestTaskApproval approval,
            string rejectionReason,
            string payloadHtml,
            string recipientEmail,
            string recipientName,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("RequestApprovalDeclined", request, recipientEmail, recipientName, cc);

        public Task<bool> SendTicketResolvedAsync(
            Ticket ticket,
            string recipientEmail,
            string recipientName,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("TicketResolved", ticket, recipientEmail, recipientName, cc);

        public Task<bool> SendChangeSubmittedAsync(
            Change change,
            string recipientEmail,
            string recipientName,
            string organizationName,
            string requestedForName,
            string implementorName,
            string approvers,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("ChangeSubmitted", change, recipientEmail, recipientName, cc);

        public Task<bool> SendChangeApprovalRequiredAsync(
            Change change,
            ChangeApproval approval,
            string organizationName,
            string requestedForName,
            string implementorName,
            string approvers,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("ChangeApprovalRequired", change, approval.ApproverEmail, approval.ApproverName, cc);

        public Task<bool> SendChangeApprovedAsync(
            Change change,
            string recipientEmail,
            string recipientName,
            string organizationName,
            string requestedForName,
            string implementorName,
            string approvers,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("ChangeApproved", change, recipientEmail, recipientName, cc);

        public Task<bool> SendChangeImplementationInProgressAsync(
            Change change,
            string recipientEmail,
            string recipientName,
            string organizationName,
            string requestedForName,
            string implementorName,
            string approvers,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("ChangeImplementationInProgress", change, recipientEmail, recipientName, cc);

        public Task<bool> SendChangeImplementedAsync(
            Change change,
            string recipientEmail,
            string recipientName,
            string organizationName,
            string requestedForName,
            string implementorName,
            string approvers,
            string completionState,
            IEnumerable<string>? cc = null,
            CancellationToken cancellationToken = default) =>
            Capture("ChangeImplemented", change, recipientEmail, recipientName, cc);

        private Task<bool> Capture(
            string kind,
            Ticket ticket,
            string recipientEmail,
            string recipientName,
            IEnumerable<string>? cc)
        {
            SendCount++;
            LastNotificationKind = kind;
            LastTicket = ticket;
            LastRecipientEmail = recipientEmail;
            LastRecipientName = recipientName;
            LastCc = cc?.ToList();
            return Task.FromResult(true);
        }
    }

    private sealed class TestTenantContext(string? tenantId, string? userId, bool isHelpdeskAdmin) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin { get; } = isHelpdeskAdmin;
    }

    private sealed class ClaimsCurrentUserAccessService : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default) =>
            Task.FromResult(CurrentUserAccessProfile.FromClaims(user));
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var auth = Request.Headers.Authorization.ToString();
            Claim[] claims;
            if (auth.Contains("NoEmail", StringComparison.OrdinalIgnoreCase))
            {
                claims =
                [
                    new Claim(ClaimTypes.Name, "No Email User"),
                    new Claim(ClaimTypes.Role, "HelpdeskAdmin")
                ];
            }
            else if (auth.Contains("DifferentDomain", StringComparison.OrdinalIgnoreCase))
            {
                claims =
                [
                    new Claim(ClaimTypes.Name, "Local User"),
                    new Claim("name", "Local User"),
                    new Claim("preferred_username", "local.user@unrelated.example"),
                    new Claim(ClaimTypes.Role, "SelfService.User"),
                    new Claim("organization_id", "org-1"),
                    new Claim("allowed_organization_id", "org-1"),
                    new Claim("scoped_permission", "SelfService.User|org-1")
                ];
            }
            else if (auth.Contains("Msp", StringComparison.OrdinalIgnoreCase))
            {
                claims =
                [
                    new Claim(ClaimTypes.Name, "MSP User"),
                    new Claim("name", "MSP User"),
                    new Claim("preferred_username", "msp@example.com"),
                    new Claim(ClaimTypes.Role, "SelfService.User"),
                    new Claim("organization_id", "org-1"),
                    new Claim("allowed_organization_id", "org-1"),
                    new Claim("allowed_organization_id", "org-managed"),
                    new Claim("managed_organization_id", "org-managed")
                ];
            }
            else if (auth.Contains("HelpdeskAdmin", StringComparison.OrdinalIgnoreCase))
            {
                claims =
                [
                    new Claim(ClaimTypes.Name, "Admin"),
                    new Claim("name", "Admin"),
                    new Claim("preferred_username", "admin@example.com"),
                    new Claim(ClaimTypes.Role, "HelpdeskAdmin"),
                    new Claim("organization_id", "org-1"),
                    new Claim("allowed_organization_id", "org-1")
                ];
            }
            else
            {
                claims =
                [
                    new Claim(ClaimTypes.Name, "Test"),
                    new Claim("name", "Example User"),
                    new Claim("preferred_username", "customer@example.com"),
                    new Claim("customer_id", "customer-1"),
                    new Claim(ClaimTypes.Role, "SelfService.User"),
                    new Claim("organization_id", "org-1"),
                    new Claim("allowed_organization_id", "org-1")
                ];
            }
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
