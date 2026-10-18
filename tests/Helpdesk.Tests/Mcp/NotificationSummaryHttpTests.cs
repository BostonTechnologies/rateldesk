using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.API.Endpoints.Notifications;
using Helpdesk.AgentClient;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Mcp.Tools;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Notification;
using Helpdesk.Shared.Models;
using Helpdesk.Tests.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Tests.Mcp;

[Collection("Incident receiver")]
public sealed class NotificationSummaryHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task General_summary_matches_entire_authorized_population_and_confirmed_read_subsets(bool postgres)
    {
        await using var fixture = await NotificationSummaryFixture.CreateAsync(postgres);
        var tools = fixture.Tools();
        var initial = await fixture.ReadEntirePopulationAsync();
        Assert.Equal(fixture.VisibleIds.Order(), initial.Select(row => row.Id).Order());
        Assert.Equal(7, initial.Count);
        Assert.Equal(5, initial.Count(row => !row.IsRead));
        Assert.Equal(3, initial.Count(row => !row.IsRead && row.Severity is NotificationSeverity.Error or NotificationSeverity.Critical));
        await fixture.AssertSummaryAsync(7, 5, 3);
        // The preserved legacy endpoint reproduces the false-zero shape the adapter used to return.
        using var errorOnly = await fixture.GetAsync("/api/v1/notifications/error-summary");
        var errorSummary = await errorOnly.Content.ReadFromJsonAsync<NotificationSummaryDto>();
        Assert.Equal(0, errorSummary!.TotalCount);
        Assert.Equal(0, errorSummary.UnreadCount);
        Assert.Equal(3, errorSummary.UnreadErrorCount);
        var summary = await tools.helpdesk_notifications("summary");
        Assert.True(summary.Success, summary.Summary);
        AssertCounters(summary.Data!, 7, 5, 3);
        Assert.Equal("/api/v1/notifications/summary", fixture.Paths.Last());
        var errors = await tools.helpdesk_notifications("unread_errors");
        Assert.True(errors.Success, errors.Summary);
        Assert.Equal(3, errors.Data!.AsArray().Count);
        Assert.StartsWith("/api/v1/notifications/unread-errors", fixture.Paths.Last(), StringComparison.Ordinal);

        var otherPopulation = await fixture.ReadEntirePopulationAsync(fixture.OtherToken);
        Assert.Equal(5, otherPopulation.Count);
        Assert.Equal(4, otherPopulation.Count(row => !row.IsRead));
        Assert.Equal(2, otherPopulation.Count(row => !row.IsRead && row.Severity is NotificationSeverity.Error or NotificationSeverity.Critical));
        var otherBefore = await fixture.ReadActorStateAsync(fixture.OtherToken);
        var before = await fixture.PersistenceSnapshotAsync();
        var markRequest = JsonSerializer.SerializeToElement(new { ids = fixture.MarkIds });
        var outbound = fixture.Paths.Count;
        var preview = await tools.helpdesk_notifications("mark_read", markRequest);
        Assert.Equal("confirmation_required", preview.Status);
        Assert.Equal(outbound, fixture.Paths.Count);
        Assert.Equal(before, await fixture.PersistenceSnapshotAsync());
        await fixture.AssertSummaryAsync(7, 5, 3);

        var marked = await tools.helpdesk_notifications("mark_read", markRequest, true);
        Assert.True(marked.Success, marked.Summary);
        Assert.Equal(2, marked.Data!["updated"]!.GetValue<int>());
        await fixture.AssertSummaryAsync(7, 3, 1);
        AssertCounters((await tools.helpdesk_notifications("summary")).Data!, 7, 3, 1);
        Assert.Equal(otherBefore, await fixture.ReadActorStateAsync(fixture.OtherToken));
        var fresh = await fixture.ReadEntirePopulationAsync();
        Assert.Equal(initial.Select(row => row.Id).Order(), fresh.Select(row => row.Id).Order());
        Assert.All(fresh.Where(row => fixture.VisibleMarkIds.Contains(row.Id)), row => Assert.True(row.IsRead));
        Assert.All(fresh.Where(row => !fixture.VisibleMarkIds.Contains(row.Id)), row =>
            Assert.Equal(initial.Single(previous => previous.Id == row.Id).IsRead, row.IsRead));
        await fixture.AssertInaccessibleRowsUnchangedAsync();

        var after = await fixture.PersistenceSnapshotAsync();
        var repeated = await tools.helpdesk_notifications("mark_read", markRequest, true);
        Assert.True(repeated.Success, repeated.Summary);
        Assert.Equal(0, repeated.Data!["updated"]!.GetValue<int>());
        var hiddenOnly = await tools.helpdesk_notifications("mark_read", JsonSerializer.SerializeToElement(new { ids = fixture.HiddenIds }), true);
        Assert.True(hiddenOnly.Success, hiddenOnly.Summary);
        Assert.Equal(0, hiddenOnly.Data!["updated"]!.GetValue<int>());
        var missingOnly = await tools.helpdesk_notifications("mark_read", JsonSerializer.SerializeToElement(new { ids = new[] { Guid.NewGuid() } }), true);
        Assert.True(missingOnly.Success, missingOnly.Summary);
        Assert.Equal(hiddenOnly.Data["updated"]!.GetValue<int>(), missingOnly.Data!["updated"]!.GetValue<int>());
        Assert.Equal(after, await fixture.PersistenceSnapshotAsync());
        await fixture.AssertSummaryAsync(7, 3, 1);
        foreach (var id in fixture.HiddenIds)
        {
            using var denied = await fixture.GetAsync("/api/v1/notifications/" + id);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_general_summary_executes_authenticated_API_and_preserves_error_query(bool postgres)
    {
        await using var fixture = await NotificationSummaryFixture.CreateAsync(postgres);
        var directory = Directory.CreateTempSubdirectory("notification-cli-");
        try
        {
            var path = Path.Combine(directory.FullName, "cli.json");
            new AgentClientConfigurationStore(path).Save(fixture.Configuration);
            var output = new StringWriter();
            var error = new StringWriter();
            var runtime = new CliRuntime(fixture.CreateHandler) { Out = output, Error = error };
            var exit = await HelpdeskCli.RunAsync(["--config", path, "notifications", "summary", "--json"], runtime);
            Assert.Equal(CliExitCodes.Success, exit);
            Assert.Empty(error.ToString());
            AssertCounters(JsonNode.Parse(output.ToString())!, 7, 5, 3);
            Assert.Equal("/api/v1/notifications/summary", Assert.Single(fixture.Paths));
            output.GetStringBuilder().Clear();
            exit = await HelpdeskCli.RunAsync(["--config", path, "notifications", "unread-errors", "--json"], runtime);
            Assert.Equal(CliExitCodes.Success, exit);
            Assert.StartsWith("/api/v1/notifications/unread-errors", fixture.Paths.Last(), StringComparison.Ordinal);
            Assert.Equal(3, JsonNode.Parse(output.ToString())!.AsArray().Count);
        }
        finally { directory.Delete(true); }
    }

    internal static void AssertCounters(JsonNode data, int total, int unread, int errors)
    {
        Assert.Equal(total, data["totalCount"]!.GetValue<int>());
        Assert.Equal(unread, data["unreadCount"]!.GetValue<int>());
        Assert.Equal(errors, data["unreadErrorCount"]!.GetValue<int>());
    }
}

internal sealed class NotificationSummaryFixture : IAsyncDisposable
{
    private readonly IncidentReceiverTests.Harness harness;
    private NotificationSummaryFixture(IncidentReceiverTests.Harness harness) => this.harness = harness;
    private readonly Guid[] ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
    public List<string> Paths { get; } = [];
    public string OtherToken { get; private set; } = string.Empty;
    public string Token => harness.Token;
    public Guid[] VisibleIds => ids[..7];
    public Guid[] VisibleMarkIds => [ids[1], ids[4]];
    public Guid[] HiddenIds => ids[7..];
    public Guid[] MarkIds => [.. VisibleMarkIds, .. HiddenIds, Guid.NewGuid()];
    public AgentClientConfiguration Configuration => AgentClientConfiguration.Empty with
    {
        ApiBaseUrl = "https://api.example.test", CredentialMode = "integration", IntegrationCredential = Token,
        CredentialTarget = "https://api.example.test"
    };

    public static async Task<NotificationSummaryFixture> CreateAsync(bool postgres,
        Action<IServiceCollection>? configureServices = null)
    {
        var harness = await IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true,
            configureServices: services =>
            {
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("NotificationAccess", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin"));
                });
                configureServices?.Invoke(services);
            }, configureApp: app => app.MapNotificationEndpoints());
        var fixture = new NotificationSummaryFixture(harness);
        var other = await harness.AddCredentialAsync();
        fixture.OtherToken = other.Token;
        await using (var scope = harness.App.Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.Users.Add(new ApplicationUser { Id = "notification-other", UserName = "notification-other", Email = "other@example.test", IsEnabled = true });
            (await identity.IntegrationCredentials.SingleAsync(row => row.Id == other.Id)).OwnerUserId = "notification-other";
            await identity.SaveChangesAsync();
        }
        await harness.EditAsync(db =>
        {
            db.Users.Add(new User { Id = "notification-other", Name = "Other synthetic reader", Email = "other@example.test", OrganizationId = harness.OrganizationId });
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "notification-other", OrganizationId = harness.OrganizationId, RoleKey = ScopedRoleCatalog.IncidentReader });
            db.Organizations.Add(new Organization { Id = "notification-foreign", Name = "Foreign synthetic organization" });
            db.Incidents.Add(new Incident { Id = "notification-incident", TrackingId = "INC-NOTIFICATION", Title = "Authorized incident", OrganizationId = harness.OrganizationId });
            db.Changes.Add(new Change { Id = "notification-change", TrackingId = "CHG-NOTIFICATION", Title = "Inaccessible module", OrganizationId = harness.OrganizationId });
            var severities = new[] { NotificationSeverity.Info, NotificationSeverity.Error, NotificationSeverity.Critical,
                NotificationSeverity.Error, NotificationSeverity.Critical, NotificationSeverity.Info, NotificationSeverity.Warning };
            var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < fixture.ids.Length; index++)
                db.Notifications.Add(new NotificationEntity
                {
                    Id = fixture.ids[index], Title = index == 6 ? "outside-campaign" : "campaign-" + index,
                    Message = "Synthetic notification", CreatedUtc = created.AddMinutes(index),
                    UserId = index < 3 || index == 8 ? "owner" : index == 9 ? "notification-other" : null,
                    IsGlobal = index >= 3 && index is not 8 and not 9,
                    TenantId = index is 7 or 8 ? "notification-foreign" : index == 11 ? null : harness.OrganizationId,
                    Reference = index is >= 3 and <= 6 ? "INC-NOTIFICATION" : index == 10 ? "CHG-NOTIFICATION" : null,
                    Severity = index < 7 ? severities[index] : NotificationSeverity.Error,
                    ReadUtc = index == 2 ? created : null
                });
            db.NotificationReads.AddRange(
                new NotificationReadEntity { NotificationId = fixture.ids[5], UserId = "owner", ReadUtc = created },
                new NotificationReadEntity { NotificationId = fixture.ids[3], UserId = "notification-other", ReadUtc = created });
            return Task.CompletedTask;
        });
        return fixture;
    }

    public HttpMessageHandler CreateHandler() => new ApiHandler(this);

    public async Task<WebApplication> StartApiBridgeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
                request.Headers.TryAddWithoutValidation("Authorization", authorization.ToString());
            if (context.Request.ContentLength is > 0)
            {
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
            using var response = await harness.Client.SendAsync(request, context.RequestAborted);
            Paths.Add(context.Request.Path + context.Request.QueryString);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.StartAsync();
        return app;
    }

    public static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    public HelpdeskTools Tools() => new(new HelpdeskAgentClient(Configuration, CreateHandler),
        new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), $"notification-adapter-{Guid.NewGuid():N}.json")));

    public async Task<HttpResponseMessage> GetAsync(string path, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token);
        return await harness.Client.SendAsync(request);
    }

    public async Task<IReadOnlyList<NotificationDto>> ReadEntirePopulationAsync(string? token = null)
    {
        var rows = new List<NotificationDto>();
        for (var page = 1; ; page++)
        {
            using var response = await GetAsync($"/api/v1/notifications?page={page}&pageSize=2", token);
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<PagedResponse<NotificationDto>>())!;
            rows.AddRange(result.Items);
            if (rows.Count >= result.TotalCount) return rows;
            Assert.NotEmpty(result.Items);
        }
    }

    public async Task AssertSummaryAsync(int total, int unread, int errors)
    {
        using var response = await GetAsync("/api/v1/notifications/summary");
        response.EnsureSuccessStatusCode();
        NotificationSummaryHttpTests.AssertCounters(JsonNode.Parse(await response.Content.ReadAsStringAsync())!, total, unread, errors);
    }

    public async Task<string> ReadActorStateAsync(string token)
    {
        using var response = await GetAsync("/api/v1/notifications/summary", token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync() + JsonSerializer.Serialize(await ReadEntirePopulationAsync(token));
    }

    public async Task<string> PersistenceSnapshotAsync()
    {
        string snapshot = string.Empty;
        await harness.EditAsync(async db => snapshot = JsonSerializer.Serialize(new
        {
            notifications = await db.Notifications.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ReadUtc }).ToListAsync(),
            receipts = await db.NotificationReads.AsNoTracking().OrderBy(row => row.NotificationId).ThenBy(row => row.UserId).Select(row => new { row.NotificationId, row.UserId, row.ReadUtc }).ToListAsync()
        }));
        return snapshot;
    }

    public Task AssertInaccessibleRowsUnchangedAsync() => harness.EditAsync(async db =>
    {
        Assert.All(await db.Notifications.AsNoTracking().Where(row => HiddenIds.Contains(row.Id)).ToListAsync(), row => Assert.Null(row.ReadUtc));
        Assert.Empty(await db.NotificationReads.AsNoTracking().Where(row => HiddenIds.Contains(row.NotificationId)).ToListAsync());
    });

    public Task AddSummaryWorkloadAsync() => harness.EditAsync(db =>
    {
        for (var index = 0; index < 800; index++)
        {
            var id = Guid.NewGuid();
            db.Notifications.Add(new NotificationEntity
            {
                Id = id, Title = "Synthetic summary workload", Message = "Synthetic notification",
                CreatedUtc = DateTime.UtcNow, IsGlobal = true,
                TenantId = index < 600 ? harness.OrganizationId : "notification-foreign",
                Reference = "INC-NOTIFICATION",
                Severity = index % 3 == 0 ? NotificationSeverity.Error : NotificationSeverity.Info
            });
            if (index % 4 == 0)
                db.NotificationReads.Add(new NotificationReadEntity { NotificationId = id, UserId = "owner", ReadUtc = DateTime.UtcNow });
            if (index % 5 == 0)
                db.NotificationReads.Add(new NotificationReadEntity { NotificationId = id, UserId = "notification-other", ReadUtc = DateTime.UtcNow });
        }
        return Task.CompletedTask;
    });

    public Task ClearSummaryWorkloadAsync() => harness.EditAsync(async db =>
    {
        await db.NotificationReads.ExecuteDeleteAsync();
        await db.Notifications.ExecuteDeleteAsync();
    });

    public ValueTask DisposeAsync() => harness.DisposeAsync();

    private sealed class ApiHandler(NotificationSummaryFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // External-auth HTTP MCP uses a separately configured downstream identity.
            if (request.RequestUri!.AbsolutePath == "/token")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = fixture.Token }) };
            fixture.Paths.Add(request.RequestUri.PathAndQuery);
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri.PathAndQuery);
            forwarded.Headers.Authorization = request.Headers.Authorization;
            if (request.Content is not null)
                forwarded.Content = new StringContent(await request.Content.ReadAsStringAsync(cancellationToken), Encoding.UTF8, "application/json");
            return await fixture.harness.Client.SendAsync(forwarded, cancellationToken);
        }
    }
}
