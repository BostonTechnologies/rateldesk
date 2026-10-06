using System.Net;
using System.Text.Json;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.ServiceLink;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed partial class ServiceLinkLifecycleTests
{
    [Theory]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "disable")]
    [InlineData(true, "disable")]
    [InlineData(false, "unlink")]
    [InlineData(true, "unlink")]
    public async Task Token_response_in_flight_observes_committed_sender_authority_before_caching_or_business_post(
        bool postgres, string authorityChange)
    {
        var businessPosts = 0;
        await using var peer = await NetRatelServiceLinkContractPeer.CreateAsync((app, remote) =>
        {
            Func<HttpContext, Task<IResult>> handler = http => AcknowledgeDispatchAsync(http, remote,
                () => Interlocked.Increment(ref businessPosts));
            app.MapPost("/internal/ingest", (Delegate)handler);
        });
        var gate = new DispatchTokenResponseGate();
        await using var local = await LocalAsync(postgres, configure: (services, _) =>
            services.AddHttpClient(ServiceLinkOutboundNetwork.TokenClientName)
                .AddHttpMessageHandler(() => new DelayedDispatchTokenResponseHandler(gate)));
        await ActivateInitiatorAsync(local, peer);

        OrchestrationResolvedSettings snapshot;
        await using (var scope = local.Services.CreateAsyncScope())
            snapshot = await scope.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                .GetResolvedOrchestratorSettingsAsync();
        Assert.True(snapshot.Enabled);
        var binding = Assert.IsType<ServiceLinkOrchestratorBinding>(snapshot.ServiceLink);

        // The real production resolver and named safe HTTP clients are used with a dedicated real
        // cache, so this assertion does not depend on unrelated API cache entries or token text.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var factory = local.Services.GetRequiredService<IHttpClientFactory>();
        var linking = local.Services.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        var tokens = new OrchestrationTokenService(factory, cache,
            scopes: local.Services.GetRequiredService<IServiceScopeFactory>(), currentLinkOptions: linking);
        var sender = new OrchestrationInternalClient(factory, tokens,
            NullLogger<OrchestrationInternalClient>.Instance, linking);
        var acquisitionsBefore = peer.TokenAcquisitions;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var dispatch = sender.IngestAsync(snapshot, DispatchRequest(), cancellation.Token);
        try
        {
            await gate.BodyRequested.Task.WaitAsync(cancellation.Token);
            Assert.False(dispatch.IsCompleted);
            Assert.Equal(acquisitionsBefore + 1, peer.TokenAcquisitions);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, Volatile.Read(ref businessPosts));

            if (authorityChange == "disable")
            {
                // This independently owned production scope commits the profile change while
                // the sender's actual successful OAuth HTTP response is still held.
                await using var disabled = local.Services.CreateAsyncScope();
                await disabled.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                    .SetLinkedOrchestratorSenderEnabledAsync(binding.LinkId,
                        binding.LinkRevision, snapshot.Revision, false, cancellation.Token);
            }
            else if (authorityChange == "unlink")
            {
                using var unlink = await local.AdminAsync(HttpMethod.Post,
                    "/api/v1/admin/service-links/links/" + peer.LinkId + "/revoke",
                    new ServiceLinkAdminAction("administrator-unlink")).WaitAsync(cancellation.Token);
                Assert.True(unlink.IsSuccessStatusCode, await unlink.Content.ReadAsStringAsync(cancellation.Token));
                var status = await StatusAsync(local, peer.Summary.AttemptId);
                Assert.False(status.LocalInboundActive);
                Assert.False(status.LocalBusinessSenderEnabled);
                Assert.Equal("revocation_pending", status.LifecycleState);
            }

            await using (var afterCommit = local.Services.CreateAsyncScope())
            {
                var current = await afterCommit.ServiceProvider.GetRequiredService<IIntegrationProviderSettingsService>()
                    .GetResolvedOrchestratorSettingsAsync(cancellation.Token);
                Assert.Equal(authorityChange == "unchanged", current.Enabled);
                Assert.Equal(binding, current.ServiceLink);
                Assert.Equal(snapshot.ProfileFingerprint, current.ProfileFingerprint);
                Assert.Equal(snapshot.Revision + (authorityChange == "unchanged" ? 0 : 1), current.Revision);
            }
            gate.Release.TrySetResult(true);

            if (authorityChange == "unchanged")
            {
                var first = await dispatch;
                Assert.Equal(OrchestrationSubmissionDisposition.Admitted, first.Disposition);
                Assert.Equal(OrchestrationExecutionOutcome.Processing, first.Outcome);
                Assert.True(first.HasExecutionIdentity);
                Assert.Equal(1, Volatile.Read(ref businessPosts));
                Assert.Equal(1, cache.Count);

                var second = await sender.IngestAsync(snapshot, DispatchRequest(), cancellation.Token);
                Assert.Equal(OrchestrationSubmissionDisposition.Admitted, second.Disposition);
                Assert.True(second.HasExecutionIdentity);
                Assert.Equal(2, Volatile.Read(ref businessPosts));
                Assert.Equal(acquisitionsBefore + 1, peer.TokenAcquisitions);
                Assert.Equal(1, cache.Count);
            }
            else
            {
                // An exact local InvalidOperationException occurs before the business send,
                // rather than being reclassified as an uncertain remote submission.
                await Assert.ThrowsAsync<InvalidOperationException>(() => dispatch);
                Assert.Equal(0, Volatile.Read(ref businessPosts));
                Assert.Equal(0, cache.Count);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    tokens.GetAccessTokenAsync(snapshot, cancellation.Token));
                Assert.Equal(acquisitionsBefore + 1, peer.TokenAcquisitions);
                Assert.Equal(0, cache.Count);
            }
        }
        finally
        {
            gate.Release.TrySetResult(true);
            cancellation.Cancel();
        }
    }

    private static OrchestrationIngestRequest DispatchRequest() => new()
    {
        RequestId = Guid.NewGuid().ToString("D"), RequestTaskId = Guid.NewGuid().ToString("D"),
        CorrelationId = Guid.NewGuid().ToString("D"),
        OrchestrationRequestDefinitionId = "synthetic-request-definition", JobName = "synthetic-authorized-job"
    };

    private static async Task<IResult> AcknowledgeDispatchAsync(HttpContext http,
        NetRatelServiceLinkContractPeer peer, Action recordPost)
    {
        recordPost();
        if (!peer.Authorize(http, "netratel.orchestration.invoke", peer.LinkId)) return Results.Unauthorized();
        var payload = await http.Request.ReadFromJsonAsync<JsonElement>(cancellationToken: http.RequestAborted);
        var requestDefinition = payload.GetProperty("NetRatelRequestDefinitionId").GetString();
        var grant = peer.Summary.Grants.Single(x => x.TargetProduct == "netratel");
        if (requestDefinition is null ||
            !grant.ResourceConstraints.RequestDefinitionIds.Contains(requestDefinition, StringComparer.Ordinal) ||
            payload.GetProperty("CorrelationId").GetString() != http.Request.Headers["X-Correlation-Id"].ToString())
            return Results.BadRequest();
        return Results.Json(new
        {
            requestId = payload.GetProperty("RequestId").GetString(), runId = Guid.NewGuid().ToString("D"),
            executionId = Guid.NewGuid().ToString("D"), status = "accepted"
        });
    }

    private sealed class DispatchTokenResponseGate
    {
        public TaskCompletionSource<bool> BodyRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Only this test's business-token named client has this handler. Bootstrap/control traffic
    // and the real safe primary HTTP handler are unchanged, and no HTTP response is fabricated.
    private sealed class DelayedDispatchTokenResponseHandler(DispatchTokenResponseGate gate) : DelegatingHandler
    {
        private int held;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && Interlocked.Exchange(ref held, 1) == 0)
                response.Content = new HeldDispatchTokenContent(response.Content, gate);
            return response;
        }
    }

    private sealed class HeldDispatchTokenContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly DispatchTokenResponseGate gate;
        public HeldDispatchTokenContent(HttpContent inner, DispatchTokenResponseGate gate)
        {
            this.inner = inner;
            this.gate = gate;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            gate.BodyRequested.TrySetResult(true);
            await gate.Release.Task.WaitAsync(cancellationToken);
            return await inner.ReadAsStreamAsync(cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using var body = await CreateContentReadStreamAsync(cancellationToken);
            await body.CopyToAsync(stream, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = inner.Headers.ContentLength ?? 0;
            return inner.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
