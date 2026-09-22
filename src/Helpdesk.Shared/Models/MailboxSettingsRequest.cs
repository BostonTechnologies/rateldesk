namespace Helpdesk.Shared.Models;

public sealed class MailboxSettingsRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public InboundMailboxProvider Provider { get; set; } = InboundMailboxProvider.Graph;
    public MailboxAuthentication Authentication { get; set; } = MailboxAuthentication.MicrosoftApplication;
    public MailboxScope Scope { get; set; } = MailboxScope.Global;
    public string? OrganizationId { get; set; }
    public long Version { get; set; } = 1;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool ClearPassword { get; set; }
    public bool ClearClientSecret { get; set; }
    public bool ConfirmExistingImport { get; set; }
    public InitialMailImport InitialImport { get; set; } = InitialMailImport.NewOnly;
    public MailboxTlsMode TlsMode { get; set; } = MailboxTlsMode.TlsOnConnect;
    public string? ProcessedFolder { get; set; }
    public bool MarkReadAfterSuccess { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 25;

    public Guid Id { get; set; }
    public string MailHost { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string MailboxAddress { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string MailboxFolder { get; set; } = "INBOX";
    public bool Enabled { get; set; } = false;
    public bool BackgroundSyncEnabled { get; set; } = false;
}
