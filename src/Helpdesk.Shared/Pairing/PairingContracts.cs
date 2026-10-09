namespace Helpdesk.Shared.Pairing;

public static class PairingContract
{
    public const string Version = "bostec.pairing.v1";
    public const string Root = "/api/pairing/v1";
    public const string PeerHeader = "X-Pairing-Peer";
}
public sealed record PairingMetadata(string Contract, string Product, string InstallationId, string Name,
    string WebOrigin, string ApiOrigin, string? ProducerInstanceId, string SigningPublicKey = "", string? ReceiverInstanceId = null);
public sealed record PairingMetadataProof(PairingMetadata Metadata, string Nonce, string Signature);
public sealed record PairingCodeDto(string Code, DateTimeOffset ExpiresAtUtc);
public sealed record PairingConnectRequest(string Address, string PairingCode, string OperationId);
public sealed record PairingExchangeRequest(string Code, string OperationId, PairingMetadata Peer, string InboundSecret, string Signature = "");
public sealed record PairingExchangeResponse(string PairId, PairingMetadata Peer, string InboundSecret, string Signature = "");
public sealed record PairingChoice(string Id, string Name, string? ParentId = null);
public sealed record PairingDirectory(PairingChoice[] Tenants, PairingChoice[] Customers);
public sealed record PairingSetupDirectory(PairingChoice[] NetRatelTenants, PairingChoice[] RatelDeskOrganizations, PairingChoice[] RatelDeskCustomers);
public sealed record PairingMapping(string Id, string PairId, string Name, string NetRatelTenantId,
    string RatelDeskOrganizationId, string? RatelDeskCustomerId, bool CreateIncidents, bool RunAutomation);
public sealed record PairingBusinessCredential(string ClientId, string ClientSecret, string TokenEndpoint,
    string Audience, string Issuer, string[] Scopes, string? SourceInstanceId, string? SourceNamespaceId);
public sealed record PairingSaveRequest(string OperationId, long Revision, PairingMapping Mapping, PairingBusinessCredential? Credential);
public sealed record PairingSaveResponse(PairingMapping Mapping, PairingBusinessCredential? Credential);
public sealed record PairingTestResult(bool Success, string Message, DateTimeOffset TestedAtUtc);
public sealed record PairingConnectionDto(string Id, string PairId, PairingMapping? Mapping, PairingMetadata Peer,
    string Status, PairingTestResult? LastTest);

