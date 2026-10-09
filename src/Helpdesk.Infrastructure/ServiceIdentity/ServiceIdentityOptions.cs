using Microsoft.Extensions.Options;
using Helpdesk.Infrastructure.Persistence.Connectivity;

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
}

public sealed class ServiceIdentityOptionsValidator : IValidateOptions<ServiceIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceIdentityOptions value)
    {
        var failures = new List<string>();
        foreach (var (key, address) in new[] { ("Issuer", value.Issuer), ("ApiBaseUrl", value.ApiBaseUrl), ("WebBaseUrl", value.WebBaseUrl) })
        {
            if (string.IsNullOrEmpty(address)) continue;
            try
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || address.Trim() != address || address.Any(char.IsControl)) throw new ArgumentException();
                IntegrationEndpointPolicy.Validate(uri, "ServiceIdentity:" + key, allowPrivateHttp: true);
            }
            catch (ArgumentException) { failures.Add($"ServiceIdentity:{key} requires a canonical HTTP or HTTPS URL with a trusted target."); }
        }
        if (value.InstanceId.Length > 0 && (!Guid.TryParse(value.InstanceId, out var installation) || installation == Guid.Empty)) failures.Add("ServiceIdentity:InstanceId must retain the persistent installation GUID.");
        if (string.IsNullOrWhiteSpace(value.Audience) || value.Audience.Length > 256 || value.Audience.Any(char.IsControl)) failures.Add("ServiceIdentity:Audience is required.");
        if (value.AccessTokenLifetimeSeconds is < 60 or > 900 || value.ClockSkewSeconds is < 0 or > 60) failures.Add("Service tokens must live for 60–900 seconds with at most 60 seconds skew.");
        if (value.CredentialMaximumAgeDays is < 1 or > 365) failures.Add("Service credential lifetime is outside supported bounds.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class ServiceIdentityScopes
{
    public const string IncidentCreate = "rateldesk.incidents.create";
    public const string IncidentReceipts = "rateldesk.incident-receipts.read";
    public const string IncidentTargets = "rateldesk.incident-targets.read";
    public const string Callback = "rateldesk.orchestration.callback";
    public static readonly string[] Business = [IncidentCreate, IncidentReceipts, IncidentTargets, Callback];
    public static readonly string[] All = Business;
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
    public const string MappingId = "mapping_id";
    public const string MappingRevision = "mapping_revision";
}
