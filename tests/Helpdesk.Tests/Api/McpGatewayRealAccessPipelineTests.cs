using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Tools;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Incidents;
using Helpdesk.API.Endpoints.Integrations;
using Helpdesk.API.Endpoints.Requests;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.API.Endpoints.Organization;
using Helpdesk.API.Middleware;
using Helpdesk.API.Services;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.Sla;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.PostgreSql;
using Json.Schema;

namespace Helpdesk.Tests.Api;

/// <summary>
/// Exercises the persisted credential-to-execution-token path against the
/// self-service and incident endpoints. This deliberately does not replace the component
/// tests for the HTTP MCP host: it proves that the API authority reloads the
/// owner, scoped assignments, and source credential for every request.
/// </summary>
public sealed partial class McpGatewayRealAccessPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_scoped_role_crud_from_discovery_executes_with_owned_readbacks_and_preserves_delegation_ceiling(bool postgreSql)
    {
        await using var harness = postgreSql
            ? await GatewayAccessHarness.CreatePostgreSqlAsync()
            : await GatewayAccessHarness.CreateAsync();
        await harness.SeedScopedRoleAdministrationAsync();
        var credential = await harness.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.TenantRolesAssign);
        var execution = await harness.DelegateAsync(credential);
        var access = await harness.GetAsync<Helpdesk.Shared.DTOs.Auth.CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.False(access.Body!.IsHelpdeskAdmin);
        Assert.Equal([HelpdeskPermissions.TenantRolesAssign], access.Body.Permissions);
        Assert.Equal(["org-a"], access.Body.AllowedOrganizationIds);
        var tools = new HelpdeskTools(new HelpdeskAgentClient(AgentClientConfiguration.Empty with
        {
            ApiBaseUrl = "https://gateway-api.example.test", CredentialMode = "gateway"
        }, new GatewayClientFactory(harness), new GatewayLifetime(), new GatewayToken(execution)),
            new AgentClientConfigurationStore(Path.Combine("work", "beta4", "unused-role-profile.json")));
        var definitions = HelpdeskToolDefinitions.Create().ToDictionary(tool => tool.ProtocolTool.Name);
        var mutationSchema = JsonSchema.FromText(definitions["helpdesk_admin_mutations"].ProtocolTool.InputSchema.GetRawText());
        var readSchema = JsonSchema.FromText(definitions["helpdesk_roles"].ProtocolTool.InputSchema.GetRawText());
        var create = new JsonObject
        {
            ["operation"] = "create_role", ["confirm"] = false,
            ["request"] = new JsonObject
            {
                ["name"] = "MCP owned incident reader", ["ownerOrganizationId"] = "org-a",
                ["permissions"] = new JsonArray(HelpdeskPermissions.IncidentRead)
            }
        };
        Assert.True(mutationSchema.Evaluate(create).IsValid);
        var missingOwner = create.DeepClone().AsObject();
        missingOwner["request"]!.AsObject().Remove("ownerOrganizationId");
        Assert.False(mutationSchema.Evaluate(missingOwner).IsValid);
        var missingDependency = create.DeepClone().AsObject();
        missingDependency["request"]!["permissions"] = new JsonArray(HelpdeskPermissions.IncidentWrite);
        Assert.False(mutationSchema.Evaluate(missingDependency).IsValid);
        async Task<HelpdeskToolResponse> MutateAsync(JsonObject envelope)
        {
            Assert.True(mutationSchema.Evaluate(envelope).IsValid, envelope.ToJsonString());
            return await tools.helpdesk_admin_mutations(envelope["operation"]!.GetValue<string>(),
                JsonSerializer.SerializeToElement(envelope["request"]), envelope["confirm"]!.GetValue<bool>());
        }
        var preview = await MutateAsync(create);
        Assert.Equal("confirmation_required", preview.Status);
        Assert.Empty(await harness.OwnedRoleAuditsAsync());
        await harness.AssertNoOwnedCustomRolesAsync();
        create["confirm"] = true;
        var created = await MutateAsync(create);
        Assert.True(created.Success, created.Summary);
        var roleId = created.Data!["id"]!.GetValue<string>();
        await harness.AssertOwnedRoleAsync(roleId, "MCP owned incident reader", [HelpdeskPermissions.IncidentRead]);
        var read = new JsonObject { ["operation"] = "get", ["request"] = new JsonObject { ["roleId"] = roleId } };
        Assert.True(readSchema.Evaluate(read).IsValid);
        var fetched = await tools.helpdesk_roles("get", JsonSerializer.SerializeToElement(read["request"]));
        Assert.True(fetched.Success, fetched.Summary);
        Assert.Equal("org-a", fetched.Data!["ownerOrganizationId"]!.GetValue<string>());
        Assert.Equal("MCP owned incident reader", fetched.Data["name"]!.GetValue<string>());
        var update = new JsonObject
        {
            ["operation"] = "update_role", ["confirm"] = true,
            ["request"] = new JsonObject { ["roleId"] = roleId, ["name"] = "MCP owned incident writer", ["permissions"] = new JsonArray(HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite) }
        };
        var updated = await MutateAsync(update);
        Assert.True(updated.Success, updated.Summary);
        await harness.AssertOwnedRoleAsync(roleId, "MCP owned incident writer", [HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite]);
        var beforeDenials = await harness.OwnedRoleAuditsAsync();
        var foreignRead = await tools.helpdesk_roles("get", JsonSerializer.SerializeToElement(new { roleId = "foreign-role" }));
        Assert.False(foreignRead.Success);
        Assert.Equal(403, foreignRead.Failure!.UpstreamStatus);
        foreach (var deniedArguments in new[]
        {
            new JsonObject { ["operation"] = "create_role", ["confirm"] = true, ["request"] = new JsonObject { ["name"] = "Foreign create", ["ownerOrganizationId"] = "org-b", ["permissions"] = new JsonArray(HelpdeskPermissions.IncidentRead) } },
            new JsonObject { ["operation"] = "update_role", ["confirm"] = true, ["request"] = new JsonObject { ["roleId"] = "foreign-role", ["name"] = "Foreign update", ["permissions"] = new JsonArray(HelpdeskPermissions.IncidentRead) } },
            new JsonObject { ["operation"] = "delete_role", ["confirm"] = true, ["request"] = new JsonObject { ["roleId"] = "foreign-role" } }
        })
        {
            var denied = await MutateAsync(deniedArguments);
            Assert.False(denied.Success);
            Assert.Equal(403, denied.Failure!.UpstreamStatus);
        }
        var ceiling = new JsonObject { ["operation"] = "create_role", ["confirm"] = true, ["request"] = new JsonObject { ["name"] = "Disallowed administration", ["ownerOrganizationId"] = "org-a", ["permissions"] = new JsonArray(HelpdeskPermissions.TenantUsersManage) } };
        var excessive = await MutateAsync(ceiling);
        Assert.False(excessive.Success);
        Assert.Equal(400, excessive.Failure!.UpstreamStatus);
        Assert.Equal(beforeDenials, await harness.OwnedRoleAuditsAsync());
        var delete = new JsonObject { ["operation"] = "delete_role", ["confirm"] = true, ["request"] = new JsonObject { ["roleId"] = roleId } };
        Assert.True((await MutateAsync(delete)).Success);
        using var absent = await harness.GetAsync(execution, $"/api/v1/admin/role-definitions/{roleId}");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        await harness.AssertNoOwnedCustomRolesAsync();
        var audits = await harness.OwnedRoleAuditsAsync();
        Assert.Equal(3, audits.Count);
        Assert.Contains(audits, audit => audit.StartsWith("Created custom role", StringComparison.Ordinal));
        Assert.Contains(audits, audit => audit.StartsWith("Updated custom role", StringComparison.Ordinal));
        Assert.Contains(audits, audit => audit.StartsWith("Deleted custom role", StringComparison.Ordinal));
        await harness.AssertForeignRoleUnchangedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_gateway_execution_tokens_never_gain_instance_admin_authority_for_real_administrative_routes(bool postgreSql)
    {
        await using var harness = postgreSql
            ? await GatewayAccessHarness.CreatePostgreSqlAsync()
            : await GatewayAccessHarness.CreateAsync();
        await harness.SetOwnerAdministratorAsync();
        var credential = await harness.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var execution = await harness.DelegateAsync(credential);
        var access = await harness.GetAsync<Helpdesk.Shared.DTOs.Auth.CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        Assert.False(access.Body!.IsHelpdeskAdmin);
        Assert.Equal([HelpdeskPermissions.IncidentRead], access.Body.Permissions);
        Assert.Equal(["org-a"], access.Body.AllowedOrganizationIds);
        foreach (var (method, path, body) in new[]
        {
            (HttpMethod.Get, "/api/v1/admin/orchestration/", (object?)null),
            (HttpMethod.Post, "/api/v1/admin/orchestration/test", null),
            (HttpMethod.Get, "/api/v1/tenants/17/branding", null),
            (HttpMethod.Put, "/api/v1/tenants/17/branding", new { BrandName = "Denied" })
        })
        {
            using var denied = await harness.SendAsync(execution, method, path, body);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incident_search_filters_authorized_records_before_count_and_paging_on_each_provider(bool postgreSql)
    {
        await using var harness = postgreSql
            ? await GatewayAccessHarness.CreatePostgreSqlAsync(unscopedData: true)
            : await GatewayAccessHarness.CreateAsync(unscopedData: true);
        await harness.SeedIncidentSearchAsync();
        var credential = await harness.CreateCredentialAsync("org-a", permissions: HelpdeskPermissions.IncidentRead);
        var execution = await harness.DelegateAsync(credential);
        var access = await harness.GetAsync<Helpdesk.Shared.DTOs.Auth.CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.Equal([HelpdeskPermissions.IncidentRead], access.Body!.Permissions);
        Assert.Equal(["org-a"], access.Body.AllowedOrganizationIds);

        async Task<PagedResponse<IncidentDto>> ListAsync(string query)
        {
            var response = await harness.GetAsync<PagedResponse<IncidentDto>>(
                execution, "/api/v1/incidents/" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Assert.IsType<PagedResponse<IncidentDto>>(response.Body);
        }

        var baseline = await ListAsync("?includeTotal=true");
        Assert.Equal(3, baseline.TotalCount);
        Assert.Equal(["incident-alpha", "incident-beta", "incident-other"], baseline.Items.Select(item => item.Id).Order());
        var blank = await ListAsync("?q=%20%20&includeTotal=true");
        Assert.Equal(3, blank.TotalCount);
        Assert.Equal(baseline.Items.Select(item => item.Id), blank.Items.Select(item => item.Id));

        var match = await ListAsync("?q=%20nEeDlE%20&includeTotal=true");
        Assert.Equal(2, match.TotalCount);
        Assert.Equal(["incident-alpha", "incident-beta"], match.Items.Select(item => item.Id).Order());
        var noMatch = await ListAsync("?q=impossible-incident-search-193&includeTotal=true");
        Assert.Equal(0, noMatch.TotalCount);
        Assert.Empty(noMatch.Items);

        var firstPage = await ListAsync("?q=needle&page=1&pageSize=1&includeTotal=true");
        var secondPage = await ListAsync("?q=needle&page=2&pageSize=1&includeTotal=true");
        Assert.Equal(2, firstPage.TotalCount);
        Assert.Equal(2, secondPage.TotalCount);
        Assert.Equal(1, firstPage.Page);
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(1, firstPage.PageSize);
        Assert.Equal("incident-beta", Assert.Single(firstPage.Items).Id);
        Assert.Equal("incident-alpha", Assert.Single(secondPage.Items).Id);
        var withoutTotal = await ListAsync("?q=needle&page=1&pageSize=1&includeTotal=false");
        Assert.Equal(1, withoutTotal.TotalCount);
        Assert.Equal("incident-beta", Assert.Single(withoutTotal.Items).Id);

        var literal = await ListAsync("?q=" + Uri.EscapeDataString(GatewayAccessHarness.LiteralIncidentSearch));
        Assert.Equal(1, literal.TotalCount);
        Assert.Equal("incident-alpha", Assert.Single(literal.Items).Id);
        var historic = await ListAsync("?q=needle&historicOnly=true");
        Assert.Equal(1, historic.TotalCount);
        Assert.Equal("incident-resolved", Assert.Single(historic.Items).Id);
        var explicitState = await ListAsync("?q=needle&state=Resolved");
        Assert.Equal("incident-resolved", Assert.Single(explicitState.Items).Id);
        var foreignOrganization = await ListAsync("?q=needle&organizationId=org-b");
        Assert.Equal(0, foreignOrganization.TotalCount);
        Assert.Empty(foreignOrganization.Items);
        using var foreignDetail = await harness.GetAsync(execution, "/api/v1/incidents/incident-foreign");
        Assert.Equal(HttpStatusCode.Forbidden, foreignDetail.StatusCode);
        using var requestDenied = await harness.GetAsync(execution, "/api/v1/requests/");
        Assert.Equal(HttpStatusCode.Forbidden, requestDenied.StatusCode);
        var foreignCredential = await harness.CreateCredentialAsync("org-b", permissions: HelpdeskPermissions.IncidentRead);
        var foreignExecution = await harness.DelegateAsync(foreignCredential);
        var foreignMatch = await harness.GetAsync<PagedResponse<IncidentDto>>(
            foreignExecution, "/api/v1/incidents/?q=needle");
        Assert.Equal(HttpStatusCode.OK, foreignMatch.StatusCode);
        Assert.Equal(1, foreignMatch.Body!.TotalCount);
        Assert.Equal("incident-foreign", Assert.Single(foreignMatch.Body.Items).Id);
    }

    [Fact]
    public async Task Same_owner_credentials_are_isolated_by_organization_through_execution_tokens()
    {
        await using var harness = await GatewayAccessHarness.CreateAsync();
        await AssertOrganizationScopeAsync(harness);
    }

    [Fact]
    public async Task Same_owner_credentials_are_isolated_by_organization_through_execution_tokens_on_postgresql()
    {
        await using var harness = await GatewayAccessHarness.CreatePostgreSqlAsync();
        await AssertOrganizationScopeAsync(harness);
        await AssertVerifierScopeAsync(harness);
    }

    [Fact]
    public async Task Setup_verifier_projects_only_its_authorized_organization_and_no_business_grants()
    {
        await using var harness = await GatewayAccessHarness.CreateAsync();
        await AssertVerifierScopeAsync(harness);
    }

    private static async Task AssertVerifierScopeAsync(GatewayAccessHarness harness)
    {
        var verifier = await harness.CreateCredentialAsync("org-b", "mcp-setup");
        var execution = await harness.DelegateAsync(verifier);
        var status = await harness.GetAsync<Helpdesk.Shared.DTOs.Auth.CurrentUserAccessDto>(execution, "/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("org-b", status.Body!.PrimaryOrganizationId);
        Assert.Equal(["org-b"], status.Body.AllowedOrganizationIds);
        Assert.Empty(status.Body.Permissions); Assert.False(status.Body.IsHelpdeskAdmin);
        using var denied = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await harness.RemoveRoleAsync("org-b");
        using var lostScopeStatus = await harness.GetAsync(execution, "/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, lostScopeStatus.StatusCode);
        await harness.AssertDelegationStatusAsync(verifier, GatewayAccessHarness.ResourceUri, HttpStatusCode.Forbidden);
    }

    private static async Task AssertOrganizationScopeAsync(GatewayAccessHarness harness)
    {
        var credentialA = await harness.CreateCredentialAsync("org-a");
        var credentialB = await harness.CreateCredentialAsync("org-b");

        var executionB = await harness.DelegateAsync(credentialB);
        var deniedList = await harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executionB, "/api/v1/self-service/requests");
        var deniedDetail = await harness.GetAsync(executionB, "/api/v1/self-service/requests/request-a");
        var deniedTasks = await harness.GetAsync(executionB, "/api/v1/self-service/requests/request-a/tasks");

        Assert.Equal(HttpStatusCode.OK, deniedList.StatusCode);
        Assert.NotNull(deniedList.Body);
        Assert.Equal(0, deniedList.Body!.TotalCount);
        Assert.Empty(deniedList.Body.Items);
        Assert.Equal(HttpStatusCode.NotFound, deniedDetail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deniedTasks.StatusCode);

        var executionA = await harness.DelegateAsync(credentialA);
        var allowedList = await harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executionA, "/api/v1/self-service/requests");

        Assert.Equal(HttpStatusCode.OK, allowedList.StatusCode);
        Assert.NotNull(allowedList.Body);
        Assert.Equal(1, allowedList.Body!.TotalCount);
        Assert.Equal("request-a", Assert.Single(allowedList.Body.Items).Id);
    }

    [Fact]
    public async Task Execution_token_rechecks_resource_role_revocation_and_account_state()
    {
        await using var harness = await GatewayAccessHarness.CreateAsync();
        var credential = await harness.CreateCredentialAsync("org-b");

        await harness.AssertDelegationStatusAsync(credential, "https://wrong.example/mcp", HttpStatusCode.Forbidden);
        var execution = await harness.DelegateAsync(credential);

        await harness.RemoveRoleAsync("org-b");
        var removedRole = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Forbidden, removedRole.StatusCode);

        await harness.RestoreRoleAsync("org-b");
        await harness.SetCredentialRevokedAsync(credential.Id);
        var revoked = await harness.GetAsync(execution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        var enabledCredential = await harness.CreateCredentialAsync("org-b");
        var enabledExecution = await harness.DelegateAsync(enabledCredential);
        await harness.SetOwnerEnabledAsync(false);
        var disabledOwner = await harness.GetAsync(enabledExecution, "/api/v1/self-service/requests");
        Assert.Equal(HttpStatusCode.Unauthorized, disabledOwner.StatusCode);
    }

    [Fact]
    public async Task Simultaneous_gateway_credentials_do_not_share_owner_scope_or_cached_authorization()
    {
        await using var harness = await GatewayAccessHarness.CreatePostgreSqlAsync();
        var credentialA = await harness.CreateCredentialAsync("org-a");
        var credentialB = await harness.CreateCredentialAsync("org-b");

        var executions = await Task.WhenAll(harness.DelegateAsync(credentialA), harness.DelegateAsync(credentialB));
        var reads = await Task.WhenAll(
            harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executions[0], "/api/v1/self-service/requests"),
            harness.GetAsync<PagedResponse<MyRequestListItemDto>>(executions[1], "/api/v1/self-service/requests"));

        Assert.Equal(HttpStatusCode.OK, reads[0].StatusCode);
        Assert.Equal("request-a", Assert.Single(reads[0].Body!.Items).Id);
        Assert.Equal(HttpStatusCode.OK, reads[1].StatusCode);
        Assert.Empty(reads[1].Body!.Items);

        var apiPurposeCredential = await harness.CreateCredentialAsync("org-a", purpose: "api");
        await harness.AssertDelegationStatusAsync(apiPurposeCredential, GatewayAccessHarness.ResourceUri, HttpStatusCode.Unauthorized);
    }

    internal sealed class GatewayAccessHarness(WebApplication application, IAsyncDisposable database, ConcurrentQueue<string> incidentListQueries) : IAsyncDisposable
    {
        public const string ResourceUri = "https://helpdesk.example/mcp";
        public const string LiteralIncidentSearch = @"Café & A+B 50%_\?";

        public HttpClient Client { get; } = application.GetTestClient();
        public ConcurrentQueue<string> IncidentListQueries { get; } = incidentListQueries;

        public static async Task<GatewayAccessHarness> CreateAsync(bool unscopedData = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            return await CreateAsync(options => options.UseSqlite(connection), connection, unscopedData);
        }

        public static async Task<GatewayAccessHarness> CreatePostgreSqlAsync(bool unscopedData = false)
        {
            var postgres = new PostgreSqlBuilder("postgres:16").Build();
            await postgres.StartAsync();
            try
            {
                return await CreateAsync(options => options.UseNpgsql(postgres.GetConnectionString()), postgres, unscopedData);
            }
            catch
            {
                await postgres.DisposeAsync();
                throw;
            }
        }

        private static async Task<GatewayAccessHarness> CreateAsync(Action<DbContextOptionsBuilder> configureDatabase, IAsyncDisposable database, bool unscopedData)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddDataProtection();
            builder.Services.AddDbContext<HelpdeskDbContext>(configureDatabase);
            builder.Services.AddDbContext<RatelDeskIdentityDbContext>(configureDatabase);
            // Incident search acceptance deliberately exposes both organizations to SQL;
            // the persisted account and credential intersection must enforce the boundary.
            builder.Services.AddScoped<ITenantContext>(_ => new TestTenantContext(unscopedData ? null : "org-a", "owner"));
            builder.Services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
            builder.Services.AddScoped<IIntegrationCredentialOwnerResolver, IntegrationCredentialOwnerResolver>();
            builder.Services.AddScoped<IAuthorizationHandler, ServiceClientManagementHandler>();
            builder.Services.AddScoped<IAuthorizationHandler, IntegrationCredentialManagementSessionHandler>();
            builder.Services.AddScoped<IAuthorizationHandler, IntegrationCredentialOwnerManagementHandler>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<ISelfServiceAudienceService, SelfServiceAudienceService>();
            builder.Services.AddScoped<IRepository<Incident>, EfRepository<Incident>>();
            builder.Services.AddSingleton(Substitute.For<IImageLinkSigner>());
            builder.Services.AddSingleton(Substitute.For<ITicketSlaService>());
            var slaRepository = Substitute.For<ITicketSlaRepository>();
            slaRepository.GetByTicketIdForUpdateAsync(Arg.Any<string>()).Returns(Task.FromResult<TicketSlaState?>(null));
            builder.Services.AddSingleton(slaRepository);
            builder.Services.AddSingleton(Substitute.For<ISlaClockService>());
            builder.Services.AddSingleton(Substitute.For<ISlaEscalationEvaluator>());
            builder.Services.AddSingleton<IDomainEventPublisher, NoOpDomainEventPublisher>();
            builder.Services.AddSingleton(Substitute.For<IOrchestrationConnectivityService>());
            builder.Services.AddSingleton(Substitute.For<IOrchestrationCatalogService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingSchemaSyncService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingDriftService>());
            builder.Services.AddSingleton(Substitute.For<IAutomationBindingImportService>());
            builder.Services.AddScoped<ICorrelationContext, TestCorrelationContext>();
            builder.Services.AddSingleton<McpExecutionTokenService>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = McpExecutionAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = McpExecutionAuthenticationHandler.SchemeName;
            })
            .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
                IntegrationCredentialAuthenticationHandler.SchemeName,
                options => options.Purpose = IntegrationCredentialAuthenticationHandler.ApiPurpose)
            .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(
                IntegrationCredentialAuthenticationHandler.McpSchemeName,
                options => options.Purpose = IntegrationCredentialAuthenticationHandler.McpPurpose)
            .AddScheme<AuthenticationSchemeOptions, McpExecutionAuthenticationHandler>(
                McpExecutionAuthenticationHandler.SchemeName,
                _ => { });
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAuthenticatedUser().RequireRole(HelpdeskPermissions.HelpdeskAdmin));
                options.AddPolicy(ServiceIdentityServiceCollectionExtensions.ManagementPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new ServiceClientManagementRequirement()));
                options.AddPolicy(IntegrationCredentialEndpoints.CredentialManagementPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new IntegrationCredentialManagementSessionRequirement()));
                options.AddPolicy(IntegrationCredentialEndpoints.OwnerManagementPolicy, policy => policy.RequireAuthenticatedUser().AddRequirements(new IntegrationCredentialOwnerManagementRequirement()));
                options.AddPolicy(IntegrationCredentialEndpoints.SelfRevocationPolicy, policy =>
                {
                    policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.SchemeName, McpExecutionAuthenticationHandler.SchemeName);
                    policy.RequireAuthenticatedUser();
                });
                options.AddPolicy(McpGatewayDelegationEndpoints.DelegationPolicy, policy =>
                {
                    policy.AddAuthenticationSchemes(IntegrationCredentialAuthenticationHandler.McpSchemeName);
                    policy.RequireAuthenticatedUser();
                });
                options.AddPolicy("SelfService.User", policy =>
                {
                    policy.AddAuthenticationSchemes(McpExecutionAuthenticationHandler.SchemeName);
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(HelpdeskPermissions.SelfServiceUser, HelpdeskPermissions.HelpdeskAdmin);
                });
                options.AddPolicy("IncidentAccess", policy => policy.RequireAuthenticatedUser().RequireRole(
                    HelpdeskPermissions.IncidentUser, HelpdeskPermissions.IncidentManager, HelpdeskPermissions.IncidentRead,
                    HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentDelete, HelpdeskPermissions.HelpdeskAdmin));
                options.AddPolicy("IncidentManager", policy => policy.RequireAuthenticatedUser().RequireRole(
                    HelpdeskPermissions.IncidentManager, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.HelpdeskAdmin));
                options.AddPolicy("RequestAccess", policy => policy.RequireAuthenticatedUser().RequireRole(
                    HelpdeskPermissions.RequestUser, HelpdeskPermissions.RequestManager, HelpdeskPermissions.RequestRead,
                    HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestDelete, HelpdeskPermissions.HelpdeskAdmin));
                options.AddPolicy("RequestManager", policy => policy.RequireAuthenticatedUser().RequireRole(
                    HelpdeskPermissions.RequestManager, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.HelpdeskAdmin));
            });

            var app = builder.Build();
            var incidentListQueries = new ConcurrentQueue<string>();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.Value?.TrimEnd('/') == "/api/v1/incidents")
                    incidentListQueries.Enqueue(context.Request.QueryString.Value ?? "");
                await next();
            });
            app.UseAuthentication();
            app.UseMiddleware<SetupVerificationExecutionBoundaryMiddleware>();
            app.UseMiddleware<UserAccessClaimsMiddleware>();
            app.UseAuthorization();
            app.MapMcpGatewayDelegationEndpoints();
            app.MapIntegrationCredentialEndpoints();
            app.MapIncidentReceiverSourceEndpoints();
            app.MapCurrentUserAccessEndpoint();
            app.MapSelfServiceMyRequestsEndpoints();
            app.MapIncidentEndpoints();
            app.MapRequestEndpoints();
            app.MapExternalOrchestrationEndpoints();
            app.MapTenantBrandingEndpoints();
            app.MapRoleDefinitionEndpoints();
            app.MapGet("/test/administration-owner", async (System.Security.Claims.ClaimsPrincipal user,
                IIntegrationCredentialOwnerResolver owners, RatelDeskIdentityDbContext identity, CancellationToken ct) =>
                await AdministrationOwnerResolver.ResolveAsync(user, owners, identity, ct))
                .RequireAuthorization(ServiceIdentityServiceCollectionExtensions.ManagementPolicy);
            app.MapGet("/test/credential-management", () => "interactive credential authority")
                .RequireAuthorization(IntegrationCredentialEndpoints.CredentialManagementPolicy);

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                await identity.Database.EnsureCreatedAsync();
                identity.Users.Add(new ApplicationUser
                {
                    Id = "owner",
                    UserName = "owner@example.test",
                    Email = "owner@example.test",
                    DisplayName = "Owner",
                    IsEnabled = true
                });
                await identity.SaveChangesAsync();

                var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                await domain.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
                domain.Organizations.AddRange(
                    new Organization { Id = "org-a", Name = "Organization A" },
                    new Organization { Id = "org-b", Name = "Organization B" });
                domain.Users.Add(new User
                {
                    Id = "owner",
                    Name = "Owner",
                    Email = "owner@example.test",
                    OrganizationId = "org-a",
                    Role = "Customer"
                });
                domain.Customers.Add(new Customer
                {
                    Id = "customer-a",
                    Name = "Owner",
                    Email = "owner@example.test",
                    OrganizationId = "org-a"
                });
                domain.CustomerAuthLinks.Add(new CustomerAuthLink
                {
                    CustomerId = "customer-a",
                    LocalAccountId = "owner",
                    DomainUserId = "owner",
                    InviteStatus = CustomerInviteStatus.Active
                });
                domain.ScopedRoleAssignments.AddRange(
                    new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-a", RoleKey = ScopedRoleCatalog.SelfServiceUser },
                    new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-b", RoleKey = ScopedRoleCatalog.SelfServiceUser });
                domain.RequestForms.Add(new RequestForm
                {
                    Id = "form-a",
                    Title = "Request form",
                    ServiceId = "service-a",
                    OrganizationId = "org-a",
                    ReleaseStatus = RequestFormReleaseStatus.Production
                });
                domain.Requests.Add(new Request
                {
                    Id = "request-a",
                    TrackingId = "REQ-A",
                    Title = "Organization A request",
                    CustomerId = "customer-a",
                    OrganizationId = "org-a",
                    RequestFormId = "form-a",
                    State = TicketState.New,
                    CreatedAt = DateTime.UtcNow
                });
                await domain.SaveChangesAsync();
            }

            await app.StartAsync();
            return new GatewayAccessHarness(app, database, incidentListQueries);
        }

        public async Task SeedIncidentSearchAsync()
        {
            await using var scope = application.Services.CreateAsyncScope();
            var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            foreach (var organizationId in new[] { "org-a", "org-b" })
            {
                domain.ScopedRoleAssignments.AddRange(
                    new ScopedRoleAssignment { UserId = "owner", OrganizationId = organizationId, RoleKey = ScopedRoleCatalog.IncidentReader },
                    new ScopedRoleAssignment { UserId = "owner", OrganizationId = organizationId, RoleKey = ScopedRoleCatalog.RequestReader });
            }
            var createdAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            domain.Incidents.AddRange(
                new Incident { Id = "incident-alpha", TrackingId = "INC-193-A", OrganizationId = "org-a", CustomerId = "customer-a", Title = "Needle " + LiteralIncidentSearch, CreatedAt = createdAt },
                new Incident { Id = "incident-beta", TrackingId = "INC-193-B", OrganizationId = "org-a", CustomerId = "customer-a", Title = "Network issue", Description = "Needle in description", CreatedAt = createdAt.AddMinutes(1) },
                new Incident { Id = "incident-other", TrackingId = "INC-193-C", OrganizationId = "org-a", CustomerId = "customer-a", Title = "Unrelated active incident", CreatedAt = createdAt.AddMinutes(2) },
                new Incident { Id = "incident-resolved", TrackingId = "INC-193-D", OrganizationId = "org-a", CustomerId = "customer-a", Title = "Needle resolved incident", State = TicketState.Resolved, CreatedAt = createdAt.AddMinutes(3) },
                new Incident { Id = "incident-foreign", TrackingId = "INC-193-E", OrganizationId = "org-b", Title = "Needle foreign organization", CreatedAt = createdAt.AddMinutes(4) });
            await domain.SaveChangesAsync();
        }

        public async Task<Credential> CreateCredentialAsync(string organizationId, string? purpose = null, string? permissions = null, string ownerUserId = "owner")
        {
            var id = Guid.NewGuid();
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            if (ownerUserId != "owner" && !await identity.Users.AnyAsync(account => account.Id == ownerUserId))
                identity.Users.Add(new ApplicationUser
                {
                    Id = ownerUserId, UserName = ownerUserId + "@example.test", Email = ownerUserId + "@example.test",
                    DisplayName = "Separate credential owner", IsEnabled = true
                });
            identity.IntegrationCredentials.Add(new IntegrationCredential
            {
                Id = id,
                OwnerUserId = ownerUserId,
                Name = $"MCP {organizationId}",
                Prefix = $"rdk_{id:N}"[..16],
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
                Purpose = purpose ?? IntegrationCredentialAuthenticationHandler.McpPurpose,
                McpResourceUri = ResourceUri,
                OrganizationId = organizationId,
                Permissions = purpose == "mcp-setup" ? "" : permissions ?? HelpdeskPermissions.SelfServiceUser,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExpiresAtUtc = purpose == "mcp-setup" ? DateTimeOffset.UtcNow.AddMinutes(4) : DateTimeOffset.UtcNow.AddDays(1)
            });
            await identity.SaveChangesAsync();
            return new Credential(id, $"rdk_{id:N}_{secret}");
        }

        public HttpMessageHandler CreateHandler() => application.GetTestServer().CreateHandler();

        public async Task<Credential> IssueCredentialAsync(IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest request)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "owner"),
                new System.Security.Claims.Claim("auth_mode", "local")], "OwnerSession"));
            var issuer = new IntegrationCredentialIssuer(new IntegrationCredentialOwnerResolver(domain, identity),
                new CurrentUserAccessService(domain, identity), domain, identity);
            var grant = await issuer.ValidateAsync(principal, request, default);
            var (record, delivery) = issuer.Create(grant, DateTimeOffset.UtcNow);
            issuer.Track(record);
            await identity.SaveChangesAsync();
            Assert.Equal(request.GrantKind, delivery.GrantKind);
            Assert.Equal("mcp", delivery.Purpose);
            return new(delivery.Id, delivery.Secret);
        }

        public async Task<string> DelegateAsync(Credential credential)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
            request.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, ResourceUri);
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var delegated = await response.Content.ReadFromJsonAsync<McpGatewayDelegationEndpoints.McpExecutionTokenResponse>();
            return Assert.IsType<string>(delegated?.AccessToken);
        }

        public async Task AssertDelegationStatusAsync(Credential credential, string resource, HttpStatusCode expectedStatus)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/mcp/execution-token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Bearer);
            request.Headers.Add(McpGatewayDelegationEndpoints.ResourceHeader, resource);
            using var response = await Client.SendAsync(request);
            Assert.Equal(expectedStatus, response.StatusCode);
        }

        public async Task<(HttpStatusCode StatusCode, T? Body)> GetAsync<T>(string executionToken, string path)
        {
            using var response = await GetAsync(executionToken, path);
            return (response.StatusCode, response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<T>()
                : default);
        }

        public async Task<HttpResponseMessage> GetAsync(string executionToken, string path)
        {
            return await SendAsync(executionToken, HttpMethod.Get, path);
        }

        public async Task<HttpResponseMessage> SendAsync(string executionToken, HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", executionToken);
            return await Client.SendAsync(request);
        }

        public Task SetOwnerAdministratorAsync() => SetOwnerAdministratorAsync(true);

        public async Task SetOwnerAdministratorAsync(bool enabled)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            (await identity.Users.SingleAsync(x => x.Id == "owner")).IsInstanceAdministrator = enabled;
            await identity.SaveChangesAsync();
        }

        public async Task SeedScopedRoleAdministrationAsync()
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "owner", OrganizationId = "org-a", RoleKey = ScopedRoleCatalog.TenantAdministrator });
            db.Roles.Add(new Role { Id = "foreign-role", Key = "custom.foreign-reader", Name = "Foreign reader", Scope = RoleScopeKind.Tenant, OwnerOrganizationId = "org-b", Permissions = [new RolePermission { Permission = HelpdeskPermissions.IncidentRead }] });
            await db.SaveChangesAsync();
        }

        public async Task AssertOwnedRoleAsync(string id, string name, string[] permissions)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var role = await db.Roles.AsNoTracking().Include(row => row.Permissions).SingleAsync(row => row.Id == id);
            Assert.Equal(name, role.Name);
            Assert.Equal("org-a", role.OwnerOrganizationId);
            Assert.Equal(permissions.Order(), role.Permissions.Select(row => row.Permission).Order());
            Assert.False(role.IsBuiltIn);
        }

        public async Task<List<string>> OwnedRoleAuditsAsync()
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            return await db.ActivityLogs.AsNoTracking().Where(row => row.UserId == "owner").OrderBy(row => row.Id).Select(row => row.Message).ToListAsync();
        }

        public async Task AssertForeignRoleUnchangedAsync()
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var foreign = await db.Roles.AsNoTracking().SingleAsync(row => row.Id == "foreign-role");
            Assert.Equal("Foreign reader", foreign.Name);
            Assert.Equal("org-b", foreign.OwnerOrganizationId);
            Assert.Equal(1, await db.RolePermissions.CountAsync(row => row.RoleId == foreign.Id));
        }

        public async Task AssertNoOwnedCustomRolesAsync()
        {
            await using var scope = application.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            Assert.False(await db.Roles.AnyAsync(row => row.OwnerOrganizationId == "org-a" && !row.IsBuiltIn));
            Assert.False(await db.RolePermissions.AnyAsync(row => !db.Roles.Any(role => role.Id == row.RoleId)));
        }

        public async Task RemoveRoleAsync(string organizationId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var assignment = await domain.ScopedRoleAssignments.SingleAsync(candidate =>
                candidate.UserId == "owner" && candidate.OrganizationId == organizationId);
            domain.ScopedRoleAssignments.Remove(assignment);
            await domain.SaveChangesAsync();
        }

        public async Task RestoreRoleAsync(string organizationId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            domain.ScopedRoleAssignments.Add(new ScopedRoleAssignment
            {
                UserId = "owner",
                OrganizationId = organizationId,
                RoleKey = ScopedRoleCatalog.SelfServiceUser
            });
            await domain.SaveChangesAsync();
        }

        public async Task SetCredentialRevokedAsync(Guid credentialId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var credential = await identity.IntegrationCredentials.SingleAsync(candidate => candidate.Id == credentialId);
            credential.RevokedAtUtc = DateTimeOffset.UtcNow;
            await identity.SaveChangesAsync();
        }

        public async Task SetCredentialExpiredAsync(Guid credentialId)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            (await identity.IntegrationCredentials.SingleAsync(candidate => candidate.Id == credentialId)).ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await identity.SaveChangesAsync();
        }

        public async Task SetOwnerEnabledAsync(bool isEnabled)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var owner = await identity.Users.SingleAsync(candidate => candidate.Id == "owner");
            owner.IsEnabled = isEnabled;
            await identity.SaveChangesAsync();
        }

        public async Task SetOrganizationEnabledAsync(string organizationId, bool enabled)
        {
            await using var scope = application.Services.CreateAsyncScope();
            var domain = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            (await domain.Organizations.SingleAsync(organization => organization.Id == organizationId)).IsEnabled = enabled;
            await domain.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    internal sealed record Credential(Guid Id, string Bearer);

    private sealed class GatewayClientFactory(GatewayAccessHarness harness) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(harness.CreateHandler()) { BaseAddress = new Uri("https://gateway-api.example.test") };
    }

    private sealed class GatewayToken(string token) : IAgentAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class GatewayLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class TestTenantContext(string? tenantId, string? userId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;
        public string? UserId { get; } = userId;
        public bool IsHelpdeskAdmin => false;
    }

    private sealed class NoOpDomainEventPublisher : IDomainEventPublisher
    {
        public Task PublishAsync(DomainEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string GetCorrelationId() => "corr-mcp-real-pipeline";
    }
}
