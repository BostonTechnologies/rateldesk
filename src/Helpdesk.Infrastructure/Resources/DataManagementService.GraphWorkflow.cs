using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Identity;
using Helpdesk.Application.Resources;
using Helpdesk.Application.Services.AI;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Helpdesk.Infrastructure.Resources;

public sealed partial class DataManagementService
{
    internal const string GraphConnectionTestClientName = "GraphDatasetConnectionTest";
    private const int MaximumTestResponseBytes = 256 * 1024;
    private static readonly TimeSpan GraphTestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ImportRecoveryDelay = TimeSpan.FromMinutes(15);
    private sealed record TestedGraphConnection(string Fingerprint, DateTimeOffset ExpiresAtUtc);
    private sealed record DraftGraphConnection(string TenantId, string ClientId, string Secret, string Fingerprint);

    public async Task<GraphConnectionTestResultDto> TestGraphConnectionAsync(GraphConnectionTestRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        var result = new GraphConnectionTestResultDto { TestedAtUtc = DateTimeOffset.UtcNow };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GraphTestTimeout);
        try
        {
            var connection = await ResolveDraftGraphConnectionAsync(dto, timeout.Token);
            var accessToken = await GetGraphAccessTokenAsync(connection.TenantId, connection.ClientId, connection.Secret, timeout.Token);
            if (string.IsNullOrWhiteSpace(accessToken)) throw new AuthenticationFailedException("No access token was issued.");
            result.AuthenticationSucceeded = true;
            using var client = _httpClientFactory.CreateClient(GraphConnectionTestClientName);
            foreach (var source in SelectedGraphSources(dto))
                result.Sources.Add(await TestGraphSourceAsync(client, source, accessToken, timeout.Token));
            var passed = result.Sources.All(source => source.Succeeded);
            result.Outcome = passed ? GraphAccessOutcome.Succeeded : result.Sources.First(source => !source.Succeeded).Outcome;
            result.Message = passed ? "Authentication and read access passed for every selected dataset." : "Authentication passed. Correct the failed dataset checks or change the selection, then test again.";
            if (passed)
            {
                // Stateless evidence works across replicas sharing the existing key ring. It contains
                // only a connection fingerprint and expiry, never draft credentials or provider tokens.
                result.TestToken = _protector.Protect(JsonSerializer.Serialize(
                    new TestedGraphConnection(connection.Fingerprint, DateTimeOffset.UtcNow.AddMinutes(15)), JsonOptions));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Outcome = GraphAccessOutcome.TimedOut;
            result.Message = "The connection test timed out. Check the connection and try again.";
        }
        catch (SecretReentryRequiredException)
        {
            result.Outcome = GraphAccessOutcome.SecretReentryRequired;
            result.Message = "Re-enter the client secret; the stored secret cannot be decrypted with the current key ring.";
        }
        catch (ArgumentException)
        {
            result.Outcome = GraphAccessOutcome.InvalidConfiguration;
            result.Message = "Enter the tenant, application ID and a client secret, and select at least one supported dataset. Re-enter the secret when changing the tenant or application.";
        }
        catch (AuthenticationFailedException)
        {
            result.Outcome = GraphAccessOutcome.AuthenticationFailed;
            result.Message = "Authentication failed. Check the tenant, application ID and client secret.";
        }
        catch (HttpRequestException)
        {
            result.Outcome = GraphAccessOutcome.Unavailable;
            result.Message = "Microsoft Graph could not be reached. Keep this draft and try the test again.";
        }
        catch (TimeoutRejectedException)
        {
            result.Outcome = GraphAccessOutcome.TimedOut;
            result.Message = "The connection test timed out. Keep the draft and test again.";
        }
        catch (BrokenCircuitException)
        {
            result.Outcome = GraphAccessOutcome.Unavailable;
            result.Message = "The connection is temporarily unavailable. Keep the draft and test again.";
        }
        return result;
    }

    private async Task<DraftGraphConnection> ResolveDraftGraphConnectionAsync(GraphConnectionTestRequestDto dto, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(dto.OrganizationId) || string.IsNullOrWhiteSpace(dto.TenantId) ||
            string.IsNullOrWhiteSpace(dto.ClientId) || SelectedGraphSources(dto).Count == 0 ||
            dto.OrganizationId.Length > 256 || dto.TenantId.Length > 256 || dto.ClientId.Length > 256 || dto.ClientSecret?.Length > 8192)
            throw new ArgumentException("The Graph connection is incomplete.");
        var tenantId = dto.TenantId.Trim();
        var clientId = dto.ClientId.Trim();
        string secret;
        string revision;
        if (!string.IsNullOrWhiteSpace(dto.ClientSecret))
        {
            secret = dto.ClientSecret.Trim();
            revision = HashKey(secret);
        }
        else if (dto.UseStoredSecret)
        {
            var settings = await _db.TenantGraphDatasetSettings.AsNoTracking()
                .SingleOrDefaultAsync(value => value.OrganizationId == dto.OrganizationId, token);
            if (settings is null || string.IsNullOrWhiteSpace(settings.ClientSecretProtected) ||
                settings.TenantId != tenantId || settings.ClientId != clientId)
                throw new ArgumentException("Re-enter the secret for this connection.");
            secret = _protector.Unprotect(settings.ClientSecretProtected);
            revision = HashKey(settings.ClientSecretProtected);
        }
        else throw new ArgumentException("Enter the client secret.");
        var fingerprint = HashKey(JsonSerializer.Serialize(new
        {
            dto.OrganizationId, TenantId = tenantId, ClientId = clientId, Revision = revision,
            Sources = SelectedGraphSources(dto)
        }, JsonOptions));
        return new(tenantId, clientId, secret, fingerprint);
    }

    private static async Task<GraphDatasetAccessResultDto> TestGraphSourceAsync(HttpClient client,
        DatasetSourceType source, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GraphSourceUrl(source, sample: true));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return GraphAccessResult(source, OutcomeForStatus(response.StatusCode), (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumTestResponseBytes)
                return GraphAccessResult(source, GraphAccessOutcome.InvalidResponse);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (content.Length + read > MaximumTestResponseBytes)
                    return GraphAccessResult(source, GraphAccessOutcome.InvalidResponse);
                content.Write(buffer, 0, read);
            }
            content.Position = 0;
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("value", out var rows) || rows.ValueKind != JsonValueKind.Array)
                return GraphAccessResult(source, GraphAccessOutcome.InvalidResponse);
            // Deliberately do not follow @odata.nextLink: this is one bounded read, not ingestion.
            return GraphAccessResult(source, GraphAccessOutcome.Succeeded, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GraphAccessResult(source, GraphAccessOutcome.TimedOut);
        }
        catch (JsonException)
        {
            return GraphAccessResult(source, GraphAccessOutcome.InvalidResponse);
        }
        catch (HttpRequestException)
        {
            return GraphAccessResult(source, GraphAccessOutcome.Unavailable);
        }
        catch (TimeoutRejectedException)
        {
            return GraphAccessResult(source, GraphAccessOutcome.TimedOut);
        }
        catch (BrokenCircuitException)
        {
            return GraphAccessResult(source, GraphAccessOutcome.Unavailable);
        }
    }

    private static GraphAccessOutcome OutcomeForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => GraphAccessOutcome.AuthenticationFailed,
        HttpStatusCode.Forbidden => GraphAccessOutcome.AccessDenied,
        HttpStatusCode.TooManyRequests => GraphAccessOutcome.Throttled,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => GraphAccessOutcome.TimedOut,
        _ => GraphAccessOutcome.Unavailable
    };

    private static GraphDatasetAccessResultDto GraphAccessResult(DatasetSourceType source, GraphAccessOutcome outcome, int? status = null)
        => new()
        {
            SourceType = source, Succeeded = outcome == GraphAccessOutcome.Succeeded, Outcome = outcome, HttpStatusCode = status,
            Message = outcome switch
            {
                GraphAccessOutcome.Succeeded => "Read access passed (an empty source is valid).",
                GraphAccessOutcome.AccessDenied => "Read access was denied. Grant the application's required source permission and administrator consent, then test again.",
                GraphAccessOutcome.AuthenticationFailed => "The source rejected authentication. Check credentials and test again.",
                GraphAccessOutcome.Throttled => "Microsoft Graph is throttling this source. Wait and test again.",
                GraphAccessOutcome.TimedOut => "The source check timed out. Test again before importing.",
                GraphAccessOutcome.InvalidResponse => "The source returned an invalid or oversized sample response. Test again before importing.",
                _ => "The source could not be reached. Keep the draft and test again."
            }
        };

    public async Task<GraphDatasetImportStatusDto?> GetGraphImportStatusAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var latest = await _db.GraphDatasetImportRuns.AsNoTracking()
            .Where(value => value.OrganizationId == organizationId).OrderByDescending(value => value.StartedAtUtcTicks)
            .FirstOrDefaultAsync(cancellationToken);
        return latest is null ? null : MapGraphImportRun(latest);
    }

    public async Task<GraphDatasetImportStatusDto?> GetGraphImportRequestStatusAsync(string organizationId, Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var run = await _db.GraphDatasetImportRuns.AsNoTracking().SingleOrDefaultAsync(
            value => value.OrganizationId == organizationId && value.RequestId == requestId, cancellationToken);
        return run is null ? null : MapGraphImportRun(run);
    }

    public async Task<GraphDatasetImportStatusDto> ReconcileInterruptedGraphImportAsync(string organizationId,
        Guid requestId, CancellationToken cancellationToken = default)
    {
        var run = await _db.GraphDatasetImportRuns.SingleOrDefaultAsync(
            value => value.OrganizationId == organizationId && value.RequestId == requestId, cancellationToken)
            ?? throw new ArgumentException("The import receipt was not found.");
        if (run.ActiveOrganizationId is null) return MapGraphImportRun(run);
        if (DateTimeOffset.UtcNow < run.StartedAtUtc.Add(ImportRecoveryDelay))
            throw new DatasetWriteConflictException("The import may still be running. Reload recorded results after the recovery time; do not start a duplicate import.");
        var results = JsonSerializer.Deserialize<List<GraphDatasetImportSourceResultDto>>(run.SourceResultsJson, JsonOptions) ?? [];
        foreach (var source in results.Where(value => value.Status == "Running"))
        {
            var definition = await _db.DatasetDefinitions.AsNoTracking().FirstOrDefaultAsync(
                value => value.OrganizationId == organizationId && value.SourceType == source.SourceType, cancellationToken);
            source.DatasetId = definition?.Id;
            source.RowCount = definition is null ? null : await _db.DatasetRows.CountAsync(value => value.DatasetId == definition.Id, cancellationToken);
            source.Status = "Unknown";
            source.Message = "Completion was not recorded. Rows may have changed; inspect the current dataset before an explicit new import.";
        }
        var cutoff = DateTimeOffset.UtcNow.Subtract(ImportRecoveryDelay).UtcTicks;
        var changed = await _db.GraphDatasetImportRuns.Where(value => value.OrganizationId == organizationId &&
                value.RequestId == requestId && value.ActiveOrganizationId != null && value.StartedAtUtcTicks <= cutoff)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ActiveOrganizationId, (string?)null)
                .SetProperty(value => value.Status, "Interrupted")
                .SetProperty(value => value.CompletedAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(value => value.SourceResultsJson, JsonSerializer.Serialize(results, JsonOptions))
                .SetProperty(value => value.Message, "The interrupted import was reconciled without importing. Inspect recorded and current data, then test again before a new explicit import."), cancellationToken);
        if (changed == 0) throw new DatasetWriteConflictException("The import receipt changed. Reload its current recorded results.");
        _db.Entry(run).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        return MapGraphImportRun(await _db.GraphDatasetImportRuns.AsNoTracking().SingleAsync(
            value => value.OrganizationId == organizationId && value.RequestId == requestId, cancellationToken));
    }

    public async Task<GraphDatasetImportStatusDto> SaveAndImportGraphAsync(GraphDatasetImportRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        if (dto.RequestId == Guid.Empty) throw new ArgumentException("A stable import request identifier is required.");
        var organizationId = dto.Connection.OrganizationId;
        var existing = await _db.GraphDatasetImportRuns.AsNoTracking().SingleOrDefaultAsync(
            value => value.OrganizationId == organizationId && value.RequestId == dto.RequestId, cancellationToken);
        if (existing is not null) return MapGraphImportRun(existing);
        var connection = await ResolveDraftGraphConnectionAsync(dto.Connection, cancellationToken);
        if (!IsCurrentGraphTestReceipt(dto.TestToken, connection.Fingerprint))
            throw new ArgumentException("Test the current connection and every selected dataset again before importing.");
        var run = new GraphDatasetImportRun
        {
            OrganizationId = organizationId, RequestId = dto.RequestId, ActiveOrganizationId = organizationId,
            ConnectionFingerprint = connection.Fingerprint, Status = "Running", StartedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            SourceResultsJson = JsonSerializer.Serialize(SelectedGraphSources(dto.Connection)
                .Select(source => new GraphDatasetImportSourceResultDto { SourceType = source }), JsonOptions)
        };
        _db.GraphDatasetImportRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsDatasetWriteConflict(error))
        {
            _db.Entry(run).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            var duplicate = await _db.GraphDatasetImportRuns.AsNoTracking().SingleOrDefaultAsync(
                value => value.OrganizationId == organizationId && value.RequestId == dto.RequestId, cancellationToken);
            if (duplicate is not null) return MapGraphImportRun(duplicate);
            throw new DatasetWriteConflictException("An import is already running for this organization. Reload its recorded results before starting another import.");
        }
        // Once the durable run is claimed, dismissing/disconnecting the browser does not cancel server work.
        // The operation has its own bounded lifetime; callers reconcile this receipt instead of auto-retrying.
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var previous = await _db.TenantGraphDatasetSettings.AsNoTracking().SingleOrDefaultAsync(value => value.OrganizationId == organizationId, lifetime.Token);
            await UpsertGraphSettingsAsync(new TenantGraphDatasetSettingsDto
            {
                OrganizationId = organizationId, TenantId = connection.TenantId, ClientId = connection.ClientId,
                ClientSecret = dto.Connection.ClientSecret,
                EnableUsers = dto.Connection.EnableUsers, EnableDevices = dto.Connection.EnableDevices,
                EnableGroups = dto.Connection.EnableGroups, EnableSharePointSites = dto.Connection.EnableSharePointSites,
                BackgroundSyncEnabled = dto.PreserveExistingSchedule && previous?.BackgroundSyncEnabled == true,
                BackgroundSyncCronExpression = dto.PreserveExistingSchedule ? previous?.BackgroundSyncCronExpression ?? "0 */6 * * *" : "0 */6 * * *"
            }, lifetime.Token);
            run.ConfigurationSaved = true;
            await _db.SaveChangesAsync(lifetime.Token);
            return await ExecuteRecordedGraphImportAsync(run, connection, lifetime.Token);
        }
        catch (Exception error)
        {
            // A status write must never replay a definition, credential, row or settings
            // mutation left pending by the failure that brought us here.
            foreach (var entry in _db.ChangeTracker.Entries().Where(entry =>
                         entry.Entity is DatasetDefinition or DatasetColumn or DatasetRow or DatasetIngestCredential or TenantGraphDatasetSettings &&
                         entry.State is Microsoft.EntityFrameworkCore.EntityState.Added or Microsoft.EntityFrameworkCore.EntityState.Modified or Microsoft.EntityFrameworkCore.EntityState.Deleted).ToArray())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            if (!run.ConfigurationSaved)
            {
                var persisted = await _db.TenantGraphDatasetSettings.AsNoTracking().SingleOrDefaultAsync(
                    value => value.OrganizationId == run.OrganizationId, CancellationToken.None);
                run.ConfigurationSaved = persisted?.TenantId == connection.TenantId && persisted.ClientId == connection.ClientId &&
                    persisted.EnableUsers == dto.Connection.EnableUsers && persisted.EnableDevices == dto.Connection.EnableDevices &&
                    persisted.EnableGroups == dto.Connection.EnableGroups && persisted.EnableSharePointSites == dto.Connection.EnableSharePointSites &&
                    !string.IsNullOrWhiteSpace(persisted.ClientSecretProtected) && StoredGraphSecretMatches(persisted.ClientSecretProtected, connection.Secret);
            }
            // Never persist/return provider exception text: it can contain credentials or response bodies.
            run.Status = error is OperationCanceledException ? "Interrupted" : "Failed";
            run.Message = run.ConfigurationSaved
                ? "Configuration was saved. Import did not finish; inspect the recorded source results before an explicit retry."
                : "Configuration save was not confirmed. Keep the draft and reload the persisted connection and recorded results before testing and retrying.";
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.ActiveOrganizationId = null;
            var sources = JsonSerializer.Deserialize<List<GraphDatasetImportSourceResultDto>>(run.SourceResultsJson, JsonOptions) ?? [];
            foreach (var source in sources)
            {
                if (source.Status == "Running")
                {
                    source.Status = "Unknown";
                    source.Message = "This source did not record a confirmed completion. Inspect its current rows before an explicit new import.";
                    await PopulateAvailableGraphSourceAsync(source, run.OrganizationId, CancellationToken.None);
                }
                else if (source.Status == "Pending")
                {
                    source.Status = "NotStarted";
                    source.Message = "This source was not started because the import ended early.";
                }
            }
            run.SourceResultsJson = JsonSerializer.Serialize(sources, JsonOptions);
            await _db.SaveChangesAsync(CancellationToken.None);
            return MapGraphImportRun(run);
        }
    }

    private bool IsCurrentGraphTestReceipt(string token, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) return false;
        try
        {
            var plaintext = _protector.Unprotect(token);
            // The existing development secret protector permits legacy plaintext credentials;
            // an import receipt must always have passed authenticated unprotection.
            if (plaintext == token) return false;
            var evidence = JsonSerializer.Deserialize<TestedGraphConnection>(plaintext, JsonOptions);
            return evidence?.Fingerprint == fingerprint && evidence.ExpiresAtUtc > DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (error is SecretReentryRequiredException or CryptographicException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private bool StoredGraphSecretMatches(string protectedSecret, string expected)
    {
        try { return _protector.Unprotect(protectedSecret).Equals(expected, StringComparison.Ordinal); }
        catch (Exception error) when (error is SecretReentryRequiredException or CryptographicException)
        {
            return false;
        }
    }

    private async Task<GraphDatasetImportStatusDto> ExecuteRecordedGraphImportAsync(GraphDatasetImportRun run,
        DraftGraphConnection connection, CancellationToken cancellationToken)
    {
        var settings = await _db.TenantGraphDatasetSettings.SingleAsync(value => value.OrganizationId == run.OrganizationId, cancellationToken);
        var accessToken = await GetGraphAccessTokenAsync(connection.TenantId, connection.ClientId, connection.Secret, cancellationToken);
        var sources = JsonSerializer.Deserialize<List<GraphDatasetImportSourceResultDto>>(run.SourceResultsJson, JsonOptions)!;
        foreach (var source in sources)
        {
            source.Status = "Running";
            run.SourceResultsJson = JsonSerializer.Serialize(sources, JsonOptions);
            await _db.SaveChangesAsync(cancellationToken);
            try
            {
                var dataset = await SyncGraphDatasetAsync(run.OrganizationId, source.SourceType, GraphSourceName(source.SourceType),
                    GraphSourceColumns(source.SourceType), GraphSourceUrl(source.SourceType, sample: false), accessToken, settings,
                    value => SetSourceSyncStamp(value, source.SourceType), cancellationToken);
                source.DatasetId = dataset.Id;
                source.RowCount = await _db.DatasetRows.CountAsync(value => value.DatasetId == dataset.Id, cancellationToken);
                source.Status = "Succeeded";
                source.Message = "Import completed; records absent from the source were removed.";
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or DatasetWriteConflictException)
            {
                source.Status = "Failed";
                source.Message = error is HttpRequestException { StatusCode: { } status }
                    ? GraphAccessResult(source.SourceType, OutcomeForStatus(status)).Message
                    : "This source could not be imported. Review its connection and permissions, then explicitly retry after testing.";
                await PopulateAvailableGraphSourceAsync(source, run.OrganizationId, cancellationToken);
            }
            source.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.SourceResultsJson = JsonSerializer.Serialize(sources, JsonOptions);
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateGraphSettingsCache(run.OrganizationId);
        }
        var successes = sources.Count(value => value.Status == "Succeeded");
        run.Status = successes == sources.Count ? "Succeeded" : successes == 0 ? "Failed" : "PartiallySucceeded";
        run.Message = $"Imported {successes} of {sources.Count} selected datasets. Each source result records its committed outcome.";
        run.CompletedAtUtc = DateTimeOffset.UtcNow;
        run.ActiveOrganizationId = null;
        settings.LastSyncStatus = run.Status;
        settings.LastSyncMessage = run.Message;
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        InvalidateGraphSettingsCache(run.OrganizationId);
        return MapGraphImportRun(run);
    }

    private async Task PopulateAvailableGraphSourceAsync(GraphDatasetImportSourceResultDto source, string organizationId, CancellationToken token)
    {
        var definition = await _db.DatasetDefinitions.AsNoTracking().FirstOrDefaultAsync(
            value => value.OrganizationId == organizationId && value.SourceType == source.SourceType, token);
        source.DatasetId = definition?.Id;
        source.RowCount = definition is null ? null : await _db.DatasetRows.CountAsync(value => value.DatasetId == definition.Id, token);
    }

    private static GraphDatasetImportStatusDto MapGraphImportRun(GraphDatasetImportRun run)
    {
        var recoveryAt = run.ActiveOrganizationId is null ? (DateTimeOffset?)null : run.StartedAtUtc.Add(ImportRecoveryDelay);
        var canReconcile = recoveryAt <= DateTimeOffset.UtcNow;
        return new()
        {
            OrganizationId = run.OrganizationId, RequestId = run.RequestId, Status = canReconcile ? "Interrupted" : run.Status,
            ConfigurationSaved = run.ConfigurationSaved, StartedAtUtc = run.StartedAtUtc, CompletedAtUtc = run.CompletedAtUtc,
            Message = canReconcile ? "This import no longer has a confirmed completion. Reconcile it without importing before starting a new test/import." : run.Message,
            Sources = JsonSerializer.Deserialize<List<GraphDatasetImportSourceResultDto>>(run.SourceResultsJson, JsonOptions) ?? [],
            CanReconcile = canReconcile, RecoveryAvailableAtUtc = recoveryAt
        };
    }

    private static List<DatasetSourceType> SelectedGraphSources(GraphConnectionTestRequestDto dto)
    {
        var result = new List<DatasetSourceType>(4);
        if (dto.EnableUsers) result.Add(DatasetSourceType.GraphUsers);
        if (dto.EnableDevices) result.Add(DatasetSourceType.GraphDevices);
        if (dto.EnableGroups) result.Add(DatasetSourceType.GraphGroups);
        if (dto.EnableSharePointSites) result.Add(DatasetSourceType.GraphSharePointSites);
        return result;
    }

    private static string GraphSourceName(DatasetSourceType source) => source switch
    {
        DatasetSourceType.GraphUsers => "Users", DatasetSourceType.GraphDevices => "Computers",
        DatasetSourceType.GraphGroups => "Groups", DatasetSourceType.GraphSharePointSites => "SharePoint sites",
        _ => throw new ArgumentException("Unsupported Graph dataset.")
    };

    private static string GraphSourceUrl(DatasetSourceType source, bool sample) => source switch
    {
        DatasetSourceType.GraphUsers => sample ? "https://graph.microsoft.com/v1.0/users?$select=id&$top=1" : "https://graph.microsoft.com/v1.0/users?$select=id,displayName,mail,userPrincipalName",
        DatasetSourceType.GraphDevices => sample ? "https://graph.microsoft.com/v1.0/devices?$select=id&$top=1" : "https://graph.microsoft.com/v1.0/devices?$select=id,displayName,operatingSystem",
        DatasetSourceType.GraphGroups => sample ? "https://graph.microsoft.com/v1.0/groups?$select=id&$top=1" : "https://graph.microsoft.com/v1.0/groups?$select=id,displayName,mail",
        DatasetSourceType.GraphSharePointSites => sample ? "https://graph.microsoft.com/v1.0/sites?search=*&$select=id&$top=1" : "https://graph.microsoft.com/v1.0/sites?search=*",
        _ => throw new ArgumentException("Unsupported Graph dataset.")
    };

    private static IReadOnlyList<DatasetColumnDto> GraphSourceColumns(DatasetSourceType source)
    {
        var names = source switch
        {
            DatasetSourceType.GraphUsers => new[] { "id", "displayName", "mail", "userPrincipalName" },
            DatasetSourceType.GraphDevices => ["id", "displayName", "operatingSystem"],
            DatasetSourceType.GraphGroups => ["id", "displayName", "mail"],
            DatasetSourceType.GraphSharePointSites => ["id", "displayName", "webUrl"],
            _ => throw new ArgumentException("Unsupported Graph dataset.")
        };
        return names.Select((name, index) => new DatasetColumnDto
        {
            Name = name, DataType = DatasetColumnDataType.Text, IsKey = index == 0,
            IsDisplay = index == 1, IsSearchable = index != 0, SortOrder = index
        }).ToArray();
    }

    private static void SetSourceSyncStamp(TenantGraphDatasetSettings settings, DatasetSourceType source)
    {
        var now = DateTimeOffset.UtcNow;
        switch (source)
        {
            case DatasetSourceType.GraphUsers: settings.LastUsersSyncUtc = now; break;
            case DatasetSourceType.GraphDevices: settings.LastDevicesSyncUtc = now; break;
            case DatasetSourceType.GraphGroups: settings.LastGroupsSyncUtc = now; break;
            case DatasetSourceType.GraphSharePointSites: settings.LastSharePointSitesSyncUtc = now; break;
            default: throw new ArgumentException("Unsupported Graph dataset.");
        }
    }
}
