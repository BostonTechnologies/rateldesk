using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.ServiceIdentity;

/// <summary>Separate from human login, SystemToken and outbound Orchestrator credentials.</summary>
public sealed class ServiceIdentityOptions
{
    public const string SectionName = "ServiceIdentity";
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = "rateldesk.services";
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string WebBaseUrl { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public bool AllowPrivateHttp { get; set; }
    public int AccessTokenLifetimeSeconds { get; set; } = 300;
    public int ClockSkewSeconds { get; set; } = 15;
    public int CredentialMaximumAgeDays { get; set; } = 90;
    public int CredentialOverlapSeconds { get; set; } = 600;
    public int TerminalControlRecoverySeconds { get; set; } = 3600;
    public List<DeploymentServiceClientOptions> Clients { get; set; } = [];
}

public sealed class DeploymentServiceClientOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Name { get; set; } = "Deployment NetRatel client";
    public string OrganizationId { get; set; } = string.Empty;
    public string PeerInstanceId { get; set; } = string.Empty;
    public string PeerTenantId { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
    public string[] CustomerIds { get; set; } = [];
    public string ResourceConstraintsJson { get; set; } = "{}";
    public Guid? SourceInstanceId { get; set; }
    public Guid? SourceNamespaceId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ServiceIdentityOptionsValidator : IValidateOptions<ServiceIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceIdentityOptions value)
    {
        if (!value.Enabled) return ValidateOptionsResult.Success;
        var failures = new List<string>();
        foreach (var (key, address) in new[] { ("Issuer", value.Issuer), ("ApiBaseUrl", value.ApiBaseUrl), ("WebBaseUrl", value.WebBaseUrl) })
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || address.Trim() != address || address.Any(char.IsControl) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                (uri.Scheme != Uri.UriSchemeHttps && !(value.AllowPrivateHttp && uri.Scheme == Uri.UriSchemeHttp)))
                failures.Add($"ServiceIdentity:{key} requires a canonical configured HTTPS URL (HTTP needs explicit deployment opt-in).");
        }
        if (string.IsNullOrWhiteSpace(value.InstanceId) || value.InstanceId.Length > 256 || value.InstanceId.Any(char.IsControl)) failures.Add("ServiceIdentity:InstanceId requires a stable installation identity.");
        if (string.IsNullOrWhiteSpace(value.Audience) || value.Audience.Length > 256 || value.Audience.Any(char.IsControl)) failures.Add("ServiceIdentity:Audience is required.");
        if (value.AccessTokenLifetimeSeconds is < 60 or > 900 || value.ClockSkewSeconds is < 0 or > 60) failures.Add("Service tokens must live for 60–900 seconds with at most 60 seconds skew.");
        if (value.CredentialMaximumAgeDays is < 1 or > 365 || value.CredentialOverlapSeconds is < 60 or > 86400 || value.TerminalControlRecoverySeconds is < 60 or > 86400) failures.Add("Service credential lifetime/overlap/recovery settings are outside supported bounds.");
        if (value.Clients.Select(x => x.ClientId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Clients.Count) failures.Add("Deployment service client IDs must be unique ignoring case.");
        foreach (var client in value.Clients)
        {
            if (!ServicePrincipalRegistry.IsValidClientId(client.ClientId) || client.ClientSecret.Length is < 32 or > 1024 || string.IsNullOrWhiteSpace(client.OrganizationId) || string.IsNullOrWhiteSpace(client.PeerInstanceId) || string.IsNullOrWhiteSpace(client.PeerTenantId)) failures.Add("Every deployment client needs a complete client identity, strong secret, organization and explicit peer instance/tenant.");
            if (client.Scopes.Length == 0 || client.Scopes.Any(x => !ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal))) failures.Add("Deployment client scopes must be supported narrow service scopes.");
            if (client.Scopes.Distinct(StringComparer.Ordinal).Count() != client.Scopes.Length || client.CustomerIds.Distinct(StringComparer.Ordinal).Count() != client.CustomerIds.Length || client.CustomerIds.Any(string.IsNullOrWhiteSpace)) failures.Add("Deployment client scope/customer lists must be explicit unique sets.");
            if (client.Scopes.Any(x => x != ServiceIdentityScopes.Callback) && (client.SourceInstanceId is null || client.CustomerIds.Length != 1)) failures.Add("Incident deployment clients require the exact producer identity and one approved customer mapping.");
            if (client.SourceInstanceId is not null && client.CustomerIds.Length != 1) failures.Add("A deployment receiver source requires exactly one approved customer mapping.");
            try { ServicePrincipalRegistry.ValidateRequest(new(client.Name, client.OrganizationId, client.PeerInstanceId, client.PeerTenantId, client.Scopes, client.CustomerIds, client.SourceInstanceId, client.SourceNamespaceId, ResourceConstraintsJson: client.ResourceConstraintsJson, ClientId: client.ClientId)); }
            catch (ArgumentException ex) { failures.Add(ex.Message); }
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class ServiceIdentityScopes
{
    public const string Verify = "bostec.service-link.verify";
    public const string Control = "bostec.service-link.control";
    public const string IncidentCreate = "rateldesk.incidents.create";
    public const string IncidentReceipts = "rateldesk.incident-receipts.read";
    public const string IncidentTargets = "rateldesk.incident-targets.read";
    public const string Callback = "rateldesk.orchestration.callback";
    public static readonly string[] Business = [IncidentCreate, IncidentReceipts, IncidentTargets, Callback];
    public static readonly string[] All = [.. Business, Verify, Control];
}

public static class ServiceIdentityClaims
{
    public const string Purpose = "rateldesk_service";
    public const string PrincipalId = "service_principal_id";
    public const string CredentialRevision = "credential_revision";
    public const string GrantRevision = "service_grant_revision";
    public const string OrganizationId = "organization_id";
    public const string PeerInstanceId = "peer_instance_id";
    public const string PeerTenantId = "peer_tenant_id";
    public const string LinkId = "link_id";
    public const string LinkRevision = "link_revision";
    public const string AttemptId = "attempt_id";
    public const string GrantHash = "grant_hash";
    public const string DirectionId = "direction_id";
}
