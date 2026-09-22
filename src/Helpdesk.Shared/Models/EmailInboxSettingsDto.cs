namespace Helpdesk.Shared.Models;

public record EmailInboxSettingsDto(
    Guid Id,
    string MailHost,
    int Port,
    bool UseSsl,
    string MailboxAddress,
    string TenantId,
    string ClientId,
    string MailboxFolder,
    bool Enabled,
    bool BackgroundSyncEnabled,
    bool HasClientSecret)
{
    public string DisplayName { get; init; } = string.Empty;
    public InboundMailboxProvider Provider { get; init; }
    public MailboxAuthentication Authentication { get; init; }
    public MailboxScope Scope { get; init; }
    public string? OrganizationId { get; init; }
    public bool Archived { get; init; }
    public long Version { get; init; }
    public string Username { get; init; } = string.Empty;
    public bool HasPassword { get; init; }
    public InitialMailImport InitialImport { get; init; }
    public MailboxTlsMode TlsMode { get; init; }
    public string? ProcessedFolder { get; init; }
    public bool MarkReadAfterSuccess { get; init; }
    public int PollIntervalSeconds { get; init; }
    public int BatchSize { get; init; }
}
