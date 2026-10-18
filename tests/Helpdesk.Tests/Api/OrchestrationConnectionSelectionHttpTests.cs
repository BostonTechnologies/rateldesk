using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Pairing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

[Collection("Incident receiver")]
public sealed class OrchestrationConnectionSelectionHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_saved_connection_drives_settings_catalog_creation_and_binding_without_legacy_organization_fields(bool postgres)
    {
        using var peer = new CatalogPeer();
        var tokens = new CapturingTokens();
        await using var h = await CreateAsync(postgres, peer, tokens);
        var first = await ConnectAsync(h, 17);
        var second = await ConnectAsync(h, 42);

        using var ambiguous = await h.AdminAsync(HttpMethod.Get, "/api/v1/admin/orchestration/catalog/jobs?organizationId=" + h.OrganizationId);
        Assert.Equal(HttpStatusCode.Conflict, ambiguous.StatusCode);
        Assert.Contains("connection_selection_required", await ambiguous.Content.ReadAsStringAsync());
        Assert.Equal(0, peer.RequestCount);

        using var settingsResponse = await h.AdminAsync(HttpMethod.Get, "/api/v1/admin/orchestration/" + Query(h.OrganizationId, second.MappingId));
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var settings = (await settingsResponse.Content.ReadFromJsonAsync<OrchestrationConnectivitySettingsDto>())!;
        Assert.True(settings.Enabled);
        Assert.Equal(h.OrganizationId, settings.LocalTenantId);
        Assert.Equal("42", settings.PeerTenantId);
        Assert.Equal(second.MappingId.ToString("D"), settings.SourceKey);
        Assert.DoesNotContain("synthetic-outbound-secret", await settingsResponse.Content.ReadAsStringAsync());

        using var jobs = await h.AdminAsync(HttpMethod.Get, "/api/v1/admin/orchestration/catalog/jobs" + Query(h.OrganizationId, second.MappingId));
        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        Assert.Equal("job-42", Assert.Single((await jobs.Content.ReadFromJsonAsync<OrchestrationCatalogJobDto[]>())!).Id);
        Assert.Equal(second.MappingId.ToString("D"), tokens.LastMappingId);
        using var definitions = await h.AdminAsync(HttpMethod.Get, "/api/v1/admin/orchestration/catalog/request-definitions" + Query(h.OrganizationId, first.MappingId));
        Assert.Equal("definition-17", Assert.Single((await definitions.Content.ReadFromJsonAsync<OrchestrationCatalogRequestDefinitionDto[]>())!).RequestDefinitionId);

        using var created = await h.AdminAsync(HttpMethod.Post, "/api/v1/admin/orchestration/catalog/request-definitions" + Query(h.OrganizationId, second.MappingId),
            new CreateOrchestrationCatalogRequestDefinitionDto { Name = "Synthetic target", TenantId = 42, ClientIdentity = "synthetic-client" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(42, (await created.Content.ReadFromJsonAsync<OrchestrationCatalogRequestDefinitionDto>())!.TenantId);
        Assert.Equal(1, peer.CreateCount);
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IOrchestrationCatalogService>();
            var input = new OrchestrationCatalogInputDefinitionDto { Key = "Path", Label = "Path", Type = "text" };
            var synced = await catalog.SyncRequestDefinitionInputsAsync("definition-42", [input], h.OrganizationId, second.MappingId);
            Assert.Equal("Path", Assert.Single(synced.Inputs).Key);
            Assert.Equal(second.MappingId.ToString("D"), tokens.LastMappingId);
            Assert.Equal(1, peer.SyncCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SyncRequestDefinitionInputsAsync("definition-17", [input], h.OrganizationId, second.MappingId));
            Assert.Equal(1, peer.SyncCount);
        }

        var taskId = Guid.NewGuid();
        var form = new RequestForm { Id = Guid.NewGuid().ToString("D"), Title = "Synthetic form", OrganizationId = h.OrganizationId,
            JsonSchema = JsonSerializer.SerializeToDocument(new { fields = Array.Empty<object>(), tasks = new[] { new { id = taskId, name = "Automate", type = "automation", order = 1 } } }) };
        await h.EditAsync(db => { db.RequestForms.Add(form); return Task.CompletedTask; });
        string bindingId;
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAutomationBindingService>();
            var binding = await bindings.CreateAsync(new CreateAutomationBindingDto { RequestFormId = form.Id, TaskTemplateId = taskId,
                SystemConnectionId = second.MappingId, OrchestrationRequestDefinitionId = "definition-42", OrchestrationJobDefinitionId = "job-42" });
            bindingId = binding.Id;
            Assert.Equal(second.MappingId, binding.SystemConnectionId);
            var syncedBinding = await scope.ServiceProvider.GetRequiredService<IAutomationBindingSchemaSyncService>().SyncAsync(bindingId);
            Assert.Equal(second.MappingId, syncedBinding.SystemConnectionId);
            var refreshed = await scope.ServiceProvider.GetRequiredService<IAutomationBindingDriftService>().RefreshAsync(form.Id);
            Assert.Equal(second.MappingId, Assert.Single(refreshed).SystemConnectionId);
            var imported = await scope.ServiceProvider.GetRequiredService<IAutomationBindingImportService>().ImportAsync(bindingId);
            Assert.Equal(second.MappingId, imported.SystemConnectionId);
        }
        await h.RestartAsync();
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAutomationBindingService>();
            Assert.Equal(second.MappingId, (await bindings.GetAsync(bindingId))!.SystemConnectionId);
            var updated = await bindings.UpdateAsync(bindingId, new UpdateAutomationBindingDto { OrchestrationRequestDefinitionName = "Reviewed target" });
            Assert.Equal(second.MappingId, updated!.SystemConnectionId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bindings.UpdateAsync(bindingId, new UpdateAutomationBindingDto { SystemConnectionId = first.MappingId }));
        }
        await h.EditAsync(async db =>
        {
            Assert.Null((await db.Organizations.SingleAsync(x => x.Id == h.OrganizationId)).OrchestrationTenantId);
            Assert.Equal(second.MappingId, (await db.AutomationBindings.SingleAsync()).SystemConnectionId);
            (await db.Set<SystemConnection>().SingleAsync(x => x.Id == second.MappingId)).State = "disabled";
        });
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAutomationBindingService>();
            var disabled = await bindings.UpdateAsync(bindingId, new UpdateAutomationBindingDto { Enabled = false, SystemConnectionId = second.MappingId,
                OrchestrationRequestDefinitionId = "definition-42", OrchestrationJobDefinitionId = "job-42" });
            Assert.False(disabled!.Enabled);
            Assert.Equal(second.MappingId, disabled.SystemConnectionId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bindings.UpdateAsync(bindingId, new UpdateAutomationBindingDto { Enabled = true }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => bindings.UpdateAsync(bindingId, new UpdateAutomationBindingDto { OrchestrationRequestDefinitionId = "definition-17" }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_organization_disabled_deleted_and_mismatched_tenants_cannot_submit_catalog_mutations(bool postgres)
    {
        using var peer = new CatalogPeer();
        await using var h = await CreateAsync(postgres, peer, new CapturingTokens());
        var connection = await ConnectAsync(h, 17);
        var path = "/api/v1/admin/orchestration/catalog/request-definitions";
        foreach (var query in new[] { Query("foreign-organization", connection.MappingId), Query(h.OrganizationId, Guid.NewGuid()) })
        {
            using var denied = await h.AdminAsync(HttpMethod.Get, path + query);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        }
        Assert.Equal(0, peer.RequestCount);
        foreach (var tenantId in new int?[] { null, 42 })
        {
            using var denied = await h.AdminAsync(HttpMethod.Post, path + Query(h.OrganizationId, connection.MappingId),
                new CreateOrchestrationCatalogRequestDefinitionDto { Name = "Synthetic target", TenantId = tenantId, ClientIdentity = "synthetic-client" });
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        }
        Assert.Equal(0, peer.CreateCount);
        peer.ReturnWrongTenant = true;
        using var incompatible = await h.AdminAsync(HttpMethod.Post, path + Query(h.OrganizationId, connection.MappingId),
            new CreateOrchestrationCatalogRequestDefinitionDto { Name = "Synthetic target", TenantId = 17, ClientIdentity = "synthetic-client" });
        Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
        Assert.Equal(1, peer.CreateCount);

        await h.EditAsync(async db => (await db.Set<SystemConnection>().SingleAsync()).State = "disabled");
        var requestCount = peer.RequestCount;
        using var disabled = await h.AdminAsync(HttpMethod.Get, path + Query(h.OrganizationId, connection.MappingId));
        Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
        await h.EditAsync(async db => db.Set<SystemConnection>().Remove(await db.Set<SystemConnection>().SingleAsync()));
        using var deleted = await h.AdminAsync(HttpMethod.Get, path + Query(h.OrganizationId, connection.MappingId));
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
        Assert.Equal(requestCount, peer.RequestCount);
    }

    [Theory]
    [InlineData(false, "import")]
    [InlineData(false, "drift")]
    [InlineData(false, "sync")]
    [InlineData(true, "import")]
    [InlineData(true, "drift")]
    [InlineData(true, "sync")]
    public async Task Changed_binding_during_catalog_await_rejects_stale_form_and_metadata_without_overwriting_new_selection(bool postgres, string operation)
    {
        using var peer = new CatalogPeer();
        await using var h = await CreateAsync(postgres, peer, new CapturingTokens());
        var first = await ConnectAsync(h, 17);
        var second = await ConnectAsync(h, 42);
        var taskId = Guid.NewGuid();
        var form = new RequestForm { Id = Guid.NewGuid().ToString("D"), Title = "Synthetic race form", OrganizationId = h.OrganizationId,
            JsonSchema = JsonSerializer.SerializeToDocument(new {
                fields = new[] { new { key = "original_field", label = "Original field", type = "text" } },
                tasks = new[] { new { id = taskId, name = "Automate", type = "automation", order = 1, payloadMapping = new Dictionary<string, string> { ["Path"] = "original_field" } } }
            }) };
        var originalSchema = form.JsonSchema.RootElement.GetRawText();
        var binding = new AutomationBinding { Id = Guid.NewGuid().ToString("D"), OrganizationId = h.OrganizationId,
            RequestFormId = form.Id, TaskTemplateId = taskId, SystemConnectionId = first.MappingId,
            OrchestrationRequestDefinitionId = "definition-17", OrchestrationJobDefinitionId = "job-17", Enabled = true,
            SyncState = AutomationBindingSyncState.Drifted, LastSyncHash = "original-hash" };
        await h.EditAsync(db => { db.RequestForms.Add(form); db.AutomationBindings.Add(binding); return Task.CompletedTask; });
        await h.EditAsync(async db => originalSchema = (await db.RequestForms.AsNoTracking().SingleAsync()).JsonSchema.RootElement.GetRawText());
        peer.BeforeCatalogResponse = async () =>
        {
            await h.EditAsync(async db =>
            {
                var current = await db.AutomationBindings.SingleAsync();
                current.SystemConnectionId = second.MappingId;
                current.OrchestrationRequestDefinitionId = "definition-42";
                current.OrchestrationJobDefinitionId = "job-42";
            });
        };
        await using (var scope = h.App.Services.CreateAsyncScope())
        {
            var exception = await Assert.ThrowsAnyAsync<IntegrationProviderConfigurationConflictException>(async () =>
            {
                if (operation == "import") await scope.ServiceProvider.GetRequiredService<IAutomationBindingImportService>().ImportAsync(binding.Id);
                else if (operation == "drift") await scope.ServiceProvider.GetRequiredService<IAutomationBindingDriftService>().RefreshAsync(form.Id);
                else await scope.ServiceProvider.GetRequiredService<IAutomationBindingSchemaSyncService>().SyncAsync(binding.Id);
            });
            Assert.Equal("binding_selection_changed", exception.Code);
            // A later save on the same unit of work must also have no stale changes to flush.
            await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().SaveChangesAsync();
        }
        await h.EditAsync(async db =>
        {
            var current = await db.AutomationBindings.SingleAsync();
            Assert.Equal(second.MappingId, current.SystemConnectionId);
            Assert.Equal("definition-42", current.OrchestrationRequestDefinitionId);
            Assert.Equal("job-42", current.OrchestrationJobDefinitionId);
            Assert.Equal("original-hash", current.LastSyncHash);
            Assert.Equal(AutomationBindingSyncState.Drifted, current.SyncState);
            Assert.Null(current.LastCorrelationId);
            Assert.Null(current.LastSyncedAtUtc);
            Assert.Equal(originalSchema, (await db.RequestForms.SingleAsync()).JsonSchema.RootElement.GetRawText());
        });
    }

    private static Task<IncidentReceiverTests.Harness> CreateAsync(bool postgres, CatalogPeer peer, CapturingTokens tokens)
        => IncidentReceiverTests.Harness.CreateAsync(postgres, serviceIdentity: true, pairingPeer: peer,
            configureServices: services =>
            {
                services.AddScoped<IOrchestrationTokenService>(_ => tokens);
                services.AddAuthorization(options => options.AddPolicy("HelpdeskAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("HelpdeskAdmin")));
            },
            configureApp: app => app.MapExternalOrchestrationEndpoints());

    private static async Task<PairingBusinessFixture.Client> ConnectAsync(IncidentReceiverTests.Harness h, int tenantId)
    {
        var client = await PairingBusinessFixture.CreateAsync(h, incidents: false, automation: true);
        await using var scope = h.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var connection = await db.Set<SystemConnection>().SingleAsync(x => x.Id == client.MappingId);
        var mapping = SystemPairingService.Read<PairingMapping>(connection.MappingJson) with { NetRatelTenantId = tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        connection.MappingJson = JsonSerializer.Serialize(mapping, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        connection.ProtectedOutboundCredential = scope.ServiceProvider.GetRequiredService<IntegrationProviderSecretProtector>().Protect(JsonSerializer.Serialize(
            new PairingBusinessCredential("synthetic-client", "synthetic-outbound-secret", "https://peer-api.example.test/connect/token", "synthetic-api", "https://peer-api.example.test", ["synthetic-scope"], null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await db.SaveChangesAsync();
        return client;
    }

    private static string Query(string organizationId, Guid connectionId) => $"?organizationId={Uri.EscapeDataString(organizationId)}&connectionId={connectionId:D}";

    private sealed class CapturingTokens : IOrchestrationTokenService
    {
        public string? LastMappingId { get; private set; }
        public Task<string> GetAccessTokenAsync(OrchestrationResolvedSettings settings, CancellationToken cancellationToken = default, bool useCache = true)
        { LastMappingId = settings.Pairing?.MappingId; return Task.FromResult("synthetic-token"); }
    }

    private sealed class CatalogPeer : HttpMessageHandler, IHttpClientFactory
    {
        public int RequestCount { get; private set; }
        public int CreateCount { get; private set; }
        public int SyncCount { get; private set; }
        public bool ReturnWrongTenant { get; set; }
        public Func<Task>? BeforeCatalogResponse { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (BeforeCatalogResponse is { } beforeResponse)
            {
                BeforeCatalogResponse = null;
                await beforeResponse();
            }
            object body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/inputs/sync", StringComparison.Ordinal))
            {
                SyncCount++;
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var tenantId = int.Parse(request.RequestUri.AbsolutePath.Split('/')[^3].Replace("definition-", ""), System.Globalization.CultureInfo.InvariantCulture);
                body = Definition(tenantId, payload.GetProperty("Inputs").Deserialize<OrchestrationCatalogInputDefinitionDto[]>());
            }
            else if (request.Method == HttpMethod.Post)
            {
                CreateCount++;
                var create = (await request.Content!.ReadFromJsonAsync<CreateOrchestrationCatalogRequestDefinitionDto>(cancellationToken))!;
                body = Definition(ReturnWrongTenant ? 99 : create.TenantId!.Value);
            }
            else if (request.RequestUri!.AbsolutePath.EndsWith("/jobs", StringComparison.Ordinal))
                body = new[] { new { id = "job-17", name = "Synthetic job", tenantId = 17 }, new { id = "job-42", name = "Other job", tenantId = 42 } };
            else body = new[] { Definition(17), Definition(42) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
        private static object Definition(int tenantId, OrchestrationCatalogInputDefinitionDto[]? inputs = null) => new { requestDefinitionId = $"definition-{tenantId}", requestDefinitionName = "Synthetic target", tenantId,
            clientIdentity = "synthetic-client", netRatelJobDefinitionId = $"job-{tenantId}", inputs = inputs ?? [] };
    }
}
