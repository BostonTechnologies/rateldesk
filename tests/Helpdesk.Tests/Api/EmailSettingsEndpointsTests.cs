using System.Net;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Application.WorkLogs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Endpoints.Email;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Helpdesk.Tests.Api;

public class EmailSettingsEndpointsTests
{
    [Fact]
    public async Task Get_RequiresHelpdeskAdminAndDoesNotReturnSettingsToDeniedCallers()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.SeedAsync();
        using var anonymous = harness.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/email-settings")).StatusCode);
        using var ordinary = harness.CreateClient("Technician");
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.GetAsync("/api/v1/email-settings")).StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsDisabledSettingsWithExplicitScopeAndRedactedCredentials()
    {
        await using var harness = await Harness.CreateAsync();
        var saved = await harness.SeedAsync(enabled: false, backgroundSyncEnabled: false);
        var settings = await harness.Client.GetFromJsonAsync<List<EmailInboxSettingsDto>>("/api/v1/email-settings");
        var row = Assert.Single(settings!);
        Assert.Equal(saved.Id, row.Id); Assert.False(row.Enabled); Assert.Equal(MailboxScope.Global, row.Scope);
        Assert.True(row.HasClientSecret);
        Assert.DoesNotContain(saved.ClientSecret, await harness.Client.GetStringAsync("/api/v1/email-settings"));
    }

    [Fact]
    public async Task Put_PreservesBlankSecretForUnchangedSourceAndRejectsStaleVersion()
    {
        await using var harness = await Harness.CreateAsync();
        var saved = await harness.SeedAsync();
        var request = Payload(saved); request.DisplayName = "Renamed support";
        var response = await harness.Client.PutAsJsonAsync($"/api/v1/email-settings/{saved.Id}", request);
        response.EnsureSuccessStatusCode();
        var updated = Assert.Single(await harness.AllSettingsAsync());
        Assert.Equal(saved.ClientSecret, updated.ClientSecret); Assert.Equal(saved.Version + 1, updated.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await harness.Client.PutAsJsonAsync($"/api/v1/email-settings/{saved.Id}", request)).StatusCode);
    }

    [Fact]
    public async Task Put_RejectsSourceChangesWithoutReassigningCredentials()
    {
        await using var harness = await Harness.CreateAsync(); var saved = await harness.SeedAsync();
        var request = Payload(saved); request.MailHost = "attacker.example.test";
        Assert.Equal(HttpStatusCode.BadRequest, (await harness.Client.PutAsJsonAsync($"/api/v1/email-settings/{saved.Id}", request)).StatusCode);
        Assert.Equal(saved.MailHost, Assert.Single(await harness.AllSettingsAsync()).MailHost);
    }

    [Fact]
    public async Task PostWithoutId_ConflictsWithExistingGlobalRatherThanUpdatingFirstRow()
    {
        await using var harness = await Harness.CreateAsync(); var saved = await harness.SeedAsync();
        var request = Payload(saved); request.Id = Guid.Empty; request.MailboxAddress = "second@example.test"; request.ClientSecret = "synthetic-secret";
        Assert.Equal(HttpStatusCode.Conflict, (await harness.Client.PostAsJsonAsync("/api/v1/email-settings", request)).StatusCode);
        Assert.Equal(saved.MailboxAddress, Assert.Single(await harness.AllSettingsAsync()).MailboxAddress);
    }

    [Fact]
    public async Task Post_ExplicitCreateReturnsNewIdAndDoesNotEnableIngestion()
    {
        await using var harness = await Harness.CreateAsync();
        var request = Payload(new EmailInboxSettings { MailboxAddress = "support@example.test", MailHost = "mail.example.test", MailboxFolder = "inbox", TenantId = DirectoryId, ClientId = ClientId });
        var response = await harness.Client.PostAsJsonAsync("/api/v1/email-settings", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<EmailInboxSettingsDto>();
        Assert.NotEqual(Guid.Empty, result!.Id); Assert.False(result.Enabled); Assert.False(result.BackgroundSyncEnabled);
    }

    [Fact]
    public async Task Test_UsesDraftDoesNotPersistAndDispatchesToSelectedProvider()
    {
        await using var harness = await Harness.CreateAsync(); var saved = await harness.SeedAsync();
        var request = Payload(saved); request.DisplayName = "Unsaved draft";
        var response = await harness.Client.PostAsJsonAsync("/api/v1/email-settings/test", request);
        response.EnsureSuccessStatusCode();
        Assert.NotEqual(request.DisplayName, Assert.Single(await harness.AllSettingsAsync()).DisplayName);
        await harness.Graph.Received(1).TestAsync(Arg.Is<EmailInboxSettings>(x => x.DisplayName == "Unsaved draft" && x.ClientSecret == saved.ClientSecret), Arg.Any<CancellationToken>());
        await harness.Imap.DidNotReceive().TestConnectionAsync(Arg.Any<ImapEmailSettings>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_ChangedDestinationCannotReuseSavedCredentials()
    {
        await using var harness = await Harness.CreateAsync(); var saved = await harness.SeedAsync();
        var request = Payload(saved); request.MailHost = "attacker.example.test";
        Assert.Equal(HttpStatusCode.BadRequest, (await harness.Client.PostAsJsonAsync("/api/v1/email-settings/test", request)).StatusCode);
        await harness.Graph.DidNotReceive().TestAsync(Arg.Any<EmailInboxSettings>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task EnableDisable_AlwaysReturnsRedactedDto(string action)
    {
        await using var harness = await Harness.CreateAsync(); var saved = await harness.SeedAsync();
        var response = await harness.Client.PostAsync($"/api/v1/email-settings/{saved.Id}/{action}", null);
        response.EnsureSuccessStatusCode(); var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(saved.ClientSecret, json); Assert.Contains("hasClientSecret", json);
    }

    [Fact]
    public async Task Diagnostics_redacts_provider_checkpoint_and_source_identity()
    {
        await using var harness = await Harness.CreateAsync();
        var saved = await harness.SeedAsync();
        await harness.WithDbAsync(async db =>
        {
            var state = await db.Set<MailboxIngestionState>().SingleAsync();
            state.Cursor = "synthetic-sensitive-provider-cursor";
            await db.SaveChangesAsync();
        });
        var json = await harness.Client.GetStringAsync($"/api/v1/email-settings/{saved.Id}/diagnostics");
        Assert.DoesNotContain("synthetic-sensitive-provider-cursor", json);
        Assert.DoesNotContain("sourceKey", json);
        Assert.Contains("hasCheckpoint", json);
    }

    [Fact]
    public async Task Historical_import_only_queues_confirmed_baseline_skips_and_never_replays_success()
    {
        await using var harness = await Harness.CreateAsync();
        var mailbox = await harness.SeedAsync();
        var skipped = new InboundMessageReceipt { MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey,
            TransportKey = "historical-1", Outcome = InboundReceiptOutcome.Ignored,
            Reason = "InitialBaselineSkipped", Acknowledged = true };
        var succeeded = new InboundMessageReceipt { MailboxId = mailbox.Id, SourceKey = mailbox.SourceKey,
            TransportKey = "handled-1", Outcome = InboundReceiptOutcome.Succeeded,
            Acknowledged = true };
        await harness.WithDbAsync(async db =>
        {
            db.Set<InboundMessageReceipt>().AddRange(skipped, succeeded);
            await db.SaveChangesAsync();
        });
        harness.Historical.PreviewAsync(Arg.Any<EmailInboxSettings>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>()).Returns(call =>
                Task.FromResult<IReadOnlyList<HistoricalSourcePreview>>(
                    [new("historical-1", "sender@example.test", "Synthetic subject", DateTimeOffset.UtcNow, true, null)]));
        var path = $"/api/v1/email-settings/{mailbox.Id}/historical";
        var preview = await harness.Client.PostAsJsonAsync($"{path}/preview", new HistoricalMailboxPreviewRequest(null, null));
        preview.EnsureSuccessStatusCode();
        Assert.Equal(skipped.Id, Assert.Single((await preview.Content.ReadFromJsonAsync<HistoricalMailboxPreviewResult>())!.Items).ReceiptId);
        Assert.Equal(HttpStatusCode.BadRequest, (await harness.Client.PostAsJsonAsync($"{path}/import",
            new HistoricalMailboxImportRequest([skipped.Id], false))).StatusCode);
        (await harness.Client.PostAsJsonAsync("/api/v1/email-settings/worker", new SetMailboxWorkerRequest(true, true)))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await harness.Client.PostAsJsonAsync($"{path}/import",
            new HistoricalMailboxImportRequest([succeeded.Id], true))).StatusCode);
        var import = await harness.Client.PostAsJsonAsync($"{path}/import",
            new HistoricalMailboxImportRequest([skipped.Id], true));
        Assert.Equal(HttpStatusCode.Accepted, import.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await harness.Client.PostAsJsonAsync($"{path}/import",
            new HistoricalMailboxImportRequest([skipped.Id], true))).StatusCode);
        await harness.WithDbAsync(async db =>
        {
            var receipt = await db.Set<InboundMessageReceipt>().SingleAsync(x => x.Id == skipped.Id);
            Assert.NotNull(receipt.HistoricalImportRequestId);
            Assert.Equal(InboundReceiptOutcome.Ignored, receipt.Outcome);
            Assert.Null((await db.Set<InboundMessageReceipt>().SingleAsync(x => x.Id == succeeded.Id)).HistoricalImportRequestId);
        });
    }

    [Fact]
    public async Task Configuration_audit_records_actor_and_actions_without_draft_secrets()
    {
        await using var harness = await Harness.CreateAsync();
        var request = Payload(new EmailInboxSettings { MailboxAddress = "support@example.test", MailHost = "mail.example.test", MailboxFolder = "inbox", TenantId = DirectoryId, ClientId = ClientId });
        request.ClientSecret = "synthetic-audit-secret";
        (await harness.Client.PostAsJsonAsync("/api/v1/email-settings", request)).EnsureSuccessStatusCode();
        var saved = Assert.Single(await harness.AllSettingsAsync());
        var update = Payload(saved); update.DisplayName = "synthetic-sensitive-display";
        (await harness.Client.PutAsJsonAsync($"/api/v1/email-settings/{saved.Id}", update)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await harness.Client.PutAsJsonAsync($"/api/v1/email-settings/{saved.Id}", update)).StatusCode);
        (await harness.Client.PostAsync($"/api/v1/email-settings/{saved.Id}/enable", null)).EnsureSuccessStatusCode();
        (await harness.Client.PostAsync($"/api/v1/email-settings/{saved.Id}/disable", null)).EnsureSuccessStatusCode();
        (await harness.Client.PostAsJsonAsync($"/api/v1/email-settings/{saved.Id}/archive", new { Confirmed = true, Version = (await harness.AllSettingsAsync()).Single().Version })).EnsureSuccessStatusCode();
        await harness.WithDbAsync(async db =>
        {
            var logs = await db.ActivityLogs.ToListAsync();
            Assert.Equal(5, logs.Count);
            Assert.All(logs, log =>
            {
                Assert.Equal("email-settings-test", log.UserId);
                Assert.Equal(saved.Id.ToString("D"), log.RelatedEntityId);
                Assert.DoesNotContain("synthetic-audit-secret", log.Message);
                Assert.DoesNotContain("synthetic-sensitive-display", log.Message);
            });
            foreach (var action in new[] { "created", "updated", "enabled", "disabled", "archived" })
                Assert.Contains(logs, log => log.Message.Contains($"configuration {action}."));
        });
    }

    private const string DirectoryId = "11111111-1111-4111-8111-111111111111";
    private const string ClientId = "22222222-2222-4222-8222-222222222222";
    private static MailboxSettingsRequest Payload(EmailInboxSettings s) => new()
    {
        Id = s.Id, Version = s.Version, MailHost = s.MailHost, MailboxAddress = s.MailboxAddress,
        TenantId = s.TenantId, ClientId = s.ClientId, MailboxFolder = s.MailboxFolder,
        Enabled = s.Enabled, BackgroundSyncEnabled = s.BackgroundSyncEnabled
    };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Harness(WebApplication app, HttpClient client, IImapEmailService imap)
        {
            _app = app;
            Client = client;
            Imap = imap;
        }

        public HttpClient Client { get; }
        public IImapEmailService Imap { get; }
        public IInboundMailboxAdapter Graph => _app.Services.GetRequiredService<IInboundMailboxAdapter>();
        public IHistoricalMailboxAdapter Historical => (IHistoricalMailboxAdapter)Graph;

        public HttpClient CreateClient(string? role = null)
        {
            var client = _app.GetTestClient();
            if (role is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", role);
            }

            return client;
        }

        public static async Task<Harness> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(_ => { var c = new SqliteConnection("Data Source=:memory:"); c.Open(); return c; });
            builder.Services.AddDbContext<HelpdeskDbContext>((sp, options) => options.UseSqlite(sp.GetRequiredService<SqliteConnection>()));
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddScoped<MailboxCredentialProtector>();
            builder.Services.AddScoped<MailboxSettingsService>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<MailboxWorkerPolicy>();
            builder.Services.AddScoped<MailboxOutgoingCredentialProtector>();
            builder.Services.AddScoped<MailboxOutgoingSettingsService>();
            builder.Services.AddScoped<MailboxSyncService>();
            builder.Services.AddScoped<MailboxEmailService>();
            builder.Services.AddScoped<SmtpMailboxSender>();
            builder.Services.AddScoped<GraphMailboxSender>();
            builder.Services.AddScoped<MailboxSenderResolver>();
            builder.Services.AddScoped<MailboxDestinationPolicy>();
            builder.Services.AddScoped<IHtmlToPlainTextConverter, HtmlToPlainTextConverter>();
            var graph = Substitute.For<IInboundMailboxAdapter, IHistoricalMailboxAdapter>(); graph.Provider.Returns(InboundMailboxProvider.Graph);
            graph.TestAsync(Arg.Any<EmailInboxSettings>(), Arg.Any<CancellationToken>()).Returns(new MailboxConnectionTest(true, "Synthetic provider test."));
            builder.Services.AddSingleton(graph);
            builder.Services.AddScoped<ITenantContext>(_ => Substitute.For<ITenantContext>());
            var imap = Substitute.For<IImapEmailService>();
            imap.TestConnectionAsync(Arg.Any<ImapEmailSettings>(), Arg.Any<CancellationToken>()).Returns(true);
            builder.Services.AddSingleton(imap);
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
                    policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin);
                });
            });

            var app = builder.Build();
            using (var scope = app.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Database.EnsureCreatedAsync();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapEmailSettingsEndpoints();
            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", HelpdeskPermissions.HelpdeskAdmin);
            return new Harness(app, client, imap);
        }

        public async Task<EmailInboxSettings> SeedAsync(
            bool enabled = true,
            bool backgroundSyncEnabled = true,
            string clientSecret = "secret",
            string mailboxAddress = "helpdesk@example.com",
            DateTimeOffset? updatedAt = null)
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var timestamp = updatedAt ?? DateTimeOffset.UtcNow;
            var settings = new EmailInboxSettings
            {
                Id = Guid.NewGuid(),
                MailHost = "outlook.office365.com",
                Port = 993,
                UseSsl = true,
                MailboxAddress = mailboxAddress,
                TenantId = DirectoryId,
                ClientId = ClientId,
                ClientSecret = clientSecret,
                CredentialVersion = 1,
                MailboxFolder = "INBOX",
                Enabled = enabled,
                BackgroundSyncEnabled = backgroundSyncEnabled,
                CreatedAt = timestamp.AddMinutes(-5),
                UpdatedAt = timestamp
            };
            settings.ClientSecret = scope.ServiceProvider.GetRequiredService<MailboxCredentialProtector>().Protect(settings.Id, clientSecret);
            settings.SourceKey = MailboxSettingsService.SourceKey(settings);
            db.EmailInboxSettings.Add(settings);
            db.Set<MailboxIngestionState>().Add(new() { MailboxId = settings.Id, SourceKey = settings.SourceKey });
            await db.SaveChangesAsync();
            return settings;
        }

        public async Task WithDbAsync(Func<HelpdeskDbContext, Task> action)
        {
            using var scope = _app.Services.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>());
        }

        public async Task<List<EmailInboxSettings>> AllSettingsAsync()
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            return (await db.EmailInboxSettings.AsNoTracking().ToListAsync()).OrderBy(x => x.CreatedAt).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
        }
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var authorization))
            {
                return Task.FromResult(AuthenticateResult.Fail("No authorization header"));
            }

            var role = authorization.ToString().Contains(HelpdeskPermissions.HelpdeskAdmin, StringComparison.OrdinalIgnoreCase)
                ? HelpdeskPermissions.HelpdeskAdmin
                : "Technician";
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "email-settings-test"),
                new Claim(ClaimTypes.Role, role)
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
