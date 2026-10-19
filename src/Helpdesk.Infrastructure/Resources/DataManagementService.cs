using System.Data;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Helpdesk.Application.Resources;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Application.Services.AI;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Helpdesk.Infrastructure.Resources;

public sealed class DataManagementService(
    Persistence.HelpdeskDbContext db,
    IMemoryCache cache,
    ISecretProtector protector,
    IHttpClientFactory httpClientFactory,
    ILogger<DataManagementService> logger) : IDataManagementService, IOperatorDatasetWriter
{
    private const string BoundLabelKey = "_boundLabels";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Persistence.HelpdeskDbContext _db = db;
    private readonly IMemoryCache _cache = cache;
    private readonly ISecretProtector _protector = protector;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<DataManagementService> _logger = logger;

    public async Task<IReadOnlyList<DatasetDefinitionDto>> ListDatasetsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var datasets = await _db.DatasetDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var columns = await _db.DatasetColumns
            .AsNoTracking()
            .Where(x => datasets.Select(d => d.Id).Contains(x.DatasetId))
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        return datasets
            .Select(dataset => MapDatasetDto(dataset, columns.Where(x => x.DatasetId == dataset.Id).ToList()))
            .ToList();
    }

    public async Task<DatasetDefinitionDto?> GetDatasetAsync(string datasetId, CancellationToken cancellationToken = default)
    {
        var definition = await GetDatasetDefinitionCachedAsync(datasetId, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        var columns = await GetDatasetColumnsCachedAsync(datasetId, cancellationToken);
        return MapDatasetDto(definition, columns);
    }

    public async Task<DatasetDefinitionDto> CreateDatasetAsync(CreateDatasetDefinitionDto dto, CancellationToken cancellationToken = default)
    {
        var normalizedColumns = NormalizeColumns(dto.Columns, dto.KeyColumn, dto.DisplayColumn, dto.SearchColumns);
        var definition = new DatasetDefinition
        {
            OrganizationId = dto.OrganizationId.Trim(),
            Name = dto.Name.Trim(),
            Slug = Slugify(dto.Name),
            SourceType = dto.SourceType,
            KeyColumn = ResolveKeyColumn(dto.KeyColumn, normalizedColumns),
            DisplayColumn = ResolveDisplayColumn(dto.DisplayColumn, normalizedColumns),
            SearchColumns = ResolveSearchColumns(dto.SearchColumns, normalizedColumns),
            IsBuiltIn = dto.SourceType != DatasetSourceType.Custom,
            IsActive = dto.IsActive,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        _db.DatasetDefinitions.Add(definition);
        foreach (var column in normalizedColumns)
        {
            _db.DatasetColumns.Add(new DatasetColumn
            {
                DatasetId = definition.Id,
                Name = column.Name.Trim(),
                DataType = column.DataType,
                IsKey = string.Equals(column.Name, definition.KeyColumn, StringComparison.OrdinalIgnoreCase),
                IsDisplay = string.Equals(column.Name, definition.DisplayColumn, StringComparison.OrdinalIgnoreCase),
                IsSearchable = definition.SearchColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase),
                SortOrder = column.SortOrder
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        InvalidateDatasetCache(definition.OrganizationId, definition.Id);
        return (await GetDatasetAsync(definition.Id, cancellationToken))!;
    }

    public async Task<DatasetDefinitionDto?> UpdateDatasetAsync(string datasetId, UpdateDatasetDefinitionDto dto, CancellationToken cancellationToken = default)
    {
        var definition = await _db.DatasetDefinitions.FirstOrDefaultAsync(x => x.Id == datasetId, cancellationToken);
        if (definition is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            definition.Name = dto.Name.Trim();
            definition.Slug = Slugify(dto.Name);
        }

        if (dto.SourceType.HasValue)
        {
            definition.SourceType = dto.SourceType.Value;
            definition.IsBuiltIn = definition.SourceType != DatasetSourceType.Custom;
        }

        List<DatasetColumnDto>? normalizedColumns = null;
        if (dto.Columns is not null)
        {
            normalizedColumns = NormalizeColumns(
                dto.Columns,
                dto.KeyColumn ?? definition.KeyColumn,
                dto.DisplayColumn ?? definition.DisplayColumn,
                dto.SearchColumns ?? definition.SearchColumns);

            var existingColumns = await _db.DatasetColumns.Where(x => x.DatasetId == datasetId).ToListAsync(cancellationToken);
            _db.DatasetColumns.RemoveRange(existingColumns);

            foreach (var column in normalizedColumns)
            {
                _db.DatasetColumns.Add(new DatasetColumn
                {
                    DatasetId = datasetId,
                    Name = column.Name.Trim(),
                    DataType = column.DataType,
                    IsKey = string.Equals(column.Name, dto.KeyColumn ?? definition.KeyColumn, StringComparison.OrdinalIgnoreCase),
                    IsDisplay = string.Equals(column.Name, dto.DisplayColumn ?? definition.DisplayColumn, StringComparison.OrdinalIgnoreCase),
                    IsSearchable = (dto.SearchColumns ?? definition.SearchColumns).Contains(column.Name, StringComparer.OrdinalIgnoreCase),
                    SortOrder = column.SortOrder
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(dto.KeyColumn))
        {
            definition.KeyColumn = dto.KeyColumn.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.DisplayColumn))
        {
            definition.DisplayColumn = dto.DisplayColumn.Trim();
        }

        if (dto.SearchColumns is not null)
        {
            definition.SearchColumns = dto.SearchColumns.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (dto.IsActive.HasValue)
        {
            definition.IsActive = dto.IsActive.Value;
        }

        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        InvalidateDatasetCache(definition.OrganizationId, definition.Id);
        return await GetDatasetAsync(datasetId, cancellationToken);
    }

    public async Task<bool> DeleteDatasetAsync(string datasetId, CancellationToken cancellationToken = default)
    {
        var definition = await _db.DatasetDefinitions.FirstOrDefaultAsync(x => x.Id == datasetId, cancellationToken);
        if (definition is null)
        {
            return false;
        }

        var columns = _db.DatasetColumns.Where(x => x.DatasetId == datasetId);
        var rows = _db.DatasetRows.Where(x => x.DatasetId == datasetId);
        var credentials = _db.DatasetIngestCredentials.Where(x => x.DatasetId == datasetId);
        _db.DatasetColumns.RemoveRange(columns);
        _db.DatasetRows.RemoveRange(rows);
        _db.DatasetIngestCredentials.RemoveRange(credentials);
        _db.DatasetDefinitions.Remove(definition);
        await _db.SaveChangesAsync(cancellationToken);
        InvalidateDatasetCache(definition.OrganizationId, datasetId);
        return true;
    }

    public async Task<PagedResult<DatasetRowDto>> GetRowsAsync(string datasetId, string? query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var normalizedQuery = query?.Trim().ToLowerInvariant();

        var rowsQuery = _db.DatasetRows
            .AsNoTracking()
            .Where(x => x.DatasetId == datasetId);

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            rowsQuery = rowsQuery.Where(x => x.SearchText.Contains(normalizedQuery));
        }

        var total = await rowsQuery.CountAsync(cancellationToken);
        var rows = await rowsQuery
            .OrderBy(x => x.ExternalKey)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<DatasetRowDto>
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            Items = rows.Select(MapRowDto).ToList()
        };
    }

    public async Task<PagedResult<DatasetOptionDto>> GetOptionsAsync(string datasetId, string organizationId, string? query, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var cacheKey = $"dataset-options:{organizationId}:{datasetId}:{page}:{pageSize}:{query?.Trim().ToLowerInvariant() ?? string.Empty}";
        if (_cache.TryGetValue(cacheKey, out PagedResult<DatasetOptionDto>? cached) && cached is not null)
        {
            return cached;
        }

        var definition = await GetDatasetDefinitionCachedAsync(datasetId, cancellationToken);
        if (definition is null || !string.Equals(definition.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase) || !definition.IsActive)
        {
            return new PagedResult<DatasetOptionDto> { Page = page, PageSize = pageSize, Total = 0 };
        }

        var normalizedQuery = query?.Trim().ToLowerInvariant();
        var rowsQuery = _db.DatasetRows
            .AsNoTracking()
            .Where(x => x.DatasetId == datasetId && x.OrganizationId == organizationId);

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            rowsQuery = rowsQuery.Where(x => x.SearchText.Contains(normalizedQuery));
        }

        var total = await rowsQuery.CountAsync(cancellationToken);
        var rows = await rowsQuery
            .OrderBy(x => x.ExternalKey)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var result = new PagedResult<DatasetOptionDto>
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            Items = rows.Select(x => MapOptionDto(x, definition)).ToList()
        };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
        return result;
    }

    public async Task<DatasetIngestCredentialCreatedDto> CreateCredentialAsync(string datasetId, CreateDatasetIngestCredentialDto dto, CancellationToken cancellationToken = default)
    {
        var dataset = await _db.DatasetDefinitions.FirstOrDefaultAsync(x => x.Id == datasetId, cancellationToken)
                      ?? throw new InvalidOperationException("Dataset not found.");
        var plainTextKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var credential = new DatasetIngestCredential
        {
            DatasetId = datasetId,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? $"Key {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}" : dto.Name.Trim(),
            KeyHash = HashKey(plainTextKey),
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        _db.DatasetIngestCredentials.Add(credential);
        await _db.SaveChangesAsync(cancellationToken);
        InvalidateDatasetCache(dataset.OrganizationId, datasetId);

        return new DatasetIngestCredentialCreatedDto
        {
            Credential = MapCredentialDto(credential),
            PlainTextKey = plainTextKey
        };
    }

    public async Task<bool> RevokeCredentialAsync(string datasetId, string credentialId, CancellationToken cancellationToken = default)
    {
        var credential = await _db.DatasetIngestCredentials
            .FirstOrDefaultAsync(x => x.DatasetId == datasetId && x.Id == credentialId, cancellationToken);
        if (credential is null)
        {
            return false;
        }

        credential.IsActive = false;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<DatasetIngestCredentialDto>> ListCredentialsAsync(string datasetId, CancellationToken cancellationToken = default)
    {
        return await _db.DatasetIngestCredentials
            .AsNoTracking()
            .Where(x => x.DatasetId == datasetId)
            .OrderByUtc(_db, x => x.CreatedAtUtc, descending: true)
            .Select(x => MapCredentialDto(x))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> UpsertRowsAsync(string datasetId, string apiKey, UpsertDatasetRowsDto dto, CancellationToken cancellationToken = default)
    {
        var dataset = await _db.DatasetDefinitions.FirstOrDefaultAsync(x => x.Id == datasetId, cancellationToken)
                      ?? throw new InvalidOperationException("Dataset not found.");

        var keyHash = HashKey(apiKey);
        var credential = await _db.DatasetIngestCredentials
            .FirstOrDefaultAsync(x => x.DatasetId == datasetId && x.IsActive && x.KeyHash == keyHash, cancellationToken);
        if (credential is null)
        {
            throw new UnauthorizedAccessException("Invalid dataset API key.");
        }

        credential.LastUsedAtUtc = DateTimeOffset.UtcNow;
        return await UpsertRowsInternalAsync(dataset, dto, cancellationToken);
    }

    public async Task<int> UpsertRowsAsOperatorAsync(string datasetId, string authorizedOrganizationId,
        bool allowBuiltInSources, UpsertDatasetRowsDto dto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var dataset = await _db.DatasetDefinitions.FirstOrDefaultAsync(
            x => x.Id == datasetId && x.OrganizationId == authorizedOrganizationId, cancellationToken);
        if (dataset is null || !allowBuiltInSources && dataset.SourceType != DatasetSourceType.Custom)
            throw new UnauthorizedAccessException("Dataset is outside the authorized operator scope.");

        if (dto.Rows is null || dto.Rows.Count > 100)
            throw new ArgumentException("Supply at most 100 rows in one operator update.", nameof(dto));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dto.Rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.ExternalKey) || row.ExternalKey.Length > 200 ||
                !keys.Add(row.ExternalKey.Trim()))
                throw new ArgumentException("Every row must have a distinct key of 1–200 characters.", nameof(dto));
            if (row.Data is null || row.Data.Count > 100 || row.Data.ToJsonString(JsonOptions).Length > 65536)
                throw new ArgumentException("Every row must contain a bounded JSON object with at most 100 fields and 65536 encoded characters.", nameof(dto));
        }
        if (JsonSerializer.Serialize(dto, JsonOptions).Length > 1048576)
            throw new ArgumentException("An operator row update may contain at most 1 MiB of JSON.", nameof(dto));

        return await UpsertRowsInternalAsync(dataset, dto, cancellationToken);
    }

    private async Task<int> UpsertRowsInternalAsync(DatasetDefinition dataset, UpsertDatasetRowsDto dto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Rows is null)
            throw new ArgumentException("Rows are required.", nameof(dto));
        var normalizedRows = new Dictionary<string, UpsertDatasetRowDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dto.Rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.ExternalKey) || row.ExternalKey.Length > 200 ||
                !normalizedRows.TryAdd(row.ExternalKey.Trim(), row))
                throw new ArgumentException("Every row must have a distinct key of 1–200 characters, ignoring case.", nameof(dto));
        }

        try
        {
            await using var transaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null
                ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                : null;
            if (_db.Database.IsRelational())
            {
                // Every operator, ingestion-key and Graph writer acquires this database
                // row before reading the case-insensitive key set. A process-local lock
                // would not protect another API instance or a background worker.
                var fenced = await _db.DatasetDefinitions
                    .Where(item => item.Id == dataset.Id && item.OrganizationId == dataset.OrganizationId &&
                                   item.SourceType == dataset.SourceType)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.UpdatedAtUtc, item => item.UpdatedAtUtc), cancellationToken);
                if (fenced != 1)
                    throw new DatasetWriteConflictException("The dataset or its source changed. Read its current definition before another update.");
            }

            var count = await ApplyRowsAsync(dataset, dto, normalizedRows, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            InvalidateDatasetCache(dataset.OrganizationId, dataset.Id);
            return count;
        }
        catch (Exception exception)
        {
            // Graph sync records a failure using this same scoped context. Do not let
            // that later SaveChanges replay rows from a rolled-back batch.
            foreach (var entry in _db.ChangeTracker.Entries<DatasetRow>().Where(entry => entry.Entity.DatasetId == dataset.Id).ToArray())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            if (IsDatasetWriteConflict(exception))
                throw new DatasetWriteConflictException("The dataset changed concurrently. Read its current rows before another update; this batch was not committed.");
            throw;
        }
    }

    private async Task<int> ApplyRowsAsync(DatasetDefinition dataset, UpsertDatasetRowsDto dto,
        IReadOnlyDictionary<string, UpsertDatasetRowDto> normalizedRows, CancellationToken cancellationToken)
    {
        var existingRows = await _db.DatasetRows
            .Where(x => x.DatasetId == dataset.Id && x.OrganizationId == dataset.OrganizationId)
            .ToListAsync(cancellationToken);
        var groups = existingRows.GroupBy(row => row.ExternalKey, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!dto.DeleteMissing && groups.Any(group => group.Skip(1).Any()))
            throw new DatasetWriteConflictException("Stored rows contain case-conflicting keys. Review all rows and submit an explicit complete replacement with deleteMissing=true and distinct keys.");
        var existingByKey = groups.ToDictionary(group => group.Key,
            group => group.OrderBy(row => row.ExternalKey, StringComparer.Ordinal).ThenBy(row => row.Id, StringComparer.Ordinal).First(),
            StringComparer.OrdinalIgnoreCase);
        if (dto.DeleteMissing)
            _db.DatasetRows.RemoveRange(groups.SelectMany(group => group.Where(row => row.Id != existingByKey[group.Key].Id)));

        foreach (var row in normalizedRows.Values)
        {
            var normalizedData = row.Data ?? new JsonObject();
            var rawJson = normalizedData.ToJsonString(JsonOptions);
            var searchText = BuildSearchText(normalizedData, dataset);

            if (existingByKey.TryGetValue(row.ExternalKey.Trim(), out var existing))
            {
                existing.DataJson = JsonDocument.Parse(rawJson);
                existing.SearchText = searchText;
                existing.RowHash = HashKey(rawJson);
                existing.SourceUpdatedAtUtc = row.SourceUpdatedAtUtc;
                existing.LastIngestedAtUtc = DateTimeOffset.UtcNow;
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                _db.DatasetRows.Add(new DatasetRow
                {
                    DatasetId = dataset.Id,
                    OrganizationId = dataset.OrganizationId,
                    ExternalKey = row.ExternalKey.Trim(),
                    DataJson = JsonDocument.Parse(rawJson),
                    SearchText = searchText,
                    RowHash = HashKey(rawJson),
                    SourceUpdatedAtUtc = row.SourceUpdatedAtUtc,
                    LastIngestedAtUtc = DateTimeOffset.UtcNow,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }

        if (dto.DeleteMissing)
        {
            var missing = existingRows.Where(x => !normalizedRows.ContainsKey(x.ExternalKey)).ToList();
            _db.DatasetRows.RemoveRange(missing);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return normalizedRows.Count;
    }

    private static bool IsDatasetWriteConflict(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException { InnerException: { } inner } => IsDatasetWriteConflict(inner),
        // Npgsql's execution strategy wraps serialization failures from direct
        // commands; retain the provider-code whitelist when unwrapping it.
        InvalidOperationException { InnerException: { } inner } => IsDatasetWriteConflict(inner),
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6 || sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        PostgresException postgres => postgres.SqlState is PostgresErrorCodes.SerializationFailure or
            PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation,
        _ => false
    };

    public async Task<TenantGraphDatasetSettingsDto?> GetGraphSettingsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await GetGraphSettingsCachedAsync(organizationId, cancellationToken);
        if (settings is null)
        {
            return null;
        }

        return MapGraphSettingsDto(settings);
    }

    public async Task<TenantGraphDatasetSettingsDto> UpsertGraphSettingsAsync(TenantGraphDatasetSettingsDto dto, CancellationToken cancellationToken = default)
    {
        var settings = await _db.TenantGraphDatasetSettings.FirstOrDefaultAsync(x => x.OrganizationId == dto.OrganizationId, cancellationToken);
        if (settings is null)
        {
            settings = new TenantGraphDatasetSettings
            {
                OrganizationId = dto.OrganizationId
            };
            _db.TenantGraphDatasetSettings.Add(settings);
        }

        settings.TenantId = dto.TenantId?.Trim();
        settings.ClientId = dto.ClientId?.Trim();
        if (!string.IsNullOrWhiteSpace(dto.ClientSecret))
        {
            settings.ClientSecretProtected = _protector.Protect(dto.ClientSecret.Trim());
        }
        settings.EnableUsers = dto.EnableUsers;
        settings.EnableDevices = dto.EnableDevices;
        settings.EnableGroups = dto.EnableGroups;
        settings.EnableSharePointSites = dto.EnableSharePointSites;
        settings.BackgroundSyncEnabled = dto.BackgroundSyncEnabled;
        settings.BackgroundSyncCronExpression = string.IsNullOrWhiteSpace(dto.BackgroundSyncCronExpression)
            ? "0 */6 * * *"
            : dto.BackgroundSyncCronExpression.Trim();
        settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        InvalidateGraphSettingsCache(dto.OrganizationId);
        return (await GetGraphSettingsAsync(dto.OrganizationId, cancellationToken))!;
    }

    public async Task<IReadOnlyList<DatasetDefinitionDto>> SyncBuiltInDatasetsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await _db.TenantGraphDatasetSettings.FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken)
                      ?? throw new InvalidOperationException("Graph dataset settings not configured.");

        try
        {
            if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecretProtected))
            {
                throw new InvalidOperationException("Graph dataset settings are incomplete.");
            }

            var secret = _protector.Unprotect(settings.ClientSecretProtected);
            var token = await GetGraphAccessTokenAsync(settings.TenantId, settings.ClientId, secret, cancellationToken);
            var synced = new List<DatasetDefinitionDto>();

            if (settings.EnableUsers)
            {
                synced.Add(await SyncGraphDatasetAsync(organizationId, DatasetSourceType.GraphUsers, "Users", new[]
                {
                    new DatasetColumnDto { Name = "id", IsKey = true, DataType = DatasetColumnDataType.Text, SortOrder = 0 },
                    new DatasetColumnDto { Name = "displayName", IsDisplay = true, IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 1 },
                    new DatasetColumnDto { Name = "mail", IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 2 },
                    new DatasetColumnDto { Name = "userPrincipalName", IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 3 }
                }, "https://graph.microsoft.com/v1.0/users?$select=id,displayName,mail,userPrincipalName", token, settings, x => x.LastUsersSyncUtc = DateTimeOffset.UtcNow, cancellationToken));
            }

            if (settings.EnableDevices)
            {
                synced.Add(await SyncGraphDatasetAsync(organizationId, DatasetSourceType.GraphDevices, "Computers", new[]
                {
                    new DatasetColumnDto { Name = "id", IsKey = true, DataType = DatasetColumnDataType.Text, SortOrder = 0 },
                    new DatasetColumnDto { Name = "displayName", IsDisplay = true, IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 1 },
                    new DatasetColumnDto { Name = "operatingSystem", IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 2 }
                }, "https://graph.microsoft.com/v1.0/devices?$select=id,displayName,operatingSystem", token, settings, x => x.LastDevicesSyncUtc = DateTimeOffset.UtcNow, cancellationToken));
            }

            if (settings.EnableGroups)
            {
                synced.Add(await SyncGraphDatasetAsync(organizationId, DatasetSourceType.GraphGroups, "Groups", new[]
                {
                    new DatasetColumnDto { Name = "id", IsKey = true, DataType = DatasetColumnDataType.Text, SortOrder = 0 },
                    new DatasetColumnDto { Name = "displayName", IsDisplay = true, IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 1 },
                    new DatasetColumnDto { Name = "mail", IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 2 }
                }, "https://graph.microsoft.com/v1.0/groups?$select=id,displayName,mail", token, settings, x => x.LastGroupsSyncUtc = DateTimeOffset.UtcNow, cancellationToken));
            }

            if (settings.EnableSharePointSites)
            {
                synced.Add(await SyncGraphDatasetAsync(organizationId, DatasetSourceType.GraphSharePointSites, "SharePoint Sites", new[]
                {
                    new DatasetColumnDto { Name = "id", IsKey = true, DataType = DatasetColumnDataType.Text, SortOrder = 0 },
                    new DatasetColumnDto { Name = "displayName", IsDisplay = true, IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 1 },
                    new DatasetColumnDto { Name = "webUrl", IsSearchable = true, DataType = DatasetColumnDataType.Text, SortOrder = 2 }
                }, "https://graph.microsoft.com/v1.0/sites?$search=*", token, settings, x => x.LastSharePointSitesSyncUtc = DateTimeOffset.UtcNow, cancellationToken));
            }

            settings.LastSyncStatus = "Succeeded";
            settings.LastSyncMessage = $"Synced {synced.Count} built-in datasets.";
            settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateGraphSettingsCache(organizationId);
            return synced;
        }
        catch (SecretReentryRequiredException ex)
        {
            _logger.LogWarning(
                ex,
                "Built-in Graph dataset sync requires secret re-entry for organization {OrganizationId}.",
                organizationId);
            settings.LastSyncStatus = "ActionRequired";
            settings.LastSyncMessage = "Stored Graph client secret can no longer be decrypted in this environment. Re-enter the client secret to re-encrypt it with the current key ring.";
            settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateGraphSettingsCache(organizationId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Built-in Graph dataset sync failed for organization {OrganizationId}.", organizationId);
            settings.LastSyncStatus = "Failed";
            settings.LastSyncMessage = ex.Message;
            settings.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            InvalidateGraphSettingsCache(organizationId);
            throw;
        }
    }

    private async Task<DatasetDefinition> GetOrCreateBuiltInDatasetAsync(
        string organizationId,
        DatasetSourceType sourceType,
        string name,
        IReadOnlyList<DatasetColumnDto> columns,
        CancellationToken cancellationToken)
    {
        var existing = await _db.DatasetDefinitions.FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.SourceType == sourceType,
            cancellationToken);

        if (existing is null)
        {
            var normalizedColumns = NormalizeColumns(
                columns,
                columns.First(x => x.IsKey).Name,
                columns.First(x => x.IsDisplay).Name,
                columns.Where(x => x.IsSearchable).Select(x => x.Name).ToList());

            var definition = new DatasetDefinition
            {
                OrganizationId = organizationId.Trim(),
                Name = name.Trim(),
                Slug = BuildBuiltInSlug(sourceType),
                SourceType = sourceType,
                KeyColumn = columns.First(x => x.IsKey).Name,
                DisplayColumn = columns.First(x => x.IsDisplay).Name,
                SearchColumns = columns.Where(x => x.IsSearchable).Select(x => x.Name).ToList(),
                IsBuiltIn = true,
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            _db.DatasetDefinitions.Add(definition);
            foreach (var column in normalizedColumns)
            {
                _db.DatasetColumns.Add(new DatasetColumn
                {
                    DatasetId = definition.Id,
                    Name = column.Name.Trim(),
                    DataType = column.DataType,
                    IsKey = string.Equals(column.Name, definition.KeyColumn, StringComparison.OrdinalIgnoreCase),
                    IsDisplay = string.Equals(column.Name, definition.DisplayColumn, StringComparison.OrdinalIgnoreCase),
                    IsSearchable = definition.SearchColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase),
                    SortOrder = column.SortOrder
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateDatasetCache(definition.OrganizationId, definition.Id);
            existing = definition;
        }

        return existing;
    }

    private async Task<DatasetDefinitionDto> SyncGraphDatasetAsync(
        string organizationId,
        DatasetSourceType sourceType,
        string name,
        IReadOnlyList<DatasetColumnDto> columns,
        string url,
        string accessToken,
        TenantGraphDatasetSettings settings,
        Action<TenantGraphDatasetSettings> updateSyncStamp,
        CancellationToken cancellationToken)
    {
        var dataset = await GetOrCreateBuiltInDatasetAsync(organizationId, sourceType, name, columns, cancellationToken);
        var rows = await FetchGraphRowsAsync(url, accessToken, cancellationToken);
        var ingestRows = new UpsertDatasetRowsDto
        {
            DeleteMissing = true,
            Rows = rows.Select(row =>
            {
                var keyValue = row[dataset.KeyColumn]?.GetValue<string?>() ?? string.Empty;
                return new UpsertDatasetRowDto
                {
                    ExternalKey = keyValue,
                    Data = row,
                    SourceUpdatedAtUtc = DateTimeOffset.UtcNow
                };
            }).Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).ToList()
        };

        await EnsureBuiltInCredentialAsync(dataset.Id, cancellationToken);
        await UpsertRowsInternalAsync(dataset, ingestRows, cancellationToken);
        updateSyncStamp(settings);
        return (await GetDatasetAsync(dataset.Id, cancellationToken))!;
    }

    private async Task<string> EnsureBuiltInCredentialAsync(string datasetId, CancellationToken cancellationToken)
    {
        var credential = await _db.DatasetIngestCredentials.FirstOrDefaultAsync(
            x => x.DatasetId == datasetId && x.Name == "Built-in Sync",
            cancellationToken);

        if (credential is not null)
        {
            return string.Empty;
        }

        var plainTextKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _db.DatasetIngestCredentials.Add(new DatasetIngestCredential
        {
            DatasetId = datasetId,
            Name = "Built-in Sync",
            KeyHash = HashKey(plainTextKey),
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return plainTextKey;
    }

    private async Task<string> GetGraphAccessTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), cancellationToken);
        return token.Token;
    }

    private async Task<List<JsonObject>> FetchGraphRowsAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var results = new List<JsonObject>();
        var nextUrl = url;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(payload)?.AsObject() ?? new JsonObject();
            if (root["value"] is JsonArray valueArray)
            {
                foreach (var item in valueArray)
                {
                    if (item is JsonObject obj)
                    {
                        results.Add(obj);
                    }
                }
            }

            nextUrl = root["@odata.nextLink"]?.GetValue<string?>();
        }

        return results;
    }

    private async Task<DatasetDefinition?> GetDatasetDefinitionCachedAsync(string datasetId, CancellationToken cancellationToken)
    {
        var cacheKey = $"dataset-definition:{datasetId}";
        if (_cache.TryGetValue(cacheKey, out DatasetDefinition? cached) && cached is not null)
        {
            return cached;
        }

        var definition = await _db.DatasetDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == datasetId, cancellationToken);
        if (definition is not null)
        {
            _cache.Set(cacheKey, definition, TimeSpan.FromMinutes(10));
        }

        return definition;
    }

    private async Task<List<DatasetColumn>> GetDatasetColumnsCachedAsync(string datasetId, CancellationToken cancellationToken)
    {
        var cacheKey = $"dataset-columns:{datasetId}";
        if (_cache.TryGetValue(cacheKey, out List<DatasetColumn>? cached) && cached is not null)
        {
            return cached;
        }

        var columns = await _db.DatasetColumns
            .AsNoTracking()
            .Where(x => x.DatasetId == datasetId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        _cache.Set(cacheKey, columns, TimeSpan.FromMinutes(10));
        return columns;
    }

    private async Task<TenantGraphDatasetSettings?> GetGraphSettingsCachedAsync(string organizationId, CancellationToken cancellationToken)
    {
        var cacheKey = $"graph-dataset-settings:{organizationId}";
        if (_cache.TryGetValue(cacheKey, out TenantGraphDatasetSettings? cached) && cached is not null)
        {
            return cached;
        }

        var settings = await _db.TenantGraphDatasetSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (settings is not null)
        {
            _cache.Set(cacheKey, settings, TimeSpan.FromMinutes(10));
        }

        return settings;
    }

    private void InvalidateDatasetCache(string organizationId, string datasetId)
    {
        _cache.Remove($"dataset-definition:{datasetId}");
        _cache.Remove($"dataset-columns:{datasetId}");
        _cache.Remove($"dataset-organization:{organizationId}");
    }

    private void InvalidateGraphSettingsCache(string organizationId)
    {
        _cache.Remove($"graph-dataset-settings:{organizationId}");
    }

    private static DatasetDefinitionDto MapDatasetDto(DatasetDefinition definition, IReadOnlyCollection<DatasetColumn> columns)
    {
        return new DatasetDefinitionDto
        {
            Id = definition.Id,
            OrganizationId = definition.OrganizationId,
            Name = definition.Name,
            Slug = definition.Slug,
            SourceType = definition.SourceType,
            KeyColumn = definition.KeyColumn,
            DisplayColumn = definition.DisplayColumn,
            SearchColumns = definition.SearchColumns,
            IsBuiltIn = definition.IsBuiltIn,
            IsActive = definition.IsActive,
            CreatedAtUtc = definition.CreatedAtUtc,
            UpdatedAtUtc = definition.UpdatedAtUtc,
            Columns = columns
                .OrderBy(x => x.SortOrder)
                .Select(x => new DatasetColumnDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    DataType = x.DataType,
                    IsKey = x.IsKey,
                    IsDisplay = x.IsDisplay,
                    IsSearchable = x.IsSearchable,
                    SortOrder = x.SortOrder
                })
                .ToList()
        };
    }

    private static DatasetRowDto MapRowDto(DatasetRow row)
    {
        return new DatasetRowDto
        {
            Id = row.Id,
            DatasetId = row.DatasetId,
            ExternalKey = row.ExternalKey,
            Data = JsonNode.Parse(row.DataJson.RootElement.GetRawText())?.AsObject() ?? new JsonObject(),
            SourceUpdatedAtUtc = row.SourceUpdatedAtUtc,
            LastIngestedAtUtc = row.LastIngestedAtUtc
        };
    }

    private static DatasetOptionDto MapOptionDto(DatasetRow row, DatasetDefinition definition)
    {
        var data = JsonNode.Parse(row.DataJson.RootElement.GetRawText())?.AsObject() ?? new JsonObject();
        var label = data[definition.DisplayColumn]?.GetValue<string?>()
                    ?? data[definition.KeyColumn]?.GetValue<string?>()
                    ?? row.ExternalKey;

        var previewParts = definition.SearchColumns
            .Where(x => !string.Equals(x, definition.DisplayColumn, StringComparison.OrdinalIgnoreCase))
            .Select(x => data[x]?.GetValue<string?>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(2)
            .ToList();

        return new DatasetOptionDto
        {
            Key = row.ExternalKey,
            Label = label,
            Preview = previewParts.Count == 0 ? null : string.Join(" | ", previewParts)
        };
    }

    private static DatasetIngestCredentialDto MapCredentialDto(DatasetIngestCredential credential)
    {
        return new DatasetIngestCredentialDto
        {
            Id = credential.Id,
            DatasetId = credential.DatasetId,
            Name = credential.Name,
            IsActive = credential.IsActive,
            LastUsedAtUtc = credential.LastUsedAtUtc,
            CreatedAtUtc = credential.CreatedAtUtc
        };
    }

    private static TenantGraphDatasetSettingsDto MapGraphSettingsDto(TenantGraphDatasetSettings settings)
    {
        return new TenantGraphDatasetSettingsDto
        {
            OrganizationId = settings.OrganizationId,
            TenantId = settings.TenantId,
            ClientId = settings.ClientId,
            EnableUsers = settings.EnableUsers,
            EnableDevices = settings.EnableDevices,
            EnableGroups = settings.EnableGroups,
            EnableSharePointSites = settings.EnableSharePointSites,
            BackgroundSyncEnabled = settings.BackgroundSyncEnabled,
            BackgroundSyncCronExpression = settings.BackgroundSyncCronExpression,
            LastUsersSyncUtc = settings.LastUsersSyncUtc,
            LastDevicesSyncUtc = settings.LastDevicesSyncUtc,
            LastGroupsSyncUtc = settings.LastGroupsSyncUtc,
            LastSharePointSitesSyncUtc = settings.LastSharePointSitesSyncUtc,
            LastSyncStatus = settings.LastSyncStatus,
            LastSyncMessage = settings.LastSyncMessage
        };
    }

    private static List<DatasetColumnDto> NormalizeColumns(IEnumerable<DatasetColumnDto> columns, string? keyColumn, string? displayColumn, IEnumerable<string>? searchColumns)
    {
        var normalized = columns
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select((column, index) =>
            {
                column.Name = column.Name.Trim();
                column.SortOrder = index;
                return column;
            })
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.Add(new DatasetColumnDto { Name = "id", DataType = DatasetColumnDataType.Text, SortOrder = 0, IsKey = true });
            normalized.Add(new DatasetColumnDto { Name = "name", DataType = DatasetColumnDataType.Text, SortOrder = 1, IsDisplay = true, IsSearchable = true });
        }

        var key = ResolveKeyColumn(keyColumn, normalized);
        var display = ResolveDisplayColumn(displayColumn, normalized);
        var search = ResolveSearchColumns(searchColumns, normalized);
        foreach (var column in normalized)
        {
            column.IsKey = string.Equals(column.Name, key, StringComparison.OrdinalIgnoreCase);
            column.IsDisplay = string.Equals(column.Name, display, StringComparison.OrdinalIgnoreCase);
            column.IsSearchable = search.Contains(column.Name, StringComparer.OrdinalIgnoreCase);
        }

        return normalized;
    }

    private static string ResolveKeyColumn(string? keyColumn, IReadOnlyList<DatasetColumnDto> columns)
    {
        return columns.FirstOrDefault(x => string.Equals(x.Name, keyColumn, StringComparison.OrdinalIgnoreCase) || x.IsKey)?.Name
               ?? columns[0].Name;
    }

    private static string ResolveDisplayColumn(string? displayColumn, IReadOnlyList<DatasetColumnDto> columns)
    {
        return columns.FirstOrDefault(x => string.Equals(x.Name, displayColumn, StringComparison.OrdinalIgnoreCase) || x.IsDisplay)?.Name
               ?? columns[0].Name;
    }

    private static List<string> ResolveSearchColumns(IEnumerable<string>? searchColumns, IReadOnlyList<DatasetColumnDto> columns)
    {
        var requested = (searchColumns ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count > 0)
        {
            return requested;
        }

        return columns.Where(x => x.IsSearchable).Select(x => x.Name).DefaultIfEmpty(columns[0].Name).ToList();
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length == 0 || builder[^1] == '-')
            {
                continue;
            }
            else
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string BuildBuiltInSlug(DatasetSourceType sourceType)
    {
        return sourceType switch
        {
            DatasetSourceType.GraphUsers => "graph-users",
            DatasetSourceType.GraphDevices => "graph-devices",
            DatasetSourceType.GraphGroups => "graph-groups",
            DatasetSourceType.GraphSharePointSites => "graph-sharepoint-sites",
            _ => $"built-in-{Slugify(sourceType.ToString())}"
        };
    }

    private static string BuildSearchText(JsonObject data, DatasetDefinition dataset)
    {
        var parts = dataset.SearchColumns
            .Select(x => data[x]?.GetValue<string?>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim().ToLowerInvariant())
            .ToList();

        if (parts.Count == 0)
        {
            parts = data.Select(x => x.Value?.GetValue<string?>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim().ToLowerInvariant())
                .ToList();
        }

        return string.Join(" ", parts);
    }

    private static string HashKey(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}

public sealed class RequestFormDatasetBindingValidator(IDataManagementService dataManagementService) : IRequestFormDatasetBindingValidator
{
    private readonly IDataManagementService _dataManagementService = dataManagementService;

    public async Task<string?> ValidateAsync(string organizationId, IReadOnlyCollection<FormField> fields, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return fields.Any(x => x.DataBinding is not null)
                ? "Data-bound fields require a tenant-scoped request form."
                : null;
        }

        var datasets = (await _dataManagementService.ListDatasetsAsync(organizationId, cancellationToken))
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            if (field.DataBinding is null)
            {
                continue;
            }

            if (!string.Equals(field.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                return $"Field '{field.Label}' can only use data binding when the field type is Text.";
            }

            if (string.IsNullOrWhiteSpace(field.DataBinding.DatasetId))
            {
                return $"Field '{field.Label}' is missing a dataset id.";
            }

            if (!datasets.TryGetValue(field.DataBinding.DatasetId, out var dataset))
            {
                return $"Field '{field.Label}' references dataset '{field.DataBinding.DatasetId}' which is not available for this tenant.";
            }

            if (!dataset.IsActive)
            {
                return $"Field '{field.Label}' references inactive dataset '{dataset.Name}'.";
            }
        }

        return null;
    }
}

public sealed class SelfServiceDatasetBindingResolver(
    IDataManagementService dataManagementService,
    ILogger<SelfServiceDatasetBindingResolver> logger) : ISelfServiceDatasetBindingResolver
{
    private const string BoundLabelKey = "_boundLabels";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly IDataManagementService _dataManagementService = dataManagementService;
    private readonly ILogger<SelfServiceDatasetBindingResolver> _logger = logger;

    public async Task<(bool Success, string PayloadJson, IReadOnlyList<string> Errors)> NormalizePayloadAsync(
        string organizationId,
        IReadOnlyCollection<FormField> fields,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        JsonObject payloadRoot;
        try
        {
            payloadRoot = JsonNode.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson)?.AsObject() ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse self-service payload for dataset binding normalization.");
            return (false, payloadJson, ["PayloadJson must be a valid JSON object."]);
        }

        var labelsNode = payloadRoot[BoundLabelKey] as JsonObject ?? new JsonObject();
        var errors = new List<string>();

        foreach (var pair in fields.Select((field, index) => new { Field = field, Key = FormFieldKeyResolver.Resolve(field, index) }))
        {
            var binding = pair.Field.DataBinding;
            if (binding is null)
            {
                continue;
            }

            var selectedKey = payloadRoot[pair.Key]?.GetValue<string?>();
            if (string.IsNullOrWhiteSpace(selectedKey))
            {
                continue;
            }

            var options = await _dataManagementService.GetOptionsAsync(binding.DatasetId, organizationId, selectedKey, 1, 25, cancellationToken);
            var match = options.Items.FirstOrDefault(x => string.Equals(x.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                if (binding.AllowFreeText)
                {
                    labelsNode[pair.Key] = selectedKey;
                    continue;
                }

                errors.Add($"Field '{pair.Field.Label}' contains an invalid selection.");
                continue;
            }

            payloadRoot[pair.Key] = match.Key;
            labelsNode[pair.Key] = match.Label;
        }

        if (labelsNode.Count > 0)
        {
            payloadRoot[BoundLabelKey] = labelsNode;
        }

        return errors.Count == 0
            ? (true, payloadRoot.ToJsonString(JsonOptions), Array.Empty<string>())
            : (false, payloadJson, errors);
    }
}
