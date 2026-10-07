using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.ServiceLink;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.ServiceIdentity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.ServiceIdentity;

public sealed class ServiceIdentityConfiguration
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; }
    public bool Enabled { get; set; }
    public string WebBaseUrl { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "rateldesk.services";
    public string InstanceId { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record ServicePublicSettingsEffective(ServiceIdentityOptions Identity, ServiceLinkOptions Linking,
    long Revision, string[] LockedFields)
{
    public ServicePublicSettingsDto ToDto()
    {
        var messages = new List<string>();
        if (!Identity.Enabled || !Linking.Enabled)
            messages.Add(LockedFields.Contains("enabled", StringComparer.Ordinal)
                ? "Connections are disabled by deployment settings. Set ServiceIdentity__Enabled and ServiceLinks__Enabled to true, then reload setup."
                : "Enable connections to complete setup.");
        var candidate = new ServiceIdentityOptions
        {
            Enabled = true, InstanceId = string.IsNullOrEmpty(Identity.InstanceId) ? "setup-pending" : Identity.InstanceId,
            WebBaseUrl = Identity.WebBaseUrl, ApiBaseUrl = Identity.ApiBaseUrl, Issuer = Identity.Issuer,
            Audience = Identity.Audience, AllowPrivateHttp = Identity.AllowPrivateHttp,
            AccessTokenLifetimeSeconds = Identity.AccessTokenLifetimeSeconds, ClockSkewSeconds = Identity.ClockSkewSeconds,
            CredentialMaximumAgeDays = Identity.CredentialMaximumAgeDays, CredentialOverlapSeconds = Identity.CredentialOverlapSeconds,
            TerminalControlRecoverySeconds = Identity.TerminalControlRecoverySeconds, Clients = Identity.Clients
        };
        var validation = new ServiceIdentityOptionsValidator().Validate(null, candidate);
        if (validation.Failed) messages.AddRange(validation.Failures);
        return new(Identity.Enabled, Linking.Enabled, Identity.WebBaseUrl, Identity.ApiBaseUrl,
            Identity.Issuer, Identity.Audience, Identity.InstanceId, Revision, LockedFields, messages.ToArray());
    }
}

public interface IServicePublicSettingsResolver
{
    Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default);
    Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default);
}

/// <summary>Fresh durable setup merges explicit deployment locks; request Host is never a public address.</summary>
public sealed class ServicePublicSettingsResolver(HelpdeskDbContext db, IConfiguration configuration,
    IOptionsMonitor<ServiceIdentityOptions> identityOptions, IOptionsMonitor<ServiceLinkOptions> linkingOptions,
    TimeProvider clock) : IServicePublicSettingsResolver
{
    public async Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) =>
        await Resolve(await db.Set<ServiceIdentityConfiguration>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == 1, ct), ct);

    public async Task<ServicePublicSettingsEffective> UpdateAsync(ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId)) throw new ArgumentException("An authenticated administrator is required.");
        if (new[] { update.WebBaseUrl, update.ApiBaseUrl, update.Issuer }.Any(value => value is null || value.Length > 2048 || value.Any(char.IsControl)) ||
            update.Audience is null || update.Audience.Length > 256 || update.Audience.Any(char.IsControl))
            throw new ArgumentException("Public Web/API addresses and issuer must be bounded canonical addresses, with a valid audience.");
        var row = await db.Set<ServiceIdentityConfiguration>().SingleOrDefaultAsync(item => item.Id == 1, ct);
        if ((row?.Revision ?? 0) != update.ExpectedRevision) throw new ServiceClientConflictException("Connection setup changed. Reload and review the current settings.");
        var current = await Resolve(row, ct);
        foreach (var field in current.LockedFields)
        {
            var changed = field switch
            {
                "enabled" => update.Enabled != current.Identity.Enabled || update.Enabled != current.Linking.Enabled,
                "webBaseUrl" => update.WebBaseUrl.TrimEnd('/') != current.Identity.WebBaseUrl,
                "apiBaseUrl" => update.ApiBaseUrl.TrimEnd('/') != current.Identity.ApiBaseUrl,
                "issuer" => update.Issuer != current.Identity.Issuer,
                "audience" => update.Audience != current.Identity.Audience,
                _ => false
            };
            if (changed) throw new ArgumentException($"The {field} setting is managed by deployment configuration. Update its ServiceIdentity/ServiceLinks deployment value and reload setup.");
        }
        if ((await db.Set<ServiceSigningKey>().AnyAsync(ct) || await db.Set<ServicePrincipalRegistration>().AnyAsync(ct)) &&
            (update.Issuer != current.Identity.Issuer || update.Audience != current.Identity.Audience))
            throw new ServiceClientConflictException("The established service issuer and audience must be preserved. A deliberate identity migration is required.");
        var candidate = new ServiceIdentityConfiguration
        {
            Revision = checked((row?.Revision ?? 0) + 1), Enabled = update.Enabled,
            WebBaseUrl = update.WebBaseUrl, ApiBaseUrl = update.ApiBaseUrl, Issuer = update.Issuer,
            Audience = update.Audience, InstanceId = string.IsNullOrEmpty(current.Identity.InstanceId) ? Guid.NewGuid().ToString("D") : current.Identity.InstanceId,
            UpdatedBy = actorId, UpdatedAtUtc = clock.GetUtcNow()
        };
        _ = await Resolve(candidate, ct);
        if (row is null) db.Set<ServiceIdentityConfiguration>().Add(candidate);
        else db.Entry(row).CurrentValues.SetValues(candidate);
        await db.SaveChangesAsync(ct);
        return await ResolveAsync(ct);
    }

    private async Task<ServicePublicSettingsEffective> Resolve(ServiceIdentityConfiguration? stored, CancellationToken ct)
    {
        var configured = identityOptions.CurrentValue; var linking = linkingOptions.CurrentValue;
        var locks = new List<string>();
        string Select(string field, string? deployment, string? persisted, string fallback, bool address = false)
        {
            var value = !string.IsNullOrWhiteSpace(deployment) ? deployment : !string.IsNullOrWhiteSpace(persisted) ? persisted : fallback;
            if (!string.IsNullOrWhiteSpace(deployment)) locks.Add(field);
            return address ? value.TrimEnd('/') : value;
        }
        if (!string.IsNullOrWhiteSpace(configured.WebBaseUrl) && !string.IsNullOrWhiteSpace(linking.WebBaseUrl) && configured.WebBaseUrl.TrimEnd('/') != linking.WebBaseUrl.TrimEnd('/') ||
            !string.IsNullOrWhiteSpace(configured.ApiBaseUrl) && !string.IsNullOrWhiteSpace(linking.ApiBaseUrl) && configured.ApiBaseUrl.TrimEnd('/') != linking.ApiBaseUrl.TrimEnd('/'))
            throw new ArgumentException("ServiceIdentity and ServiceLinks addresses disagree. Configure one public Web/API profile.");
        var branding = await db.Set<InstanceBranding>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == 1, ct);
        var configuredWeb = !string.IsNullOrWhiteSpace(configured.WebBaseUrl) ? configured.WebBaseUrl : !string.IsNullOrWhiteSpace(linking.WebBaseUrl) ? linking.WebBaseUrl : configuration["Branding:ApplicationUrl"];
        var web = Select("webBaseUrl", configuredWeb, stored?.WebBaseUrl,
            branding?.ApplicationUrl ?? configuration["PublicWebAppUrl"] ?? "", true);
        var configuredApi = !string.IsNullOrWhiteSpace(configured.ApiBaseUrl) ? configured.ApiBaseUrl : !string.IsNullOrWhiteSpace(linking.ApiBaseUrl) ? linking.ApiBaseUrl : configuration["StorageOptions:PublicApiBaseUrl"];
        var api = Select("apiBaseUrl", configuredApi, stored?.ApiBaseUrl, "", true);
        var signingIssuer = await db.Set<ServiceSigningKey>().AsNoTracking().Where(row => row.ActiveSlot == 1).Select(row => row.Issuer).SingleOrDefaultAsync(ct);
        var issuer = Select("issuer", configured.Issuer, stored?.Issuer, signingIssuer ?? (string.IsNullOrEmpty(api) ? "" : api + "/services"));
        var audience = Select("audience", configuration["ServiceIdentity:Audience"], stored?.Audience, configured.Audience);
        var initializedInstance = await db.InstanceInitializations.AsNoTracking().Where(row => row.Id == InstanceInitialization.SingletonId)
            .Select(row => (Guid?)row.InstanceId).SingleOrDefaultAsync(ct);
        var instance = Select("instanceId", configured.InstanceId, stored?.InstanceId,
            initializedInstance is { } installed && installed != Guid.Empty ? installed.ToString("D") : "");
        var enabled = stored?.Enabled ?? configured.Enabled;
        if (bool.TryParse(configuration["ServiceIdentity:Enabled"], out var explicitIdentity)) { enabled = explicitIdentity; locks.Add("enabled"); }
        var linkEnabled = stored?.Enabled ?? linking.Enabled;
        if (bool.TryParse(configuration["ServiceLinks:Enabled"], out var explicitLink)) { linkEnabled = explicitLink; locks.Add("enabled"); }
        var identity = new ServiceIdentityOptions
        {
            Enabled = enabled, WebBaseUrl = web, ApiBaseUrl = api, Issuer = issuer, Audience = audience, InstanceId = instance,
            AllowPrivateHttp = configured.AllowPrivateHttp || linking.AllowPrivateHttp, Clients = configured.Clients,
            AccessTokenLifetimeSeconds = configured.AccessTokenLifetimeSeconds, ClockSkewSeconds = configured.ClockSkewSeconds,
            CredentialMaximumAgeDays = configured.CredentialMaximumAgeDays, CredentialOverlapSeconds = configured.CredentialOverlapSeconds,
            TerminalControlRecoverySeconds = configured.TerminalControlRecoverySeconds
        };
        var links = new ServiceLinkOptions
        {
            Enabled = enabled && linkEnabled, WebBaseUrl = web, ApiBaseUrl = api, GatewayBaseUrl = linking.GatewayBaseUrl,
            AllowPrivateHttp = identity.AllowPrivateHttp, BootstrapLifetimeSeconds = linking.BootstrapLifetimeSeconds,
            TerminalControlRecoverySeconds = linking.TerminalControlRecoverySeconds, WorkerIntervalSeconds = linking.WorkerIntervalSeconds,
            MaximumPayloadBytes = linking.MaximumPayloadBytes, AutomaticRotationEnabled = linking.AutomaticRotationEnabled,
            RotationAgeDays = linking.RotationAgeDays, RotationOfferLifetimeSeconds = linking.RotationOfferLifetimeSeconds,
            RotationOverlapSeconds = linking.RotationOverlapSeconds, RotationPolicyRevision = linking.RotationPolicyRevision
        };
        if (identity.Enabled)
        {
            var validation = new ServiceIdentityOptionsValidator().Validate(null, identity);
            if (validation.Failed) throw new ArgumentException(string.Join(" ", validation.Failures));
            var linkValidation = new ServiceLinkOptionsValidator(Options.Create(identity)).Validate(null, links);
            if (linkValidation.Failed) throw new ArgumentException(string.Join(" ", linkValidation.Failures));
            if (signingIssuer is not null && signingIssuer != issuer)
                throw new ServiceClientConflictException("The durable signing issuer must be preserved. Restore its configured value or perform a deliberate identity migration.");
        }
        return new(identity, links, stored?.Revision ?? 0, locks.Distinct(StringComparer.Ordinal).ToArray());
    }
}
