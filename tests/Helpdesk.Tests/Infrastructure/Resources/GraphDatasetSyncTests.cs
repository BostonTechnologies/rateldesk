using System.Net;
using System.Text;
using Azure.Core;
using Helpdesk.Application.Services.AI;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Helpdesk.Tests.Infrastructure.Resources;

public sealed class GraphDatasetSyncTests
{
    [Fact]
    public async Task AllSourcesUseSupportedSiteSearchAndPersistEveryPage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
        await using var db = new HelpdeskDbContext(options, new Tenant(), new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync();
        db.TenantGraphDatasetSettings.Add(new TenantGraphDatasetSettings
        {
            OrganizationId = "org-graph", TenantId = "fixture-tenant", ClientId = "fixture-client", ClientSecretProtected = "protected-fixture",
            EnableUsers = true, EnableDevices = true, EnableGroups = true, EnableSharePointSites = true
        });
        await db.SaveChangesAsync();
        var protector = Substitute.For<ISecretProtector>();
        protector.Unprotect("protected-fixture").Returns("fixture-secret");
        using var provider = new GraphProvider();
        using var client = new HttpClient(provider);
        var clients = Substitute.For<IHttpClientFactory>();
        clients.CreateClient(Arg.Any<string>()).Returns(client);
        var credentials = Substitute.For<IGraphDatasetCredentialFactory>();
        credentials.Create("fixture-tenant", "fixture-client", "fixture-secret").Returns(new FixtureToken());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new DataManagementService(db, cache, protector, clients, NullLogger<DataManagementService>.Instance, credentials);
        var datasets = await service.SyncBuiltInDatasetsAsync("org-graph");
        Assert.Equal(4, datasets.Count);
        var sites = Assert.Single(datasets, x => x.SourceType == DatasetSourceType.GraphSharePointSites);
        Assert.Equal(new[] { "site-1", "site-2" }, await db.DatasetRows.Where(x => x.DatasetId == sites.Id).OrderBy(x => x.ExternalKey).Select(x => x.ExternalKey).ToArrayAsync());
        Assert.Equal(5, await db.DatasetRows.CountAsync());
        Assert.Equal(4, await db.DatasetIngestCredentials.CountAsync());
        var settings = await db.TenantGraphDatasetSettings.SingleAsync();
        Assert.Equal("Succeeded", settings.LastSyncStatus);
        Assert.NotNull(settings.LastSharePointSitesSyncUtc);
        Assert.Equal(2, provider.Requests.Count(x => x.AbsolutePath == "/v1.0/sites"));
    }

    private sealed class GraphProvider : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;Requests.Add(uri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
            string body;
            if (uri.AbsolutePath == "/v1.0/sites")
            {
                if (!uri.Query.StartsWith("?search=*", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                body = uri.Query.Contains("skiptoken=second", StringComparison.Ordinal)
                    ? """{"value":[{"id":"site-2","displayName":"Second site","webUrl":"https://example.test/sites/second"}]}"""
                    : """{"value":[{"id":"site-1","displayName":"First site","webUrl":"https://example.test/sites/first"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/sites?search=*&$skiptoken=second"}""";
            }
            else
            {
                Assert.Contains(uri.AbsolutePath, new[] { "/v1.0/users", "/v1.0/devices", "/v1.0/groups" });
                body = """{"value":[{"id":"row-1","displayName":"Synthetic row","mail":"fixture@example.test","userPrincipalName":"fixture@example.test","operatingSystem":"Linux"}]}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FixtureToken : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("fixture-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Tenant : ITenantContext
    {
        public string? TenantId => "org-graph";
        public string? UserId => "fixture-operator";
        public bool IsHelpdeskAdmin => true;
    }
}
