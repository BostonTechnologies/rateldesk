using Dodo.Primitives;
using FluentValidation;
using Helpdesk.API.Authentication;
using Helpdesk.API.Documentation;
using Helpdesk.API.Email;
using Helpdesk.API.Configuration;
using Helpdesk.API.DependencyInjection;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.API.Endpoints.Activity;
using Helpdesk.API.Endpoints.Attachments;
using Helpdesk.API.Endpoints.Branding;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Captcha;
using Helpdesk.API.Endpoints.Changes;
using Helpdesk.API.Endpoints.Categories;
using Helpdesk.API.Endpoints.Customers;
using Helpdesk.API.Endpoints.Dashboard;
using Helpdesk.API.Endpoints.Email;
using Helpdesk.API.Endpoints.Errors;
using Helpdesk.API.Endpoints.Incidents;
using Helpdesk.API.Endpoints.Notifications;
using Helpdesk.API.Endpoints.Organization;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.API.Endpoints.Ops;
using Helpdesk.API.Endpoints.Presence;
using Helpdesk.API.Endpoints.Reports;
using Helpdesk.API.Endpoints.Resources;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.RequestTasks;
using Helpdesk.API.Endpoints.Search;
using Helpdesk.API.Endpoints.Sla;
using Helpdesk.API.Endpoints.SupportNotifications;
using Helpdesk.API.Endpoints.Services;
using Helpdesk.API.Endpoints.System;
using Helpdesk.API.Endpoints.Tickets;
using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.API.Endpoints.Timeline;
using Helpdesk.API.Endpoints.Users;
using Helpdesk.API.Endpoints.WorkLogs;
using Helpdesk.API.Endpoints.AiAssistant;
using Helpdesk.API.Hubs;
using Helpdesk.API.Services;
using Helpdesk.API.Middleware;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Build;
using Helpdesk.API.Ops;
using Helpdesk.API.Validators;
using Helpdesk.API.Background;
using Helpdesk.API.Bootstrap;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Events;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Logging;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.SeedData;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Health;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AppServices = Helpdesk.Application.Services;
using SharedServices = Helpdesk.Shared.Services;

var operatorCommand = BootstrapOperatorCommand.IsRequested(args);
// The shipped API runs from /app. Keep relative storage paths anchored there
// when a command is invoked from another shell directory. Explicit host
// content roots still select mounted appsettings, independently of storage paths.
if (operatorCommand) Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = operatorCommand
    ? WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = BootstrapOperatorCommand.ResolveContentRoot(AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("DOTNET_CONTENTROOT"),
            Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT"))
    })
    : WebApplication.CreateBuilder(args);
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var systemTokenSecret = builder.Configuration["SYSTEM_TOKEN_SECRET"] ?? builder.Configuration["SystemTokenSecret"];
var aiAgentOpsLogBuffer = new AiAgentOpsLogBuffer();
var skipDatabaseStartup = builder.Configuration.GetValue<bool>("Helpdesk:SkipDatabaseStartup");
var bootstrapSettings = builder.Configuration.GetSection(BootstrapOptions.SectionName).Get<BootstrapOptions>() ?? new BootstrapOptions();
var bootstrapOptions = new BootstrapOptions
{
    StateDirectory = bootstrapSettings.StateDirectory,
    DataDirectory = bootstrapSettings.DataDirectory,
    SetupCode = bootstrapSettings.SetupCode,
    Unattended = bootstrapSettings.Unattended,
    Interactive = new BootstrapInteractiveOptions
    {
        OrganizationName = bootstrapSettings.Interactive.OrganizationName ?? builder.Configuration["Branding:OrganizationName"],
        ApplicationName = bootstrapSettings.Interactive.ApplicationName ?? builder.Configuration["Branding:ApplicationName"],
        ApplicationUrl = bootstrapSettings.Interactive.ApplicationUrl ?? builder.Configuration["Branding:ApplicationUrl"],
        TimeZoneId = bootstrapSettings.Interactive.TimeZoneId
    }
};
var bootstrapStateStore = new FileBootstrapStateStore(bootstrapOptions);
if (operatorCommand)
{
    var exitCode = await BootstrapOperatorCommand.ExecuteAsync(
        args, builder.Configuration, bootstrapOptions, Console.Out, Console.Error);
    if (exitCode.HasValue)
    {
        Environment.ExitCode = exitCode.Value;
        return;
    }
}
BootstrapDescriptor? bootstrapDescriptor = null;
if (!skipDatabaseStartup)
{
    var startupKeyPath = builder.Configuration["DataProtection:KeyRingPath"] ?? Path.Combine(bootstrapOptions.StateDirectory, "keys");
    var startupProtection = DataProtectionProvider.Create(new DirectoryInfo(startupKeyPath),
        options => options.SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "Helpdesk-Keyring"));
    bootstrapDescriptor = await new BootstrapStartupService(bootstrapStateStore, bootstrapOptions, startupProtection)
        .ResolveAsync(builder.Configuration);
}

if (args is ["--rotate-setup-code"])
{
    if (bootstrapDescriptor is null)
    {
        await Console.Error.WriteLineAsync("No bootstrap-managed setup state was found for the selected configuration.");
        Environment.ExitCode = 1;
        return;
    }

    await using var rotationLease = await BootstrapOperationLease.AcquireAsync(bootstrapOptions.StateDirectory, CancellationToken.None);
    var currentDescriptor = await bootstrapStateStore.LoadOrCreateAsync();
    var rotationKeyPath = builder.Configuration["DataProtection:KeyRingPath"] ?? Path.Combine(bootstrapOptions.StateDirectory, "keys");
    var rotationProtection = DataProtectionProvider.Create(new DirectoryInfo(rotationKeyPath),
        options => options.SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "Helpdesk-Keyring"));
    await BootstrapStartupService.ReconcileSelectedMarkerAsync(
        bootstrapStateStore, currentDescriptor, rotationProtection, CancellationToken.None);
    var setupCode = await bootstrapStateStore.RotateSetupCodeAsync();
    if (string.IsNullOrWhiteSpace(setupCode))
    {
        await Console.Error.WriteLineAsync("The setup code cannot be rotated after setup is complete or while recovery is required.");
        Environment.ExitCode = 1;
        return;
    }

    await Console.Out.WriteLineAsync(setupCode);
    return;
}


if (bootstrapDescriptor is { State: BootstrapState.Ready, Provider: "Sqlite", SqlitePath: not null })
{
    var bootstrapKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"]
                              ?? Path.Combine(bootstrapOptions.StateDirectory, "keys");
    var bootstrapApplicationName = builder.Configuration["DataProtection:ApplicationName"] ?? "Helpdesk-Keyring";
    var runtimeSettings = new Dictionary<string, string?>
    {
        ["Database:Provider"] = "Sqlite",
        ["Database:Sqlite:Path"] = bootstrapDescriptor.SqlitePath,
        ["Database:Sqlite:CreateIfMissing"] = "false",
        ["Authentication:Mode"] = builder.Configuration["Authentication:Mode"] ?? (bootstrapDescriptor.AdoptedLegacy ? "Oidc" : "Local"),
        ["DataProtection:KeyRingPath"] = bootstrapKeyRingPath
    };
    if (string.IsNullOrWhiteSpace(builder.Configuration["StorageOptions:ImageSigningSecret"]))
    {
        runtimeSettings["StorageOptions:ImageSigningSecret"] = BootstrapRuntimeSecretStore.GetOrCreateImageSigningSecret(
            bootstrapOptions,
            bootstrapKeyRingPath,
            bootstrapApplicationName);
    }
    builder.Configuration.AddInMemoryCollection(runtimeSettings);
}
else if (bootstrapDescriptor is { State: BootstrapState.Ready, Provider: "PostgreSql", ProtectedPostgreSqlConnection: not null })
{
    var bootstrapKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"]
                              ?? Path.Combine(bootstrapOptions.StateDirectory, "keys");
    var bootstrapApplicationName = builder.Configuration["DataProtection:ApplicationName"] ?? "Helpdesk-Keyring";
    try
    {
        var provider = DataProtectionProvider.Create(
            new DirectoryInfo(bootstrapKeyRingPath),
            configuration => configuration.SetApplicationName(bootstrapApplicationName));
        var connectionString = provider
            .CreateProtector("RatelDesk.Bootstrap.PostgreSqlConnection.v1")
            .Unprotect(bootstrapDescriptor.ProtectedPostgreSqlConnection);
        var runtimeSettings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "PostgreSql",
            ["ConnectionStrings:HelpdeskDb"] = connectionString,
            ["Authentication:Mode"] = builder.Configuration["Authentication:Mode"] ?? (bootstrapDescriptor.AdoptedLegacy ? "Oidc" : "Local"),
            ["DataProtection:KeyRingPath"] = bootstrapKeyRingPath
        };
        if (string.IsNullOrWhiteSpace(builder.Configuration["StorageOptions:ImageSigningSecret"]))
        {
            runtimeSettings["StorageOptions:ImageSigningSecret"] = BootstrapRuntimeSecretStore.GetOrCreateImageSigningSecret(
                bootstrapOptions,
                bootstrapKeyRingPath,
                bootstrapApplicationName);
        }
        builder.Configuration.AddInMemoryCollection(runtimeSettings);
    }
    catch (Exception exception) when (exception is CryptographicException or IOException)
    {
        throw new InvalidOperationException("The PostgreSQL bootstrap descriptor cannot be recovered. Operator recovery is required.", exception);
    }
}

var localAuthenticationOptions = builder.Configuration.GetSection(LocalAuthenticationOptions.SectionName).Get<LocalAuthenticationOptions>() ?? new LocalAuthenticationOptions();
var localAuthenticationCookieName = localAuthenticationOptions.AllowInsecureLocalhost
    ? "RatelDesk.Local"
    : "__Host-RatelDesk.Local";

if (args is ["--initialize-unattended"])
{
    if (bootstrapDescriptor is null)
    {
        await Console.Error.WriteLineAsync("Unattended initialization is available only for a bootstrap-managed, unconfigured instance.");
        return;
    }

    var bootstrapKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"]
                              ?? Path.Combine(bootstrapOptions.StateDirectory, "keys");
    var bootstrapApplicationName = builder.Configuration["DataProtection:ApplicationName"] ?? "Helpdesk-Keyring";
    var dataProtection = DataProtectionProvider.Create(
        new DirectoryInfo(bootstrapKeyRingPath),
        configuration => configuration.SetApplicationName(bootstrapApplicationName));
    var command = new UnattendedBootstrapCommand(
        bootstrapStateStore,
        bootstrapOptions,
        dataProtection,
        new PostgreSqlSetupPreflightService());
    var result = await command.InitializeAsync(bootstrapDescriptor, CancellationToken.None);
    if (!result.Succeeded)
    {
        await Console.Error.WriteLineAsync(result.Error ?? "Unattended initialization could not be completed.");
        return;
    }

    await Console.Out.WriteLineAsync("RatelDesk initialization completed. Start the API normally to serve the application.");
    return;
}

if (args is ["--recover-local-admin", var recoveryEmail])
{
    var recoveryToken = await LocalAdminRecoveryCommand.GenerateActivationTokenAsync(builder.Configuration, recoveryEmail);
    if (string.IsNullOrWhiteSpace(recoveryToken))
    {
        await Console.Error.WriteLineAsync("No matching local instance administrator was found, or the selected identity store is unavailable.");
        return;
    }

    await Console.Out.WriteLineAsync(recoveryToken);
    return;
}


if (bootstrapDescriptor is not null && bootstrapDescriptor.State is not BootstrapState.Ready)
{
    var bootstrapKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"]
                              ?? Path.Combine(bootstrapOptions.StateDirectory, "keys");
    Directory.CreateDirectory(bootstrapKeyRingPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(bootstrapKeyRingPath))
        .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "Helpdesk-Keyring");
    builder.Services.AddSingleton(bootstrapOptions);
    builder.Services.AddSingleton<IBootstrapStateStore>(bootstrapStateStore);
    builder.Services.AddSingleton<BootstrapSessionService>();
    builder.Services.AddSingleton<BootstrapInitializationService>();
    builder.Services.AddSingleton<PostgreSqlSetupPreflightService>();
    builder.Services.AddHostedService<BootstrapRuntimeTransitionWatcher>();
    builder.Services.AddProblemDetails();
    builder.Services.AddRateLimiter(BootstrapEndpoints.ConfigureRateLimiting);

    var bootstrapApp = builder.Build();
    if (bootstrapDescriptor.State is BootstrapState.Unconfigured or BootstrapState.Configuring)
        bootstrapApp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RatelDesk.Setup")
            .LogInformation("RatelDesk setup is required. Open /setup and retrieve the setup code by running dotnet /app/Helpdesk.API.dll --show-setup-code in the API container. For non-secret diagnostics, run dotnet /app/Helpdesk.API.dll --setup-status. The setup code is not written to logs.");
    bootstrapApp.UseExceptionHandler();
    bootstrapApp.UseRateLimiter();
    bootstrapApp.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
    bootstrapApp.MapGet("/health/ready", () => bootstrapDescriptor.State == BootstrapState.RecoveryRequired
        ? Results.Json(new { status = "recovery-required" }, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Json(new { status = "awaiting-setup" })).AllowAnonymous();
    bootstrapApp.MapBootstrapEndpoints();
    await bootstrapApp.RunAsync();
    return;
}

builder.AddServiceDefaults();
builder.Services.AddSingleton(aiAgentOpsLogBuffer);
builder.Logging.AddProvider(new AiAgentOpsLoggerProvider(aiAgentOpsLogBuffer));
// TODO: GlobalTypeMapper is deprecated, switch to AddJsonOptions
NpgsqlConnection.GlobalTypeMapper.EnableDynamicJson(); // Opt in to Npgsql's dynamic JSON (https://www.npgsql.org/doc/types/json.html)
var runStartupTasks = Environment.GetEnvironmentVariable("RUN_MIGRATIONS") == "true";
var hangfireSettings = builder.Configuration.GetSection("Hangfire").Get<HangfireSettings>() ?? new HangfireSettings();
var hangfireConnectionString = builder.Configuration.GetConnectionString(hangfireSettings.ConnectionStringName);
var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var databaseProvider = databaseOptions.ResolveProvider(builder.Configuration.GetConnectionString("HelpdeskDb"));
var usePostgreSqlHangfire = !skipDatabaseStartup && databaseProvider is DatabaseProvider.PostgreSql;
var useSqliteHangfire = !skipDatabaseStartup && databaseProvider is DatabaseProvider.Sqlite;
var useHangfireRuntime = usePostgreSqlHangfire || useSqliteHangfire;
var sqliteHangfirePath = useSqliteHangfire
    ? Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(databaseOptions.Sqlite.Path))!,
        "rateldesk.hangfire.db")
    : null;
if (usePostgreSqlHangfire && string.IsNullOrWhiteSpace(hangfireConnectionString))
{
    throw new InvalidOperationException($"ConnectionStrings:{hangfireSettings.ConnectionStringName} is required.");
}

// DataProtection (shared ring for API + Web)
var dpSection = builder.Configuration.GetSection("DataProtection");
var keyRingPath = dpSection["KeyRingPath"]
                  ?? Path.Combine(Path.GetTempPath(), "rateldesk", "keys");

Directory.CreateDirectory(keyRingPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName(dpSection["ApplicationName"] ?? "Helpdesk-Keyring");
builder.Services.AddHelpdeskInfrastructure(builder.Configuration);
if (skipDatabaseStartup)
{
    var testDatabaseRoot = new InMemoryDatabaseRoot();
    var testDatabaseName = builder.Configuration["Helpdesk:TestDatabaseName"] ?? $"helpdesk-tests-{Guid.NewGuid():N}";
#pragma warning disable ASP0000
    var testDatabaseProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();
#pragma warning restore ASP0000

    builder.Services.RemoveAll<DbContextOptions<HelpdeskDbContext>>();
    builder.Services.AddDbContext<HelpdeskDbContext>(options =>
        options
            .UseInMemoryDatabase(testDatabaseName, testDatabaseRoot)
            .UseInternalServiceProvider(testDatabaseProvider)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
}
builder.Services.Configure<SlaEvaluationJobSettings>(options =>
{
    options.BatchSize = hangfireSettings.BatchSize;
});
builder.Services.AddSingleton(new HangfireRuntimeStatus(
    hangfireSettings.QueueName,
    "/hangfire",
    useHangfireRuntime ? "Ops UI / database" : "Disabled for test runtime",
    usePostgreSqlHangfire ? "Application PostgreSQL database" :
    useSqliteHangfire ? "Dedicated SQLite job database" : "No scheduler storage configured"));
if (useHangfireRuntime)
{
    builder.Services.AddHangfire(config =>
    {
        config
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings();
        if (usePostgreSqlHangfire)
        {
            config.UsePostgreSqlStorage(options => options.UseNpgsqlConnection(hangfireConnectionString!));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(sqliteHangfirePath!)!);
        config.UseSQLiteStorage(sqliteHangfirePath!);
    });
    builder.Services.AddHangfireServer(options =>
    {
        options.Queues = [hangfireSettings.QueueName];
    });
    builder.Services.AddScoped<IGraphDatasetSyncScheduleService, GraphDatasetSyncScheduleService>();
    builder.Services.AddScoped<IHangfireRuntimeAdminService, HangfireRuntimeAdminService>();
}
else
{
    builder.Services.AddScoped<IGraphDatasetSyncScheduleService, DisabledGraphDatasetSyncScheduleService>();
    builder.Services.AddScoped<IHangfireRuntimeAdminService, DisabledHangfireRuntimeAdminService>();
}
builder.Services.AddScoped<SlaEvaluationHangfireJob>();
builder.Services.AddScoped<SlaReportDispatchHangfireJob>();
builder.Services.AddScoped<TaskEscalationHangfireJob>();
builder.Services.AddScoped<RequestTaskRetryHangfireJob>();
builder.Services.AddScoped<RequestTaskApprovalTimeoutHangfireJob>();
builder.Services.AddScoped<GraphDatasetSyncHangfireJob>();
builder.Services.AddHealthChecks()
    .AddCheck<GraphEmailHealthCheck>("graph-email");
builder.Services.AddSingleton<IUserPresenceService, UserPresenceService>();
if (!skipDatabaseStartup)
{
    builder.Logging.Services.AddSingleton<NotificationLoggerProvider>(sp =>
        new NotificationLoggerProvider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            builder.Environment.EnvironmentName,
            builder.Environment.ApplicationName,
            typeof(Program).Assembly.GetName().Name ?? "Helpdesk.API"));
    builder.Logging.Services.AddSingleton<ILoggerProvider>(sp =>
        sp.GetRequiredService<NotificationLoggerProvider>());
}

// Background jobs
builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
if (!skipDatabaseStartup)
{
    builder.Services.AddHostedService<BackgroundJobRunner>();
    builder.Services.AddHostedService<Helpdesk.Infrastructure.Email.MailboxIngestionCoordinator>();
}
builder.Services.AddScoped<ISelfServiceAudienceService, SelfServiceAudienceService>();

builder.Services.AddSignalR();
builder.Services.AddRequestBus(typeof(Program).Assembly, typeof(CreateIncidentCommand).Assembly);
builder.Services.AddValidatorsFromAssemblyContaining<CreateServiceDtoValidator>();

builder.Services.AddSingleton<AppServices.Tickets.ITicketRefGeneratorService,
                              AppServices.Tickets.TicketRefGeneratorService>();
builder.Services.AddSingleton<SharedServices.IEmailBlacklistService,
                              SharedServices.InMemoryEmailBlacklistService>();

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<SharedServices.IErrorLogRepository,
    SharedServices.InMemoryErrorLogRepository>();

builder.Services.Configure<ImapEmailSettings>(_ => { });
builder.Services.Configure<EmailIngestionOptions>(
    builder.Configuration.GetSection("EmailIngestion"));
builder.Services.AddOptions<NotificationFeatureOptions>()
    .Bind(builder.Configuration.GetSection("Notifications"));

// Email Imap ingestion.
builder.Services.AddSingleton<AppServices.Email.IImapEmailService, AppServices.Email.ImapEmailService>();
if (!skipDatabaseStartup)
{
    // Canonical mailbox coordinator owns every inbound connection.
}


//builder.Services.AddSingleton<IEmailService, PostalEmailService>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("TicketSubmission", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ip,
            factory: partition => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
    options.AddPolicy("LocalLogin", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.OnRejected = (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            context.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return new ValueTask();
    };
});

// User OIDC
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "Bearer";               // the policy-scheme
    options.DefaultAuthenticateScheme = "Bearer";
    options.DefaultChallengeScheme = "Bearer";
})
.AddPolicyScheme("Bearer", "Bearer", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var configuredSystemIssuer = builder.Configuration["SystemToken:Issuer"]?.TrimEnd('/');
        var configuredAuthentikIssuer = builder.Configuration["Authentication:Authentik:Authority"]?.TrimEnd('/');
        var configuredAuthentikAiAgentIssuer = builder.Configuration["Authentication:AuthentikAiAgent:Authority"]?.TrimEnd('/');

        // If there is no bearer token, forward to a concrete scheme (NOT to "Bearer")
        var auth = context.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return localAuthenticationOptions.SupportsLocalAccounts && context.Request.Cookies.ContainsKey(localAuthenticationCookieName)
                ? LocalAuthenticationOptions.Scheme
                : "Azure";

        var token = auth.Substring("Bearer ".Length).Trim();
        if (token.StartsWith("rdk_", StringComparison.OrdinalIgnoreCase))
            return IntegrationCredentialAuthenticationHandler.SchemeName;
        if (token.StartsWith(McpExecutionTokenService.TokenPrefix, StringComparison.OrdinalIgnoreCase))
            return McpExecutionAuthenticationHandler.SchemeName;
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var iss = jwt.Issuer ?? string.Empty;
            var tokenUse = jwt.Claims.FirstOrDefault(c => c.Type == "token_use")?.Value;
            var authMode = jwt.Claims.FirstOrDefault(c => c.Type == "auth_mode")?.Value;

            var isSystemToken =
                string.Equals(tokenUse, "system", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(authMode, "development", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(configuredSystemIssuer)
                 && string.Equals(iss.TrimEnd('/'), configuredSystemIssuer, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(tokenUse, "system", StringComparison.OrdinalIgnoreCase));

            if (isSystemToken)
                return "System";

            if (!string.IsNullOrWhiteSpace(configuredAuthentikAiAgentIssuer)
                && string.Equals(iss.TrimEnd('/'), configuredAuthentikAiAgentIssuer, StringComparison.OrdinalIgnoreCase))
            {
                return "AuthentikAiAgent";
            }

            if (!string.IsNullOrWhiteSpace(configuredAuthentikIssuer)
                && string.Equals(iss.TrimEnd('/'), configuredAuthentikIssuer, StringComparison.OrdinalIgnoreCase))
            {
                return "Authentik";
            }

            if (iss.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
                iss.Contains("sts.windows.net", StringComparison.OrdinalIgnoreCase)) return "Azure";
        }
        catch { /* fall through */ }

        return "Azure";
    };
})
.AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
    IntegrationCredentialAuthenticationHandler.SchemeName,
    options => options.Purpose = IntegrationCredentialAuthenticationHandler.ApiPurpose)
.AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
    IntegrationCredentialAuthenticationHandler.McpSchemeName,
    options => options.Purpose = IntegrationCredentialAuthenticationHandler.McpPurpose)
.AddScheme<AuthenticationSchemeOptions, McpExecutionAuthenticationHandler>(
    McpExecutionAuthenticationHandler.SchemeName,
    _ => { })
.AddCookie(LocalAuthenticationOptions.Scheme, options =>
{
    options.Cookie.Name = localAuthenticationCookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = localAuthenticationOptions.AllowInsecureLocalhost
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = async context =>
    {
        if (!localAuthenticationOptions.SupportsLocalAccounts)
        {
            context.RejectPrincipal();
            return;
        }
        var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            context.RejectPrincipal();
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        var claimedRevision = context.Principal!.FindFirstValue("authorization_revision");
        var claimedStamp = context.Principal.FindFirstValue("security_stamp");
        var hasAdministratorClaim = context.Principal.IsInRole("HelpdeskAdmin");
        if (user is null || !user.IsEnabled ||
            !string.Equals(claimedRevision, user.AuthorizationRevision.ToString(global::System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !string.Equals(claimedStamp, user.SecurityStamp, StringComparison.Ordinal) ||
            hasAdministratorClaim != user.IsInstanceAdministrator)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(LocalAuthenticationOptions.Scheme);
        }
    };
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
})
.AddJwtBearer("Authentik", options =>
{
    var authority = builder.Configuration["Authentication:Authentik:Authority"];
    var audience = builder.Configuration["Authentication:Authentik:Audience"];
    if (!string.IsNullOrWhiteSpace(authority))
    {
        options.Authority = authority;
    }
    options.RequireHttpsMetadata = !string.IsNullOrWhiteSpace(authority)
        && authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    options.IncludeErrorDetails = builder.Environment.IsDevelopment();
    var validIssuers = string.IsNullOrWhiteSpace(authority)
        ? Array.Empty<string>()
        : new[] { authority.TrimEnd('/'), $"{authority.TrimEnd('/')}/" };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuers = validIssuers,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromMinutes(2),
        NameClaimType = "preferred_username",
        RoleClaimType = "roles"
    };
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            if (string.Equals(localAuthenticationOptions.Mode, "Local", StringComparison.OrdinalIgnoreCase))
                context.NoResult();
            return Task.CompletedTask;
        },
        OnTokenValidated = context =>
        {
            if (string.Equals(localAuthenticationOptions.Mode, "Local", StringComparison.OrdinalIgnoreCase))
            {
                context.Fail("External user authentication is disabled.");
                return Task.CompletedTask;
            }
            if (context.Principal?.Identity is not ClaimsIdentity identity)
            {
                return Task.CompletedTask;
            }

            var roleMappings = builder.Configuration.GetSection("Authentication:Authentik:RoleMappings")
                .Get<Dictionary<string, string>>() ?? new Dictionary<string, string>();
            var claimValues = context.Principal.FindAll("groups")
                .Concat(context.Principal.FindAll("roles"))
                .Select(c => c.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var value in claimValues)
            {
                if (roleMappings.TryGetValue(value, out var mappedRole) && !string.IsNullOrWhiteSpace(mappedRole))
                {
                    identity.AddClaim(new Claim("provider_role", mappedRole));
                }
            }

            return Task.CompletedTask;
        }
    };
})
.AddJwtBearer("Azure", options =>
{
    var az = builder.Configuration.GetSection("AzureAd");
    var tenantId = az["TenantId"];
    options.RequireHttpsMetadata = false;
    // Prefer naming these clearly:
    var clientId = az["ClientId"] ?? az["Audience"];     // GUID of your API app registration
    var appIdUri = az["AppIdUri"] ?? $"api://{clientId}"; // App ID URI (often api://<GUID>)

    options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

    // Accept both GUID and AppIdUri as valid audiences
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        // Accept both v2 and v1 Azure issuers for your tenant
        ValidIssuers = new[]
        {
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            $"https://sts.windows.net/{tenantId}/"
        },

        ValidateAudience = true,
        ValidAudiences = new[] { clientId!, appIdUri! },

        // Azure app roles are in the "roles" claim
        RoleClaimType = "roles",
        NameClaimType = "preferred_username",
        ClockSkew = TimeSpan.FromMinutes(10),
    };

    options.IncludeErrorDetails = builder.Environment.IsDevelopment();

    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (string.Equals(localAuthenticationOptions.Mode, "Local", StringComparison.OrdinalIgnoreCase))
                context.Fail("External user authentication is disabled.");
            return Task.CompletedTask;
        },
        OnMessageReceived = ctx =>
        {
            if (string.Equals(localAuthenticationOptions.Mode, "Local", StringComparison.OrdinalIgnoreCase))
            {
                ctx.NoResult();
                return Task.CompletedTask;
            }
            if (string.IsNullOrWhiteSpace(ctx.Token) &&
                ctx.Request.Path.StartsWithSegments("/api/v1/incidents", StringComparison.OrdinalIgnoreCase) &&
                ctx.Request.Path.Value?.Contains("/timeline/stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                var accessToken = ctx.Request.Query["access_token"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    ctx.Token = accessToken;
                }
            }

            return Task.CompletedTask;
        }
    };
})
.AddJwtBearer("AuthentikAiAgent", options =>
{
    var authority = builder.Configuration["Authentication:AuthentikAiAgent:Authority"];
    var audience = builder.Configuration["Authentication:AuthentikAiAgent:Audience"];
    if (!string.IsNullOrWhiteSpace(authority))
    {
        options.Authority = authority;
    }
    options.RequireHttpsMetadata = !string.IsNullOrWhiteSpace(authority)
        && authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    options.IncludeErrorDetails = builder.Environment.IsDevelopment();
    var validAuthentikIssuers = string.IsNullOrWhiteSpace(authority)
        ? Array.Empty<string>()
        : new[] { authority.TrimEnd('/'), $"{authority.TrimEnd('/')}/" };
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuers = validAuthentikIssuers,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromMinutes(2),
        NameClaimType = "preferred_username",
        RoleClaimType = ClaimTypes.Role
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is not ClaimsIdentity identity)
            {
                return Task.CompletedTask;
            }

            var requiredGroups = builder.Configuration.GetSection("Authentication:AuthentikAiAgent:RequiredGroups").Get<string[]>() ?? [];
            var actualGroups = context.Principal.FindAll("groups").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (requiredGroups.Length > 0 && requiredGroups.Any(group => !actualGroups.Contains(group)))
            {
                context.Fail("Authentik AI agent token is missing one or more required groups.");
                return Task.CompletedTask;
            }

            var sessionRoles = builder.Configuration.GetSection("Authentication:AuthentikAiAgent:SessionRoles").Get<string[]>() ?? [];
            foreach (var role in sessionRoles.Where(role => !string.IsNullOrWhiteSpace(role)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
                identity.AddClaim(new Claim("roles", role));
            }

            identity.AddClaim(new Claim("auth_mode", "ai_agent"));
            identity.AddClaim(new Claim("identity_provider", "authentik_ai_agent"));
            return Task.CompletedTask;
        }
    };
})
.AddJwtBearer("System", o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["SystemToken:Issuer"],
        ValidateAudience = true,
        ValidAudience = builder.Configuration["SystemToken:Audience"],
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = BuildSymmetricKey(systemTokenSecret!),
        NameClaimType = "preferred_username",
        RoleClaimType = ClaimTypes.Role
    };
    o.IncludeErrorDetails = builder.Environment.IsDevelopment();
    o.RequireHttpsMetadata = false;

    // Ensure the System scheme does NOT try to validate normal Bearer tokens.
    // Only authenticate when an explicit system token is provided.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            // 1) Prefer custom header `X-System-Token`
            var sys = ctx.Request.Headers["X-System-Token"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(sys))
            {
                ctx.Token = sys;
                return Task.CompletedTask;
            }

            // 2) Or allow `Authorization: System <token>`
            var auth = ctx.Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("System ", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Token = auth.Substring("System ".Length).Trim();
                return Task.CompletedTask;
            }

            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = auth.Substring("Bearer ".Length).Trim();
                try
                {
                    var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
                    var tokenUse = jwt.Claims.FirstOrDefault(c => c.Type == "token_use")?.Value;
                    var authMode = jwt.Claims.FirstOrDefault(c => c.Type == "auth_mode")?.Value;
                    if (string.Equals(tokenUse, "system", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(authMode, "development", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Token = token;
                        return Task.CompletedTask;
                    }
                }
                catch
                {
                    ctx.NoResult();
                    return Task.CompletedTask;
                }
            }

            // Otherwise, skip authentication for this scheme to avoid interfering with Azure bearer auth.
            ctx.NoResult();
            return Task.CompletedTask;
        }
    };
})
.AddJwtBearer("OrchestrationM2M", options =>
{
    var orchestrationAuthority = builder.Configuration["Orchestration:Provider:Authority"]
        ?? builder.Configuration["Orchestration:Provider:BaseUrl"];
    if (string.IsNullOrWhiteSpace(orchestrationAuthority) && builder.Environment.IsDevelopment())
    {
        orchestrationAuthority = "https://localhost:9222";
    }

    var orchestrationCallbackAudience = builder.Configuration["M2M:ClientId"];
    if (string.IsNullOrWhiteSpace(orchestrationCallbackAudience))
    {
        orchestrationCallbackAudience = "helpdesk.api";
    }

    options.Authority = orchestrationAuthority;
    options.Audience = orchestrationCallbackAudience;
    options.RequireHttpsMetadata = !string.IsNullOrWhiteSpace(orchestrationAuthority)
        && orchestrationAuthority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    options.IncludeErrorDetails = builder.Environment.IsDevelopment();
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = string.IsNullOrWhiteSpace(orchestrationAuthority) ? null : orchestrationAuthority.TrimEnd('/'),
        ValidateAudience = true,
        ValidAudience = orchestrationCallbackAudience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromMinutes(2),
        NameClaimType = "azp"
    };
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = async context =>
        {
            context.HttpContext.Items["OrchestrationM2MRejectedReason"] =
                OrchestrationCallbackEndpoints.ResolveRejectedReasonFromException(context.Exception);
            var domainEvents = context.HttpContext.RequestServices.GetService<IDomainEventPublisher>();
            var correlationContext = context.HttpContext.RequestServices.GetService<ICorrelationContext>();
            if (domainEvents is not null && correlationContext is not null)
            {
                var correlationId = correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
                await OrchestrationCallbackEndpoints.PublishRejectedAsync(
                    domainEvents,
                    OrchestrationCallbackEndpoints.ResolveRejectedReasonFromException(context.Exception),
                    null,
                    correlationId,
                    context.HttpContext.RequestAborted);
                context.HttpContext.Items["OrchestrationM2MRejectedPublished"] = true;
            }
        },
        OnChallenge = async context =>
        {
            if (context.HttpContext.Items.TryGetValue("OrchestrationM2MRejectedPublished", out var alreadyPublished)
                && alreadyPublished is true)
            {
                return;
            }
            var reason = context.HttpContext.Items["OrchestrationM2MRejectedReason"] as string
                         ?? "InvalidToken";
            var domainEvents = context.HttpContext.RequestServices.GetService<IDomainEventPublisher>();
            var correlationContext = context.HttpContext.RequestServices.GetService<ICorrelationContext>();
            if (domainEvents is not null && correlationContext is not null)
            {
                var correlationId = correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
                await OrchestrationCallbackEndpoints.PublishRejectedAsync(
                    domainEvents,
                    reason,
                    null,
                    correlationId,
                    context.HttpContext.RequestAborted);
            }
        }
    };
});
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy(McpGatewayDelegationEndpoints.DelegationPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.McpSchemeName);
        policy.RequireAuthenticatedUser();
    });

    opts.AddPolicy(IntegrationCredentialEndpoints.CredentialManagementPolicy, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new IntegrationCredentialManagementSessionRequirement());
    });
    opts.AddPolicy(IntegrationCredentialEndpoints.SelfRevocationPolicy, policy =>
    {
        policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
    });

    opts.AddPolicy("HelpdeskAdmin", p =>
    {
        // Do not pin schemes here so tests (and custom schemes) can satisfy the policy.
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
        {
            return ctx.User.Claims.Any(c =>
                (c.Type == "roles" || c.Type == System.Security.Claims.ClaimTypes.Role) &&
                string.Equals(c.Value, "HelpdeskAdmin", StringComparison.OrdinalIgnoreCase));
        });
    });

    opts.AddPolicy("HelpdeskStaff", p =>
    {
        // Do not pin schemes here so tests (and custom schemes) can satisfy the policy.
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
        {
            return ctx.User.Claims.Any(c =>
                (c.Type == "roles" || c.Type == System.Security.Claims.ClaimTypes.Role) &&
                (string.Equals(c.Value, "HelpdeskAdmin", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, "Technician", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, HelpdeskPermissions.IncidentManager, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, HelpdeskPermissions.RequestManager, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, HelpdeskPermissions.ChangeManager, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, HelpdeskPermissions.IncidentWrite, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, HelpdeskPermissions.RequestWrite, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, HelpdeskPermissions.ChangeWrite, StringComparison.OrdinalIgnoreCase)));
        });
    });

    opts.AddPolicy("DataManagementAccess", p =>
    {
        // Do not pin schemes here so tests (and custom schemes) can satisfy the policy.
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
        {
            return ctx.User.Claims.Any(c =>
                (c.Type == "roles" || c.Type == System.Security.Claims.ClaimTypes.Role) &&
                (string.Equals(c.Value, HelpdeskPermissions.HelpdeskAdmin, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Value, HelpdeskPermissions.DataManagementAdmin, StringComparison.OrdinalIgnoreCase)));
        });
    });

    opts.AddPolicy("AuthentikAiAgentApi", p =>
    {
        p.AddAuthenticationSchemes("AuthentikAiAgent");
        p.RequireAuthenticatedUser();
    });

    opts.AddPolicy("TenantPowerUser", p => p.RequireRole("PowerUser"));
    opts.AddPolicy("TenantUser", p => p.RequireRole("User"));
    opts.AddPolicy(HelpdeskPermissions.SelfServiceUser, p => p.RequireRole(HelpdeskPermissions.SelfServiceUser, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("IncidentAccess", p => p.RequireRole(HelpdeskPermissions.IncidentUser, HelpdeskPermissions.IncidentManager, HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentDelete, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("IncidentManager", p => p.RequireRole(HelpdeskPermissions.IncidentManager, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("RequestAccess", p => p.RequireRole(HelpdeskPermissions.RequestUser, HelpdeskPermissions.RequestManager, HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestDelete, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("RequestManager", p => p.RequireRole(HelpdeskPermissions.RequestManager, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("ChangeAccess", p => p.RequireRole(HelpdeskPermissions.ChangeUser, HelpdeskPermissions.ChangeManager, HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeDelete, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("ChangeManager", p => p.RequireRole(HelpdeskPermissions.ChangeManager, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("TicketReadAccess", p => p.RequireRole(
        HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentUser, HelpdeskPermissions.IncidentManager,
        HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestUser, HelpdeskPermissions.RequestManager,
        HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeUser, HelpdeskPermissions.ChangeManager,
        HelpdeskPermissions.SelfServiceUser, HelpdeskPermissions.HelpdeskAdmin));
    opts.AddPolicy("NotificationAccess", p => p.RequireAuthenticatedUser());

    opts.AddPolicy("SystemBlazorWeb", p =>
    {
        p.AddAuthenticationSchemes("System");
        p.RequireAuthenticatedUser();
        p.RequireRole("system.blazor-web");
    });

    opts.AddPolicy("OrchestrationM2MOnly", p =>
    {
        p.AddAuthenticationSchemes("OrchestrationM2M");
        p.RequireAuthenticatedUser();
    });

});

builder.Services.AddScoped<IIntegrationCredentialOwnerResolver, IntegrationCredentialOwnerResolver>();
builder.Services.AddScoped<IAuthorizationHandler, IntegrationCredentialManagementSessionHandler>();
builder.Services.AddSingleton<McpExecutionTokenService>();


builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Debug);
builder.Logging.AddFilter("Helpdesk", LogLevel.Warning);

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        RatelDeskOpenApiCatalog.TransformDocumentAsync(document, cancellationToken);
        document.Info.Version = BuildInfoProvider.FromAssembly(typeof(Program).Assembly, builder.Environment.EnvironmentName).Version;
        RatelDeskOpenApiCatalog.ConfigureSecuritySchemes(document, localAuthenticationCookieName);

        return Task.CompletedTask;
    });

    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        RatelDeskOpenApiCatalog.TransformOperationAsync(operation, context.Description, context.Document, context.ApplicationServices, cancellationToken);

        return Task.CompletedTask;
    });
});

var app = builder.Build();

if (!skipDatabaseStartup)
{
    using (var scope = app.Services.CreateScope())
    {
        var ctx = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var identityDb = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
        await ctx.Database.MigrateAsync();
        await identityDb.Database.MigrateAsync();
        await TicketCategorySeed.SeedAsync(ctx);
        await SlaPolicySeed.SeedAsync(ctx);
        await RoleDefinitionSeeder.EnsureBuiltInsAsync(ctx);

        await scope.ServiceProvider.GetRequiredService<Helpdesk.Infrastructure.Email.MailboxConfigurationMigration>()
            .RunAsync(CancellationToken.None);
    }
}
else
{
    app.Logger.LogInformation("Skipping database startup tasks because Helpdesk:SkipDatabaseStartup is enabled.");
}

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment() &&
    app.Configuration.GetValue<bool>("Helpdesk:E2eSeedData"))
{
    await SeedLocalE2eDataAsync(app);
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionNotificationMiddleware>();
// Legacy private attachments must only be served through parent-resource authorization.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/attachments", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});
app.UseStaticFiles();

app.MapOpenApi().AllowAnonymous();

// Health checks
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).WithTags("Health");
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).WithTags("Health");
if (app.Environment.IsDevelopment())
{
    IResult WriteDebugLog(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Helpdesk.Infrastructure.Services.GraphEmailService");
        logger.LogWarning("TEST WARNING FROM DEBUG ENDPOINT");
        return Results.Ok(new { ok = true });
    }

    app.MapGet("/api/v1/debug/log-test", ([FromServices] ILoggerFactory loggerFactory) => WriteDebugLog(loggerFactory))
        .WithTags("Ops")
        .WithSummary("Write test log entry");

    app.MapGet("/api/debug/log-test", ([FromServices] ILoggerFactory loggerFactory) => WriteDebugLog(loggerFactory))
        .WithTags("Ops")
        .WithSummary("Write test log entry");
}

app.UseAuthentication();
app.UseMiddleware<Helpdesk.API.Middleware.LocalCookieCsrfMiddleware>();
app.UseMiddleware<UserAccessClaimsMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

if (useHangfireRuntime)
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireDashboardAuthorizationFilter()]
    });
}

app.MapCurrentUserAccessEndpoint();
app.MapIntegrationCredentialEndpoints();
app.MapMcpGatewayDelegationEndpoints();
app.MapGet("/api/v1/setup/status", () => Results.Ok(new { state = "Ready" }))
    .AllowAnonymous()
    .WithTags("Setup");
app.MapInstanceBrandingEndpoints();
if (localAuthenticationOptions.SupportsLocalAccounts)
{
    app.MapLocalAuthenticationEndpoints();
}

app.MapUserEndpoints();
app.MapTenantAdministrationEndpoints();
app.MapCustomerAuthEndpoints();
app.MapPresenceEndpoints();
app.MapDashboardEndpoints();
app.MapTicketEndpoints();
app.MapTicketSlaEndpoints();
app.MapPublicTicketEndpoints();
app.MapTicketSubmissionEndpoints();
app.MapTicketingLookupEndpoints();
app.MapCaptchaEndpoints();
app.MapSystemTokenEndpoints();
app.MapGet("/api/v1/auth/ai-agent/status", (ClaimsPrincipal user) => Results.Ok(new
{
    authenticated = user.Identity?.IsAuthenticated == true,
    name = user.Identity?.Name,
    authMode = user.FindFirst("auth_mode")?.Value,
    identityProvider = user.FindFirst("identity_provider")?.Value,
    groups = user.FindAll("groups").Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
    roles = user.FindAll(ClaimTypes.Role).Concat(user.FindAll("roles")).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray()
})).RequireAuthorization("AuthentikAiAgentApi")
    .WithTags("Authentication");
app.MapAiAgentOpsEndpoints();
app.MapActivityEndpoints();
app.MapIncidentEndpoints();
app.MapEmailEndpoints();
app.MapEmailSettingsEndpoints();
app.MapInboundEmailRuleEndpoints();
app.MapEmailLayoutEndpoints();
app.MapEmailTemplateEndpoints();
app.MapRequestEndpoints();
app.MapRequestWorkflowTimelineEndpoints();
app.MapSelfServiceRequestEndpoints();
app.MapSelfServiceMyRequestsEndpoints();
app.MapDatasetEndpoints();
app.MapHangfireOpsEndpoints();
app.MapRequestTaskEndpoints();
app.MapRequestTaskApprovalEndpoints();
app.MapWorkflowOpsEndpoints();
app.MapChangeEndpoints();
app.MapTicketCategoryEndpoints();
app.MapServiceEndpoints();
app.MapSlaPolicyEndpoints();
app.MapWorkingCalendarEndpoints();
app.MapTenantSlaSettingsEndpoints();
app.MapSlaReportSubscriptionEndpoints();
app.MapAttachmentEndpoints();
app.MapErrorLoggingEndpoints();
app.MapWorkLogEndpoints();
app.MapOrganizationChangeParticipantEndpoints();
app.MapAdminTenantEndpoints();
app.MapTenantBrandingEndpoints();
app.MapExternalOrchestrationEndpoints();
app.MapExternalOrchestrationCallbackEndpoints();
app.MapAiAssistantAiAssistantEndpoints();
Helpdesk.API.Endpoints.AiAssistant.Chat.AiAssistantChatEndpoints.MapAiAssistantChatEndpoints(app);
app.MapNotificationEndpoints();
app.MapSupportGroupEndpoints();
app.MapSupportGroupMemberEndpoints();
app.MapOrganizationSupportCoverageEndpoints();
app.MapSupportNotificationSubscriptionEndpoints();
app.MapUserSupportNotificationPreferenceEndpoints();
app.MapTimelineEndpoints();
app.MapSlaReportEndpoints();
#if DEBUG
app.MapGet("/__debug/me", (HttpContext ctx) => new
{
    authScheme = ctx.User.Identities.Select(i => i.AuthenticationType).ToArray(),
    name = ctx.User.Identity?.Name,
    roles = ctx.User.Claims.Where(c => c.Type == "roles" || c.Type == ClaimTypes.Role).Select(c => c.Value).ToArray()
}).RequireAuthorization().WithTags("System");
#endif
app.MapGet("/health/db", async ([FromServices] HelpdeskDbContext db, CancellationToken token) =>
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", token);
            return Results.Ok();
        }
        catch
        {
            return Results.Problem("Database unavailable", statusCode: 503);
        }
    })
    .WithName("GetDatabaseHealth")
    .WithSummary("Database health check")
    .WithDescription("Returns 200 when the database is reachable.")
    .WithTags("Health");

app.MapGlobalSearchLookupEndpoints();
app.MapRoleDefinitionEndpoints();
MapCrudEndpoints<Organization>(app, "/api/v1/organizations");
MapCrudEndpoints<Customer>(app, "/api/v1/customers");
MapCrudEndpoints<Asset>(app, "/api/v1/assets");
MapCrudEndpoints<AutomationRule>(app, "/api/v1/automation/rules");

app.MapPost("/api/v1/ingestEmail", async (
    [FromBody] string rawEmail,
    [FromServices] SharedServices.IRepository<Incident> repo,
    [FromServices] Helpdesk.Application.Sla.ITicketSlaInitializer ticketSlaInitializer,
    [FromServices] AppServices.Tickets.ITicketRefGeneratorService refs,
    [FromServices] SharedServices.IRepository<BlockedEntity> blocked,
    [FromServices] SharedServices.IRepository<ActivityLog> logs) =>
{
    var sender = GetSenderEmail(rawEmail);
    if (sender is not null)
    {
        var domain = sender.Split('@').Last();
        if (await blocked.IsEmailBlockedAsync(sender) || await blocked.IsDomainBlockedAsync(domain))
        {
            await logs.CreateAsync(new ActivityLog
            {
                Timestamp = DateTime.UtcNow,
                UserId = string.Empty,
                Message = $"Rejected email from {sender}"
            });
            return Results.NoContent(); // 204
        }
    }

    var parts = rawEmail.Split('\n', 2);
    var title = parts.Length > 0 ? parts[0].Trim() : string.Empty;
    var description = parts.Length > 1 ? parts[1].Trim() : string.Empty;

    if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(description))
    {
        return Results.Problem("Failed to convert email", statusCode: 400);
    }

    var ticket = new Incident { Title = title, Description = description };

    ticket.Id = await refs.NextReferenceAsync("INC");
    await repo.CreateAsync(ticket);
    try
    {
        await ticketSlaInitializer.InitializeAsync(ticket);
    }
    catch
    {
        // Email ingestion should not fail if SLA initialization fails.
    }

    return Results.Created($"/api/v1/incidents/{ticket.Id}", ticket); // 201
}).RequireAuthorization("HelpdeskAdmin").WithTags("Email");

app.MapHub<NotificationHub>("/notification-hub")
    .RequireAuthorization("NotificationAccess");

if (app.Environment.IsDevelopment())
{
    app.MapGet("/_diag/dp", (IDataProtectionProvider p) =>
    {
        var prot = p.CreateProtector("DevelopmentDiagnostics");
        var s = prot.Protect("ok");
        var u = prot.Unprotect(s);
        return Results.Ok(new { protectedLength = s.Length, unprotected = u });
    }).WithTags("System");
}

if (app.Environment.IsDevelopment() && runStartupTasks)
{
    await SeedAdminUserAsync(app);
}

if (!skipDatabaseStartup)
{
    await SeedInboundEmailRulesAsync(app);
    await SeedEmailLayoutsAsync(app);
    await SeedEmailTemplatesAsync(app);
}

if (useHangfireRuntime)
{
    var recurringJobManager = app.Services.GetRequiredService<IRecurringJobManager>();
    if (hangfireSettings.SlaEvaluationEnabled)
    {
        recurringJobManager.AddOrUpdate<SlaEvaluationHangfireJob>(
            recurringJobId: "sla-evaluation",
            queue: hangfireSettings.QueueName,
            methodCall: job => job.RunAsync(CancellationToken.None),
            cronExpression: hangfireSettings.SlaEvaluationCron,
            options: new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });
    }
    recurringJobManager.AddOrUpdate<SlaReportDispatchHangfireJob>(
        recurringJobId: "sla-report-dispatch",
        queue: hangfireSettings.QueueName,
        methodCall: job => job.RunAsync(CancellationToken.None),
        cronExpression: "*/15 * * * *",
        options: new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });
    recurringJobManager.AddOrUpdate<TaskEscalationHangfireJob>(
        recurringJobId: "task-sla-escalation",
        queue: hangfireSettings.QueueName,
        methodCall: job => job.RunAsync(CancellationToken.None),
        cronExpression: hangfireSettings.TaskEscalationCron,
        options: new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });
    recurringJobManager.AddOrUpdate<RequestTaskRetryHangfireJob>(
        recurringJobId: "request-task-retry",
        queue: hangfireSettings.QueueName,
        methodCall: job => job.RunAsync(CancellationToken.None),
        cronExpression: hangfireSettings.TaskRetryCron,
        options: new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });
    recurringJobManager.AddOrUpdate<RequestTaskApprovalTimeoutHangfireJob>(
        recurringJobId: "request-approval-timeouts",
        queue: hangfireSettings.QueueName,
        methodCall: job => job.RunAsync(CancellationToken.None),
        cronExpression: hangfireSettings.ApprovalTimeoutCron,
        options: new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    using var hangfireScope = app.Services.CreateScope();
    await hangfireScope.ServiceProvider.GetRequiredService<IGraphDatasetSyncScheduleService>().ReconcileAllAsync();
}
else
{
    app.Logger.LogInformation("Skipping Hangfire startup because Helpdesk:SkipDatabaseStartup is enabled.");
}

/* claim debug
app.Use(async (ctx, next) =>
{
    if (ctx.User?.Identity?.IsAuthenticated == true)
    {
        var roles = ctx.User.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == "roles").Select(c => c.Value);
        var scopes = ctx.User.Claims.Where(c => c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope")
                                    .Select(c => c.Value);
        Console.WriteLine($"User={ctx.User.Identity.Name} roles=[{string.Join(",", roles)}] scopes=[{string.Join(",", scopes)}]");
    }
    await next();
});
*/

app.Run();

static SymmetricSecurityKey BuildSymmetricKey(string secret)
{
    // If you store hex/base64, decode; otherwise fall back to UTF8.
    try { return new SymmetricSecurityKey(Convert.FromHexString(secret)); } catch { }
    try { return new SymmetricSecurityKey(Convert.FromBase64String(secret)); } catch { }
    return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
}

static void MapCrudEndpoints<T>(WebApplication app, string route) where T : class
{
    var group = app.MapGroup(route)
        .WithTags(typeof(T).Name + "s")
        .RequireAuthorization("HelpdeskAdmin");
    group.MapGet("/", async ([FromServices] SharedServices.IRepository<T> repo) => await repo.GetAllAsync());
    group.MapGet("/{id}", async ([FromRoute] string id, [FromServices] SharedServices.IRepository<T> repo) =>
        await repo.GetAsync(id) is T entity ? Results.Ok(entity) : Results.Problem("Resource not found", statusCode: 404));
    group.MapPost("/", async ([FromBody] T entity, [FromServices] SharedServices.IRepository<T> repo, IServiceProvider services) =>
    {
        var idProperty = typeof(T).GetProperty("Id");
        if (idProperty is null)
        {
            return Results.Problem("Entity is missing Id property", statusCode: 400);
        }

        var current = idProperty.GetValue(entity);
        if (current is not string id || string.IsNullOrWhiteSpace(id))
        {
            id = Uuid.CreateVersion7().ToString();
            idProperty.SetValue(entity, id);
        }

        var created = await repo.CreateAsync(entity);

        if (idProperty.GetValue(created) is string idValue)
        {
            return Results.Created($"{route}/{idValue}", created);
        }

        return Results.Problem("ID assignment failed", statusCode: 400);
    });

    group.MapPut("/{id}", async ([FromRoute] string id, [FromBody] T entity, [FromServices] SharedServices.IRepository<T> repo, [FromServices] HelpdeskDbContext db) =>
    {
        var idProperty = typeof(T).GetProperty("Id");
        if (idProperty is null)
        {
            return Results.Problem("Invalid entity", statusCode: 400);
        }

        if (entity is Customer requestedCustomer)
        {
            var existingCustomer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(customer => customer.Id == id);
            if (existingCustomer is not null &&
                !string.Equals(existingCustomer.OrganizationId, requestedCustomer.OrganizationId, StringComparison.OrdinalIgnoreCase) &&
                await db.CustomerAuthLinks.AsNoTracking().AnyAsync(link => link.CustomerId == id))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["organizationId"] = ["Customers with a linked login cannot be moved here. Use an explicit access move workflow so existing organization access is not stranded."]
                });
            }
        }

        idProperty.SetValue(entity, id);
        return await repo.UpdateAsync(entity) is T updated
            ? Results.Ok(updated)
            : Results.Problem("Resource not found", statusCode: 404);
    });
    group.MapDelete("/{id}", async ([FromRoute] string id, [FromServices] SharedServices.IRepository<T> repo) =>
        await repo.DeleteAsync(id)
            ? Results.NoContent()
            : Results.Problem("Resource not found", statusCode: 404));
}

static async Task SeedAdminUserAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<SharedServices.IRepository<User>>();
    var roles = scope.ServiceProvider.GetRequiredService<SharedServices.IRepository<Role>>();
    var orgs = scope.ServiceProvider.GetRequiredService<SharedServices.IRepository<Organization>>();

    // --- START OF FIX ---
    // Check for and create the 'HelpdeskAdmin' role to match the policy and the user.
    var allRoles = await roles.GetAllAsync();
    if (!allRoles.Any(r => r.Name == "HelpdeskAdmin"))
    {
        await roles.CreateAsync(new Role { Name = "HelpdeskAdmin" });
    }
    // --- END OF FIX ---

    var allOrgs = await orgs.GetAllAsync();
    var devOrg = allOrgs.FirstOrDefault(o => o.Name == "DevOrg");
    if (devOrg is null)
    {
        devOrg = new Organization { Id = Uuid.CreateVersion7().ToString(), Name = "DevOrg", State = Helpdesk.Shared.Models.EntityState.Enabled };
        await orgs.CreateAsync(devOrg);
    }

    var allUsers = await users.GetAllAsync();
    if (!allUsers.Any(u => u.Email == "admin@test.io"))
    {
        var admin = new User
        {
            Id = Uuid.CreateVersion7().ToString(),
            Name = "Admin",
            Email = "admin@test.io",
            Role = "HelpdeskAdmin", // This is correct!
            OrganizationId = devOrg.Id,
            HashedPassword = BCrypt.Net.BCrypt.HashPassword("admin")
        };
        await users.CreateAsync(admin);
    }
}

static async Task SeedInboundEmailRulesAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
    const string defaultRuleName = "Forwarded customer email from support user";
    if (await db.InboundEmailRules.AnyAsync(x => x.ScopeType == InboundEmailRuleScopeType.Global && x.Name == defaultRuleName))
    {
        return;
    }

    var now = DateTimeOffset.UtcNow;
    db.InboundEmailRules.Add(new InboundEmailRule
    {
        ScopeType = InboundEmailRuleScopeType.Global,
        Name = defaultRuleName,
        Description = "Disabled template for creating a new incident for the original requester when an authorized support user forwards a customer email.",
        Enabled = false,
        Priority = 100,
        StopProcessing = true,
        ConditionsJson = """
            [{"type":0},{"type":1},{"type":2},{"type":3},{"type":5}]
            """,
        ActionsJson = """
            [{"type":0,"actionKey":"CreateIncidentForOriginalForwardedSender"}]
            """,
        CreatedBy = "system",
        UpdatedBy = "system",
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    });
    await db.SaveChangesAsync();
}

static async Task SeedLocalE2eDataAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();

    var now = DateTime.UtcNow;
    if (!await db.Incidents.AnyAsync())
    {
        db.Incidents.Add(new Incident
        {
            Id = "e2e-incident-1",
            TrackingId = "INC-E2E-001",
            Title = "Local UX validation incident",
            Description = "Deterministic local-only data used by the Playwright validation harness.",
            Priority = TicketPriority.Medium,
            State = TicketState.New,
            CreatedAt = now,
            UpdatedAt = now,
            RequesterEmail = "e2e@example.test"
        });
    }

    if (!await db.Services.AnyAsync())
    {
        db.Services.AddRange(
            new Service { Id = "e2e-service-access", Name = "Account access", Description = "Request account or access assistance." },
            new Service { Id = "e2e-service-device", Name = "Device support", Description = "Request help with a managed device." },
            new Service { Id = "e2e-service-network", Name = "Network assistance", Description = "Request help with network connectivity." },
            new Service { Id = "e2e-service-software", Name = "Software support", Description = "Request help with approved software." });
    }

    await db.SaveChangesAsync();
}

static async Task SeedEmailTemplatesAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var templateRepo = scope.ServiceProvider.GetRequiredService<SharedServices.IRepository<EmailTemplate>>();
    var layoutRepo = scope.ServiceProvider.GetRequiredService<SharedServices.IRepository<EmailLayout>>();
    var sanitizer = scope.ServiceProvider.GetRequiredService<IHtmlSanitizerService>();
    var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
    var defaultLayout = (await layoutRepo.GetAllAsync())
        .Where(x =>
            x.TenantId is null &&
            string.Equals(x.Name, EmailSeedDefaults.DefaultLayoutName, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(x => x.IsSystem)
        .FirstOrDefault();

    foreach (var templateName in EmailSeedDefaults.TemplateNames)
    {
        var existing = (await templateRepo.GetAllAsync()).FirstOrDefault(t => t.Name == templateName);
        var templatePath = Path.Combine(env.WebRootPath, "email-templates", $"{templateName}.html");
        if (!File.Exists(templatePath))
            continue;

        var content = sanitizer.Sanitize(await File.ReadAllTextAsync(templatePath));
        if (existing is null)
        {
            var newTemplate = new EmailTemplate
            {
                Name = templateName,
                Subject = EmailSeedDefaults.GenerateSubjectFromName(templateName),
                HtmlContent = content,
                LayoutId = defaultLayout?.Id,
                IsSystem = true,
                Version = EmailSeedDefaults.BrandedBaselineVersion
            };
            await templateRepo.CreateAsync(newTemplate);
        }
        else if (EmailSeedDefaults.ShouldUpgradeDefaultTemplate(existing.IsSystem, existing.Version, existing.HtmlContent))
        {
            existing.Subject = EmailSeedDefaults.GenerateSubjectFromName(templateName);
            existing.HtmlContent = content;
            existing.LayoutId = defaultLayout?.Id;
            existing.IsSystem = true;
            existing.Version = EmailSeedDefaults.BrandedBaselineVersion;
            existing.UpdatedUtc = DateTime.UtcNow;
            await templateRepo.UpdateAsync(existing);
        }
        else if (existing.IsSystem && existing.LayoutId is null && defaultLayout is not null)
        {
            existing.LayoutId = defaultLayout.Id;
            existing.UpdatedUtc = DateTime.UtcNow;
            await templateRepo.UpdateAsync(existing);
        }
    }
}

static async Task SeedEmailLayoutsAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var layoutRepo = scope.ServiceProvider.GetRequiredService<SharedServices.IRepository<EmailLayout>>();
    var sanitizer = scope.ServiceProvider.GetRequiredService<IHtmlSanitizerService>();

    var defaultLayout = sanitizer.Sanitize(EmailSeedDefaults.DefaultLayoutHtml);
    var existing = (await layoutRepo.GetAllAsync())
        .Where(x =>
            x.TenantId is null &&
            string.Equals(x.Name, EmailSeedDefaults.DefaultLayoutName, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(x => x.IsSystem)
        .FirstOrDefault();

    if (existing is null)
    {
        await layoutRepo.CreateAsync(new EmailLayout
        {
            Name = EmailSeedDefaults.DefaultLayoutName,
            HtmlContent = defaultLayout,
            IsSystem = true,
            TenantId = null,
            Version = EmailSeedDefaults.BrandedBaselineVersion
        });
        return;
    }

    if (!EmailSeedDefaults.ShouldUpgradeDefaultLayout(existing.IsSystem, existing.Version, existing.HtmlContent))
        return;

    existing.HtmlContent = defaultLayout;
    existing.IsSystem = true;
    existing.Version = EmailSeedDefaults.BrandedBaselineVersion;
    existing.UpdatedUtc = DateTime.UtcNow;
    await layoutRepo.UpdateAsync(existing);
}

static string? GetSenderEmail(string raw)
{
    foreach (var line in raw.Split('\n'))
    {
        if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
        {
            return line.Substring(5).Trim();
        }
    }
    return null;
}

public partial class Program { }
