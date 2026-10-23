using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Helpdesk.API.Background;
using Helpdesk.API.Endpoints.Resources;
using Helpdesk.Application.Resources;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Tests.Api;

public sealed class DatasetEndpointsAuthorizationTests
{
    [Fact]
    public async Task ClientAdmin_CanCreateAndListCustomDatasetsForOwnTenant()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("ClientAdmin");

        var create = await harness.Client.PostAsJsonAsync("/api/v1/resources/datasets", new CreateDatasetDefinitionDto
        {
            OrganizationId = "org-alpha",
            Name = "People",
            SourceType = DatasetSourceType.Custom,
            KeyColumn = "id",
            DisplayColumn = "name",
            Columns =
            [
                new DatasetColumnDto { Name = "id", IsKey = true },
                new DatasetColumnDto { Name = "name", IsDisplay = true }
            ]
        });
        var datasets = await harness.Client.GetFromJsonAsync<List<DatasetDefinitionDto>>("/api/v1/resources/datasets?organizationId=org-alpha");

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Contains(datasets!, x => x.Name == "People" && x.OrganizationId == "org-alpha");
    }

    [Fact]
    public async Task ClientAdmin_CannotAccessAnotherTenantDatasetsOrGraphSettings()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("ClientAdmin");

        var list = await harness.Client.GetAsync("/api/v1/resources/datasets?organizationId=org-other");
        var dataset = await harness.Client.GetAsync("/api/v1/resources/datasets/other-custom");
        var rows = await harness.Client.GetAsync("/api/v1/resources/datasets/other-custom/rows?page=1&pageSize=25");
        var graph = await harness.Client.PutAsJsonAsync("/api/v1/resources/datasets/graph-settings", new TenantGraphDatasetSettingsDto
        {
            OrganizationId = "org-other",
            EnableUsers = true
        });
        var sync = await harness.Client.PostAsync("/api/v1/resources/datasets/graph-settings/org-other/sync", null);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dataset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rows.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, graph.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, sync.StatusCode);
    }

    [Fact]
    public async Task ClientAdmin_CanSaveGraphSettingsAndTriggerBuiltInSyncForOwnTenant()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("ClientAdmin");

        var graph = await harness.Client.PutAsJsonAsync("/api/v1/resources/datasets/graph-settings", new TenantGraphDatasetSettingsDto
        {
            OrganizationId = "org-alpha",
            EnableUsers = true
        });
        var sync = await harness.Client.PostAsync("/api/v1/resources/datasets/graph-settings/org-alpha/sync", null);

        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
    }

    [Fact]
    public async Task ClientAdmin_CannotGenerateApiKeysForBuiltInDatasets()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("ClientAdmin");

        var createKey = await harness.Client.PostAsJsonAsync("/api/v1/resources/datasets/alpha-graph-users/credentials", new CreateDatasetIngestCredentialDto
        {
            Name = "bad key"
        });

        Assert.Equal(HttpStatusCode.Forbidden, createKey.StatusCode);
    }

    [Fact]
    public async Task HelpdeskAdmin_CanAccessAnyTenantDatasets()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("Admin");

        var list = await harness.Client.GetFromJsonAsync<List<DatasetDefinitionDto>>("/api/v1/resources/datasets?organizationId=org-other");
        var dataset = await harness.Client.GetFromJsonAsync<DatasetDefinitionDto>("/api/v1/resources/datasets/other-custom");

        Assert.Contains(list!, x => x.Id == "other-custom");
        Assert.Equal("org-other", dataset!.OrganizationId);
    }

    [Theory]
    [InlineData("ClientAdmin", "alpha-custom", HttpStatusCode.OK)]
    [InlineData("ClientAdmin", "other-custom", HttpStatusCode.Forbidden)]
    [InlineData("ClientAdmin", "alpha-graph-users", HttpStatusCode.Forbidden)]
    [InlineData("Admin", "other-custom", HttpStatusCode.OK)]
    [InlineData("Admin", "alpha-graph-users", HttpStatusCode.OK)]
    [InlineData("SelfService", "alpha-custom", HttpStatusCode.Forbidden)]
    public async Task OperatorRowUpdates_EnforceStoredDatasetScopeAndSource(string actor, string datasetId, HttpStatusCode expected)
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync(actor);
        var result = await harness.Client.PostAsJsonAsync($"/api/v1/resources/datasets/{datasetId}/rows", new UpsertDatasetRowsDto
        {
            Rows = [new UpsertDatasetRowDto { ExternalKey = "synthetic-row" }],
            DeleteMissing = false
        });

        Assert.Equal(expected, result.StatusCode);
        var writer = harness.Services.GetRequiredService<FakeDataManagementService>();
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, writer.OperatorWrites);
    }

    [Fact]
    public async Task Administrator_session_does_not_replace_the_ingestion_api_key_contract()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("Admin");
        var result = await harness.Client.PostAsJsonAsync("/api/v1/resources/datasets/alpha-custom/rows:upsert",
            new UpsertDatasetRowsDto { Rows = [new UpsertDatasetRowDto { ExternalKey = "synthetic-row" }] });

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, harness.Services.GetRequiredService<FakeDataManagementService>().OperatorWrites);
    }

    [Fact]
    public async Task ClientAdmin_CannotAccess_SelfService_Dataset_Options()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("ClientAdmin");

        var response = await harness.Client.GetAsync("/api/v1/self-service/datasets/alpha-custom/options?page=1&pageSize=25");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SelfServiceUser_CanAccess_Its_Tenant_Dataset_Options()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("SelfService");

        var response = await harness.Client.GetAsync("/api/v1/self-service/datasets/alpha-custom/options?page=1&pageSize=25");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MixedAssignments_DoNotCombineDatasetPermissionWithOtherTenantMembership()
    {
        using var harness = await DatasetEndpointsHarness.CreateAsync("Mixed");
        var own = await harness.Client.GetAsync("/api/v1/resources/datasets?organizationId=org-alpha");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        var read = await harness.Client.GetAsync("/api/v1/resources/datasets/other-custom");
        var list = await harness.Client.GetAsync("/api/v1/resources/datasets?organizationId=org-other");
        var delete = await harness.Client.DeleteAsync("/api/v1/resources/datasets/other-custom");
        var create = await harness.Client.PostAsJsonAsync("/api/v1/resources/datasets", new CreateDatasetDefinitionDto
        {
            OrganizationId = "org-other", Name = "Denied", SourceType = DatasetSourceType.Custom
        });
        var credential = await harness.Client.PostAsJsonAsync("/api/v1/resources/datasets/other-custom/credentials",
            new CreateDatasetIngestCredentialDto { Name = "Denied" });
        var graph = await harness.Client.PutAsJsonAsync("/api/v1/resources/datasets/graph-settings",
            new TenantGraphDatasetSettingsDto { OrganizationId = "org-other", EnableUsers = true });
        Assert.All(new[] { read, list, delete, create, credential, graph },
            response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    private sealed class DatasetEndpointsHarness : IDisposable
    {
        private readonly WebApplication _app;

        private DatasetEndpointsHarness(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }
        public IServiceProvider Services => _app.Services;

        public static async Task<DatasetEndpointsHarness> CreateAsync(string actor)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<FakeDataManagementService>();
            builder.Services.AddSingleton<IDataManagementService>(provider => provider.GetRequiredService<FakeDataManagementService>());
            builder.Services.AddSingleton<IOperatorDatasetWriter>(provider => provider.GetRequiredService<FakeDataManagementService>());
            builder.Services.AddSingleton<ICurrentUserAccessService, TestCurrentUserAccessService>();
            builder.Services.AddSingleton<IGraphDatasetSyncScheduleService, NoopGraphDatasetSyncScheduleService>();
            builder.Services.AddSingleton<ITenantContext>(new TestTenantContext("org-alpha", "self-service", false));
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("DataManagementAccess", policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.HelpdeskAdmin, HelpdeskPermissions.DataManagementAdmin);
                });
                options.AddPolicy(HelpdeskPermissions.SelfServiceUser, policy =>
                {
                    policy.AddAuthenticationSchemes("Test");
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.SelfServiceUser, HelpdeskPermissions.HelpdeskAdmin);
                });
            });

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapDatasetEndpoints();
            await app.StartAsync();

            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test", actor);
            return new DatasetEndpointsHarness(app, client);
        }

        public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("Authorization", out var header))
            {
                return Task.FromResult(AuthenticateResult.Fail("No authorization header"));
            }

            var actor = header.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "ClientAdmin";
            Claim[] claims = actor switch
            {
                "Admin" =>
                [
                    new Claim(ClaimTypes.NameIdentifier, "admin"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.HelpdeskAdmin),
                    new Claim("roles", HelpdeskPermissions.HelpdeskAdmin)
                ],
                "Mixed" =>
                [
                    new Claim(ClaimTypes.NameIdentifier, "mixed"),
                    new Claim("tenant_id", "org-alpha"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.DataManagementAdmin),
                    new Claim("allowed_organization_id", "org-other"),
                    new Claim("scoped_permission", $"{HelpdeskPermissions.DataManagementAdmin}|org-alpha"),
                    new Claim("scoped_permission", $"{HelpdeskPermissions.SelfServiceUser}|org-other")
                ],
                "SelfService" =>
                [
                    new Claim(ClaimTypes.NameIdentifier, "self-service"),
                    new Claim("tenant_id", "org-alpha"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.SelfServiceUser),
                    new Claim("roles", HelpdeskPermissions.SelfServiceUser)
                ],
                _ =>
                [
                    new Claim(ClaimTypes.NameIdentifier, "client-admin"),
                    new Claim("tenant_id", "org-alpha"),
                    new Claim(ClaimTypes.Role, HelpdeskPermissions.DataManagementAdmin),
                    new Claim("roles", HelpdeskPermissions.DataManagementAdmin),
                    new Claim("groups", AuthentikRbacGroups.ClientAdmin)
                ]
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TestCurrentUserAccessService : ICurrentUserAccessService
    {
        public Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
        {
            var roles = user.FindAll(ClaimTypes.Role)
                .Concat(user.FindAll("roles"))
                .Concat(user.FindAll("groups"))
                .Select(x => x.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var isAdmin = roles.Contains(HelpdeskPermissions.HelpdeskAdmin);
            var tenantId = user.FindFirst("tenant_id")?.Value;
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                allowed.Add(tenantId);
            }

            allowed.UnionWith(user.FindAll("allowed_organization_id").Select(claim => claim.Value));
            if (roles.Contains(AuthentikRbacGroups.ClientAdmin))
            {
                roles.Add(HelpdeskPermissions.DataManagementAdmin);
                roles.Add(HelpdeskRoleBundles.DataManagementAdmin);
            }

            return Task.FromResult(new CurrentUserAccessProfile(
                true,
                user.Identity?.Name,
                null,
                tenantId,
                null,
                null,
                isAdmin,
                roles,
                roles,
                allowed,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            {
                ScopedPermissionGrants = user.FindAll("scoped_permission")
                    .Select(claim => ScopedPermissionGrant.TryParse(claim.Value))
                    .OfType<ScopedPermissionGrant>().ToHashSet()
            });
        }
    }

    private sealed class NoopGraphDatasetSyncScheduleService : IGraphDatasetSyncScheduleService
    {
        public Task ReconcileAsync(string organizationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReconcileAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyDiagnosticsAsync(TenantGraphDatasetSettingsDto dto, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestTenantContext(string? tenantId, string? userId, bool isHelpdeskAdmin) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin { get; } = isHelpdeskAdmin;
    }

    private sealed class FakeDataManagementService : IDataManagementService, IOperatorDatasetWriter
    {
        public int OperatorWrites { get; private set; }

        private readonly Dictionary<string, DatasetDefinitionDto> _datasets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha-custom"] = Dataset("alpha-custom", "org-alpha", "Alpha Custom", DatasetSourceType.Custom),
            ["alpha-graph-users"] = Dataset("alpha-graph-users", "org-alpha", "Users", DatasetSourceType.GraphUsers),
            ["other-custom"] = Dataset("other-custom", "org-other", "Other Custom", DatasetSourceType.Custom)
        };

        private readonly Dictionary<string, TenantGraphDatasetSettingsDto> _settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["org-alpha"] = new() { OrganizationId = "org-alpha" },
            ["org-other"] = new() { OrganizationId = "org-other" }
        };

        public Task<IReadOnlyList<DatasetDefinitionDto>> ListDatasetsAsync(string organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatasetDefinitionDto>>(_datasets.Values.Where(x => x.OrganizationId == organizationId).ToList());

        public Task<DatasetDefinitionDto?> GetDatasetAsync(string datasetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_datasets.GetValueOrDefault(datasetId));

        public Task<DatasetDefinitionDto> CreateDatasetAsync(CreateDatasetDefinitionDto dto, CancellationToken cancellationToken = default)
        {
            var id = $"{dto.OrganizationId}-{Guid.NewGuid():N}";
            var dataset = Dataset(id, dto.OrganizationId, dto.Name, dto.SourceType);
            dataset.Columns = dto.Columns;
            _datasets[id] = dataset;
            return Task.FromResult(dataset);
        }

        public Task<DatasetDefinitionDto?> UpdateDatasetAsync(string datasetId, UpdateDatasetDefinitionDto dto, CancellationToken cancellationToken = default)
        {
            if (!_datasets.TryGetValue(datasetId, out var dataset))
            {
                return Task.FromResult<DatasetDefinitionDto?>(null);
            }

            dataset.Name = dto.Name ?? dataset.Name;
            dataset.SourceType = dto.SourceType ?? dataset.SourceType;
            return Task.FromResult<DatasetDefinitionDto?>(dataset);
        }

        public Task<bool> DeleteDatasetAsync(string datasetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_datasets.Remove(datasetId));

        public Task<PagedResult<DatasetRowDto>> GetRowsAsync(string datasetId, string? query, int page, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<DatasetRowDto> { Page = page, PageSize = pageSize, Total = 0 });

        public Task<PagedResult<DatasetOptionDto>> GetOptionsAsync(string datasetId, string organizationId, string? query, int page, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<DatasetOptionDto> { Page = page, PageSize = pageSize, Total = 0 });

        public Task<DatasetOptionDto?> GetOptionByKeyAsync(string datasetId, string organizationId, string key, CancellationToken cancellationToken = default) => Task.FromResult<DatasetOptionDto?>(null);

        public Task<DatasetIngestCredentialCreatedDto> CreateCredentialAsync(string datasetId, CreateDatasetIngestCredentialDto dto, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DatasetIngestCredentialCreatedDto
            {
                PlainTextKey = "plain-key",
                Credential = new DatasetIngestCredentialDto
                {
                    Id = "key-1",
                    DatasetId = datasetId,
                    Name = dto.Name,
                    IsActive = true,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                }
            });

        public Task<bool> RevokeCredentialAsync(string datasetId, string credentialId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<DatasetIngestCredentialDto>> ListCredentialsAsync(string datasetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatasetIngestCredentialDto>>([]);

        public Task<int> UpsertRowsAsync(string datasetId, string apiKey, UpsertDatasetRowsDto dto, CancellationToken cancellationToken = default) =>
            Task.FromResult(dto.Rows.Count);

        public Task<int> UpsertRowsAsOperatorAsync(string datasetId, string authorizedOrganizationId,
            bool allowBuiltInSources, UpsertDatasetRowsDto dto, CancellationToken cancellationToken = default)
        {
            var dataset = _datasets[datasetId];
            Assert.Equal(dataset.OrganizationId, authorizedOrganizationId);
            Assert.True(allowBuiltInSources || dataset.SourceType == DatasetSourceType.Custom);
            OperatorWrites++;
            return Task.FromResult(dto.Rows.Count);
        }

        public Task<TenantGraphDatasetSettingsDto?> GetGraphSettingsAsync(string organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings.GetValueOrDefault(organizationId));

        public Task<TenantGraphDatasetSettingsDto> UpsertGraphSettingsAsync(TenantGraphDatasetSettingsDto dto, CancellationToken cancellationToken = default)
        {
            _settings[dto.OrganizationId] = dto;
            return Task.FromResult(dto);
        }

        public Task<IReadOnlyList<DatasetDefinitionDto>> SyncBuiltInDatasetsAsync(string organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatasetDefinitionDto>>(_datasets.Values.Where(x => x.OrganizationId == organizationId && x.SourceType != DatasetSourceType.Custom).ToList());

        private static DatasetDefinitionDto Dataset(string id, string organizationId, string name, DatasetSourceType sourceType) => new()
        {
            Id = id,
            OrganizationId = organizationId,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            SourceType = sourceType,
            IsBuiltIn = sourceType != DatasetSourceType.Custom,
            IsActive = true,
            KeyColumn = "id",
            DisplayColumn = "name",
            Columns =
            [
                new DatasetColumnDto { Name = "id", IsKey = true },
                new DatasetColumnDto { Name = "name", IsDisplay = true }
            ]
        };
    }
}
