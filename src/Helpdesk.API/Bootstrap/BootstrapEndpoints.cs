using Helpdesk.API.Endpoints.PublicIngress;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Helpdesk.API.Bootstrap;

public static class BootstrapEndpoints
{
    public static void MapBootstrapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/setup/status", async (
            [FromServices] IBootstrapStateStore stateStore,
            [FromServices] BootstrapOptions options,
            [FromServices] IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var descriptor = await stateStore.LoadOrCreateAsync(cancellationToken);
            return Results.Ok(ToResponse(descriptor, options, IsStorageManaged(configuration)));
        })
        .AllowAnonymous()
        .WithTags("Setup");

        app.MapPost("/api/v1/setup/session", async (
            [FromBody] UnlockSetupRequest request,
            [FromServices] IBootstrapStateStore stateStore,
            [FromServices] BootstrapSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            var descriptor = await stateStore.LoadOrCreateAsync(cancellationToken);
            return sessions.TryCreate(descriptor, request.SetupCode, out var session)
                ? Results.Ok(new SetupSessionResponse(session!, DateTimeOffset.UtcNow.AddMinutes(10)))
                : Results.Unauthorized();
        })
        .AllowAnonymous()
        .RequirePublicAdmission(PublicOperation.Setup)
        .WithTags("Setup");

        app.MapPost("/api/v1/setup/storage", async (
            [FromHeader(Name = "X-RatelDesk-Setup-Session")] string? session,
            [FromBody] SelectStorageRequest request,
            [FromServices] IBootstrapStateStore stateStore,
            [FromServices] BootstrapSessionService sessions,
            [FromServices] BootstrapOptions options,
            [FromServices] PostgreSqlSetupPreflightService postgreSqlPreflight,
            [FromServices] IDataProtectionProvider dataProtection,
            [FromServices] IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            await using var lease = await BootstrapOperationLease.AcquireAsync(options.StateDirectory, cancellationToken);
            var currentDescriptor = await stateStore.LoadOrCreateAsync(cancellationToken);
            if (!sessions.IsValid(session, currentDescriptor))
            {
                return Results.Unauthorized();
            }

            var reconciled = await BootstrapStartupService.ReconcileSelectedMarkerAsync(
                stateStore, currentDescriptor, dataProtection, cancellationToken);
            if (reconciled is not null)
                return Results.Conflict(ToResponse(reconciled, options, IsStorageManaged(configuration)));

            if (IsStorageManaged(configuration))
            {
                // Connection details stay on the server; setup cannot override deployment-owned storage.
                return currentDescriptor.State == BootstrapState.Configuring
                    ? Results.Ok(ToResponse(currentDescriptor, options, true))
                    : Results.Conflict(ToResponse(currentDescriptor, options, true));
            }

            if (request.Provider is not ("Sqlite" or "PostgreSql"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["provider"] = ["Provider must be Sqlite or PostgreSql."]
                });
            }

            if (string.Equals(request.Provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
            {
                var connectionString = request.PostgreSqlConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(request.PostgreSqlHost))
                {
                    connectionString = new NpgsqlConnectionStringBuilder
                    {
                        Host = request.PostgreSqlHost.Trim(),
                        Port = request.PostgreSqlPort.GetValueOrDefault(5432),
                        Database = request.PostgreSqlDatabase?.Trim(),
                        Username = request.PostgreSqlUsername?.Trim(),
                        Password = request.PostgreSqlPassword,
                        SslMode = request.PostgreSqlUseTls ? SslMode.Require : SslMode.Prefer
                    }.ConnectionString;
                }

                var preflight = await postgreSqlPreflight.CheckAsync(connectionString, cancellationToken);
                if (!preflight.Succeeded)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["postgreSqlConnectionString"] = [preflight.Error!]
                    });
                }

                var protectedConnection = dataProtection
                    .CreateProtector("RatelDesk.Bootstrap.PostgreSqlConnection.v1")
                    .Protect(connectionString!);
                var postgreSqlDescriptor = await stateStore.UpdateAsync(current => current.State switch
                {
                    BootstrapState.Unconfigured or BootstrapState.Configuring => current with
                    {
                        State = BootstrapState.Configuring,
                        Provider = request.Provider,
                        SqlitePath = null,
                        ProtectedPostgreSqlConnection = protectedConnection,
                        OperationId = Guid.NewGuid()
                    },
                    _ => current
                }, cancellationToken);

                return postgreSqlDescriptor.State == BootstrapState.Configuring
                    ? Results.Ok(ToResponse(postgreSqlDescriptor, options))
                    : Results.Conflict(ToResponse(postgreSqlDescriptor, options));
            }

            var sqlitePath = Path.GetFullPath(string.IsNullOrWhiteSpace(request.SqlitePath)
                ? Path.Combine(options.DataDirectory, "rateldesk.db")
                : request.SqlitePath);
            var allowedDataDirectory = Path.GetFullPath(options.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!sqlitePath.StartsWith(allowedDataDirectory, StringComparison.Ordinal))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["sqlitePath"] = ["SQLite data must be stored under the configured bootstrap data directory."]
                });
            }

            var descriptor = await stateStore.UpdateAsync(current => current.State switch
            {
                BootstrapState.Unconfigured or BootstrapState.Configuring => current with
                {
                    State = BootstrapState.Configuring,
                    Provider = request.Provider,
                    SqlitePath = sqlitePath,
                    ProtectedPostgreSqlConnection = null,
                    OperationId = Guid.NewGuid()
                },
                _ => current
            }, cancellationToken);

            return descriptor.State == BootstrapState.Configuring
                ? Results.Ok(ToResponse(descriptor, options))
                : Results.Conflict(ToResponse(descriptor, options));
        })
        .AllowAnonymous()
        .WithTags("Setup");

        app.MapPost("/api/v1/setup/initialize", async (
            [FromHeader(Name = "X-RatelDesk-Setup-Session")] string? session,
            [FromBody] FirstAdministratorRequest request,
            [FromServices] IBootstrapStateStore stateStore,
            [FromServices] BootstrapSessionService sessions,
            [FromServices] BootstrapInitializationService initializer,
            [FromServices] IHostApplicationLifetime applicationLifetime,
            [FromServices] ILoggerFactory loggerFactory,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var descriptor = await stateStore.LoadOrCreateAsync(cancellationToken);
            if (!sessions.IsValid(session, descriptor))
            {
                return Results.Unauthorized();
            }

            var logger = loggerFactory.CreateLogger("RatelDesk.Setup");
            var traceId = context.TraceIdentifier;
            BootstrapInitializationResult result;
            try
            {
                result = await initializer.InitializeAsync(descriptor, request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Never log the submitted account, password, setup session, connection
                // string or raw provider message. SQLSTATE and exception type are safe diagnostics.
                var sqlState = (exception.GetBaseException() as PostgresException)?.SqlState;
                logger.LogError("Setup initialization failed. ExceptionType={ExceptionType} SqlState={SqlState} TraceId={TraceId}",
                    exception.GetType().Name, sqlState, traceId);
                return Results.Problem(
                    "Initialization did not finish. Check the API logs and setup status before retrying.",
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Setup initialization failed",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "setup_initialization_failed",
                        ["traceId"] = traceId
                    });
            }

            if (!result.Succeeded)
            {
                logger.LogWarning("Setup initialization rejected. Code={Code} Fields={Fields} TraceId={TraceId}",
                    result.Code, string.Join(",", result.Errors?.Keys ?? []), traceId);
                var extensions = new Dictionary<string, object?>
                {
                    ["code"] = result.Code,
                    ["traceId"] = traceId
                };
                if (result.Errors is { Count: > 0 })
                    return Results.ValidationProblem(
                        result.Errors.ToDictionary(pair => pair.Key, pair => pair.Value),
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Correct the indicated setup fields",
                        extensions: extensions);

                return Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict,
                    title: "Setup initialization could not complete", extensions: extensions);
            }

            _ = StopBootstrapHostAfterResponseAsync(applicationLifetime);
            return Results.Ok(ToResponse(result.Descriptor!));
        })
        .AllowAnonymous()
        .WithTags("Setup");
    }

    private sealed record UnlockSetupRequest(string? SetupCode);

    private sealed record SelectStorageRequest(
        string? Provider,
        string? SqlitePath,
        string? PostgreSqlConnectionString,
        string? PostgreSqlHost,
        int? PostgreSqlPort,
        string? PostgreSqlDatabase,
        string? PostgreSqlUsername,
        string? PostgreSqlPassword,
        bool PostgreSqlUseTls = true);

    private sealed record SetupSessionResponse(string Session, DateTimeOffset ExpiresAtUtc);

    private static BootstrapStatusResponse ToResponse(BootstrapDescriptor descriptor, BootstrapOptions? options = null, bool storageManaged = false) =>
        // This host only serves setup. The normal API reports Ready after its
        // services have started; a committed descriptor here means it must restart.
        new(descriptor.State == BootstrapState.Ready ? "Restarting" : descriptor.State.ToString(), descriptor.Provider, options is null ? null : new BootstrapInteractiveDefaultsResponse(
            TrimOrNull(options.Interactive.OrganizationName),
            TrimOrNull(options.Interactive.ApplicationName),
            TrimOrNull(options.Interactive.ApplicationUrl),
            TrimOrNull(options.Interactive.TimeZoneId)), storageManaged);

    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsStorageManaged(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString("HelpdeskDb")) ||
        !string.IsNullOrWhiteSpace(configuration["Database:Sqlite:Path"]);

    private sealed record BootstrapStatusResponse(string State, string? Provider, BootstrapInteractiveDefaultsResponse? Defaults, bool StorageManaged);

    private sealed record BootstrapInteractiveDefaultsResponse(
        string? OrganizationName,
        string? ApplicationName,
        string? ApplicationUrl,
        string? TimeZoneId);

    private static async Task StopBootstrapHostAfterResponseAsync(IHostApplicationLifetime applicationLifetime)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        applicationLifetime.StopApplication();
    }
}
