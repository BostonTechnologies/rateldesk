namespace Helpdesk.Shared.ServiceIdentity;

public sealed record ServicePublicSettingsDto(bool Enabled, bool LinksEnabled, string WebBaseUrl,
    string ApiBaseUrl, string Issuer, string Audience, string InstanceId, long Revision,
    string[] LockedFields, string[] Readiness);

public sealed record ServicePublicSettingsUpdate(long ExpectedRevision, bool Enabled,
    string WebBaseUrl, string ApiBaseUrl, string Issuer, string Audience);
