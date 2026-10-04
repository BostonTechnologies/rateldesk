using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

/// <summary>Real local HTTP and supported provider storage for the service-link contract tests.</summary>
internal sealed class ServiceLinkKestrelPeer : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rateldesk-service-link-tests", Guid.NewGuid().ToString("N"));
    private readonly Action<IServiceCollection, IConfiguration> configureServices;
    private readonly Action<WebApplication> mapEndpoints;
    private readonly Dictionary<string, string?> configuration;
    private PostgreSqlContainer? container;
    private WebApplication? app;
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => app!.Services;
    public string BaseUrl { get; }
    public string OrganizationId { get; } = Guid.NewGuid().ToString("D");
    public string CustomerId { get; } = Guid.NewGuid().ToString("D");
    public Guid InstanceId { get; } = Guid.NewGuid();
    public LifecycleTestClock Clock { get; } = new();

    private ServiceLinkKestrelPeer(Action<IServiceCollection, IConfiguration> configureServices,
        Action<WebApplication> mapEndpoints)
    {
        this.configureServices = configureServices;
        this.mapEndpoints = mapEndpoints;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}";
        configuration = new()
        {
            ["StorageOptions:RootPath"] = Path.Combine(root, "storage"),
            ["StorageOptions:PublicApiBaseUrl"] = BaseUrl,
            ["StorageOptions:ImageSigningSecret"] = "synthetic-receiver-signing-key-32-characters",
            ["PublicWebAppUrl"] = BaseUrl,
            ["Netclaw:Enabled"] = "false",
            ["ServiceIdentity:Enabled"] = "true",
            ["ServiceIdentity:Issuer"] = BaseUrl,
            ["ServiceIdentity:ApiBaseUrl"] = BaseUrl,
            ["ServiceIdentity:WebBaseUrl"] = BaseUrl,
            ["ServiceIdentity:Audience"] = "rateldesk-test-api",
            ["ServiceIdentity:InstanceId"] = InstanceId.ToString("D"),
            ["ServiceIdentity:AllowPrivateHttp"] = "true",
            ["ServiceLinks:Enabled"] = "true",
            ["ServiceLinks:WebBaseUrl"] = BaseUrl,
            ["ServiceLinks:ApiBaseUrl"] = BaseUrl,
            ["ServiceLinks:AllowPrivateHttp"] = "true",
            ["ServiceLinks:BootstrapLifetimeSeconds"] = "120",
            ["ServiceLinks:AutomaticRotationEnabled"] = "false"
        };
    }

    public static async Task<ServiceLinkKestrelPeer> CreateAsync(bool postgres,
        Action<IServiceCollection, IConfiguration> configureServices, Action<WebApplication> mapEndpoints)
    {
        var peer = new ServiceLinkKestrelPeer(configureServices, mapEndpoints);
        Directory.CreateDirectory(peer.root);
        peer.configuration["Database:Provider"] = postgres ? "PostgreSql" : "Sqlite";
        peer.configuration["Database:Sqlite:Path"] = Path.Combine(peer.root, "service-links.db");
        if (postgres)
        {
            peer.container = new PostgreSqlBuilder("postgres:16").Build();
            await peer.container.StartAsync();
            peer.configuration["ConnectionStrings:HelpdeskDb"] = peer.container.GetConnectionString();
        }
        await peer.StartAsync();
        await using var scope = peer.Services.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
        await identity.Database.MigrateAsync();
        identity.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", Email = "owner@example.test",
            IsEnabled = true, IsInstanceAdministrator = true });
        await identity.SaveChangesAsync();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        await db.Database.MigrateAsync();
        db.InstanceInitializations.Add(new() { InstanceId = peer.InstanceId, OperationId = Guid.NewGuid(),
            SetupVersion = "service-link-contract-test", CompletedAtUtc = peer.Clock.GetUtcNow() });
        db.Organizations.Add(new() { Id = peer.OrganizationId, Name = "Synthetic link organization", IsEnabled = true });
        db.Customers.Add(new() { Id = peer.CustomerId, OrganizationId = peer.OrganizationId,
            Name = "Synthetic link customer", Email = "customer@example.test", IsEnabled = true });
        await db.SaveChangesAsync();
        return peer;
    }

    private async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(BaseUrl);
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")))
            .SetApplicationName("RatelDeskServiceLinkContractTests");
        builder.Services.AddHelpdeskInfrastructure(builder.Configuration);
        builder.Services.AddDbContext<HelpdeskDbContext>(options =>
            options.LogTo(Console.Error.WriteLine, LogLevel.Error));
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<TimeProvider>();
        builder.Services.AddSingleton<TimeProvider>(Clock);
        configureServices(builder.Services, builder.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddScoped<IIntegrationCredentialOwnerResolver, IntegrationCredentialOwnerResolver>();
        builder.Services.AddScoped<IAuthorizationHandler, IntegrationCredentialManagementSessionHandler>();
        builder.Services.AddAuthentication("LifecycleTest")
            .AddPolicyScheme("LifecycleTest", "LifecycleTest", options => options.ForwardDefaultSelector = http =>
                http.Request.Headers.ContainsKey("X-Synthetic-Admin") ? "LifecycleAdmin" : "RatelDeskService")
            .AddScheme<AuthenticationSchemeOptions, LifecycleAdminAuthentication>("LifecycleAdmin", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(IntegrationCredentialEndpoints.CredentialManagementPolicy, policy =>
            { policy.RequireAuthenticatedUser(); policy.AddRequirements(new IntegrationCredentialManagementSessionRequirement()); });
            options.AddPolicy("HelpdeskAdmin", policy => policy.RequireRole("HelpdeskAdmin"));
        });
        app = builder.Build();
        app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication();
        app.UseMiddleware<Helpdesk.API.Middleware.UserAccessClaimsMiddleware>();
        app.UseAuthorization();
        app.Use(async (http, next) =>
        {
            if (!http.Request.Headers.ContainsKey("X-Synthetic-Lost-Response")) { await next(); return; }
            var transport = http.Response.Body;
            await using var discarded = new MemoryStream();
            http.Response.Body = discarded;
            try { await next(); }
            finally { http.Response.Body = transport; }
            // The actual handler and relational commit have completed; no result is delivered.
            http.Abort();
        });
        mapEndpoints(app);
        await app.StartAsync();
        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl) };
    }

    public async Task RestartAsync()
    {
        Client.Dispose();
        await app!.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartAsync();
    }

    public async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(ProjectLifecycle(path, body));
        request.Headers.Add("X-Synthetic-Admin", "true");
        return await Client.SendAsync(request);
    }

    public async Task<HttpResponseMessage> ServiceAsync(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(ProjectLifecycle(path, body));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Client.SendAsync(request);
    }

    private static object ProjectLifecycle(string path, object body) => body is ServiceLinkLifecycleRequest lifecycle
        ? ServiceLinkLifecycleProjection.Build(path[(path.LastIndexOf('/') + 1)..], lifecycle) : body;

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        if (container is not null) await container.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class LifecycleAdminAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Role, "HelpdeskAdmin"),
                new Claim("auth_mode", "local")], Scheme.Name)), Scheme.Name)));
    }
}

internal sealed class LifecycleTestClock : TimeProvider
{
    private DateTimeOffset current = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan elapsed) => current += elapsed;
}
