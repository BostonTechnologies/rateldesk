using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Helpdesk.Application.Resources;
using Helpdesk.Application.Services.AI;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Infrastructure.Resources;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Infrastructure.Resources;

public sealed class GraphDatasetWorkflowTests
{
    [Fact]
    public async Task Registered_connection_test_reads_once_and_exposes_throttling_under_real_service_defaults()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        fixture.Provider.Failures["/v1.0/users"] = HttpStatusCode.TooManyRequests;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite", ["Database:Sqlite:Path"] = Path.Combine(Path.GetTempPath(), "graph-http-fixture.db")
        });
        builder.AddServiceDefaults();
        builder.Services.AddHelpdeskInfrastructure(builder.Configuration);
        builder.Services.Configure<HttpClientFactoryOptions>("GraphDatasetConnectionTest", options =>
            options.HttpMessageHandlerBuilderActions.Add(handler =>
            {
                var previous = handler.PrimaryHandler;
                handler.PrimaryHandler = fixture.Provider;
                previous.Dispose();
            }));
        await using var provider = builder.Services.BuildServiceProvider();
        var result = await fixture.Service(db, cache, provider.GetRequiredService<IHttpClientFactory>()).TestGraphConnectionAsync(Draft());
        Assert.Equal(GraphAccessOutcome.Throttled, Assert.Single(result.Sources).Outcome);
        Assert.Null(result.TestToken);
        Assert.Single(fixture.Provider.Requests);
        Assert.Equal(0, await db.TenantGraphDatasetSettings.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Connection_test_checks_selected_sources_once_and_never_writes(bool postgres, bool storedSecret)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        if (storedSecret) await fixture.SeedSettingsAsync(db);
        var before = await db.TenantGraphDatasetSettings.AsNoTracking().ToArrayAsync();
        var draft = Draft(storedSecret);
        draft.EnableDevices = draft.EnableGroups = draft.EnableSharePointSites = true;
        fixture.Provider.Empty = true;
        var result = await fixture.Service(db, cache).TestGraphConnectionAsync(draft);
        Assert.True(result.AuthenticationSucceeded);
        Assert.Equal(GraphAccessOutcome.Succeeded, result.Outcome);
        Assert.NotEmpty(result.TestToken!);
        Assert.Equal(4, result.Sources.Count);
        Assert.All(result.Sources, source => Assert.True(source.Succeeded));
        Assert.Equal(4, fixture.Provider.Requests.Count);
        Assert.All(fixture.Provider.Requests, uri => Assert.Contains("$top=1", uri.Query));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await db.TenantGraphDatasetSettings.AsNoTracking().ToArrayAsync()));
        Assert.Equal(0, await db.DatasetDefinitions.CountAsync());
        Assert.Equal(0, await db.DatasetRows.CountAsync());
        Assert.Equal(0, await db.DatasetIngestCredentials.CountAsync());
        Assert.Equal(0, await db.GraphDatasetImportRuns.CountAsync());
        if (storedSecret)
        {
            var metadata = await fixture.Service(db, cache).GetGraphSettingsAsync("org-graph");
            Assert.True(metadata!.HasStoredSecret);
            Assert.Null(metadata.ClientSecret);
        }
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("fixture-access-token", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(403, GraphAccessOutcome.AccessDenied)]
    [InlineData(429, GraphAccessOutcome.Throttled)]
    [InlineData(504, GraphAccessOutcome.TimedOut)]
    public async Task Failed_source_access_has_typed_safe_feedback_and_no_import_proof(int status, GraphAccessOutcome expected)
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        fixture.Provider.Failures["/v1.0/users"] = (HttpStatusCode)status;
        var result = await fixture.Service(db, cache).TestGraphConnectionAsync(Draft());
        Assert.True(result.AuthenticationSucceeded);
        Assert.Equal(expected, Assert.Single(result.Sources).Outcome);
        Assert.Null(result.TestToken);
        Assert.DoesNotContain("provider-secret", JsonSerializer.Serialize(result));
        Assert.Equal(0, await db.TenantGraphDatasetSettings.CountAsync());
        Assert.Equal(0, await db.GraphDatasetImportRuns.CountAsync());
    }

    [Fact]
    public async Task Invalid_authentication_and_oversized_or_malformed_responses_are_safe_and_do_not_write()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        fixture.Credentials.Reject = true;
        var service = fixture.Service(db, cache);
        var denied = await service.TestGraphConnectionAsync(Draft());
        Assert.False(denied.AuthenticationSucceeded);
        Assert.Equal(GraphAccessOutcome.AuthenticationFailed, denied.Outcome);
        Assert.Null(denied.TestToken);
        Assert.Empty(fixture.Provider.Requests);
        fixture.Credentials.Reject = false;
        fixture.Provider.Payload = new string('x', 300_000);
        var oversized = await service.TestGraphConnectionAsync(Draft());
        Assert.Equal(GraphAccessOutcome.InvalidResponse, Assert.Single(oversized.Sources).Outcome);
        fixture.Provider.Payload = "[]";
        var malformed = await service.TestGraphConnectionAsync(Draft());
        Assert.Equal(GraphAccessOutcome.InvalidResponse, Assert.Single(malformed.Sources).Outcome);
        Assert.Equal(0, await db.DatasetDefinitions.CountAsync());
        Assert.Equal(0, await db.TenantGraphDatasetSettings.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_proof_is_bound_to_connection_scope_and_sources_and_works_in_another_service_scope(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        GraphConnectionTestResultDto tested;
        var draft = Draft();
        await using (var firstDb = fixture.Context())
        using (var firstCache = new MemoryCache(new MemoryCacheOptions()))
            tested = await fixture.Service(firstDb, firstCache).TestGraphConnectionAsync(draft);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        draft.EnableGroups = true;
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAndImportGraphAsync(new()
            { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! }));
        draft.EnableGroups = false;
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAndImportGraphAsync(new()
            { RequestId = Guid.NewGuid(), Connection = draft, TestToken = fixture.ExpiredReceipt(tested.TestToken!) }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAndImportGraphAsync(new()
            { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! + "tampered" }));
        draft.ClientId = "another-client";
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAndImportGraphAsync(new()
            { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! }));
        draft.ClientId = "fixture-client";
        draft.OrganizationId = "org-other";
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAndImportGraphAsync(new()
            { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! }));
        draft.OrganizationId = "org-graph";
        var imported = await service.SaveAndImportGraphAsync(new()
            { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! });
        Assert.Equal("Succeeded", imported.Status);
        Assert.Equal(2, Assert.Single(imported.Sources).RowCount);
        Assert.True(imported.ConfigurationSaved);
        Assert.Equal(2, await db.DatasetRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_records_partial_commits_preserves_schedule_and_retry_does_not_import_twice(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        await fixture.SeedSettingsAsync(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var draft = Draft(storedSecret: true);
        draft.EnableGroups = true;
        var tested = await service.TestGraphConnectionAsync(draft);
        fixture.Provider.FailOnImportsOnly = true;
        fixture.Provider.Failures["/v1.0/groups"] = HttpStatusCode.Forbidden;
        var request = new GraphDatasetImportRequestDto { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! };
        var imported = await service.SaveAndImportGraphAsync(request);
        Assert.Equal("PartiallySucceeded", imported.Status);
        Assert.True(imported.ConfigurationSaved);
        var users = Assert.Single(imported.Sources, source => source.SourceType == DatasetSourceType.GraphUsers);
        Assert.Equal("Succeeded", users.Status);
        Assert.Equal(2, users.RowCount);
        var groups = Assert.Single(imported.Sources, source => source.SourceType == DatasetSourceType.GraphGroups);
        Assert.Equal("Failed", groups.Status);
        var settings = await db.TenantGraphDatasetSettings.SingleAsync();
        Assert.True(settings.BackgroundSyncEnabled);
        Assert.Equal("15 */4 * * *", settings.BackgroundSyncCronExpression);
        Assert.NotNull(settings.LastUsersSyncUtc);
        Assert.Null(settings.LastGroupsSyncUtc);
        var requests = fixture.Provider.Requests.Count;
        var repeated = await service.SaveAndImportGraphAsync(request);
        Assert.Equal(imported.RequestId, repeated.RequestId);
        Assert.Equal(requests, fixture.Provider.Requests.Count);
        Assert.Equal(imported.RequestId, (await service.GetGraphImportRequestStatusAsync("org-graph", imported.RequestId))!.RequestId);
        Assert.Null(await service.GetGraphImportRequestStatusAsync("org-graph", Guid.NewGuid()));
        Assert.Null(await service.GetGraphImportRequestStatusAsync("org-other", imported.RequestId));
        await using var reload = fixture.Context();
        using var reloadCache = new MemoryCache(new MemoryCacheOptions());
        Assert.Equal("PartiallySucceeded", (await fixture.Service(reload, reloadCache).GetGraphImportStatusAsync("org-graph"))!.Status);
        Assert.Equal(1, await reload.GraphDatasetImportRuns.CountAsync());
        Assert.Equal(2, await reload.DatasetRows.CountAsync());
    }

    [Fact]
    public async Task A_handled_import_interruption_finishes_the_receipt_without_an_eternally_running_source()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var draft = Draft();
        draft.EnableGroups = true;
        var tested = await service.TestGraphConnectionAsync(draft);
        fixture.Provider.CancelImports = true;
        var result = await service.SaveAndImportGraphAsync(new() { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! });
        Assert.Equal("Interrupted", result.Status);
        Assert.True(result.ConfigurationSaved);
        Assert.Equal("Unknown", result.Sources[0].Status);
        Assert.Equal(0, result.Sources[0].RowCount);
        Assert.Equal("NotStarted", result.Sources[1].Status);
        Assert.DoesNotContain(result.Sources, source => source.Status == "Running");
        Assert.Null((await db.GraphDatasetImportRuns.AsNoTracking().SingleAsync()).ActiveOrganizationId);
        Assert.Equal(0, await db.DatasetRows.CountAsync());
        Assert.Equal(0, await db.DatasetIngestCredentials.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_imports_share_a_durable_claim_across_service_scopes(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var firstDb = fixture.Context();
        await using var secondDb = fixture.Context();
        using var firstCache = new MemoryCache(new MemoryCacheOptions());
        using var secondCache = new MemoryCache(new MemoryCacheOptions());
        var first = fixture.Service(firstDb, firstCache);
        var second = fixture.Service(secondDb, secondCache);
        var draft = Draft();
        var tested = await first.TestGraphConnectionAsync(draft);
        var request = new GraphDatasetImportRequestDto { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! };
        fixture.Provider.HoldImports = true;
        var importing = first.SaveAndImportGraphAsync(request);
        try
        {
            await fixture.Provider.ImportEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var count = fixture.Provider.Requests.Count;
            var same = await second.SaveAndImportGraphAsync(request);
            Assert.Equal("Running", same.Status);
            Assert.Equal(request.RequestId, same.RequestId);
            await Assert.ThrowsAsync<DatasetWriteConflictException>(() => second.SaveAndImportGraphAsync(new()
                { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! }));
            Assert.Equal(count, fixture.Provider.Requests.Count);
        }
        finally
        {
            fixture.Provider.ImportRelease.TrySetResult();
            await importing.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal("Succeeded", (await importing).Status);
        Assert.Equal(1, await secondDb.GraphDatasetImportRuns.CountAsync());
        Assert.Equal(2, await secondDb.DatasetRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_import_is_visible_and_deliberately_replaces_prior_rows(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var draft = Draft();
        var tested = await service.TestGraphConnectionAsync(draft);
        await service.SaveAndImportGraphAsync(new() { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! });
        Assert.Equal(2, await db.DatasetRows.CountAsync());
        fixture.Provider.Empty = true;
        tested = await service.TestGraphConnectionAsync(draft);
        var empty = await service.SaveAndImportGraphAsync(new() { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! });
        Assert.Equal("Succeeded", empty.Status);
        Assert.Equal(0, Assert.Single(empty.Sources).RowCount);
        Assert.Equal(0, await db.DatasetRows.CountAsync());
        Assert.Equal(1, await db.DatasetDefinitions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_schema_ingestion_search_and_options_preserve_raw_values_and_invariant_scalar_labels(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var definition = await service.CreateDatasetAsync(new()
            {
                RequestId = Guid.NewGuid(), OrganizationId = "org-graph", Name = "Typed custom dataset",
                KeyColumn = "typedKey", DisplayColumn = "amount",
                SearchColumns = ["amount", "enabled", "timestamp", "typedKey", "complex", "nothing"],
                Columns =
                [
                    new() { Name = "typedKey", DataType = DatasetColumnDataType.Number },
                    new() { Name = "amount", DataType = DatasetColumnDataType.Number },
                    new() { Name = "enabled", DataType = DatasetColumnDataType.Boolean },
                    new() { Name = "timestamp", DataType = DatasetColumnDataType.DateTime },
                    new() { Name = "complex", DataType = DatasetColumnDataType.Text },
                    new() { Name = "nothing", DataType = DatasetColumnDataType.Text }
                ]
            });
            var offsetDate = new DateTimeOffset(2026, 11, 5, 7, 8, 9, TimeSpan.FromHours(2));
            var utcDate = new DateTime(2026, 11, 6, 7, 8, 9, DateTimeKind.Utc);
            await service.UpsertRowsAsOperatorAsync(definition.Id, "org-graph", false, new()
            {
                Rows =
                [
                    new() { ExternalKey = "stable-1", Data = new JsonObject
                    {
                        ["typedKey"] = 7, ["amount"] = 1234.5m, ["enabled"] = true,
                        ["timestamp"] = offsetDate, ["complex"] = new JsonObject { ["nested"] = "ignored" }, ["nothing"] = null
                    } },
                    new() { ExternalKey = "stable-2", Data = new JsonObject
                    {
                        ["typedKey"] = 9876.5m, ["amount"] = null, ["enabled"] = false,
                        ["timestamp"] = utcDate, ["complex"] = new JsonArray(1, 2), ["nothing"] = null
                    } },
                    new() { ExternalKey = "external-fallback", Data = new JsonObject
                    {
                        ["typedKey"] = new JsonArray(1), ["amount"] = new JsonObject(),
                        ["enabled"] = null, ["timestamp"] = null, ["complex"] = null, ["nothing"] = null
                    } }
                ]
            });

            Assert.Equal("stable-1", Assert.Single((await service.GetRowsAsync(definition.Id, "1234.5", 1, 25)).Items).ExternalKey);
            Assert.Equal("stable-2", Assert.Single((await service.GetRowsAsync(definition.Id, "9876.5", 1, 25)).Items).ExternalKey);
            Assert.Empty((await service.GetRowsAsync(definition.Id, "1234,5", 1, 25)).Items);
            Assert.Equal("stable-2", Assert.Single((await service.GetRowsAsync(definition.Id, "2026-11-06", 1, 25)).Items).ExternalKey);
            var option = Assert.Single((await service.GetOptionsAsync(definition.Id, "org-graph", "true", 1, 25)).Items);
            Assert.Equal("stable-1", option.Key);
            Assert.Equal("1234.5", option.Label);
            Assert.Equal("true | 2026-11-05T07:08:09+02:00", option.Preview);
            var typedKeyFallback = await service.GetOptionByKeyAsync(definition.Id, "org-graph", "STABLE-2");
            Assert.Equal("stable-2", typedKeyFallback!.Key);
            Assert.Equal("9876.5", typedKeyFallback.Label);
            Assert.Equal("false | 2026-11-06T07:08:09Z", typedKeyFallback.Preview);
            var externalKeyFallback = await service.GetOptionByKeyAsync(definition.Id, "org-graph", "external-fallback");
            Assert.Equal("external-fallback", externalKeyFallback!.Label);
            Assert.Null(externalKeyFallback.Preview);

            await using var readback = fixture.Context();
            var saved = (await readback.DatasetRows.AsNoTracking().SingleAsync(row => row.ExternalKey == "stable-1")).DataJson.RootElement;
            Assert.Equal(JsonValueKind.Number, saved.GetProperty("amount").ValueKind);
            Assert.Equal(1234.5m, saved.GetProperty("amount").GetDecimal());
            Assert.Equal(JsonValueKind.True, saved.GetProperty("enabled").ValueKind);
            Assert.Equal(JsonValueKind.String, saved.GetProperty("timestamp").ValueKind);
            Assert.Equal(offsetDate, saved.GetProperty("timestamp").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Object, saved.GetProperty("complex").ValueKind);
            Assert.Equal(JsonValueKind.Null, saved.GetProperty("nothing").ValueKind);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"value\":{}}")]
    [InlineData(true, "{\"value\":{}}")]
    [InlineData(false, "{\"value\":[null]}")]
    [InlineData(true, "{\"value\":[null]}")]
    [InlineData(false, "{\"value\":[{}]}")]
    [InlineData(true, "{\"value\":[{}]}")]
    [InlineData(false, "{\"value\":[{\"id\":null}]}")]
    [InlineData(true, "{\"value\":[{\"id\":null}]}")]
    [InlineData(false, "{\"value\":[{\"id\":\" \"}]}")]
    [InlineData(true, "{\"value\":[{\"id\":\" \"}]}")]
    [InlineData(false, "{\"value\":[{\"id\":42}]}")]
    [InlineData(true, "{\"value\":[{\"id\":42}]}")]
    public async Task Malformed_successful_import_pages_never_clear_previously_saved_rows(bool postgres, string malformed)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var draft = Draft();
        var tested = await service.TestGraphConnectionAsync(draft);
        await service.SaveAndImportGraphAsync(new() { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! });
        var before = await db.DatasetRows.AsNoTracking().OrderBy(row => row.ExternalKey).Select(row => row.DataJson).ToArrayAsync();
        tested = await service.TestGraphConnectionAsync(draft);
        fixture.Provider.Payload = malformed;
        var failed = await service.SaveAndImportGraphAsync(new() { RequestId = Guid.NewGuid(), Connection = draft, TestToken = tested.TestToken! });
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("Failed", Assert.Single(failed.Sources).Status);
        Assert.Equal(2, Assert.Single(failed.Sources).RowCount);
        var after = await db.DatasetRows.AsNoTracking().OrderBy(row => row.ExternalKey).Select(row => row.DataJson).ToArrayAsync();
        Assert.Equal(before.Select(row => row.RootElement.GetRawText()), after.Select(row => row.RootElement.GetRawText()));
        Assert.Equal(2, after.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_run_requires_explicit_bounded_reconciliation_without_import(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        var started = DateTimeOffset.UtcNow.AddMinutes(-16);
        var run = new GraphDatasetImportRun
        {
            OrganizationId = "org-graph", RequestId = Guid.NewGuid(), ActiveOrganizationId = "org-graph",
            StartedAtUtc = started, StartedAtUtcTicks = started.UtcTicks, Status = "Running", ConfigurationSaved = true,
            SourceResultsJson = JsonSerializer.Serialize(new[]
            {
                new GraphDatasetImportSourceResultDto { SourceType = DatasetSourceType.GraphUsers, Status = "Succeeded", RowCount = 2 },
                new GraphDatasetImportSourceResultDto { SourceType = DatasetSourceType.GraphGroups, Status = "Running" }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        db.GraphDatasetImportRuns.Add(run);
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var uncertain = await service.GetGraphImportStatusAsync("org-graph");
        Assert.True(uncertain!.CanReconcile);
        Assert.Equal("org-graph", (await db.GraphDatasetImportRuns.AsNoTracking().SingleAsync()).ActiveOrganizationId);
        var reconciled = await service.ReconcileInterruptedGraphImportAsync("org-graph", run.RequestId);
        Assert.Equal("Interrupted", reconciled.Status);
        Assert.False(reconciled.CanReconcile);
        Assert.Equal("Succeeded", reconciled.Sources[0].Status);
        Assert.Equal("Unknown", reconciled.Sources[1].Status);
        Assert.Null((await db.GraphDatasetImportRuns.AsNoTracking().SingleAsync()).ActiveOrganizationId);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(0, await db.DatasetDefinitions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_retry_is_idempotent_and_changed_payload_is_rejected_while_legacy_defaults_work(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var legacy = await service.CreateDatasetAsync(new() { OrganizationId = "org-graph", Name = "Legacy default schema" });
        Assert.Equal(new[] { "id", "name" }, legacy.Columns.Select(column => column.Name));
        Assert.Equal(new[] { "name" }, legacy.SearchColumns);
        var draft = new CreateDatasetDefinitionDto
        {
            OrganizationId = "org-graph", Name = "Guided dataset", RequestId = Guid.NewGuid(), KeyColumn = "id", DisplayColumn = "name",
            Columns = [new() { Name = "id", IsKey = true }, new() { Name = "name", IsDisplay = true, IsSearchable = true }]
        };
        var created = await service.CreateDatasetAsync(draft);
        var repeated = await service.CreateDatasetAsync(draft);
        Assert.Equal(created.Id, repeated.Id);
        Assert.Empty(created.SearchColumns);
        Assert.All(created.Columns, column => Assert.False(column.IsSearchable));
        Assert.Equal(created.Id, (await service.GetCreateResultAsync(draft.OrganizationId, draft.RequestId.Value))!.Id);
        Assert.Null(await service.GetCreateResultAsync("org-other", draft.RequestId.Value));
        Assert.Equal(2, await db.DatasetDefinitions.CountAsync());
        Assert.Equal(0, await db.DatasetIngestCredentials.CountAsync());
        draft.Name = "Changed name";
        await Assert.ThrowsAsync<DatasetWriteConflictException>(() => service.CreateDatasetAsync(draft));
        Assert.Equal(2, await db.DatasetDefinitions.CountAsync());
    }

    [Fact]
    public async Task Explicit_invalid_schema_fields_are_rejected_without_silent_dropping_or_writes()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var error = await Assert.ThrowsAsync<DatasetDefinitionValidationException>(() => service.CreateDatasetAsync(new()
        {
            OrganizationId = "org-graph", Name = "Invalid", RequestId = Guid.NewGuid(), KeyColumn = "missing", DisplayColumn = "missing",
            SearchColumns = ["missing"], Columns = [new() { Name = "name" }, new() { Name = "NAME" }, new() { Name = " ", DataType = (DatasetColumnDataType)42 }]
        }));
        Assert.Contains("Columns[1].Name", error.Errors.Keys);
        Assert.Contains("Columns[2].Name", error.Errors.Keys);
        Assert.Contains("Columns[2].DataType", error.Errors.Keys);
        Assert.Contains("KeyColumn", error.Errors.Keys);
        Assert.Contains("DisplayColumn", error.Errors.Keys);
        Assert.Contains("SearchColumns", error.Errors.Keys);
        Assert.Equal(0, await db.DatasetDefinitions.CountAsync());
        Assert.Equal(0, await db.DatasetColumns.CountAsync());
    }

    private static GraphConnectionTestRequestDto Draft(bool storedSecret = false) => new()
    {
        OrganizationId = "org-graph", TenantId = "fixture-tenant", ClientId = "fixture-client",
        ClientSecret = storedSecret ? null : "fixture-secret", UseStoredSecret = storedSecret, EnableUsers = true
    };

    private sealed class Fixture(string connectionString, PostgreSqlContainer? postgres, string? sqlitePath) : IAsyncDisposable
    {
        public GraphProvider Provider { get; } = new();
        public FixtureCredentials Credentials { get; } = new();
        private readonly ISecretProtector protector = new FixtureProtector();
        public string ExpiredReceipt(string receipt)
        {
            var value = JsonNode.Parse(protector.Unprotect(receipt))!.AsObject();
            value["expiresAtUtc"] = JsonValue.Create(DateTimeOffset.UtcNow.AddMinutes(-1));
            return protector.Protect(value.ToJsonString());
        }
        public HelpdeskDbContext Context()
        {
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>();
            if (postgres is null) options.UseSqlite(connectionString);
            else options.UseNpgsql(connectionString);
            return new(options.Options, new Tenant(), new HttpContextAccessor());
        }
        public DataManagementService Service(HelpdeskDbContext db, IMemoryCache cache, IHttpClientFactory? configuredClients = null)
        {
            var clients = configuredClients ?? Substitute.For<IHttpClientFactory>();
            if (configuredClients is null)
                clients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(Provider, disposeHandler: false));
            return new(db, cache, protector, clients, NullLogger<DataManagementService>.Instance, Credentials);
        }
        public async Task SeedSettingsAsync(HelpdeskDbContext db)
        {
            db.TenantGraphDatasetSettings.Add(new()
            {
                OrganizationId = "org-graph", TenantId = "fixture-tenant", ClientId = "fixture-client",
                ClientSecretProtected = protector.Protect("fixture-secret"), EnableUsers = true,
                BackgroundSyncEnabled = true, BackgroundSyncCronExpression = "15 */4 * * *"
            });
            await db.SaveChangesAsync();
        }
        public static async Task<Fixture> CreateAsync(bool usePostgres)
        {
            PostgreSqlContainer? postgres = null;
            string? sqlitePath = null;
            string connection;
            if (usePostgres)
            {
                postgres = new PostgreSqlBuilder("postgres:16").Build();
                await postgres.StartAsync();
                connection = postgres.GetConnectionString();
            }
            else
            {
                sqlitePath = Path.Combine(Path.GetTempPath(), $"graph-workflow-{Guid.NewGuid():N}.db");
                connection = $"Data Source={sqlitePath};Pooling=False";
            }
            var fixture = new Fixture(connection, postgres, sqlitePath);
            await using var db = fixture.Context();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            Provider.Dispose();
            if (postgres is not null) await postgres.DisposeAsync();
            if (sqlitePath is not null)
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sqlitePath + suffix);
        }
    }

    private sealed class GraphProvider : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public Dictionary<string, HttpStatusCode> Failures { get; } = [];
        public bool FailOnImportsOnly { get; set; }
        public bool Empty { get; set; }
        public string? Payload { get; set; }
        public bool HoldImports { get; set; }
        public bool CancelImports { get; set; }
        public TaskCompletionSource ImportEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ImportRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            Assert.Equal("graph.microsoft.com", uri.Host);
            Assert.Equal("fixture-access-token", request.Headers.Authorization?.Parameter);
            if (CancelImports && !uri.Query.Contains("$top=1", StringComparison.Ordinal))
                throw new OperationCanceledException();
            if (HoldImports && !uri.Query.Contains("$top=1", StringComparison.Ordinal))
            {
                ImportEntered.TrySetResult();
                await ImportRelease.Task.WaitAsync(cancellationToken);
            }
            if (Failures.TryGetValue(uri.AbsolutePath, out var status) && (!FailOnImportsOnly || !uri.Query.Contains("$top=1", StringComparison.Ordinal)))
                return new HttpResponseMessage(status) { Content = new StringContent("provider-secret must stay private") };
            var second = uri.Query.Contains("skiptoken=second", StringComparison.Ordinal);
            var body = Payload ?? (Empty ? "{\"value\":[]}" : second ? "{\"value\":[{\"id\":\"second\",\"displayName\":\"Second\"}]}"
                : "{\"value\":[{\"id\":\"first\",\"displayName\":\"First\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users?$skiptoken=second\"}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class FixtureCredentials : IGraphDatasetCredentialFactory
    {
        public bool Reject { get; set; }
        public TokenCredential Create(string tenantId, string clientId, string clientSecret)
        {
            Assert.Equal("fixture-secret", clientSecret);
            return new FixtureToken(Reject);
        }
    }
    private sealed class FixtureToken(bool reject) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => reject ? throw new AuthenticationFailedException("provider-secret authentication failure") : new("fixture-access-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class FixtureProtector : ISecretProtector
    {
        private readonly IDataProtector protector = new EphemeralDataProtectionProvider().CreateProtector("graph-workflow-fixture");
        public string Protect(string plaintext) => protector.Protect(plaintext);
        public string Unprotect(string protectedValue) => protector.Unprotect(protectedValue);
    }
    private sealed class Tenant : ITenantContext
    {
        public string? TenantId => "org-graph";
        public string? UserId => "synthetic-operator";
        public bool IsHelpdeskAdmin => true;
    }
}
