using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceIdentity;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.ServiceLink;

public sealed class ServiceLinkOptions
{
    public const string SectionName = "ServiceLinks";
    public bool Enabled { get; set; }
    public string WebBaseUrl { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string? GatewayBaseUrl { get; set; }
    public bool AllowPrivateHttp { get; set; }
    public int BootstrapLifetimeSeconds { get; set; } = 900;
    public int TerminalControlRecoverySeconds { get; set; } = 3600;
    public int WorkerIntervalSeconds { get; set; } = 15;
    public int MaximumPayloadBytes { get; set; } = 131072;
    public bool AutomaticRotationEnabled { get; set; } = true;
    public int RotationAgeDays { get; set; } = 60;
    public int RotationOfferLifetimeSeconds { get; set; } = 900;
    public int RotationOverlapSeconds { get; set; } = 600;
    public long RotationPolicyRevision { get; set; } = 1;
}

public sealed class ServiceLinkOptionsValidator(IOptions<ServiceIdentityOptions>? identityOptions = null) : IValidateOptions<ServiceLinkOptions>
{
    public ValidateOptionsResult Validate(string? name, ServiceLinkOptions options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        try
        {
            IntegrationEndpointPolicy.Validate(new Uri(options.WebBaseUrl, UriKind.Absolute), nameof(options.WebBaseUrl), options.AllowPrivateHttp);
            IntegrationEndpointPolicy.Validate(new Uri(options.ApiBaseUrl, UriKind.Absolute), nameof(options.ApiBaseUrl), options.AllowPrivateHttp);
            if (options.GatewayBaseUrl is not null) IntegrationEndpointPolicy.Validate(new Uri(options.GatewayBaseUrl, UriKind.Absolute), nameof(options.GatewayBaseUrl), options.AllowPrivateHttp);
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException) { return ValidateOptionsResult.Fail(e.Message); }
        if (options.BootstrapLifetimeSeconds is < 120 or > 3600 || options.TerminalControlRecoverySeconds is < 60 or > 86400 ||
            options.WorkerIntervalSeconds is < 1 or > 60 || options.MaximumPayloadBytes is < 4096 or > 1048576 ||
            options.RotationAgeDays is < 1 or > 365 || options.RotationOfferLifetimeSeconds is < 120 or > 3600 ||
            options.RotationOverlapSeconds is < 60 or > 3600 || options.RotationPolicyRevision is < 1 or > 9007199254740991)
            return ValidateOptionsResult.Fail("Service link lifetime, bounds or rotation policy is outside its supported range.");
        if (identityOptions?.Value is { Enabled: true } identity &&
            (options.RotationOverlapSeconds > identity.CredentialOverlapSeconds || options.AutomaticRotationEnabled && options.RotationAgeDays >= identity.CredentialMaximumAgeDays))
            return ValidateOptionsResult.Fail("Service-link rotation must start before the issuer credential maximum age and its overlap cannot exceed the issuer's validated overlap.");
        return ValidateOptionsResult.Success;
    }
}
