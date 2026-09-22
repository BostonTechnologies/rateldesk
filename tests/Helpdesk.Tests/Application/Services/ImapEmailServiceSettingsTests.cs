using Helpdesk.Application.Services.Email;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Tests.Application.Services;

public class ImapEmailServiceSettingsTests
{
    [Fact]
    public async Task CurrentInboxSettings_LoadsFromMigratedSqliteAndComparesUtcInstants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new HelpdeskDbContext(new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Helpdesk.Infrastructure.SqliteMigrations"))
            .Options, NSubstitute.Substitute.For<Helpdesk.Shared.Services.ITenantContext>(), new Microsoft.AspNetCore.Http.HttpContextAccessor());
        await db.Database.MigrateAsync();
        Assert.Empty(await ImapEmailService.LoadOrderedInboxSettingsAsync(db.EmailInboxSettings.AsNoTracking(), CancellationToken.None));
        var earlier = CreateSettings(false, false, "earlier@example.com", DateTimeOffset.Parse("2026-09-13T12:00:00+02:00"));
        var later = CreateSettings(false, false, "later@example.com", DateTimeOffset.Parse("2026-09-13T06:01:00-04:00"));
        var enabled = CreateSettings(true, true, "enabled@example.com", earlier.UpdatedAt.AddDays(-1));
        // Archived rows represent the pre-reconciliation historical sources.
        earlier.Archived = later.Archived = enabled.Archived = true;
        db.EmailInboxSettings.AddRange(earlier, later, enabled);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ordered = await ImapEmailService.LoadOrderedInboxSettingsAsync(db.EmailInboxSettings.AsNoTracking(), CancellationToken.None);

        Assert.Equal(new[] { enabled.Id, later.Id, earlier.Id }, ordered.Select(settings => settings.Id));
    }

    [Fact]
    public void CurrentInboxSettingsOrdering_PrefersEnabledBackgroundSyncRecord()
    {
        var disabledOldest = CreateSettings(enabled: false, backgroundSyncEnabled: false, "disabled-oldest", DateTimeOffset.UtcNow.AddDays(-3));
        var enabledWithoutBackground = CreateSettings(enabled: true, backgroundSyncEnabled: false, "enabled-no-background", DateTimeOffset.UtcNow);
        var current = CreateSettings(enabled: true, backgroundSyncEnabled: true, "current", DateTimeOffset.UtcNow.AddDays(-1));

        var ordered = ImapEmailService.OrderByCurrentInboxSettings(new[] { disabledOldest, enabledWithoutBackground, current }.AsQueryable()).ToList();

        Assert.Equal(current.Id, ordered[0].Id);
        Assert.Equal(enabledWithoutBackground.Id, ordered[1].Id);
        Assert.Equal(disabledOldest.Id, ordered[2].Id);
    }

    [Fact]
    public void CurrentInboxSettingsOrdering_FallsBackToMostRecentlyUpdatedWhenAllDisabled()
    {
        var older = CreateSettings(enabled: false, backgroundSyncEnabled: false, "older", DateTimeOffset.UtcNow.AddDays(-2));
        var newer = CreateSettings(enabled: false, backgroundSyncEnabled: false, "newer", DateTimeOffset.UtcNow.AddDays(-1));

        var ordered = ImapEmailService.OrderByCurrentInboxSettings(new[] { older, newer }.AsQueryable()).ToList();

        Assert.Equal(newer.Id, ordered[0].Id);
    }

    [Fact]
    public void EmailInboxSettingsDisabled_DisablesImapProcessing()
    {
        var mapped = ImapEmailService.ToImapSettings(CreateSettings(enabled: false, backgroundSyncEnabled: true), requireBackgroundSync: true);

        Assert.False(mapped.Enabled);
        Assert.Equal(ImapTestStatus.Never, mapped.LastTestStatus);
    }

    [Fact]
    public void EmailInboxSettingsBackgroundSyncDisabled_DisablesImapProcessing()
    {
        var mapped = ImapEmailService.ToImapSettings(CreateSettings(enabled: true, backgroundSyncEnabled: false), requireBackgroundSync: true);

        Assert.False(mapped.Enabled);
        Assert.Equal(ImapTestStatus.Never, mapped.LastTestStatus);
    }

    [Fact]
    public void EmailInboxSettingsEnabled_MapsToImapSettings()
    {
        var mapped = ImapEmailService.ToImapSettings(CreateSettings(enabled: true, backgroundSyncEnabled: true), requireBackgroundSync: true);

        Assert.True(mapped.Enabled);
        Assert.Equal(ImapTestStatus.Success, mapped.LastTestStatus);
        Assert.Equal("outlook.office365.com", mapped.Host);
        Assert.Equal(993, mapped.Port);
        Assert.True(mapped.UseSsl);
        Assert.Equal("helpdesk@example.com", mapped.UserEmail);
        Assert.Equal("INBOX", mapped.Mailbox);
        Assert.Equal("tenant", mapped.TenantId);
        Assert.Equal("client", mapped.ClientId);
        Assert.Equal("secret", mapped.ClientSecret);
    }

    private static EmailInboxSettings CreateSettings(bool enabled, bool backgroundSyncEnabled)
        => CreateSettings(enabled, backgroundSyncEnabled, "helpdesk@example.com", DateTimeOffset.UtcNow);

    private static EmailInboxSettings CreateSettings(
        bool enabled,
        bool backgroundSyncEnabled,
        string mailboxAddress,
        DateTimeOffset updatedAt)
        => new()
        {
            Id = Guid.NewGuid(),
            MailHost = "outlook.office365.com",
            Port = 993,
            UseSsl = true,
            MailboxAddress = mailboxAddress,
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = "secret",
            MailboxFolder = "INBOX",
            Enabled = enabled,
            BackgroundSyncEnabled = backgroundSyncEnabled,
            CreatedAt = updatedAt.AddMinutes(-5),
            UpdatedAt = updatedAt
        };
}
