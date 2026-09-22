using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Shared.Models;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Email;

public sealed class MailboxSettingsService(HelpdeskDbContext db, MailboxCredentialProtector secrets)
{
    public static string SourceKey(EmailInboxSettings mailbox) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { mailbox.Provider.ToString(), mailbox.Authentication.ToString(),
            mailbox.MailHost.Trim().ToLowerInvariant(), mailbox.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mailbox.MailboxAddress.Trim().ToLowerInvariant(), mailbox.Username.Trim(), mailbox.TenantId.Trim(), mailbox.ClientId.Trim(), mailbox.MailboxFolder }))));

    public async Task<EmailInboxSettings> PrepareAsync(MailboxSettingsRequest request, EmailInboxSettings? existing, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Provider) || !Enum.IsDefined(request.Authentication) || !Enum.IsDefined(request.Scope) ||
            !Enum.IsDefined(request.TlsMode) || !Enum.IsDefined(request.InitialImport)) throw new ArgumentException("Unsupported mailbox option.");
        if (!MailAddress.TryCreate(request.MailboxAddress, out var parsed) || parsed.Address != request.MailboxAddress.Trim())
            throw new ArgumentException("A valid mailbox address is required.");
        if ((request.DisplayName?.Length ?? 0) > 200 || (request.MailHost?.Length ?? 0) > 253 || (request.MailboxFolder?.Length ?? 0) > 512 ||
            request.PollIntervalSeconds is < 10 or > 300 || request.BatchSize is < 1 or > 100 || request.Port is < 1 or > 65535)
            throw new ArgumentException("Mailbox fields exceed supported bounds.");
        if ((request.Scope == MailboxScope.Global && request.OrganizationId is not null) ||
            (request.Scope == MailboxScope.Organization && !await db.Organizations.AnyAsync(x => x.Id == request.OrganizationId, ct)))
            throw new ArgumentException("Select a valid RatelDesk organization for a dedicated mailbox.");
        if (request.Provider == InboundMailboxProvider.Graph && request.Authentication != MailboxAuthentication.MicrosoftApplication)
            throw new ArgumentException("Graph requires Microsoft application authentication.");
        if (request.Authentication == MailboxAuthentication.MicrosoftApplication &&
            (!Guid.TryParse(request.TenantId, out _) || !Guid.TryParse(request.ClientId, out _)))
            throw new ArgumentException("Microsoft directory and client IDs must be valid GUIDs.");
        if (request.Provider != InboundMailboxProvider.Graph && string.IsNullOrWhiteSpace(request.MailHost))
            throw new ArgumentException("A mail server is required.");
        if (request.Authentication == MailboxAuthentication.Password && string.IsNullOrWhiteSpace(request.Username))
            throw new ArgumentException("A protocol username is required.");
        if (request.Provider == InboundMailboxProvider.Pop3 && (request.InitialImport == InitialMailImport.ExistingUnread ||
            !string.IsNullOrEmpty(request.ProcessedFolder) || request.MarkReadAfterSuccess))
            throw new ArgumentException("POP3 supports retention, not unread filtering or folder/read dispositions.");
        if (existing is null && request.InitialImport != InitialMailImport.NewOnly && !request.ConfirmExistingImport)
            throw new ArgumentException("Existing-message import requires explicit confirmation.");
        if (existing is not null && (existing.Archived || existing.Version != request.Version))
            throw new InvalidOperationException("Configuration changed; reload before saving.");
        var result = new EmailInboxSettings
        {
            Id = existing?.Id ?? Guid.NewGuid(), DisplayName = (request.DisplayName?.Trim() ?? string.Empty), Provider = request.Provider,
            Authentication = request.Authentication, Scope = request.Scope, OrganizationId = request.OrganizationId,
            Version = existing?.Version ?? 1, MailHost = (request.MailHost?.Trim() ?? string.Empty), Port = request.Port,
            UseSsl = request.TlsMode == MailboxTlsMode.TlsOnConnect, TlsMode = request.TlsMode,
            MailboxAddress = request.MailboxAddress.Trim().ToLowerInvariant(), Username = (request.Username?.Trim() ?? string.Empty), TenantId = (request.TenantId?.Trim() ?? string.Empty),
            ClientId = (request.ClientId?.Trim() ?? string.Empty), MailboxFolder = request.Provider == InboundMailboxProvider.Pop3 ? string.Empty : request.MailboxFolder ?? string.Empty,
            Enabled = request.Enabled, BackgroundSyncEnabled = request.BackgroundSyncEnabled, InitialImport = request.InitialImport,
            ProcessedFolder = request.ProcessedFolder, MarkReadAfterSuccess = request.MarkReadAfterSuccess,
            PollIntervalSeconds = request.PollIntervalSeconds, BatchSize = request.BatchSize,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            LegacySource = existing?.LegacySource ?? false, CredentialVersion = 1
        };
        result.SourceKey = SourceKey(result);
        if (existing is not null && (existing.SourceKey != result.SourceKey || existing.OrganizationId != result.OrganizationId || existing.Scope != result.Scope))
            throw new ArgumentException("Source or ownership changes require archiving and creating a new mailbox. Stored credentials cannot be reassigned through a draft.");
        result.ClientSecret = request.ClearClientSecret ? string.Empty : !string.IsNullOrEmpty(request.ClientSecret)
            ? secrets.Protect(result.Id, request.ClientSecret) : existing?.ClientSecret ?? string.Empty;
        result.Password = request.ClearPassword ? string.Empty : !string.IsNullOrEmpty(request.Password)
            ? secrets.Protect(result.Id, request.Password) : existing?.Password ?? string.Empty;
        if (result.Authentication == MailboxAuthentication.Password) result.ClientSecret = string.Empty;
        else result.Password = string.Empty;
        if (result.Enabled && (result.Authentication == MailboxAuthentication.Password ? result.Password : result.ClientSecret).Length == 0)
            throw new ArgumentException("An enabled mailbox requires credentials.");
        return result;
    }

    public static EmailInboxSettingsDto ToDto(EmailInboxSettings s) => new(s.Id, s.MailHost, s.Port, s.UseSsl,
        s.MailboxAddress, s.TenantId, s.ClientId, s.MailboxFolder, s.Enabled, s.BackgroundSyncEnabled, s.ClientSecret.Length > 0)
    {
        DisplayName = s.DisplayName, Provider = s.Provider, Authentication = s.Authentication, Scope = s.Scope,
        OrganizationId = s.OrganizationId, Archived = s.Archived, Version = s.Version, Username = s.Username,
        HasPassword = s.Password.Length > 0, InitialImport = s.InitialImport, TlsMode = s.TlsMode,
        ProcessedFolder = s.ProcessedFolder, MarkReadAfterSuccess = s.MarkReadAfterSuccess,
        PollIntervalSeconds = s.PollIntervalSeconds, BatchSize = s.BatchSize
    };
}
