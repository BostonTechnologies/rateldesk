namespace Helpdesk.Shared.Models;

public enum MailboxOutgoingTransport { Smtp, Graph }

public sealed class MailboxOutgoingSettings
{
    public Guid MailboxId { get; set; }
    public long Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public MailboxOutgoingTransport Transport { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public MailboxTlsMode SmtpTlsMode { get; set; } = MailboxTlsMode.StartTls;
    public string SmtpUsername { get; set; } = string.Empty;
    public string ProtectedSmtpPassword { get; set; } = string.Empty;
    public long? LastTestUnixMilliseconds { get; set; }
    public long? TestedVersion { get; set; }
    public string? LastTestCode { get; set; }
}

public sealed record MailboxOutgoingSettingsDto(Guid MailboxId, long Version, bool Enabled,
    MailboxOutgoingTransport Transport, string DisplayName, string MailboxAddress,
    string SmtpHost, int SmtpPort, MailboxTlsMode SmtpTlsMode, string SmtpUsername,
    bool HasSmtpPassword, long? LastTestUnixMilliseconds, long? TestedVersion,
    string? LastTestCode);

public sealed record MailboxOutgoingSettingsRequest(long Version, bool Enabled,
    MailboxOutgoingTransport Transport, string DisplayName, string SmtpHost, int SmtpPort,
    MailboxTlsMode SmtpTlsMode, string SmtpUsername, string SmtpPassword,
    bool ClearSmtpPassword);

public sealed record MailboxSendTestRequest(string Recipient, bool Confirmed);
