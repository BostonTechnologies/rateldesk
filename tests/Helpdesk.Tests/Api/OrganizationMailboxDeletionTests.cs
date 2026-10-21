using Helpdesk.API.Endpoints.Organization;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace Helpdesk.Tests.Api;

public sealed class OrganizationMailboxDeletionTests
{
    [Fact]
    public async Task Archived_unused_mailbox_and_organization_are_deleted_but_audit_is_retained()
    {
        await using var connection = await OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var (organization, mailbox) = await SeedAsync(db);
        db.ActivityLogs.Add(new ActivityLog { RelatedEntityId = mailbox.Id.ToString("D"), Message = "Mailbox configuration archived." });
        await db.SaveChangesAsync();

        var result = await OrganizationDeletionEndpoint.DeleteAsync(organization.Id, new EfRepository<Organization>(db), db, default);

        Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(await db.Organizations.AnyAsync(x => x.Id == organization.Id));
        Assert.False(await db.EmailInboxSettings.AnyAsync(x => x.Id == mailbox.Id));
        Assert.False(await db.Set<MailboxLease>().AnyAsync(x => x.MailboxId == mailbox.Id));
        Assert.False(await db.Set<MailboxIngestionState>().AnyAsync(x => x.MailboxId == mailbox.Id));
        Assert.True(await db.ActivityLogs.AnyAsync(x => x.RelatedEntityId == mailbox.Id.ToString("D")));
    }

    [Fact]
    public async Task Archived_tested_mailboxes_and_outgoing_configuration_are_disposed_together()
    {
        await using var connection = await OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var (organization, first) = await SeedAsync(db);
        var second = new EmailInboxSettings { Id = Guid.NewGuid(), Scope = MailboxScope.Organization,
            OrganizationId = organization.Id, Archived = true, Enabled = false,
            BackgroundSyncEnabled = false, MailboxAddress = "second@example.invalid" };
        db.EmailInboxSettings.Add(second);
        db.Set<MailboxLease>().Add(new MailboxLease { MailboxId = second.Id });
        db.Set<MailboxIngestionState>().Add(new MailboxIngestionState { MailboxId = second.Id,
            SourceKey = "test-second", LastTestUnixMilliseconds = 1234, TestedVersion = 1 });
        db.Set<MailboxOutgoingSettings>().Add(new MailboxOutgoingSettings { MailboxId = first.Id,
            Enabled = false, SmtpHost = "smtp.example.invalid", SmtpUsername = "synthetic",
            LastTestUnixMilliseconds = 5678, TestedVersion = 1, LastTestCode = "Authenticated" });
        await db.SaveChangesAsync();

        var result = await OrganizationDeletionEndpoint.DeleteAsync(organization.Id,
            new EfRepository<Organization>(db), db, default);

        Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(await db.Organizations.AnyAsync(x => x.Id == organization.Id));
        Assert.False(await db.EmailInboxSettings.AnyAsync(x => x.OrganizationId == organization.Id));
        Assert.False(await db.Set<MailboxOutgoingSettings>().AnyAsync(x => x.MailboxId == first.Id));
        Assert.False(await db.Set<MailboxIngestionState>().AnyAsync(x =>
            x.MailboxId == first.Id || x.MailboxId == second.Id));
    }

    [Fact]
    public async Task Retained_outbox_delivery_returns_conflict_without_disposing_configuration()
    {
        await using var connection = await OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var (organization, mailbox) = await SeedAsync(db);
        db.Set<MailboxOutboxEffect>().Add(new MailboxOutboxEffect { EffectKey = "synthetic-delivery",
            DispatchGroup = $"mailbox:{mailbox.Id:N}", Kind = MailboxEffectKind.Email,
            State = MailboxEffectState.Completed, Payload = "{}" });
        await db.SaveChangesAsync();

        var result = await OrganizationDeletionEndpoint.DeleteAsync(organization.Id,
            new EfRepository<Organization>(db), db, default);

        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.True(await db.Organizations.AnyAsync(x => x.Id == organization.Id));
        Assert.True(await db.EmailInboxSettings.AnyAsync(x => x.Id == mailbox.Id));
        Assert.True(await db.Set<MailboxOutboxEffect>().AnyAsync(x => x.DispatchGroup == $"mailbox:{mailbox.Id:N}"));
    }

    [Fact]
    public async Task Retained_receipt_returns_conflict_without_partial_delete()
    {
        await using var connection = await OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var (organization, mailbox) = await SeedAsync(db);
        db.Set<InboundMessageReceipt>().Add(new InboundMessageReceipt { MailboxId = mailbox.Id, SourceKey = "test", TransportKey = "message-1" });
        await db.SaveChangesAsync();

        await using var deleting = CreateContext(connection);
        var result = await OrganizationDeletionEndpoint.DeleteAsync(organization.Id, new EfRepository<Organization>(deleting), deleting, default);

        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
        await using var readback = CreateContext(connection);
        Assert.True(await readback.Organizations.AnyAsync(x => x.Id == organization.Id));
        Assert.True(await readback.EmailInboxSettings.AnyAsync(x => x.Id == mailbox.Id));
        Assert.True(await readback.Set<InboundMessageReceipt>().AnyAsync(x => x.MailboxId == mailbox.Id));
    }

    [Fact]
    public async Task Other_organization_dependency_rolls_back_mailbox_disposal()
    {
        await using var connection = await OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var (organization, mailbox) = await SeedAsync(db);
        db.Organizations.Add(new Organization { Name = "Supported organization", ItSupportOrganizationId = organization.Id });
        await db.SaveChangesAsync();

        await using var deleting = CreateContext(connection);
        var result = await OrganizationDeletionEndpoint.DeleteAsync(organization.Id, new EfRepository<Organization>(deleting), deleting, default);

        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
        await using var readback = CreateContext(connection);
        Assert.True(await readback.Organizations.AnyAsync(x => x.Id == organization.Id));
        Assert.True(await readback.EmailInboxSettings.AnyAsync(x => x.Id == mailbox.Id));
        Assert.True(await readback.Set<MailboxLease>().AnyAsync(x => x.MailboxId == mailbox.Id));
    }

    [Fact]
    public async Task PostgreSql_archived_mailbox_disposal_and_retained_receipt_conflict()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var postgres = new PostgreSqlBuilder().WithImage("postgres:16").Build();
        await postgres.StartAsync(timeout.Token);
        var options = new DbContextOptionsBuilder<HelpdeskDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        await db.Database.EnsureCreatedAsync(timeout.Token);
        var (cleanOrganization, cleanMailbox) = await SeedAsync(db);
        var (retainedOrganization, retainedMailbox) = await SeedAsync(db);
        var cleanIngestion = await db.Set<MailboxIngestionState>()
            .SingleAsync(x => x.MailboxId == cleanMailbox.Id, timeout.Token);
        cleanIngestion.LastTestUnixMilliseconds = 1234;
        cleanIngestion.TestedVersion = 1;
        db.Set<MailboxOutgoingSettings>().Add(new MailboxOutgoingSettings { MailboxId = cleanMailbox.Id,
            Enabled = false, SmtpHost = "smtp.example.invalid", SmtpUsername = "synthetic" });
        db.Set<InboundMessageReceipt>().Add(new InboundMessageReceipt
        {
            MailboxId = retainedMailbox.Id, SourceKey = "test", TransportKey = "retained-message"
        });
        await db.SaveChangesAsync(timeout.Token);

        await using var deleting = new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        var clean = await OrganizationDeletionEndpoint.DeleteAsync(cleanOrganization.Id,
            new EfRepository<Organization>(deleting), deleting, timeout.Token);
        var retained = await OrganizationDeletionEndpoint.DeleteAsync(retainedOrganization.Id,
            new EfRepository<Organization>(deleting), deleting, timeout.Token);

        Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)clean).StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)retained).StatusCode);
        await using var readback = new HelpdeskDbContext(options, Substitute.For<ITenantContext>(), new HttpContextAccessor());
        Assert.False(await readback.Organizations.AnyAsync(x => x.Id == cleanOrganization.Id, timeout.Token));
        Assert.False(await readback.EmailInboxSettings.AnyAsync(x => x.Id == cleanMailbox.Id, timeout.Token));
        Assert.False(await readback.Set<MailboxOutgoingSettings>()
            .AnyAsync(x => x.MailboxId == cleanMailbox.Id, timeout.Token));
        Assert.True(await readback.Organizations.AnyAsync(x => x.Id == retainedOrganization.Id, timeout.Token));
        Assert.True(await readback.EmailInboxSettings.AnyAsync(x => x.Id == retainedMailbox.Id, timeout.Token));
        Assert.True(await readback.Set<InboundMessageReceipt>().AnyAsync(x => x.MailboxId == retainedMailbox.Id, timeout.Token));
    }

    private static async Task<(Organization Organization, EmailInboxSettings Mailbox)> SeedAsync(HelpdeskDbContext db)
    {
        var organization = new Organization { Name = "Disposable" };
        var mailbox = new EmailInboxSettings { Id = Guid.NewGuid(), Scope = MailboxScope.Organization,
            OrganizationId = organization.Id, Archived = true, Enabled = false, BackgroundSyncEnabled = false };
        db.Organizations.Add(organization);
        db.EmailInboxSettings.Add(mailbox);
        db.Set<MailboxLease>().Add(new MailboxLease { MailboxId = mailbox.Id });
        db.Set<MailboxIngestionState>().Add(new MailboxIngestionState { MailboxId = mailbox.Id, SourceKey = "test" });
        await db.SaveChangesAsync();
        return (organization, mailbox);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static HelpdeskDbContext CreateContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<HelpdeskDbContext>().UseSqlite(connection).Options,
        Substitute.For<ITenantContext>(), new HttpContextAccessor());
}
