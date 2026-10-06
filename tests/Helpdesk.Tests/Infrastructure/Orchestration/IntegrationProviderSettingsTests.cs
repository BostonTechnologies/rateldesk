using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.ServiceLink;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.AiAssistant.Chat;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.ServiceLink;
using System.Text.Json;
using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Helpdesk.Tests.Infrastructure.Orchestration;

public sealed class IntegrationProviderSettingsTests
{
    private static ServiceLinkOrchestratorBinding LinkedBinding(long credentialRevision = 1) =>
        new("organization-1", "tenant-17", "peer-instance", "approved-link", 1, credentialRevision,
            ServiceLinkCanonicalJson.HashObject(ApprovedProviderSummary()), "initiator_to_responder");

    private static ServiceLinkGrantSummary ApprovedProviderSummary() => new()
    {
        AttemptId = "provider-authorization-fixture", LinkId = "approved-link", ProposedLinkRevision = 1,
        DescriptorHash = new string('b', 64), ExpiresAt = "2099-01-01T00:00:00Z",
        InitiatorInstanceId = "local-instance", ResponderInstanceId = "peer-instance",
        InitiatorEndpointSnapshot = ProviderLocalMetadata(),
        ResponderEndpointSnapshot = new()
        {
            Product = "netratel", ProductVersion = "provider-authority-fixture", InstanceId = "peer-instance",
            SourceInstanceId = "12345678-1234-1234-1234-123456789abc", WebBaseUrl = "https://peer.example.test", ApiBaseUrl = "https://api.peer.example.test",
            OauthIssuer = "https://issuer.peer.example.test", Audience = "netratel.services",
            OauthMetadataUrl = "https://api.peer.example.test/.well-known/oauth-authorization-server",
            TokenEndpoint = "https://api.peer.example.test/connect/token", JwksUri = "https://api.peer.example.test/.well-known/jwks.json",
            ServiceLinkEndpoint = "https://api.peer.example.test" + ServiceLinkContract.EndpointPath,
            ApprovalEndpoint = "https://peer.example.test/account/integrations/rateldesk/approve", CallbackEndpoint = "https://peer.example.test/account/integrations/rateldesk/callback"
        },
        Grants =
        [
            new ServiceLinkGrant
            {
                DirectionId = ServiceLinkContract.InitiatorToResponder, CallerSnapshot = "initiator", TargetSnapshot = "responder",
                CallerProduct = "rateldesk", CallerInstanceId = "local-instance", CallerTenantId = "organization-1",
                TargetProduct = "netratel", TargetInstanceId = "peer-instance", TargetTenantId = "tenant-17",
                Issuer = "https://issuer.peer.example.test", Audience = "netratel.services", Capabilities = ["orchestration"],
                Scopes = ["netratel.orchestration.invoke", "netratel.orchestration.read"],
                ResourceConstraints = new() { TenantId = "tenant-17", ResourceIds = ["approved-resource"] }
            },
            new ServiceLinkGrant
            {
                DirectionId = ServiceLinkContract.ResponderToInitiator, CallerSnapshot = "responder", TargetSnapshot = "initiator",
                CallerProduct = "netratel", CallerInstanceId = "peer-instance", CallerTenantId = "tenant-17",
                TargetProduct = "rateldesk", TargetInstanceId = "local-instance", TargetTenantId = "organization-1",
                Issuer = "https://issuer.local.example.test", Audience = "rateldesk.services", Capabilities = ["orchestration-callback"],
                Scopes = [ServiceIdentityScopes.Callback],
                ResourceConstraints = new() { OrganizationId = "organization-1", CustomerIds = ["customer-1"] },
                SourceInstanceId = "12345678-1234-1234-1234-123456789abc", SourceNamespaceId = "abcdef12-1234-1234-1234-123456789abc"
            }
        ]
    };

    private static ServiceLinkMetadata ProviderLocalMetadata() => new()
    {
        Product = "rateldesk", ProductVersion = "provider-authority-fixture", InstanceId = "local-instance",
        WebBaseUrl = "https://local.example.test", ApiBaseUrl = "https://api.local.example.test",
        OauthIssuer = "https://issuer.local.example.test", Audience = "rateldesk.services",
        OauthMetadataUrl = "https://api.local.example.test/.well-known/oauth-authorization-server",
        TokenEndpoint = "https://api.local.example.test/connect/token", JwksUri = "https://api.local.example.test/.well-known/jwks.json",
        ServiceLinkEndpoint = "https://api.local.example.test" + ServiceLinkContract.EndpointPath,
        ApprovalEndpoint = "https://local.example.test/account/integration-credentials/link/approve",
        CallbackEndpoint = "https://local.example.test/account/integration-credentials/link/callback"
    };

    private static UpdateOrchestrationConnectivitySettingsDto LinkedRequest(int revision, string? secret = "synthetic-linked-secret") => new()
    {
        ExpectedRevision = revision, Enabled = false, BaseUrl = "https://api.peer.example.test",
        Authority = "https://issuer.peer.example.test", TokenEndpoint = "https://api.peer.example.test/connect/token",
        Audience = "netratel.services", Scope = "netratel.orchestration.read netratel.orchestration.invoke",
        ClientId = "dedicated-outbound", ClientSecret = secret
    };

    [Fact]
    public async Task Linked_provider_stages_in_existing_store_and_requires_ownership_and_revision_to_enable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var staged = await service.StageLinkedOrchestratorSettingsAsync(LinkedRequest(0), LinkedBinding());
        Assert.False(staged.Enabled);
        Assert.False(staged.ManagedSenderEnabled);
        Assert.Equal("approved-link", staged.LinkId);
        Assert.Equal("synthetic-linked-secret", (await service.GetResolvedOrchestratorSettingsAsync()).ClientSecret);
        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() =>
            service.SetLinkedOrchestratorSenderEnabledAsync("foreign-link", 1, staged.Revision, true));
        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() =>
            service.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, staged.Revision - 1, true));
        var active = await service.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, staged.Revision, true);
        Assert.True(active.Enabled);
        Assert.False((await service.GetResolvedOrchestratorSettingsAsync()).Enabled); // A configuration update alone grants no sender authority.
        await RecordApprovedSenderAuthorityAsync(fixture);
        Assert.True((await service.GetResolvedOrchestratorSettingsAsync()).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateOrchestratorSettingsAsync(LinkedRequest(active.Revision)));
        Assert.Single(await fixture.Db.M2MConnectivitySettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Linked_provider_secret_cannot_cross_tenant_or_credential_revision_and_runtime_reads_current_database()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var staged = await service.StageLinkedOrchestratorSettingsAsync(LinkedRequest(0), LinkedBinding());
        await Assert.ThrowsAsync<ArgumentException>(() => service.StageLinkedOrchestratorSettingsAsync(
            LinkedRequest(staged.Revision, null), LinkedBinding() with { PeerTenantId = "other-tenant" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StageLinkedOrchestratorSettingsAsync(
            LinkedRequest(staged.Revision, null), LinkedBinding(2)));
        var active = await service.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, staged.Revision, true);
        await RecordApprovedSenderAuthorityAsync(fixture);
        // ExecuteUpdate bypasses this context's tracked old entity, as another replica does.
        await fixture.Db.M2MConnectivitySettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.ManagedSenderEnabled, false));
        Assert.False((await service.GetResolvedOrchestratorSettingsAsync()).Enabled);
        await fixture.Db.M2MConnectivitySettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.PeerTenantId, "substituted-tenant"));
        var substituted = await service.GetResolvedOrchestratorSettingsAsync();
        Assert.True(substituted.SecretUnavailable);
        Assert.Null(substituted.ClientSecret);
    }

    [Fact]
    public async Task Guided_profile_respects_deployment_and_existing_manual_ownership()
    {
        await using var fixture = await Fixture.CreateAsync();
        var deployment = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestrator:BaseUrl"] = "", ["Orchestrator:Enabled"] = "false"
        }).Build();
        var owned = fixture.CreateService(deployment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owned.StageLinkedOrchestratorSettingsAsync(LinkedRequest(0), LinkedBinding()));
        Assert.Empty(await fixture.Db.M2MConnectivitySettings.ToListAsync());
        var manual = fixture.CreateService();
        await manual.UpdateOrchestratorSettingsAsync(LinkedRequest(0));
        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => manual.StageLinkedOrchestratorSettingsAsync(LinkedRequest(1), LinkedBinding()));
        Assert.Null((await fixture.Db.M2MConnectivitySettings.SingleAsync()).LinkId);
    }

    [Fact]
    public async Task Cached_link_token_is_refused_after_durable_profile_disable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = fixture.CreateService();
        var staged = await settings.StageLinkedOrchestratorSettingsAsync(LinkedRequest(0), LinkedBinding());
        await settings.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, staged.Revision, true);
        await RecordApprovedSenderAuthorityAsync(fixture);
        var snapshot = await settings.GetResolvedOrchestratorSettingsAsync();
        using var provider = new ServiceCollection().AddSingleton<IIntegrationProviderSettingsService>(settings)
            .AddSingleton(fixture.IdentityOptions).AddSingleton(fixture.LinkOptions).BuildServiceProvider();
        var factory = Substitute.For<IHttpClientFactory>();
        var handler = new StaticTokenHandler();
        using var http = new HttpClient(handler);
        factory.CreateClient("OrchestrationToken").Returns(http);
        factory.CreateClient(ServiceLinkOutboundNetwork.TokenClientName).Returns(http);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var tokens = new OrchestrationTokenService(factory, memory, scopes: provider.GetRequiredService<IServiceScopeFactory>(),
            currentLinkOptions: provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());
        Assert.Equal("synthetic-access-token", await tokens.GetAccessTokenAsync(snapshot));
        Assert.Equal("synthetic-access-token", await tokens.GetAccessTokenAsync(snapshot));
        Assert.Equal(1, handler.Requests);
        var successor = await settings.StageLinkedOrchestratorSettingsAsync(LinkedRequest(snapshot.Revision, "synthetic-successor-secret"), LinkedBinding(2));
        await settings.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, successor.Revision, true);
        var rotated = await settings.GetResolvedOrchestratorSettingsAsync();
        Assert.Equal(snapshot.ServiceLink!.LinkId, rotated.ServiceLink!.LinkId);
        Assert.Equal(2, rotated.ServiceLink.CredentialRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.GetAccessTokenAsync(snapshot));
        Assert.Equal("synthetic-access-token", await tokens.GetAccessTokenAsync(rotated));
        Assert.Equal(2, handler.Requests);
        await fixture.Db.M2MConnectivitySettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.ManagedSenderEnabled, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.GetAccessTokenAsync(rotated));
    }

    private sealed class StaticTokenHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("{\"access_token\":\"synthetic-access-token\",\"expires_in\":300}") });
        }
    }

    private static async Task RecordApprovedSenderAuthorityAsync(Fixture fixture)
    {
        var summary = ApprovedProviderSummary();
        var inbound = summary.Grants.Single(x => x.DirectionId == ServiceLinkContract.ResponderToInitiator);
        var principal = new ServicePrincipalRegistration
        {
            Name = "Synthetic inbound authority", ClientId = "synthetic-inbound", NormalizedClientId = "SYNTHETIC-INBOUND",
            OrganizationId = "organization-1", PeerInstanceId = "peer-instance", PeerTenantId = "tenant-17",
            LinkId = "approved-link", AttemptId = summary.AttemptId, GrantHash = ServiceLinkCanonicalJson.HashObject(summary),
            DescriptorHash = summary.DescriptorHash, DirectionId = inbound.DirectionId, LinkRevision = 1, Status = "active",
            AllowedScopesJson = JsonSerializer.Serialize(inbound.Scopes), CustomerIdsJson = JsonSerializer.Serialize(inbound.ResourceConstraints.CustomerIds),
            ResourceConstraintsJson = JsonSerializer.Serialize(inbound.ResourceConstraints),
            SourceInstanceId = Guid.Parse(inbound.SourceInstanceId!), SourceNamespaceId = Guid.Parse(inbound.SourceNamespaceId!)
        };
        fixture.Db.Organizations.Add(new Organization { Id = "organization-1", Name = "Synthetic local organization", IsEnabled = true });
        fixture.Db.Customers.Add(new Customer { Id = "customer-1", Name = "Synthetic approved customer", OrganizationId = "organization-1", IsEnabled = true });
        fixture.Db.Set<ServicePrincipalRegistration>().Add(principal);
        fixture.Db.Set<ServicePrincipalSecret>().Add(new()
        {
            ServicePrincipalId = principal.Id, CredentialRevision = 1, Status = "active", SecretHash = new string('c', 64), Salt = new string('d', 64),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
        });
        fixture.Db.IncidentReceiverSources.Add(new()
        {
            SourceNamespaceId = principal.SourceNamespaceId.Value, SourceInstanceId = principal.SourceInstanceId.Value,
            OrganizationId = principal.OrganizationId, CustomerId = "customer-1", IsEnabled = true
        });
        fixture.Db.IncidentReceiverPrincipalBindings.Add(new()
        {
            SourceNamespaceId = principal.SourceNamespaceId.Value, PrincipalKind = "service_principal", PrincipalId = principal.Id.ToString("N"), IsEnabled = true
        });
        // An isolated durable-authority fixture for provider resolution, not evidence of a completed pairing ceremony.
        fixture.Db.Set<ServiceLinkAttempt>().Add(new ServiceLinkAttempt
        {
            AttemptId = "provider-authorization-fixture", Role = "initiator", LocalTenantId = "organization-1",
            PeerTenantId = "tenant-17", PeerInstanceId = "peer-instance", LinkId = "approved-link", LinkRevision = 1,
            GrantHash = principal.GrantHash, GrantSummaryJson = JsonSerializer.Serialize(summary), DescriptorHash = summary.DescriptorHash,
            InboundPrincipalId = principal.Id, LifecycleState = "active", Decision = "commit",
            LocalInboundActive = true, LocalBusinessSenderEnabled = true, PeerActiveAcknowledged = true
        });
        await fixture.Db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("registration-revoked")]
    [InlineData("credential-revoked")]
    [InlineData("credential-expired")]
    [InlineData("credential-retired")]
    [InlineData("missing-current-credential")]
    [InlineData("organization-disabled")]
    [InlineData("customer-disabled")]
    [InlineData("source-disabled")]
    [InlineData("source-changed")]
    [InlineData("principal-binding-disabled")]
    [InlineData("scope-changed")]
    [InlineData("constraint-changed")]
    [InlineData("grant-invalid")]
    public async Task Held_linked_snapshot_observes_current_inbound_grant_even_when_lifecycle_flags_remain_active(string changedAuthority)
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = fixture.CreateService();
        var staged = await settings.StageLinkedOrchestratorSettingsAsync(LinkedRequest(0), LinkedBinding());
        await settings.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, staged.Revision, true);
        await RecordApprovedSenderAuthorityAsync(fixture);
        var snapshot = await settings.GetResolvedOrchestratorSettingsAsync();
        Assert.True(snapshot.Enabled);
        using var provider = new ServiceCollection().AddSingleton<IIntegrationProviderSettingsService>(settings)
            .AddSingleton(fixture.IdentityOptions).AddSingleton(fixture.LinkOptions).BuildServiceProvider();
        var factory = Substitute.For<IHttpClientFactory>();
        var handler = new StaticTokenHandler();
        using var http = new HttpClient(handler);
        factory.CreateClient("OrchestrationToken").Returns(http);
        factory.CreateClient(ServiceLinkOutboundNetwork.TokenClientName).Returns(http);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var tokens = new OrchestrationTokenService(factory, memory, scopes: provider.GetRequiredService<IServiceScopeFactory>(),
            currentLinkOptions: provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());
        await tokens.GetAccessTokenAsync(snapshot);
        Assert.Equal(1, handler.Requests);

        // Bypass tracked rows as a different replica or source administrator does; the lifecycle flags deliberately stay stale.
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        switch (changedAuthority)
        {
            case "registration-revoked":
                await fixture.Db.Set<ServicePrincipalRegistration>().ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "revoked")); break;
            case "credential-revoked":
                await fixture.Db.Set<ServicePrincipalSecret>().ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "revoked")); break;
            case "credential-expired":
                await fixture.Db.Set<ServicePrincipalSecret>().ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, past)); break;
            case "credential-retired":
                await fixture.Db.Set<ServicePrincipalSecret>().ExecuteUpdateAsync(s => s.SetProperty(x => x.RetireAtUtc, past)); break;
            case "missing-current-credential":
                await fixture.Db.Set<ServicePrincipalRegistration>().ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentCredentialRevision, 2L)); break;
            case "organization-disabled":
                await fixture.Db.Organizations.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, Helpdesk.Shared.Models.EntityState.Blocked)); break;
            case "customer-disabled":
                await fixture.Db.Customers.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, Helpdesk.Shared.Models.EntityState.Blocked)); break;
            case "source-disabled":
                await fixture.Db.IncidentReceiverSources.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false)); break;
            case "source-changed":
                var changedSource = Guid.NewGuid();
                await fixture.Db.IncidentReceiverSources.ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceInstanceId, changedSource)); break;
            case "principal-binding-disabled":
                await fixture.Db.IncidentReceiverPrincipalBindings.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false)); break;
            case "scope-changed":
                await fixture.Db.Set<ServicePrincipalRegistration>().ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowedScopesJson, "[]")); break;
            case "constraint-changed":
                var changed = JsonSerializer.Serialize(ApprovedProviderSummary().Grants[1].ResourceConstraints with { TaskIds = ["unapproved-task"] });
                await fixture.Db.Set<ServicePrincipalRegistration>().ExecuteUpdateAsync(s => s.SetProperty(x => x.ResourceConstraintsJson, changed)); break;
            case "grant-invalid":
                await fixture.Db.Set<ServiceLinkAttempt>().ExecuteUpdateAsync(s => s.SetProperty(x => x.GrantSummaryJson, "{}")); break;
            default: throw new ArgumentOutOfRangeException(nameof(changedAuthority));
        }

        var durableAttempt = await fixture.Db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
        Assert.True(durableAttempt.LocalInboundActive);
        Assert.True(durableAttempt.LocalBusinessSenderEnabled);
        Assert.Equal("active", durableAttempt.LifecycleState);
        Assert.False((await settings.GetResolvedOrchestratorSettingsAsync()).Enabled);
        Assert.False((await settings.GetOrchestratorSettingsAsync()).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.GetAccessTokenAsync(snapshot));
        Assert.Equal(1, handler.Requests); // No cached-token reuse or token-endpoint call after the durable grant changed.
    }

    [Theory]
    [InlineData("Enabled", "false")]
    [InlineData("Enabled", null)]
    [InlineData("Issuer", "https://changed-issuer.example.test")]
    [InlineData("Audience", "changed.services")]
    [InlineData("InstanceId", "changed-instance")]
    [InlineData("ApiBaseUrl", "https://changed-api.example.test")]
    [InlineData("WebBaseUrl", "https://changed-web.example.test")]
    public async Task Held_linked_snapshot_observes_current_local_service_identity_without_changing_stored_grant(string field, string? changedValue)
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = fixture.CreateService();
        var staged = await settings.StageLinkedOrchestratorSettingsAsync(LinkedRequest(0), LinkedBinding());
        await settings.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, staged.Revision, true);
        await RecordApprovedSenderAuthorityAsync(fixture);
        var snapshot = await settings.GetResolvedOrchestratorSettingsAsync();
        Assert.True(snapshot.Enabled);
        using var provider = new ServiceCollection().AddSingleton<IIntegrationProviderSettingsService>(settings)
            .AddSingleton(fixture.IdentityOptions).AddSingleton(fixture.LinkOptions).BuildServiceProvider();
        var factory = Substitute.For<IHttpClientFactory>();
        var handler = new StaticTokenHandler();
        using var http = new HttpClient(handler);
        factory.CreateClient("OrchestrationToken").Returns(http);
        factory.CreateClient(ServiceLinkOutboundNetwork.TokenClientName).Returns(http);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var tokens = new OrchestrationTokenService(factory, memory, scopes: provider.GetRequiredService<IServiceScopeFactory>(),
            currentLinkOptions: provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());
        await tokens.GetAccessTokenAsync(snapshot);
        fixture.Configuration[$"ServiceIdentity:{field}"] = changedValue;
        fixture.Configuration.Reload();

        var current = await settings.GetResolvedOrchestratorSettingsAsync();
        Assert.False(current.Enabled);
        Assert.Equal(snapshot.Revision, current.Revision);
        Assert.Equal(snapshot.ServiceLink!.GrantHash, current.ServiceLink!.GrantHash);
        Assert.False((await settings.GetOrchestratorSettingsAsync()).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.GetAccessTokenAsync(snapshot));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Reconnect_replaces_only_terminal_disabled_same_tenant_link_and_requires_fresh_secret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var staged = await service.StageLinkedOrchestratorSettingsAsync(LinkedRequest(0), LinkedBinding());
        await RecordApprovedSenderAuthorityAsync(fixture);
        var nextBinding = LinkedBinding() with { LinkId = "newly-approved-reconnect" };
        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() =>
            service.StageLinkedOrchestratorSettingsAsync(LinkedRequest(staged.Revision), nextBinding));
        var stopped = await service.SetLinkedOrchestratorSenderEnabledAsync("approved-link", 1, staged.Revision, false);
        var oldAttempt = await fixture.Db.Set<ServiceLinkAttempt>().SingleAsync();
        oldAttempt.LifecycleState = "revoked"; oldAttempt.LocalInboundActive = false; oldAttempt.LocalBusinessSenderEnabled = false;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => service.StageLinkedOrchestratorSettingsAsync(
            LinkedRequest(stopped.Revision), nextBinding with { LocalTenantId = "foreign-organization" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StageLinkedOrchestratorSettingsAsync(LinkedRequest(stopped.Revision, null), nextBinding));
        var replacement = await service.StageLinkedOrchestratorSettingsAsync(LinkedRequest(stopped.Revision, "synthetic-reconnect-secret"), nextBinding);
        Assert.Equal("newly-approved-reconnect", replacement.LinkId);
        Assert.False(replacement.Enabled);
        Assert.Single(await fixture.Db.M2MConnectivitySettings.ToListAsync());
        Assert.Equal("synthetic-reconnect-secret", (await service.GetResolvedOrchestratorSettingsAsync()).ClientSecret);
        Assert.Equal("revoked", (await fixture.Db.Set<ServiceLinkAttempt>().SingleAsync()).LifecycleState);
    }

    [Fact]
    public async Task Held_manual_database_profile_cannot_send_cached_tokens_after_host_rotation_or_disable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = fixture.CreateService();
        await settings.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0, Enabled = true, BaseUrl = "https://original.example.test",
            ClientId = "manual-outbound", ClientSecret = "synthetic-original-secret"
        });
        using var provider = new ServiceCollection().AddSingleton<IIntegrationProviderSettingsService>(settings)
            .AddSingleton(fixture.IdentityOptions).AddSingleton(fixture.LinkOptions).BuildServiceProvider();
        var factory = Substitute.For<IHttpClientFactory>();
        var handler = new StaticTokenHandler();
        using var http = new HttpClient(handler);
        factory.CreateClient("OrchestrationToken").Returns(http);
        factory.CreateClient(ServiceLinkOutboundNetwork.TokenClientName).Returns(http);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var tokens = new OrchestrationTokenService(factory, memory, scopes: provider.GetRequiredService<IServiceScopeFactory>(),
            currentLinkOptions: provider.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());
        var original = await settings.GetResolvedOrchestratorSettingsAsync();
        await tokens.GetAccessTokenAsync(original);
        await settings.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = original.Revision, Enabled = true, BaseUrl = "https://successor.example.test",
            ClientId = "manual-outbound", ClientSecret = "synthetic-successor-secret"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.GetAccessTokenAsync(original));
        Assert.Equal(1, handler.Requests);
        var successor = await settings.GetResolvedOrchestratorSettingsAsync();
        await tokens.GetAccessTokenAsync(successor);
        Assert.Equal(2, handler.Requests);
        await fixture.Db.M2MConnectivitySettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.GetAccessTokenAsync(successor));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public void Canonical_configuration_takes_precedence_over_legacy_aliases()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:BaseUrl"] = "https://canonical.example.test",
                ["Orchestrator:Enabled"] = "false",
                ["Orchestration:Provider:BaseUrl"] = "https://legacy.example.test",
                ["Orchestration:Provider:Enabled"] = "true",
                ["Netclaw:Endpoint"] = "https://canonical.example.test/hub/session",
                ["AiAssistantChat:Endpoint"] = "https://legacy.example.test/hub/session"
            })
            .Build();

        var orchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);
        var netclaw = IntegrationConfigurationAliases.ReadNetclaw(configuration);

        Assert.False(orchestrator.Enabled);
        Assert.Equal("https://canonical.example.test", orchestrator.BaseUrl);
        Assert.Equal("https://canonical.example.test/hub/session", netclaw.Endpoint);
    }

    [Theory]
    [InlineData("Authority")]
    [InlineData("TokenEndpoint")]
    [InlineData("Audience")]
    [InlineData("Scope")]
    public async Task Explicit_empty_operator_identity_does_not_acquire_an_inferred_default(string field)
    {
        await using var fixture = await Fixture.CreateAsync();
        var values = new Dictionary<string, string?>
        {
            ["Orchestrator:BaseUrl"] = "https://peer.example.test", ["Orchestrator:Enabled"] = "true",
            ["Orchestrator:ClientId"] = "deployment-client", ["Orchestrator:ClientSecret"] = "synthetic-deployment-secret",
            [$"Orchestrator:{field}"] = ""
        };
        var resolved = await fixture.CreateService(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).GetResolvedOrchestratorSettingsAsync();
        var actual = field switch { "Authority" => resolved.Authority, "TokenEndpoint" => resolved.TokenEndpoint, "Audience" => resolved.Audience, _ => resolved.Scope };
        Assert.Equal(string.Empty, actual);
        var tokens = new OrchestrationTokenService(Substitute.For<IHttpClientFactory>(), new MemoryCache(new MemoryCacheOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.GetAccessTokenAsync(resolved));
    }

    [Fact]
    public void Legacy_provider_partial_credential_cannot_borrow_an_unrelated_global_secret()
    {
        var options = IntegrationConfigurationAliases.ReadOrchestrator(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orchestration:Provider:BaseUrl"] = "https://legacy.example.test", ["Orchestration:Provider:ClientId"] = "provider-client",
            ["M2M:ClientId"] = "global-client", ["M2M:ClientSecret"] = "global-secret"
        }).Build());
        Assert.Equal("provider-client", options.ClientId);
        Assert.Null(options.ClientSecret);
    }

    [Fact]
    public void Higher_priority_legacy_configuration_can_override_packaged_canonical_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "false",
                ["Orchestrator:BaseUrl"] = "",
                ["Orchestrator:ProviderName"] = "NetRatel orchestrator"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestration:Provider:Enabled"] = "true",
                ["Orchestration:Provider:BaseUrl"] = "https://legacy.example.test",
                ["M2M:ClientId"] = "legacy-client",
                ["M2M:ClientSecret"] = "legacy-secret"
            })
            .Build();

        var orchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);

        Assert.True(orchestrator.Enabled);
        Assert.Equal("https://legacy.example.test", orchestrator.BaseUrl);
        Assert.Equal("legacy-client", orchestrator.ClientId);
        Assert.Equal("legacy-secret", orchestrator.ClientSecret);
    }

    [Fact]
    public void Explicit_canonical_disable_wins_over_lower_priority_legacy_enable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestration:Provider:Enabled"] = "true",
                ["Orchestration:Provider:BaseUrl"] = "https://legacy.example.test"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "false",
                ["Orchestrator:BaseUrl"] = "https://disabled.example.test"
            })
            .Build();

        var orchestrator = IntegrationConfigurationAliases.ReadOrchestrator(configuration);

        Assert.False(orchestrator.Enabled);
        Assert.Equal("https://disabled.example.test", orchestrator.BaseUrl);
    }

    [Fact]
    public void Packaged_appsettings_defaults_are_database_managed()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Helpdesk.sln")))
            repository = repository.Parent;
        var path = Path.Combine(repository?.FullName ?? throw new InvalidOperationException("Repository root was not found."), "src", "Helpdesk.API", "appsettings.json");
        Assert.True(File.Exists(path), $"Expected the repository appsettings file at {path}.");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        Assert.False(IntegrationConfigurationAliases.HasDeploymentOrchestratorConfiguration(configuration));
        Assert.False(IntegrationConfigurationAliases.HasDeploymentNetclawConfiguration(configuration));
    }

    [Fact]
    public void Explicit_empty_operator_values_are_authoritative()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:Enabled"] = "",
                ["Orchestrator:BaseUrl"] = ""
            })
            .Build();

        Assert.True(IntegrationConfigurationAliases.HasDeploymentOrchestratorConfiguration(configuration));
        Assert.Equal("Orchestrator", IntegrationConfigurationAliases.GetDeploymentSourceKey(configuration, netclaw: false));
    }

    [Fact]
    public void Malformed_deployment_values_fail_with_the_controlling_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Netclaw:Enabled"] = "sometimes"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => IntegrationConfigurationAliases.ReadNetclaw(configuration));

        Assert.Contains("Netclaw:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sometimes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deployment_reads_do_not_invent_a_saved_timestamp()
    {
        await using var fixture = await Fixture.CreateAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orchestrator:BaseUrl"] = "https://netratel.example.test"
            })
            .Build();
        var service = fixture.CreateService(configuration);

        var first = await service.GetOrchestratorSettingsAsync();
        var second = await service.GetOrchestratorSettingsAsync();

        Assert.True(first.ManagedByDeployment);
        Assert.Null(first.UpdatedAtUtc);
        Assert.Null(second.UpdatedAtUtc);
    }

    [Fact]
    public async Task Provider_secrets_are_protected_and_revision_conflicts_are_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            BaseUrl = "https://netratel.example.test",
            Authority = "https://netratel.example.test",
            ClientId = "rateldesk-beta4",
            ClientSecret = "synthetic-secret",
            Scope = "netratel.api"
        });

        Assert.Equal(1, saved.Revision);
        Assert.True(saved.HasClientSecret);
        var stored = await fixture.Db.M2MConnectivitySettings.SingleAsync();
        Assert.NotEqual("synthetic-secret", stored.ProtectedClientSecret);
        Assert.NotEqual(string.Empty, stored.ProtectedClientSecret);

        var resolved = await service.GetResolvedOrchestratorSettingsAsync();
        Assert.Equal("synthetic-secret", resolved.ClientSecret);
        Assert.Equal("/internal/health", resolved.HealthPath);

        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() =>
            service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
            {
                ExpectedRevision = 0,
                Enabled = false
            }));
    }

    [Fact]
    public async Task Retained_secret_cannot_cross_an_authenticated_destination_boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            BaseUrl = "https://provider-a.example.test",
            Authority = "https://provider-a.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider-b.example.test",
            Authority = "https://provider-b.example.test",
            ClientId = "client-a"
        }));

        var metadata = await service.GetOrchestratorSettingsAsync();
        Assert.True(metadata.HasClientSecret);
        Assert.Equal("configured", metadata.SecretState);
    }

    [Fact]
    public async Task Same_authenticated_destination_may_retain_secret_without_decrypting_metadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        var updated = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            RemoteSystemName = "renamed"
        });

        Assert.True(updated.HasClientSecret);
        Assert.Equal("configured", updated.SecretState);
        Assert.Equal("secret-a", (await service.GetResolvedOrchestratorSettingsAsync()).ClientSecret);
    }

    [Fact]
    public async Task Draft_diagnostic_resolves_a_candidate_without_persisting_or_rebinding_a_secret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        var draft = await service.ResolveOrchestratorDraftAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            RemoteSystemName = "draft-only"
        });

        Assert.Equal("draft", draft.Source);
        Assert.Equal(saved.Revision, draft.Revision);
        Assert.Equal("secret-a", draft.ClientSecret);
        Assert.Equal("draft-only", draft.RemoteSystemName);
        var stored = await fixture.Db.M2MConnectivitySettings.SingleAsync();
        Assert.Equal(saved.Revision, stored.Revision);
        Assert.Equal("https://provider.example.test", stored.RemoteBaseUrl);
        Assert.Null(stored.LastTestedAtUtc);
    }

    [Fact]
    public async Task Draft_diagnostic_rejects_a_changed_destination_without_a_replacement_secret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var saved = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            BaseUrl = "https://provider-a.example.test",
            Authority = "https://provider-a.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveOrchestratorDraftAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = saved.Revision,
            Enabled = true,
            BaseUrl = "https://provider-b.example.test",
            Authority = "https://provider-b.example.test",
            ClientId = "client-a"
        }));

        Assert.Equal(saved.Revision, (await fixture.Db.M2MConnectivitySettings.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Netclaw_draft_diagnostic_does_not_apply_or_persist_candidate_settings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var draft = await service.ResolveNetclawDraftAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = false,
            Endpoint = "https://netclaw.example.test/hub/session",
            DeviceToken = "draft-device-token",
            Instance = "dev"
        });

        Assert.Equal("draft", draft.Source);
        Assert.Equal("draft-device-token", draft.DeviceToken);
        Assert.Empty(await fixture.Db.NetclawConnectivitySettings.ToListAsync());
        Assert.False(fixture.ChatOptions.Value.Enabled);
    }

    [Fact]
    public async Task Pairing_preflight_rechecks_the_current_revision_after_loading_a_tracked_draft()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-netclaw-revision-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("test");
        var baseOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(connectionString)
            .Options;
        try
        {
            await using (var initializer = new HelpdeskDbContext(baseOptions, tenant, new HttpContextAccessor()))
            {
                await initializer.Database.EnsureCreatedAsync();
                initializer.NetclawConnectivitySettings.Add(new NetclawConnectivitySettings
                {
                    ProviderKey = "Netclaw",
                    Revision = 1,
                    Endpoint = "https://saved-provider.example.test/hub/session",
                    ProfileFingerprint = "synthetic-profile-revision-1"
                });
                await initializer.SaveChangesAsync();
            }

            async Task AdvanceProfileRevisionAsync(CancellationToken cancellationToken)
            {
                await using var concurrentEditor = new HelpdeskDbContext(baseOptions, tenant, new HttpContextAccessor());
                var stored = await concurrentEditor.NetclawConnectivitySettings.SingleAsync(cancellationToken);
                stored.Revision = 2;
                stored.ProfileFingerprint = "synthetic-profile-revision-2";
                await concurrentEditor.SaveChangesAsync(cancellationToken);
            }

            var interceptor = new AdvanceProfileRevisionBeforeSecondReadInterceptor(AdvanceProfileRevisionAsync);
            var interceptedOptions = new DbContextOptionsBuilder<HelpdeskDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var serviceDb = new HelpdeskDbContext(interceptedOptions, tenant, new HttpContextAccessor());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            using var currentOptions = CreateCurrentOptions(configuration);
            var service = new IntegrationProviderSettingsService(
                serviceDb,
                configuration,
                new IntegrationProviderSecretProtector(new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                Options.Create(new AiAssistantChatOptions()),
                new DatabaseOptions { Provider = "PostgreSql" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance,
                currentIdentityOptions: currentOptions.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>(),
                currentLinkOptions: currentOptions.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());

            var exception = await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() =>
                service.ResolveNetclawPairingTargetAsync(new UpdateNetclawConnectivitySettingsDto
                {
                    ExpectedRevision = 1,
                    Enabled = true,
                    Instance = "dev",
                    Endpoint = "https://new-provider.example.test",
                    DeviceToken = "synthetic-preflight-token"
                }, "synthetic-admin", legacyOwnershipReviewToken: null));

            Assert.Equal("configuration_revision_conflict", exception.Code);
            Assert.Equal(2, interceptor.ProfileRevision);
            await using var verification = new HelpdeskDbContext(baseOptions, tenant, new HttpContextAccessor());
            Assert.Equal(2, (await verification.NetclawConnectivitySettings.SingleAsync()).Revision);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Missing_data_protection_key_is_safe_and_exposed_as_unavailable_only_when_resolved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var protectingProvider = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
        var service = fixture.CreateService(protectionProvider: protectingProvider);
        await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            BaseUrl = "https://provider.example.test",
            Authority = "https://provider.example.test",
            ClientId = "client-a",
            ClientSecret = "secret-a"
        });

        var metadataService = fixture.CreateService(protectionProvider: new EphemeralDataProtectionProvider(NullLoggerFactory.Instance));
        var metadata = await metadataService.GetOrchestratorSettingsAsync();
        Assert.True(metadata.HasClientSecret);
        Assert.Equal("configured", metadata.SecretState);

        var resolved = await metadataService.GetResolvedOrchestratorSettingsAsync();
        Assert.True(resolved.SecretUnavailable);
        Assert.Equal("unavailable", resolved.SecretState);
        Assert.Null(resolved.ClientSecret);
    }

    [Fact]
    public async Task Concurrent_first_profile_creation_has_one_winner()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-provider-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            await using (var initializer = new HelpdeskDbContext(options, tenant, new HttpContextAccessor()))
                await initializer.Database.EnsureCreatedAsync();

            await using var firstDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await using var secondDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
            using var currentOptions = CreateCurrentOptions(configuration);
            var first = CreateService(firstDb, configuration, protection, currentOptions);
            var second = CreateService(secondDb, configuration, protection, currentOptions);
            var request = new UpdateOrchestrationConnectivitySettingsDto
            {
                ExpectedRevision = 0,
                Enabled = false,
                BaseUrl = "https://provider.example.test"
            };

            var saves = new[]
            {
                first.UpdateOrchestratorSettingsAsync(request),
                second.UpdateOrchestratorSettingsAsync(request)
            };
            try { await Task.WhenAll(saves); }
            catch (Exception)
            {
                var failedSave = saves.Single(x => x.IsFaulted);
                Assert.IsType<IntegrationProviderConfigurationConflictException>(failedSave.Exception?.InnerException);
            }

            await using var verification = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            Assert.Single(await verification.M2MConnectivitySettings.ToListAsync());
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Test_result_for_an_old_revision_cannot_overwrite_current_profile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var first = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = false,
            BaseUrl = "https://provider.example.test"
        });
        var oldFingerprint = first.ProfileFingerprint;
        var current = await service.UpdateOrchestratorSettingsAsync(new UpdateOrchestrationConnectivitySettingsDto
        {
            ExpectedRevision = first.Revision,
            Enabled = false,
            BaseUrl = "https://provider.example.test",
            RemoteSystemName = "new profile"
        });

        Assert.False(await service.RecordOrchestratorTestAsync(first.Revision, oldFingerprint, succeeded: true));
        var stored = await fixture.Db.M2MConnectivitySettings.SingleAsync();
        Assert.Equal(current.Revision, stored.Revision);
        Assert.Null(stored.LastTestedAtUtc);
        Assert.Null(stored.LastTestSucceeded);
    }

    [Fact]
    public async Task Persisted_netclaw_profile_is_stored_but_not_enabled_on_sqlite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var saved = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Enabled = true,
            Endpoint = "https://netclaw.example.test/hub/session",
            DeviceToken = "synthetic-device-token"
        });

        Assert.True(saved.Enabled);
        Assert.False(saved.RuntimeSupported);
        Assert.Contains("PostgreSQL", saved.RuntimeIssue, StringComparison.Ordinal);
        Assert.False(fixture.ChatOptions.Value.Enabled);
        Assert.NotEqual("synthetic-device-token", (await fixture.Db.NetclawConnectivitySettings.SingleAsync()).ProtectedDeviceToken);
    }

    [Fact]
    public async Task Legacy_session_owner_must_be_explicitly_confirmed_before_database_provider_change()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync();
        var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
        var service = fixture.CreateService(protectionProvider: protection);

        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Endpoint = "https://provider-b.example.test/hub/session",
            DeviceToken = "synthetic-token-b"
        }));
        var confirmed = await service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = "https://provider-a.example.test/hub/session",
            ExpectedEligibleConversations = 1,
            ConversationIds = [legacy.Id]
        }, "synthetic-admin");
        Assert.Equal(1, confirmed.BoundConversations);
        Assert.Equal(confirmed.ProviderProfileFingerprint,
            (await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).ProviderProfileFingerprint);
        var audit = await fixture.Db.ActivityLogs.SingleAsync();
        Assert.Equal("synthetic-admin", audit.UserId);
        Assert.Contains("HistoricalInstance=dev", audit.Message, StringComparison.Ordinal);
        Assert.Contains("HistoricalEndpoint=https://provider-a.example.test/hub/session", audit.Message, StringComparison.Ordinal);
        Assert.Contains($"ProviderFingerprint={confirmed.ProviderProfileFingerprint}", audit.Message, StringComparison.Ordinal);
        Assert.Contains("BoundConversations=1", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-token", audit.Message, StringComparison.Ordinal);

        var providerA = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = 0,
            Endpoint = "https://provider-a.example.test/hub/session",
            DeviceToken = "synthetic-token-a"
        });
        Assert.Equal(confirmed.ProviderProfileFingerprint, providerA.ProfileFingerprint);
        var rotatedA = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = providerA.Revision,
            Endpoint = providerA.Endpoint,
            DeviceToken = "synthetic-token-a-rotated"
        });
        Assert.Equal(providerA.ProfileFingerprint, rotatedA.ProfileFingerprint);

        var providerB = await service.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
        {
            ExpectedRevision = rotatedA.Revision,
            Endpoint = "https://provider-b.example.test/hub/session",
            DeviceToken = "synthetic-token-b"
        });
        var restartedService = fixture.CreateService(protectionProvider: protection);
        var resolvedB = await restartedService.GetResolvedNetclawSettingsAsync();
        Assert.Equal(providerB.ProfileFingerprint, resolvedB.ProfileFingerprint);
        Assert.False(resolvedB.CanAdoptLegacySessions);
        Assert.NotEqual(resolvedB.ProfileFingerprint,
            (await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).ProviderProfileFingerprint);
    }

    [Theory]
    [InlineData("http://10.23.45.67/hub/session")]
    [InlineData("http://[fd12:3456:789a::42]/hub/session")]
    public async Task Legacy_confirmation_accepts_explicit_private_http_and_preserves_only_selected_history(string historicalEndpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var selected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "selected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session-a",
            ProviderProfileFingerprint = null
        };
        var unselected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "unselected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session-b",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().AddRange(selected, unselected);
        fixture.Db.Set<AiAssistantChatEvent>().AddRange(
            new AiAssistantChatEvent
            {
                ConversationId = selected.Id,
                Sequence = 1,
                Type = "user",
                Text = "Synthetic transcript to preserve"
            },
            new AiAssistantChatEvent
            {
                ConversationId = unselected.Id,
                Sequence = 1,
                Type = "user",
                Text = "Unselected transcript to preserve"
            });
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        var confirmed = await service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = historicalEndpoint,
            AllowPrivateHttp = true,
            ExpectedEligibleConversations = 1,
            ConversationIds = [selected.Id]
        }, "synthetic-admin", timeout.Token);

        Assert.Equal(1, confirmed.BoundConversations);
        var conversations = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .OrderBy(conversation => conversation.Id).ToListAsync(timeout.Token);
        var persistedSelected = Assert.Single(conversations, conversation => conversation.Id == selected.Id);
        var persistedUnselected = Assert.Single(conversations, conversation => conversation.Id == unselected.Id);
        Assert.Equal("synthetic-remote-session-a", persistedSelected.AiAssistantSessionId);
        Assert.Equal(confirmed.ProviderProfileFingerprint, persistedSelected.ProviderProfileFingerprint);
        Assert.Equal("synthetic-remote-session-b", persistedUnselected.AiAssistantSessionId);
        Assert.Null(persistedUnselected.ProviderProfileFingerprint);
        var transcript = await fixture.Db.Set<AiAssistantChatEvent>().AsNoTracking().ToListAsync(timeout.Token);
        Assert.Equal("Synthetic transcript to preserve",
            Assert.Single(transcript, chatEvent => chatEvent.ConversationId == selected.Id).Text);
        Assert.Equal("Unselected transcript to preserve",
            Assert.Single(transcript, chatEvent => chatEvent.ConversationId == unselected.Id).Text);
        var audit = await fixture.Db.ActivityLogs.SingleAsync(timeout.Token);
        Assert.Equal("synthetic-admin", audit.UserId);
        Assert.Contains("Legacy Netclaw session owner confirmed", audit.Message, StringComparison.Ordinal);
        Assert.Contains($"HistoricalEndpoint={historicalEndpoint}", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-device-token", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-remote-session-a", audit.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-remote-session-b", audit.Message, StringComparison.Ordinal);
        Assert.Single(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
    }

    [Fact]
    public async Task Legacy_confirmation_private_http_without_opt_in_leaves_session_and_audit_unchanged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "http://10.23.45.67/hub/session",
                AllowPrivateHttp = false,
                ExpectedEligibleConversations = 1,
                ConversationIds = [legacy.Id]
            },
            "synthetic-admin", timeout.Token));

        Assert.Equal("HistoricalEndpoint", exception.ParamName);
        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(conversation => conversation.Id == legacy.Id, timeout.Token);
        Assert.Equal("synthetic-remote-session", persisted.AiAssistantSessionId);
        Assert.Null(persisted.ProviderProfileFingerprint);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
    }

    [Theory]
    [InlineData("http://203.0.113.42/hub/session", "dev", true)]
    [InlineData("https://provider-a.example.test/wrong-path", "dev", false)]
    [InlineData("https://provider-a.example.test/hub/session", "production", false)]
    public async Task Legacy_confirmation_keeps_historical_endpoint_validation(
        string historicalEndpoint,
        string historicalInstance,
        bool allowPrivateHttp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-remote-session",
            ProviderProfileFingerprint = null
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = historicalInstance,
            HistoricalEndpoint = historicalEndpoint,
            AllowPrivateHttp = allowPrivateHttp,
            ExpectedEligibleConversations = 1,
            ConversationIds = [legacy.Id]
        }, "synthetic-admin", timeout.Token));

        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .SingleAsync(conversation => conversation.Id == legacy.Id, timeout.Token);
        Assert.Null(persisted.ProviderProfileFingerprint);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
    }

    [Fact]
    public async Task Legacy_confirmation_rejects_null_conversation_ids()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 1,
                ConversationIds = null!
            },
            "synthetic-admin"));

        Assert.Equal("ConversationIds", exception.ParamName);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync());
    }

    [Fact]
    public async Task Legacy_confirmation_rejects_duplicate_conversation_ids()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var conversationId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 1,
                ConversationIds = [conversationId, conversationId]
            },
            "synthetic-admin"));

        Assert.Equal("ConversationIds", exception.ParamName);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync());
    }

    [Fact]
    public async Task Legacy_confirmation_rejects_a_changed_eligible_count_without_binding_or_audit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var fixture = await Fixture.CreateAsync();
        var eligible = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "eligible-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-eligible-session"
        };
        var alreadyBound = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "already-bound-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "synthetic-bound-session",
            ProviderProfileFingerprint = "previous-provider-fingerprint"
        };
        fixture.Db.Set<AiAssistantChatConversation>().AddRange(eligible, alreadyBound);
        await fixture.Db.SaveChangesAsync(timeout.Token);
        var service = fixture.CreateService();

        await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 2,
                ConversationIds = [eligible.Id, alreadyBound.Id]
            },
            "synthetic-admin", timeout.Token));

        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .ToDictionaryAsync(conversation => conversation.Id, timeout.Token);
        Assert.Null(persisted[eligible.Id].ProviderProfileFingerprint);
        Assert.Equal("previous-provider-fingerprint", persisted[alreadyBound.Id].ProviderProfileFingerprint);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync(timeout.Token));
    }

    [Fact]
    public async Task Legacy_confirmation_binds_only_selected_ids_and_leaves_other_sessions_unbound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var selected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "selected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a"
        };
        var unselected = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "unselected-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-b"
        };
        fixture.Db.Set<AiAssistantChatConversation>().AddRange(selected, unselected);
        await fixture.Db.SaveChangesAsync();
        var service = fixture.CreateService();

        var confirmed = await service.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalInstance = "dev",
            HistoricalEndpoint = "https://provider-a.example.test/hub/session",
            ExpectedEligibleConversations = 1,
            ConversationIds = [selected.Id]
        }, "synthetic-admin");

        Assert.Equal(1, confirmed.BoundConversations);
        var persisted = await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking()
            .OrderBy(conversation => conversation.Id).ToListAsync();
        Assert.Equal(confirmed.ProviderProfileFingerprint,
            persisted.Single(conversation => conversation.Id == selected.Id).ProviderProfileFingerprint);
        Assert.Null(persisted.Single(conversation => conversation.Id == unselected.Id).ProviderProfileFingerprint);
        Assert.Equal(unselected.Id,
            Assert.Single(await service.GetUnboundNetclawLegacySessionsAsync()).ConversationId);
    }

    [Fact]
    public async Task Legacy_confirmation_rejects_endpoint_credentials_without_audit_record()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a"
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmNetclawLegacySessionsAsync(
            new ConfirmNetclawLegacySessionsDto
            {
                HistoricalInstance = "dev",
                HistoricalEndpoint = "https://synthetic-user:synthetic-password@provider-a.example.test/hub/session",
                ExpectedEligibleConversations = 1,
                ConversationIds = [legacy.Id]
            },
            "synthetic-admin"));

        Assert.Equal("HistoricalEndpoint", exception.ParamName);
        Assert.Empty(await fixture.Db.ActivityLogs.ToListAsync());
    }

    [Fact]
    public async Task Deployment_owned_provider_cannot_claim_unbound_or_confirmed_legacy_sessions_by_endpoint_alone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var legacy = new AiAssistantChatConversation
        {
            OrganizationId = "synthetic-tenant",
            TicketId = "synthetic-ticket",
            TicketType = "incidents",
            AiAssistantSessionId = "session-a"
        };
        fixture.Db.Set<AiAssistantChatConversation>().Add(legacy);
        await fixture.Db.SaveChangesAsync();
        var configurationA = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Netclaw:Endpoint"] = "https://provider-a.example.test/hub/session",
            ["Netclaw:Enabled"] = "true",
            ["Netclaw:DeviceToken"] = "synthetic-token-a"
        }).Build();
        var serviceA = fixture.CreateService(configurationA);
        Assert.False((await serviceA.GetResolvedNetclawSettingsAsync()).CanAdoptLegacySessions);
        var confirmed = await serviceA.ConfirmNetclawLegacySessionsAsync(new ConfirmNetclawLegacySessionsDto
        {
            HistoricalEndpoint = "https://provider-a.example.test/hub/session",
            ExpectedEligibleConversations = 1,
            ConversationIds = [legacy.Id]
        }, "synthetic-admin");
        Assert.Equal(confirmed.ProviderProfileFingerprint, (await serviceA.GetResolvedNetclawSettingsAsync()).ProfileFingerprint);

        var configurationB = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Netclaw:Endpoint"] = "https://provider-b.example.test/hub/session",
            ["Netclaw:Enabled"] = "true",
            ["Netclaw:DeviceToken"] = "synthetic-token-b"
        }).Build();
        var resolvedB = await fixture.CreateService(configurationB).GetResolvedNetclawSettingsAsync();
        Assert.False(resolvedB.CanAdoptLegacySessions);
        Assert.NotEqual(confirmed.ProviderProfileFingerprint, resolvedB.ProfileFingerprint);
        Assert.Equal(confirmed.ProviderProfileFingerprint,
            (await fixture.Db.Set<AiAssistantChatConversation>().AsNoTracking().SingleAsync(x => x.Id == legacy.Id)).ProviderProfileFingerprint);
    }

    [Fact]
    public async Task Newer_netclaw_revision_wins_when_an_older_runtime_apply_resumes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"rateldesk-runtime-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            await using (var initializer = new HelpdeskDbContext(options, tenant, new HttpContextAccessor()))
                await initializer.Database.EnsureCreatedAsync();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var protection = new EphemeralDataProtectionProvider(NullLoggerFactory.Instance);
            var runtime = new AiAssistantChatRuntimeState(Options.Create(new AiAssistantChatOptions()));
            var transport = new BlockingRuntimeTransport(runtime);
            using var currentOptions = CreateCurrentOptions(configuration);
            await using var firstDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await using var secondDb = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            var first = CreateService(firstDb, configuration, protection, currentOptions, transport, runtime);
            var second = CreateService(secondDb, configuration, protection, currentOptions, transport, runtime);

            var older = first.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
            {
                ExpectedRevision = 0,
                Enabled = false,
                Endpoint = "https://provider-a.example.test/hub/session"
            });
            await transport.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var newer = await second.UpdateNetclawSettingsAsync(new UpdateNetclawConnectivitySettingsDto
            {
                ExpectedRevision = 1,
                Enabled = false,
                Endpoint = "https://provider-b.example.test/hub/session"
            });
            transport.ReleaseFirstApply.TrySetResult();

            await Assert.ThrowsAsync<IntegrationProviderConfigurationConflictException>(() => older);
            Assert.Equal(2, newer.Revision);
            Assert.NotNull(newer.LastAppliedAtUtc);
            Assert.Equal(2, runtime.Current.Revision);
            Assert.Equal("https://provider-b.example.test/hub/session", runtime.Current.Endpoint);
            await using var verification = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            Assert.NotNull((await verification.NetclawConnectivitySettings.SingleAsync()).LastAppliedAtUtc);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static ServiceProvider CreateCurrentOptions(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddRatelDeskServiceIdentity(configuration);
        services.AddServiceLinkProtocol(configuration);
        return services.BuildServiceProvider();
    }

    private static IntegrationProviderSettingsService CreateService(
        HelpdeskDbContext db,
        IConfiguration configuration,
        IDataProtectionProvider protectionProvider,
        ServiceProvider currentOptions,
        IAiAssistantChatTransport? chatRuntime = null,
        IAiAssistantChatRuntimeState? runtimeState = null)
        => new(
            db,
            configuration,
            new IntegrationProviderSecretProtector(protectionProvider),
            Options.Create(new AiAssistantChatOptions()),
            new DatabaseOptions { Provider = "Sqlite" },
            TimeProvider.System,
            NullLogger<IntegrationProviderSettingsService>.Instance,
            chatRuntime,
            runtimeState,
            currentOptions.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>(),
            currentOptions.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());

    private sealed class AdvanceProfileRevisionBeforeSecondReadInterceptor(
        Func<CancellationToken, Task> advanceProfileRevision) : DbCommandInterceptor
    {
        private int profileReadCount;
        private int profileRevision;

        public int ProfileRevision => Volatile.Read(ref profileRevision);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("NetclawConnectivitySettings", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref profileReadCount) == 2)
            {
                await advanceProfileRevision(cancellationToken);
                Volatile.Write(ref profileRevision, 2);
            }

            return result;
        }
    }

    private sealed class BlockingRuntimeTransport(IAiAssistantChatRuntimeState runtime) : IAiAssistantChatTransport
    {
        public TaskCompletionSource FirstApplyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReconfigureAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<bool> TryReconfigureAsync(AiAssistantChatRuntimeSnapshot snapshot, CancellationToken ct)
        {
            if (snapshot.Revision == 1)
            {
                FirstApplyEntered.TrySetResult();
                await ReleaseFirstApply.Task.WaitAsync(ct);
            }

            return runtime.TryPublish(snapshot);
        }

        public Task ReconcileAsync(Guid conversation, CancellationToken ct) => Task.CompletedTask;
        public Task RetireAsync(Guid conversation, CancellationToken ct) => Task.CompletedTask;
        public Task SendAsync(Guid conversation, Guid messageId, string text, CancellationToken ct) => Task.CompletedTask;
        public Task RespondAsync(Guid conversation, string callId, string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider currentOptions;
        private readonly List<ServiceProvider> alternateOptions = [];
        public HelpdeskDbContext Db { get; }
        public IOptions<AiAssistantChatOptions> ChatOptions { get; } = Options.Create(new AiAssistantChatOptions());
        public IConfigurationRoot Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceIdentity:Enabled"] = "true", ["ServiceLinks:Enabled"] = "true", ["ServiceIdentity:Issuer"] = "https://issuer.local.example.test",
            ["ServiceIdentity:Audience"] = "rateldesk.services", ["ServiceIdentity:InstanceId"] = "local-instance",
            ["ServiceIdentity:ApiBaseUrl"] = "https://api.local.example.test", ["ServiceIdentity:WebBaseUrl"] = "https://local.example.test",
            ["ServiceLinks:ApiBaseUrl"] = "https://api.local.example.test", ["ServiceLinks:WebBaseUrl"] = "https://local.example.test"
        }).Build();
        public IOptionsMonitor<ServiceIdentityOptions> IdentityOptions => currentOptions.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>();
        public IOptionsMonitor<ServiceLinkOptions> LinkOptions => currentOptions.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();

        private Fixture(SqliteConnection connection, HelpdeskDbContext db)
        {
            this.connection = connection;
            Db = db;
            currentOptions = CreateCurrentOptions(Configuration);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("test");
            var options = new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options;
            var db = new HelpdeskDbContext(options, tenant, new HttpContextAccessor());
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public IntegrationProviderSettingsService CreateService(
            IConfiguration? configuration = null,
            IDataProtectionProvider? protectionProvider = null)
        {
            var serviceConfiguration = configuration ?? Configuration;
            var serviceOptions = currentOptions;
            if (!ReferenceEquals(serviceConfiguration, Configuration))
            {
                serviceOptions = CreateCurrentOptions(serviceConfiguration);
                alternateOptions.Add(serviceOptions);
            }
            return new(
                Db,
                serviceConfiguration,
                new IntegrationProviderSecretProtector(protectionProvider ?? new EphemeralDataProtectionProvider(NullLoggerFactory.Instance)),
                ChatOptions,
                new DatabaseOptions { Provider = "Sqlite" },
                TimeProvider.System,
                NullLogger<IntegrationProviderSettingsService>.Instance,
                currentIdentityOptions: serviceOptions.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>(),
                currentLinkOptions: serviceOptions.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var options in alternateOptions) options.Dispose();
            currentOptions.Dispose();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
