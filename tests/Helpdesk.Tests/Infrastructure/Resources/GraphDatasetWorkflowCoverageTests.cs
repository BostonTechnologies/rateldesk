using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Resources;
using Helpdesk.Application.Services.AI;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Helpdesk.Tests.Infrastructure.Resources;

public sealed partial class GraphDatasetWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editing_typed_schema_preserves_identity_rows_and_named_credentials_and_rejects_invalid_changes(bool postgres)
    {
        await using var fixture = await Fixture.CreateAsync(postgres);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var original = await service.CreateDatasetAsync(new()
        {
            RequestId = Guid.NewGuid(), OrganizationId = "org-graph", Name = "Editable inventory",
            KeyColumn = "id", DisplayColumn = "name", SearchColumns = ["name"],
            Columns =
            [
                new() { Name = "id", DataType = DatasetColumnDataType.Text, IsKey = true },
                new() { Name = "name", DataType = DatasetColumnDataType.Text, IsDisplay = true },
                new() { Name = "amount", DataType = DatasetColumnDataType.Number },
                new() { Name = "enabled", DataType = DatasetColumnDataType.Boolean }
            ]
        });
        await service.UpsertRowsAsOperatorAsync(original.Id, "org-graph", false, new()
        {
            Rows = [new() { ExternalKey = "part-1", Data = new JsonObject
            {
                ["id"] = "part-1", ["name"] = "Original row", ["amount"] = 12.5m, ["enabled"] = true
            } }]
        });
        var key = await service.CreateCredentialAsync(original.Id, new() { Name = "Retained explicit pipeline" });
        var originalRows = JsonSerializer.Serialize(await db.DatasetRows.AsNoTracking().Where(row => row.DatasetId == original.Id).ToArrayAsync());
        var originalCredentials = JsonSerializer.Serialize(await db.DatasetIngestCredentials.AsNoTracking().Where(item => item.DatasetId == original.Id).ToArrayAsync());

        var metadata = await service.UpdateDatasetAsync(original.Id, new() { Name = "Edited inventory", IsActive = false });
        Assert.Equal(original.Id, metadata!.Id);
        Assert.False(metadata.IsActive);
        Assert.Equal(original.Columns.Select(column => column.Name), metadata.Columns.Select(column => column.Name));
        var edited = await service.UpdateDatasetAsync(original.Id, new()
        {
            SourceType = DatasetSourceType.Custom, IsActive = true, Columns = original.Columns,
            KeyColumn = "amount", DisplayColumn = "enabled", SearchColumns = ["amount", "enabled"]
        });
        Assert.Equal(original.Id, edited!.Id);
        Assert.Equal("Edited inventory", edited.Name);
        Assert.True(edited.IsActive);
        Assert.False(edited.IsBuiltIn);
        Assert.Equal("amount", Assert.Single(edited.Columns, column => column.IsKey).Name);
        Assert.Equal("enabled", Assert.Single(edited.Columns, column => column.IsDisplay).Name);
        Assert.Equal(new[] { "amount", "enabled" }, edited.SearchColumns);
        Assert.Equal("amount", (await service.GetDatasetAsync(original.Id))!.KeyColumn);
        var option = await service.GetOptionByKeyAsync(original.Id, "org-graph", "PART-1");
        Assert.Equal("part-1", option!.Key);
        Assert.Equal("true", option.Label);
        Assert.Equal("12.5", option.Preview);
        Assert.Equal(key.Credential.Id, Assert.Single(await service.ListCredentialsAsync(original.Id)).Id);
        Assert.Equal(originalRows, JsonSerializer.Serialize(await db.DatasetRows.AsNoTracking().Where(row => row.DatasetId == original.Id).ToArrayAsync()));
        Assert.Equal(originalCredentials, JsonSerializer.Serialize(await db.DatasetIngestCredentials.AsNoTracking().Where(item => item.DatasetId == original.Id).ToArrayAsync()));

        await service.CreateDatasetAsync(new() { OrganizationId = "org-graph", Name = "Another inventory" });
        var beforeRejections = await SnapshotAsync();
        (UpdateDatasetDefinitionDto Draft, string[] Errors)[] invalidChanges =
        [
            (new() { Columns = [] }, ["Columns"]),
            (new()
            {
                SourceType = (DatasetSourceType)999, KeyColumn = "missing", DisplayColumn = "missing", SearchColumns = ["unknown"],
                Columns =
                [
                    new() { Name = "amount", IsKey = true }, new() { Name = "AMOUNT", IsKey = true },
                    new() { Name = " ", DataType = (DatasetColumnDataType)999, IsDisplay = true },
                    new() { Name = "enabled", IsDisplay = true }
                ]
            }, ["SourceType", "Columns[1].Name", "Columns[2].Name", "Columns[2].DataType", "KeyColumn", "DisplayColumn", "SearchColumns"]),
            (new() { KeyColumn = "missing" }, ["KeyColumn"]),
            (new() { DisplayColumn = "missing" }, ["DisplayColumn"]),
            (new() { SearchColumns = ["unknown"] }, ["SearchColumns"]),
            (new() { Name = "Another inventory" }, ["Name"]),
            (new() { Name = " " }, ["Name"])
        ];
        foreach (var change in invalidChanges)
        {
            var error = await Assert.ThrowsAsync<DatasetDefinitionValidationException>(() => service.UpdateDatasetAsync(original.Id, change.Draft));
            Assert.All(change.Errors, field => Assert.Contains(field, error.Errors.Keys));
            // A later status/configuration save must not replay a rejected schema edit.
            await db.SaveChangesAsync();
            Assert.Equal(beforeRejections, await SnapshotAsync());
        }
        Assert.Null(await service.UpdateDatasetAsync("missing-dataset", new() { Name = "Missing" }));

        async Task<string> SnapshotAsync()
        {
            await using var readback = fixture.Context();
            return JsonSerializer.Serialize(new
            {
                Definition = await readback.DatasetDefinitions.AsNoTracking().SingleAsync(item => item.Id == original.Id),
                Columns = await readback.DatasetColumns.AsNoTracking().Where(item => item.DatasetId == original.Id).OrderBy(item => item.SortOrder).ThenBy(item => item.Name).ToArrayAsync(),
                Rows = await readback.DatasetRows.AsNoTracking().Where(item => item.DatasetId == original.Id).OrderBy(item => item.Id).ToArrayAsync(),
                Credentials = await readback.DatasetIngestCredentials.AsNoTracking().Where(item => item.DatasetId == original.Id).OrderBy(item => item.Id).ToArrayAsync()
            });
        }
    }

    [Theory]
    [InlineData("network", GraphAccessOutcome.Unavailable)]
    [InlineData("timeout", GraphAccessOutcome.TimedOut)]
    [InlineData("circuit", GraphAccessOutcome.Unavailable)]
    public async Task Source_transport_exceptions_have_safe_typed_results_without_writes(string failure, GraphAccessOutcome outcome)
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        fixture.Provider.ReadException = GraphTransportFailure(failure);
        var result = await fixture.Service(db, cache).TestGraphConnectionAsync(Draft());
        Assert.True(result.AuthenticationSucceeded);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(outcome, Assert.Single(result.Sources).Outcome);
        Assert.Null(result.TestToken);
        Assert.DoesNotContain("provider-secret", JsonSerializer.Serialize(result));
        Assert.Single(fixture.Provider.Requests);
        await AssertNoGraphWritesAsync(db);
    }

    [Theory]
    [InlineData("network", GraphAccessOutcome.Unavailable)]
    [InlineData("timeout", GraphAccessOutcome.TimedOut)]
    [InlineData("circuit", GraphAccessOutcome.Unavailable)]
    [InlineData("canceled", GraphAccessOutcome.TimedOut)]
    public async Task Credential_transport_exceptions_have_safe_typed_results_without_writes(string failure, GraphAccessOutcome outcome)
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        fixture.Credentials.Failure = GraphTransportFailure(failure);
        var result = await fixture.Service(db, cache).TestGraphConnectionAsync(Draft());
        Assert.False(result.AuthenticationSucceeded);
        Assert.Equal(outcome, result.Outcome);
        Assert.Empty(result.Sources);
        Assert.Null(result.TestToken);
        Assert.DoesNotContain("provider-secret", JsonSerializer.Serialize(result));
        Assert.Empty(fixture.Provider.Requests);
        await AssertNoGraphWritesAsync(db);
    }

    [Fact]
    public async Task Stored_secret_reentry_is_actionable_without_changing_the_saved_connection()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await fixture.SeedSettingsAsync(db);
        var before = JsonSerializer.Serialize(await db.TenantGraphDatasetSettings.AsNoTracking().SingleAsync());
        var protector = Substitute.For<ISecretProtector>();
        protector.Unprotect(Arg.Any<string>()).Returns(_ => throw new SecretReentryRequiredException("provider-secret keyring failure"));
        var result = await fixture.Service(db, cache, secretProtector: protector).TestGraphConnectionAsync(Draft(storedSecret: true));
        Assert.False(result.AuthenticationSucceeded);
        Assert.Equal(GraphAccessOutcome.SecretReentryRequired, result.Outcome);
        Assert.Contains("Re-enter", result.Message);
        Assert.Null(result.TestToken);
        Assert.DoesNotContain("provider-secret", JsonSerializer.Serialize(result));
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(before, JsonSerializer.Serialize(await db.TenantGraphDatasetSettings.AsNoTracking().SingleAsync()));
        Assert.Equal(0, await db.DatasetDefinitions.CountAsync());
        Assert.Equal(0, await db.DatasetRows.CountAsync());
        Assert.Equal(0, await db.DatasetIngestCredentials.CountAsync());
        Assert.Equal(0, await db.GraphDatasetImportRuns.CountAsync());
    }

    [Fact]
    public async Task Invalid_connection_or_absent_stored_secret_requires_correction_without_network_or_writes()
    {
        await using var fixture = await Fixture.CreateAsync(false);
        await using var db = fixture.Context();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = fixture.Service(db, cache);
        var missingTenant = Draft();
        missingTenant.TenantId = " ";
        var invalid = await service.TestGraphConnectionAsync(missingTenant);
        var missingStored = await service.TestGraphConnectionAsync(Draft(storedSecret: true));
        Assert.All(new[] { invalid, missingStored }, result =>
        {
            Assert.Equal(GraphAccessOutcome.InvalidConfiguration, result.Outcome);
            Assert.False(result.AuthenticationSucceeded);
            Assert.Null(result.TestToken);
            Assert.Empty(result.Sources);
        });
        Assert.Empty(fixture.Provider.Requests);
        await AssertNoGraphWritesAsync(db);
    }

    private static Exception GraphTransportFailure(string kind) => kind switch
    {
        "network" => new HttpRequestException("provider-secret network detail"),
        "timeout" => new TimeoutRejectedException("provider-secret timeout detail"),
        "circuit" => new BrokenCircuitException("provider-secret circuit detail"),
        "canceled" => new OperationCanceledException("provider-secret cancellation detail"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task AssertNoGraphWritesAsync(HelpdeskDbContext db)
    {
        Assert.Equal(0, await db.TenantGraphDatasetSettings.CountAsync());
        Assert.Equal(0, await db.DatasetDefinitions.CountAsync());
        Assert.Equal(0, await db.DatasetRows.CountAsync());
        Assert.Equal(0, await db.DatasetIngestCredentials.CountAsync());
        Assert.Equal(0, await db.GraphDatasetImportRuns.CountAsync());
    }
}
