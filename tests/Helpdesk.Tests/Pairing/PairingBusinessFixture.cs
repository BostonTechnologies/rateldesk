using System.Text.Json;
using Helpdesk.Infrastructure.Pairing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Shared.Pairing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Tests.Api;

internal static class PairingBusinessFixture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Client(CreatedServiceClient Created, Guid MappingId, string PairId, string PeerId);
    public static async Task<Client> CreateAsync(IncidentReceiverTests.Harness h, bool incidents = true, bool automation = false, bool draft = false)
    {
        await using var scope = h.App.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var peerId = Guid.NewGuid().ToString("D"); var pairId = SystemPairingService.PairId(h.ReceiverId.ToString("D"), peerId); var id = Guid.NewGuid();
        var metadata = new PairingMetadata(PairingContract.Version, "netratel", peerId, "Synthetic NetRatel", "https://peer.example.test", "https://peer-api.example.test", h.SourceId.ToString("D"), "synthetic-key");
        var mapping = new PairingMapping(id.ToString("D"), pairId, "Synthetic scoped mapping", "17", h.OrganizationId, incidents ? h.CustomerId : null, incidents, automation);
        var pair = new SystemPair { Id = pairId, PeerInstallationId = peerId, PeerJson = JsonSerializer.Serialize(metadata, Json), OwnerId = "owner", State = "paired" };
        var connection = new SystemConnection { Id = id, PairId = pairId, OwnerId = "owner", MappingJson = JsonSerializer.Serialize(mapping, Json), State = "draft", PairGeneration = 1 };
        db.Add(pair); db.Add(connection); await db.SaveChangesAsync();
        var scopes = new List<string>(); if (incidents) scopes.AddRange([ServiceIdentityScopes.IncidentCreate, ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets]); if (automation) scopes.Add(ServiceIdentityScopes.Callback);
        var client = await scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>().CreateAsync(new(mapping.Name, h.OrganizationId, peerId, "17", scopes.ToArray(), incidents ? [h.CustomerId] : [], incidents ? h.SourceId : null, incidents ? id : null, id), "owner");
        connection.InboundPrincipalId = client.Principal.Id; connection.State = draft ? "draft" : "connected"; await db.SaveChangesAsync();
        return new(client, id, pairId, peerId);
    }
    public static async Task<string> TokenAsync(IncidentReceiverTests.Harness h, Client client, string[]? scopes = null)
    {
        using var response = await RequestTokenAsync(h, client, scopes ?? ServicePrincipalRegistry.ReadArray(client.Created.Principal.AllowedScopesJson));
        Xunit.Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString()!;
    }
    public static Task<HttpResponseMessage> RequestTokenAsync(IncidentReceiverTests.Harness h, Client client, string[] scopes) => h.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
    { ["grant_type"] = "client_credentials", ["client_id"] = client.Created.Principal.ClientId, ["client_secret"] = client.Created.ClientSecret, ["scope"] = string.Join(' ', scopes) }));
    public static async Task<HttpResponseMessage> SendAsync(IncidentReceiverTests.Harness h, string token, HttpMethod method, string path, object? body = null, int replica = 0)
    {
        using var request = new HttpRequestMessage(method, path); request.Headers.Authorization = new("Bearer", token); request.Headers.Add("X-NetRatel-Source-Instance", h.SourceId.ToString("D"));
        if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
        return await h.ReplicaClient(replica).SendAsync(request);
    }
}
