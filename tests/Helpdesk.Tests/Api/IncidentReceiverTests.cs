using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Diagnostics;
using Helpdesk.API.Bootstrap;
using Helpdesk.API.Authentication;
using Helpdesk.API.Background;
using Helpdesk.API.DependencyInjection;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.API.Endpoints.Incidents;
using Helpdesk.API.Endpoints.Integrations;
using Helpdesk.API.Endpoints.Orchestration;
using Helpdesk.API.Endpoints.Pairing;
using Helpdesk.API.Services;
using Helpdesk.Application.Incidents;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Services.Email;
using Helpdesk.Infrastructure;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Helpdesk.Tests.Api;

[CollectionDefinition("Incident receiver", DisableParallelization = true)]
public sealed class IncidentReceiverCollection;

[Collection("Incident receiver")]
public sealed class IncidentReceiverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Api_processes_reopen_database_and_preserve_receipt_and_receiver_identity(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.PrepareProcessBootstrapAsync();
        var first = await h.StartApiProcessAsync();
        string accepted;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(h.Payload()) };
            h.Authorize(request, "process-restart");
            using var response = await first.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            accepted = await response.Content.ReadAsStringAsync();
        }
        finally { await Harness.StopProcessAsync(first.Process, first.Client); }
        var second = await h.StartApiProcessAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(h.Payload()) };
            h.Authorize(request, "process-restart");
            using var response = await second.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(accepted, await response.Content.ReadAsStringAsync());
            using var lookup = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrations/netratel/incident-receipts/process-restart");
            h.Authorize(lookup);
            using var receipt = await second.Client.SendAsync(lookup);
            Assert.Equal(accepted, await receipt.Content.ReadAsStringAsync());
            using var capabilityRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
            h.Authorize(capabilityRequest);
            using var capability = await second.Client.SendAsync(capabilityRequest);
            Assert.Equal(HttpStatusCode.OK, capability.StatusCode);
            var cap = JsonDocument.Parse(await capability.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(h.ReceiverId.ToString("D"), cap.GetProperty("receiverInstanceId").GetString());
            var counts = await h.CountsAsync();
            Assert.Equal(1, counts.Incidents); Assert.Equal(1, counts.Receipts);
            Assert.Equal(1, counts.Confirmation); Assert.Equal(1, counts.Support);
            using var encodedRequest = new HttpRequestMessage(HttpMethod.Get,
                new Uri(second.Client.BaseAddress + "api/v1/integrations/netratel/incident-receipts/%61",
                    new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
            h.Authorize(encodedRequest);
            using var encodedResponse = await second.Client.SendAsync(encodedRequest);
            Assert.Equal(HttpStatusCode.BadRequest, encodedResponse.StatusCode);
            foreach (var dotKey in new[] { ".", ".." })
            {
                using var dotCreate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(h.Payload()) };
                h.Authorize(dotCreate, dotKey);
                using var dotCreated = await second.Client.SendAsync(dotCreate);
                Assert.Equal(HttpStatusCode.Created, dotCreated.StatusCode);
                using var dotLookup = new HttpRequestMessage(HttpMethod.Get,
                    new Uri(second.Client.BaseAddress + "api/v1/integrations/netratel/incident-receipts/" + dotKey,
                        new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }));
                h.Authorize(dotLookup);
                using var dotReceipt = await second.Client.SendAsync(dotLookup);
                Assert.Equal(HttpStatusCode.OK, dotReceipt.StatusCode);
            }
            var concurrentProcess = await h.StartApiProcessAsync();
            try
            {
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = Enumerable.Range(0, 12).Select(async index =>
                {
                    await start.Task;
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(h.Payload()) };
                    h.Authorize(request, "process-concurrent");
                    return await (index % 2 == 0 ? second.Client : concurrentProcess.Client).SendAsync(request);
                }).ToArray();
                start.SetResult();
                var responses = await Task.WhenAll(requests);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
                Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
                Assert.Single((await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()))).Distinct());
                Assert.Equal(4, (await h.CountsAsync()).Incidents);
                Assert.Equal(4, (await h.CountsAsync()).Receipts);
                Assert.Equal(4, (await h.CountsAsync()).Confirmation);
                foreach (var concurrentResponse in responses) concurrentResponse.Dispose();
            }
            finally { await Harness.StopProcessAsync(concurrentProcess.Process, concurrentProcess.Client); }
        }
        finally { await Harness.StopProcessAsync(second.Process, second.Client); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_does_not_rerun_mutable_assignee_selection(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var payload = h.Payload(); payload.AssignedToId = "support-user";
        using var created = await h.CreateIncidentAsync("assignee-history", payload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var accepted = await created.Content.ReadAsStringAsync();
        await h.EditAsync(async db => (await db.SupportGroupMembers.SingleAsync()).IsEnabled = false);
        using var replay = await h.CreateIncidentAsync("assignee-history", payload);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(accepted, await replay.Content.ReadAsStringAsync());
        using var fresh = await h.CreateIncidentAsync("new-assignee", payload);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fresh.StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_create_and_keyed_replay_preserve_accepted_result_and_one_effect_set(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        using var nonJsonRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/")
            { Content = new StringContent("ordinary payload", Encoding.UTF8, "text/plain") };
        h.Authorize(nonJsonRequest);
        using var nonJson = await h.Client.SendAsync(nonJsonRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonJson.StatusCode);
        using var emptyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/");
        h.Authorize(emptyRequest);
        using var empty = await h.Client.SendAsync(emptyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(new Counts(0, 0, 0, 0, 0, 0, 0), await h.CountsAsync());
        using var ordinary = await h.CreateIncidentAsync(null);
        Assert.Equal(HttpStatusCode.Created, ordinary.StatusCode);
        Assert.False(JsonDocument.Parse(await ordinary.Content.ReadAsStringAsync()).RootElement.TryGetProperty("integrationReceipt", out _));
        using var created = await h.CreateIncidentAsync("one");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync();
        var accepted = JsonDocument.Parse(body).RootElement;
        var receipt = accepted.GetProperty("integrationReceipt");
        Assert.Equal(accepted.GetProperty("id").GetString(), receipt.GetProperty("incidentId").GetString());
        Assert.Equal(accepted.GetProperty("trackingId").GetString(), receipt.GetProperty("trackingId").GetString());
        Assert.Equal(accepted.GetProperty("organizationId").GetString(), receipt.GetProperty("organizationId").GetString());
        Assert.Equal(accepted.GetProperty("customerId").GetString(), receipt.GetProperty("customerId").GetString());
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var counts = await h.CountsAsync();
        await h.EditAsync(async db =>
        {
            (await db.Incidents.SingleAsync(x => x.Id == receipt.GetProperty("incidentId").GetString())).Title = "Later edit";
            (await db.TicketCategories.SingleAsync()).IsActive = false;
            var customer = await db.Customers.SingleAsync();
            customer.Email = "changed@example.test"; customer.Name = "Changed customer name";
            (await db.Organizations.SingleAsync()).Name = "Changed organization name";
        });
        using var replay = await h.CreateIncidentAsync("one");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(created.Headers.Location, replay.Headers.Location);
        Assert.Equal(counts, await h.CountsAsync());
        using var lookup = await h.GetAsync("/api/v1/integrations/netratel/incident-receipts/one");
        Assert.Equal(body, await lookup.Content.ReadAsStringAsync());
        Assert.Equal(created.Headers.Location, lookup.Headers.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_requests_across_two_hosts_converge_on_one_database_receipt(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.AddReplicaAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(0, 12).Select(async index =>
        {
            await start.Task;
            return await h.CreateIncidentAsync("concurrent", replica: index % 2);
        }).ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(pending);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, response => Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Status: {response.StatusCode}"));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        var counts = await h.CountsAsync();
        Assert.Equal(1, counts.Incidents);
        Assert.Equal(1, counts.Receipts);
        Assert.Equal(1, counts.Sla);
        Assert.Equal(1, counts.Categories);
        Assert.Equal(1, counts.Confirmation);
        Assert.Equal(1, counts.Support);
        Assert.True(counts.Effects >= 3);
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_response_after_commit_and_host_restart_reconcile_without_reexecution(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var lost = await Assert.ThrowsAsync<HttpRequestException>(() => h.CreateIncidentAsync("lost", loseResponse: true));
        Assert.IsAssignableFrom<IOException>(lost.InnerException);
        var counts = await h.CountsAsync();
        Assert.Equal(1, counts.Receipts);
        await h.RestartAsync();
        using var replay = await h.CreateIncidentAsync("lost");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(counts, await h.CountsAsync());
        using var capabilities = await h.GetAsync("/api/v1/integrations/netratel/capabilities");
        var cap = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(h.ReceiverId.ToString("D"), cap.GetProperty("receiverInstanceId").GetString());
        Assert.Equal(IncidentReceiverContract.Version, cap.GetProperty("contractVersion").GetString());
        Assert.True(cap.GetProperty("atomicIncidentReceiptAndEffects").GetBoolean());
        Assert.False(cap.GetProperty("receiptEvictionEnabled").GetBoolean());
        Assert.Equal(7776000, cap.GetProperty("minimumReceiptRetentionSeconds").GetInt32());
        Assert.Equal(2592000, cap.GetProperty("maximumAutomaticReplaySeconds").GetInt32());
        Assert.Equal("https://receiver.example.test/prefix/api/v1/incidents/", cap.GetProperty("createEndpoint").GetString());
        Assert.Equal("api_bearer", Assert.Single(cap.GetProperty("authenticationModes").EnumerateArray()).GetString());
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot, "docs", "contracts", "rateldesk-incident-create.v1.json")));
        foreach (var field in contract.RootElement.GetProperty("capabilitySchema").GetProperty("properties").EnumerateObject())
        {
            Assert.True(cap.TryGetProperty(field.Name, out var actual), $"Missing capability field: {field.Name}");
            if (field.Value.TryGetProperty("const", out var expected)) Assert.Equal(expected.GetRawText(), actual.GetRawText());
        }
        Assert.Equal(h.SourceId.ToString("D"), cap.GetProperty("sourceInstanceId").GetString());
        Assert.Equal(h.NamespaceId.ToString("D"), cap.GetProperty("sourceNamespaceId").GetString());
        await h.EditAsync(async db => (await db.IncidentCreateReceipts.SingleAsync()).CommittedAtUtc = DateTimeOffset.UtcNow.AddYears(-2));
        using var old = await h.CreateIncidentAsync("lost");
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        await using var scope = h.App.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<MailboxOutboxStore>();
        var emailEffects = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().Set<MailboxOutboxEffect>()
            .Where(x => x.Kind == MailboxEffectKind.Email && x.ReceiptId != null).ToListAsync();
        var effectId = emailEffects.Single(x => JsonSerializer.Deserialize<IngressEmailEffect>(x.Payload)!.SupportDeliveryId is null).Id;
        var claim = await outbox.TryClaimAsync(effectId, "restarted-dispatcher", default);
        Assert.NotNull(claim);
        Assert.Null(await outbox.TryClaimAsync(effectId, "other-dispatcher", default));
        Assert.True(await outbox.CompleteAsync(claim, true, null, default));
        Assert.Null(await outbox.TryClaimAsync(effectId, "another-dispatcher", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fault_before_commit_rolls_back_receipt_incident_sla_categories_and_effects(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        h.Fault.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.CreateIncidentAsync("rollback"));
        Assert.Equal(new Counts(0, 0, 0, 0, 0, 0, 0), await h.CountsAsync());
        h.Fault.Enabled = false;
        using var retried = await h.CreateIncidentAsync("rollback");
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var counts = await h.CountsAsync();
        Assert.Equal(1, counts.Sla);
        Assert.Equal(1, counts.Confirmation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Equivalent_json_and_defaults_replay_and_every_consumed_field_change_conflicts(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var original = h.Payload();
        original.CategoryIds = [];
        using var created = await h.CreateIncidentAsync("canonical", original);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var equivalent = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/");
        equivalent.Content = new StringContent(JsonSerializer.Serialize(new
        {
            categoryIds = new Guid[0], requesterEmail = "ignored@example.test", ccRecipients = new string[0],
            attachments = new string[0], linkedAssetIds = new string[0], priority = 0,
            organizationId = h.OrganizationId, customerId = h.CustomerId, description = "<b>Description</b>", title = "Incident"
        }), Encoding.UTF8, "application/json");
        h.Authorize(equivalent, "canonical");
        using var replay = await h.Client.SendAsync(equivalent);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var changes = new Action<CreateIncidentDto>[]
        {
            dto => dto.Title = "Changed", dto => dto.Description = "Changed", dto => dto.Priority = TicketPriority.High,
            dto => dto.AssignedToId = "unknown-assignee", dto => dto.LinkedAssetIds = ["asset"],
            dto => dto.Attachments = ["attachment"], dto => dto.DueDate = DateTime.UtcNow.Date,
            dto => dto.Impact = "Changed", dto => dto.CcRecipients = ["cc@example.test"], dto => dto.CategoryIds = [h.CategoryId]
        };
        foreach (var change in changes)
        {
            var altered = h.Payload(); altered.CategoryIds = []; change(altered);
            using var conflict = await h.CreateIncidentAsync("canonical", altered);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("idempotency-payload-conflict", JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
        foreach (var mappingChange in new Action<CreateIncidentDto>[] { dto => dto.CustomerId = Guid.NewGuid().ToString(), dto => dto.OrganizationId = Guid.NewGuid().ToString() })
        {
            var altered = h.Payload(); mappingChange(altered);
            using var forbidden = await h.CreateIncidentAsync("canonical", altered);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        Assert.Equal(1, (await h.CountsAsync()).Incidents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_authorization_loss_and_foreign_credentials_cannot_expose_receipts(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        using var accepted = await h.CreateIncidentAsync("secure");
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var alternatives = new[] { "revoked", "expired", "owner-disabled", "wrong-purpose", "insufficient-permission", "foreign-organization" };
        foreach (var condition in alternatives)
        {
            await h.ChangeCredentialAsync(condition);
            using var denied = await h.CreateIncidentAsync("secure");
            Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            using var lookup = await h.GetAsync("/api/v1/integrations/netratel/incident-receipts/secure");
            Assert.True(lookup.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            await h.ChangeCredentialAsync("reset");
        }
        foreach (var disabled in new[] { "customer", "organization", "source", "binding" })
        {
            await h.SetEnabledAsync(disabled, false);
            using var lookup = await h.GetAsync("/api/v1/integrations/netratel/incident-receipts/secure");
            Assert.Equal(HttpStatusCode.Forbidden, lookup.StatusCode);
            using var replay = await h.CreateIncidentAsync("secure");
            Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
            await h.SetEnabledAsync(disabled, true);
        }
        using var foreign = await h.CreateIncidentAsync("secure", source: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(1, (await h.CountsAsync()).Incidents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Administrator_rotation_binds_replacement_to_same_namespace_and_audits_grants(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        using var initial = await h.CreateIncidentAsync("rotate");
        var body = await initial.Content.ReadAsStringAsync();
        var replacement = await h.AddCredentialAsync();
        using var unaffiliated = await h.CreateIncidentAsync("rotate", credential: replacement.Token);
        Assert.Equal(HttpStatusCode.Forbidden, unaffiliated.StatusCode);
        using var integrationAdminRequest = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v1/integrations/netratel/sources/{h.NamespaceId}/principal-bindings/{replacement.Id}")
            { Content = JsonContent.Create(new { expectedRevision = 1, isEnabled = true }) };
        h.Authorize(integrationAdminRequest);
        using var integrationAdmin = await h.Client.SendAsync(integrationAdminRequest);
        Assert.Equal(HttpStatusCode.Forbidden, integrationAdmin.StatusCode);
        using var authorized = await h.AdminAsync(HttpMethod.Put, $"/api/v1/integrations/netratel/sources/{h.NamespaceId}/principal-bindings/{replacement.Id}", new { expectedRevision = 1, isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        await h.ChangeCredentialAsync("revoked");
        using var replay = await h.CreateIncidentAsync("rotate", credential: replacement.Token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        using var revoked = await h.CreateIncidentAsync("rotate");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await using var scope = h.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        Assert.Equal(2, await db.IncidentReceiverSourceAudits.CountAsync());
        Assert.Equal(2, (await db.IncidentReceiverSources.SingleAsync()).Revision);
        using var reclaim = await h.AdminAsync(HttpMethod.Post, "/api/v1/integrations/netratel/sources/", new
            { sourceInstanceId = h.SourceId, organizationId = h.OrganizationId, customerId = h.CustomerId, credentialId = replacement.Id });
        Assert.Equal(HttpStatusCode.Conflict, reclaim.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Target_validation_is_read_only_bounded_and_matches_current_create_rules(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        using var valid = await h.PostTargetAsync(new { organizationId = h.OrganizationId, customerId = h.CustomerId, categoryIds = new[] { h.CategoryId, h.CategoryId } });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var mapping = JsonDocument.Parse(await valid.Content.ReadAsStringAsync()).RootElement.GetProperty("mapping");
        Assert.Equal(JsonValueKind.Null, mapping.GetProperty("assignedToId").ValueKind);
        Assert.Single(mapping.GetProperty("categoryIds").EnumerateArray());
        using var malformed = await h.PostTargetAsync(new { organizationId = h.OrganizationId });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var foreign = await h.PostTargetAsync(new { organizationId = h.OrganizationId, customerId = "foreign" });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var badAssignee = await h.PostTargetAsync(new { organizationId = h.OrganizationId, customerId = h.CustomerId, assignedToId = "foreign-user" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badAssignee.StatusCode);
        var badPayload = h.Payload(); badPayload.AssignedToId = "foreign-user";
        using var invalidCreate = await h.CreateIncidentAsync("invalid-assignee", badPayload);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidCreate.StatusCode);
        await h.EditAsync(async db => (await db.TicketCategories.SingleAsync()).IsActive = false);
        using var badCategory = await h.PostTargetAsync(new { organizationId = h.OrganizationId, customerId = h.CustomerId, categoryIds = new[] { h.CategoryId } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCategory.StatusCode);
        using var invalidCategoryCreate = await h.CreateIncidentAsync("invalid-category");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidCategoryCreate.StatusCode);
        Assert.Equal(new Counts(0, 0, 0, 0, 0, 0, 0), await h.CountsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Header_key_body_and_lookup_bounds_never_fall_back_to_normal_create(bool postgres)
    {
        await using var h = await Harness.CreateAsync(postgres);
        foreach (var key in new[] { "", "a b", "a/b", "a\\b", "%61", "a?b", "a#b", "é", new string('a', 257) })
        {
            using var response = await h.CreateIncidentAsync(key);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var onlySource in new[] { true, false })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(h.Payload()) };
            request.Headers.Authorization = new("Bearer", h.Token);
            request.Headers.Add(onlySource ? IncidentReceiverContract.SourceHeader : IncidentReceiverContract.KeyHeader,
                onlySource ? h.SourceId.ToString("D") : "key");
            using var response = await h.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var multiple = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(h.Payload()) };
        h.Authorize(multiple, "first"); multiple.Headers.Add(IncidentReceiverContract.KeyHeader, "second");
        using var duplicate = await h.Client.SendAsync(multiple);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var oversized = h.Payload(); oversized.Description = new string('a', 140000);
        using var tooLarge = await h.CreateIncidentAsync("oversized", oversized);
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        using var lookup = await h.GetAsync("/api/v1/integrations/netratel/incident-receipts/absent");
        Assert.Equal(HttpStatusCode.NotFound, lookup.StatusCode);
        using var longest = await h.CreateIncidentAsync(new string('a', 256));
        Assert.Equal(HttpStatusCode.Created, longest.StatusCode);
        using var caseChanged = await h.CreateIncidentAsync(new string('A', 256));
        Assert.Equal(HttpStatusCode.Created, caseChanged.StatusCode);
        Assert.Equal(2, (await h.CountsAsync()).Receipts);
    }

    public sealed record Counts(int Incidents, int Receipts, int Sla, int Categories, int Effects, int Confirmation, int Support);

    internal sealed class CommitFault : SaveChangesInterceptor
    {
        public bool Enabled;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<IncidentCreateReceipt>().Any(x => x.Entity.AcceptedJson.Length > 0))
                throw new InvalidOperationException("Synthetic precommit fault.");
            return ValueTask.FromResult(result);
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly List<WebApplication> apps = [];
        private readonly List<HttpClient> clients = [];
        private PostgreSqlContainer? container;
        private readonly string root = Path.Combine(Path.GetTempPath(), "rateldesk-receiver-tests", Guid.NewGuid().ToString("N"));
        private string connectionString = string.Empty;
        private bool postgres;
        private bool serviceIdentity;
        private IHttpClientFactory? pairingPeer;
        private readonly ECDsa callbackSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public WebApplication App => apps[0];
        public HttpClient Client => clients[0];
        public HttpClient ReplicaClient(int index) => clients[index];
        public string OrganizationId { get; } = Guid.NewGuid().ToString("D");
        public string CustomerId { get; } = Guid.NewGuid().ToString("D");
        public Guid SourceId { get; } = Guid.NewGuid();
        public Guid ReceiverId { get; } = Guid.NewGuid();
        public Guid CategoryId { get; } = Guid.NewGuid();
        public Guid NamespaceId { get; private set; }
        public Guid CredentialId { get; private set; }
        public string Token { get; private set; } = string.Empty;
        public CommitFault Fault { get; } = new();

        public static async Task<Harness> CreateAsync(bool postgres, bool serviceIdentity = false, IHttpClientFactory? pairingPeer = null)
        {
            Npgsql.NpgsqlConnection.GlobalTypeMapper.EnableDynamicJson();
            var h = new Harness { postgres = postgres, serviceIdentity = serviceIdentity, pairingPeer = pairingPeer };
            Directory.CreateDirectory(h.root);
            if (postgres)
            {
                h.container = new PostgreSqlBuilder("postgres:16").Build();
                await h.container.StartAsync();
                h.connectionString = h.container.GetConnectionString();
            }
            else h.connectionString = $"Data Source={Path.Combine(h.root, "receiver.db")}";
            await h.AddReplicaAsync();
            await using (var scope = h.App.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
                await identity.Database.MigrateAsync();
                identity.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", Email = "owner@example.test", IsEnabled = true, IsInstanceAdministrator = true });
                await identity.SaveChangesAsync();
                var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
                Assert.False(db.Database.HasPendingModelChanges());
                var priorMigration = db.Database.GetMigrations().Reverse().Skip(1).First();
                await db.GetService<IMigrator>().MigrateAsync(priorMigration);
                db.InstanceInitializations.Add(new() { InstanceId = h.ReceiverId, OperationId = Guid.NewGuid(), SetupVersion = "test", CompletedAtUtc = DateTimeOffset.UtcNow });
                db.Organizations.Add(new() { Id = h.OrganizationId, Name = "Synthetic organization", IsEnabled = true });
                db.Customers.Add(new() { Id = h.CustomerId, OrganizationId = h.OrganizationId, Email = "customer@example.test", Name = "Synthetic customer" });
                db.TicketCategories.Add(new() { Id = h.CategoryId, Name = "Synthetic category", Type = TicketCategoryType.Incident, IsActive = true, CreatedUtc = DateTime.UtcNow });
                db.EmailTemplates.Add(new() { Name = "NewTicketConfirmation", Subject = "Confirmation {{TicketRef}}", HtmlContent = "<p>{{TicketRef}}</p>" });
                db.EmailTemplates.Add(new() { Name = "SupportTicketCreatedUnassigned", Subject = "Support {{TicketRef}}", HtmlContent = "<p>{{TicketRef}}</p>" });
                db.Users.Add(new() { Id = "support-user", OrganizationId = h.OrganizationId, Name = "Synthetic support", Email = "support@example.test" });
                db.SupportGroups.Add(new() { Id = "support-group", OwningOrganizationId = h.OrganizationId, Name = "Synthetic group" });
                db.SupportGroupMembers.Add(new() { SupportGroupId = "support-group", UserId = "support-user" });
                db.OrganizationSupportCoverages.Add(new() { CustomerOrganizationId = h.OrganizationId, ProviderOrganizationId = h.OrganizationId, SupportGroupId = "support-group" });
                db.SupportNotificationSubscriptions.Add(new() { CustomerOrganizationId = h.OrganizationId, RecipientId = "support-group" });
                db.SlaPolicies.Add(new() { Name = "Synthetic SLA", ResponseTimeHours = 1, ResolutionTimeHours = 4, IsActive = true });
                await db.SaveChangesAsync();
                await db.Database.MigrateAsync();
            }
            (h.CredentialId, h.Token) = await h.AddCredentialAsync();
            using var registered = await h.AdminAsync(HttpMethod.Post, "/api/v1/integrations/netratel/sources/", new
                { sourceInstanceId = h.SourceId, organizationId = h.OrganizationId, customerId = h.CustomerId, credentialId = h.CredentialId });
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            h.NamespaceId = (await registered.Content.ReadFromJsonAsync<IncidentReceiverSource>())!.SourceNamespaceId;
            return h;
        }

        public async Task AddReplicaAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = postgres ? "PostgreSql" : "Sqlite", ["Database:Sqlite:Path"] = Path.Combine(root, "receiver.db"),
                ["ConnectionStrings:HelpdeskDb"] = postgres ? connectionString : null,
                ["StorageOptions:RootPath"] = Path.Combine(root, "storage"), ["StorageOptions:PublicApiBaseUrl"] = "https://receiver.example.test/prefix",
                ["StorageOptions:ImageSigningSecret"] = "synthetic-receiver-signing-key-32-characters",
                ["PublicWebAppUrl"] = "https://receiver.example.test", ["Netclaw:Enabled"] = "false"
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ServiceIdentity:Enabled"] = serviceIdentity.ToString(),
                ["ServiceIdentity:Issuer"] = "https://receiver.example.test/services",
                ["ServiceIdentity:ApiBaseUrl"] = "https://receiver.example.test",
                ["ServiceIdentity:WebBaseUrl"] = "https://receiver-web.example.test",
                ["ServiceIdentity:Audience"] = "rateldesk.services",
                ["ServiceIdentity:InstanceId"] = ReceiverId.ToString("D")
            });
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys"))).SetApplicationName("ReceiverTests");
            builder.Services.AddHelpdeskInfrastructure(builder.Configuration);
            builder.Services.AddRatelDeskServiceIdentity(builder.Configuration);
            builder.Services.AddSystemPairing();
            if (pairingPeer is not null) builder.Services.AddSingleton(pairingPeer);
            builder.Services.RemoveAll<IHostedService>();
            builder.Services.AddDbContext<HelpdeskDbContext>(options => options.AddInterceptors(Fault));
            builder.Services.AddSingleton<ITicketRefGeneratorService, TicketRefGeneratorService>();
            builder.Services.AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>();
            builder.Services.AddRequestBus(typeof(CreateIncidentCommand).Assembly);
            builder.Services.AddScoped<IIncidentReceiverAuthorization, IncidentReceiverAuthorization>();
            builder.Services.AddScoped<IAuthorizationHandler, IncidentCreateBoundaryHandler>();
            builder.Services.AddScoped<IncidentReceiver>();
            builder.Services.AddScoped<IIntegrationCredentialOwnerResolver, IntegrationCredentialOwnerResolver>();
            builder.Services.AddScoped<IAuthorizationHandler, IntegrationCredentialManagementSessionHandler>();
            builder.Services.AddAuthentication("ReceiverTest")
                .AddPolicyScheme("ReceiverTest", "ReceiverTest", options => options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey("X-Synthetic-Admin") ? "SyntheticAdmin" :
                    context.Request.Headers.Authorization.ToString().StartsWith("Bearer rdk_", StringComparison.Ordinal)
                        ? IntegrationCredentialAuthenticationHandler.SchemeName : ServiceIdentityAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, AdminAuthentication>("SyntheticAdmin", _ => { })
                .AddScheme<IntegrationCredentialAuthenticationOptions, IntegrationCredentialAuthenticationHandler>(IntegrationCredentialAuthenticationHandler.SchemeName,
                    options => options.Purpose = "api")
;
            builder.Services.AddAuthorization(options =>
            {
                options.AddPolicy("IncidentAccess", policy =>
                {
                    policy.RequireAuthenticatedUser();
                    if (serviceIdentity) policy.RequireRole("Incident.Read", "Incident.Write", "HelpdeskAdmin");
                });
                options.AddPolicy(IncidentCreateBoundaryRequirement.Policy, policy => policy.AddRequirements(new IncidentCreateBoundaryRequirement()));
                options.AddPolicy("IncidentManager", policy => policy.RequireAuthenticatedUser());
                options.AddPolicy(IntegrationCredentialEndpoints.CredentialManagementPolicy, policy =>
                { policy.RequireAuthenticatedUser(); policy.AddRequirements(new IntegrationCredentialManagementSessionRequirement()); });
            });
            builder.Services.AddServiceOrchestrationCallbacks();
            var app = builder.Build();
            app.UseAuthentication();
            if (serviceIdentity) app.UseMiddleware<Helpdesk.API.Middleware.UserAccessClaimsMiddleware>();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.Use(async (http, next) =>
            {
                await next();
                if (http.Request.Headers.ContainsKey("X-Synthetic-Lost-Response")) throw new IOException("Synthetic response loss after commit.");
            });
            app.MapIncidentEndpoints();
            app.MapIncidentReceiverEndpoints();
            app.MapIncidentReceiverSourceEndpoints();
            app.MapServiceIdentityEndpoints();
            app.MapSystemPairing();
            app.MapExternalOrchestrationCallbackEndpoints();
            await app.StartAsync();
            apps.Add(app); clients.Add(app.GetTestClient());
        }

        public async Task RestartAsync()
        {
            foreach (var client in clients) client.Dispose(); clients.Clear();
            foreach (var app in apps) await app.DisposeAsync(); apps.Clear();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await AddReplicaAsync();
        }

        public string LegacyCallbackToken()
        {
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken("https://legacy-callback.example.test", "rateldesk.legacy-callback",
                [new Claim("client_id", "legacy-netratel-callback")], DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(new ECDsaSecurityKey(callbackSigningKey), SecurityAlgorithms.EcdsaSha256));
            return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        }

        public async Task PrepareProcessBootstrapAsync()
        {
            var options = new BootstrapOptions { StateDirectory = Path.Combine(root, "bootstrap"), DataDirectory = root };
            var store = new FileBootstrapStateStore(options);
            await store.LoadOrCreateAsync();
            await using var scope = App.Services.CreateAsyncScope();
            var marker = await scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>().InstanceInitializations.SingleAsync();
            await store.UpdateAsync(current => current with { InstanceId = ReceiverId, OperationId = marker.OperationId,
                State = BootstrapState.Configuring, Provider = postgres ? "PostgreSql" : "Sqlite", SqlitePath = postgres ? null : Path.Combine(root, "receiver.db") });
        }

        public async Task<(Process Process, HttpClient Client)> StartApiProcessAsync()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var assembly = Path.Combine(TestEnvironment.RepositoryRoot, "src", "Helpdesk.API", "bin", configuration, "net10.0", "Helpdesk.API.dll");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { WorkingDirectory = Path.Combine(TestEnvironment.RepositoryRoot, "src", "Helpdesk.API"), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(assembly);
            foreach (var pair in new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}", ["ASPNETCORE_ENVIRONMENT"] = "Production", ["Helpdesk__SkipDatabaseStartup"] = "false",
                ["Database__Provider"] = postgres ? "PostgreSql" : "Sqlite", ["Database__Sqlite__Path"] = Path.Combine(root, "receiver.db"),
                ["ConnectionStrings__HelpdeskDb"] = postgres ? connectionString : "", ["Bootstrap__StateDirectory"] = Path.Combine(root, "bootstrap"),
                ["Bootstrap__DataDirectory"] = root, ["DataProtection__KeyRingPath"] = Path.Combine(root, "bootstrap", "keys"),
                ["Authentication__Mode"] = "Local", ["Authentication__Local__AllowInsecureLocalhost"] = "true",
                ["StorageOptions__RootPath"] = Path.Combine(root, "storage"), ["StorageOptions__ImageSigningSecret"] = "synthetic-receiver-signing-key-32-characters",
                ["StorageOptions__PublicApiBaseUrl"] = "https://receiver.example.test/prefix", ["Netclaw__Enabled"] = "false",
                ["ExchangeEmail__Enabled"] = "false", ["EmailIngestion__Enabled"] = "false", ["Hangfire__Enabled"] = "false",
                ["SYSTEM_TOKEN_SECRET"] = "synthetic-process-system-secret-at-least-32-characters", ["Logging__LogLevel__Default"] = "Warning"
            }) start.Environment[pair.Key] = pair.Value;
            var process = Process.Start(start)!;
            var logs = new StringBuilder();
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) lock (logs) logs.AppendLine(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (logs) logs.AppendLine(args.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            using var readinessDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var readinessTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            try
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (process.HasExited) throw new InvalidOperationException($"API process exited: {logs}");
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrations/netratel/capabilities");
                        Authorize(request);
                        using var response = await client.SendAsync(request, readinessDeadline.Token);
                        if (response.IsSuccessStatusCode) return (process, client);
                    }
                    catch (HttpRequestException error) { lock (logs) logs.AppendLine($"API listener pending: {error.HttpRequestError}."); }
                    await readinessTimer.WaitForNextTickAsync(readinessDeadline.Token);
                }
                throw new TimeoutException($"API process readiness timed out: {logs}");
            }
            catch { await StopProcessAsync(process, client); throw; }
        }

        public static async Task StopProcessAsync(Process process, HttpClient client)
        {
            client.Dispose();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process.Dispose();
        }

        public CreateIncidentDto Payload() => new() { Title = "Incident", Description = "Description", OrganizationId = OrganizationId,
            CustomerId = CustomerId, CategoryIds = [CategoryId] };
        public void Authorize(HttpRequestMessage request, string? key = null, Guid? source = null, string? credential = null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential ?? Token);
            if (key is not null) request.Headers.Add(IncidentReceiverContract.KeyHeader, key);
            if (key is not null || request.Method == HttpMethod.Get || request.RequestUri!.ToString().EndsWith("/validate", StringComparison.Ordinal))
                request.Headers.Add(IncidentReceiverContract.SourceHeader, (source ?? SourceId).ToString("D"));
        }
        public async Task<HttpResponseMessage> CreateIncidentAsync(string? key, CreateIncidentDto? payload = null,
            int replica = 0, bool loseResponse = false, Guid? source = null, string? credential = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/") { Content = JsonContent.Create(payload ?? Payload()) };
            Authorize(request, key, source, credential);
            if (loseResponse) request.Headers.Add("X-Synthetic-Lost-Response", "true");
            return await clients[replica].SendAsync(request);
        }
        public async Task<HttpResponseMessage> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path); Authorize(request);
            return await Client.SendAsync(request);
        }
        public async Task<HttpResponseMessage> PostTargetAsync(object payload)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/integrations/netratel/targets/validate") { Content = JsonContent.Create(payload) };
            Authorize(request); return await Client.SendAsync(request);
        }
        public async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? payload = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = payload is null ? null : JsonContent.Create(payload) };
            request.Headers.Add("X-Synthetic-Admin", "true"); return await Client.SendAsync(request);
        }
        public async Task<(Guid Id, string Token)> AddCredentialAsync()
        {
            var id = Guid.NewGuid(); var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using var scope = App.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            identity.IntegrationCredentials.Add(new() { Id = id, OwnerUserId = "owner", Purpose = "api", OrganizationId = OrganizationId,
                Name = "Synthetic credential", Prefix = "rdk_test", Permissions = "Incident.Read Incident.Write", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                CreatedAtUtc = DateTimeOffset.UtcNow, SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))) });
            await identity.SaveChangesAsync(); return (id, $"rdk_{id:N}_{secret}");
        }
        public async Task<Counts> CountsAsync()
        {
            await using var scope = App.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
            var effects = await db.Set<MailboxOutboxEffect>().ToListAsync();
            return new(await db.Incidents.CountAsync(), await db.IncidentCreateReceipts.CountAsync(), await db.TicketSlaStates.CountAsync(),
                await db.IncidentCategoryLinks.CountAsync(), effects.Count,
                effects.Count(effect => effect.ReceiptId != null && effect.Kind == MailboxEffectKind.Email &&
                    JsonSerializer.Deserialize<IngressEmailEffect>(effect.Payload)!.SupportDeliveryId is null), await db.SupportNotificationDeliveries.CountAsync());
        }
        public async Task EditAsync(Func<HelpdeskDbContext, Task> edit)
        { await using var scope = App.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>(); await edit(db); await db.SaveChangesAsync(); }
        public async Task ChangeCredentialAsync(string condition)
        {
            await using var scope = App.Services.CreateAsyncScope(); var identity = scope.ServiceProvider.GetRequiredService<RatelDeskIdentityDbContext>();
            var credential = await identity.IntegrationCredentials.SingleAsync(x => x.Id == CredentialId);
            var owner = await identity.Users.SingleAsync(); owner.IsEnabled = true;
            credential.RevokedAtUtc = null; credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1); credential.Purpose = "api";
            credential.Permissions = "Incident.Read Incident.Write"; credential.OrganizationId = OrganizationId;
            switch (condition)
            {
                case "revoked": credential.RevokedAtUtc = DateTimeOffset.UtcNow; break;
                case "expired": credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-1); break;
                case "owner-disabled": owner.IsEnabled = false; break;
                case "wrong-purpose": credential.Purpose = "mcp"; break;
                case "insufficient-permission": credential.Permissions = "Incident.Delete"; break;
                case "foreign-organization": credential.OrganizationId = Guid.NewGuid().ToString(); break;
            }
            await identity.SaveChangesAsync();
        }
        public Task SetEnabledAsync(string entity, bool enabled) => EditAsync(async db =>
        {
            switch (entity)
            {
                case "customer": (await db.Customers.SingleAsync()).IsEnabled = enabled; break;
                case "organization": (await db.Organizations.SingleAsync()).IsEnabled = enabled; break;
                case "source": (await db.IncidentReceiverSources.SingleAsync()).IsEnabled = enabled; break;
                case "binding": (await db.IncidentReceiverPrincipalBindings.SingleAsync()).IsEnabled = enabled; break;
            }
        });
        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients) client.Dispose();
            foreach (var app in apps) await app.DisposeAsync();
            if (container is not null) await container.DisposeAsync();
            callbackSigningKey.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true);
        }
    }

    private sealed class AdminAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"),
                new Claim("auth_mode", "local")], Scheme.Name)), Scheme.Name)));
    }
}
