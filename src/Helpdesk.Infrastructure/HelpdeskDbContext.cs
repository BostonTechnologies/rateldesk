using Helpdesk.Shared.Models;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Services;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Helpdesk.Infrastructure.Persistence;

public class HelpdeskDbContext(
    DbContextOptions<HelpdeskDbContext> options,
    ITenantContext tenantContext,
    IHttpContextAccessor httpContextAccessor) : DbContext(options)
{
    private readonly ITenantContext _tenantContext = tenantContext;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    private bool _ingressScope;
    private string? _ingressOrganizationId;

    internal string? IngressOrganizationId => _ingressOrganizationId;

    public void BeginIngressRoutingScope()
    {
        if (_httpContextAccessor.HttpContext is not null)
            throw new InvalidOperationException("Ingress system scope is only available to background processing.");
        _ingressScope = true;
        _ingressOrganizationId = null;
    }

    public void RestrictIngressToOrganization(string organizationId)
    {
        if (!_ingressScope || string.IsNullOrWhiteSpace(organizationId))
            throw new InvalidOperationException("An explicit ingress scope and organization are required.");
        _ingressOrganizationId = organizationId;
    }

    internal void RestoreIngressOrganization(string? organizationId)
    {
        if (!_ingressScope)
            throw new InvalidOperationException("An explicit ingress scope is required.");
        _ingressOrganizationId = organizationId;
    }

    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerAuthLink> CustomerAuthLinks => Set<CustomerAuthLink>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<BlockedEntity> BlockedEntities => Set<BlockedEntity>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<ImapEmailSettings> ImapEmailSettings => Set<ImapEmailSettings>();
    public DbSet<EmailInboxSettings> EmailInboxSettings => Set<EmailInboxSettings>();
    public DbSet<InboundEmailRule> InboundEmailRules => Set<InboundEmailRule>();
    public DbSet<InboundEmailProcessingLog> InboundEmailProcessingLogs => Set<InboundEmailProcessingLog>();

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ScopedRoleAssignment> ScopedRoleAssignments => Set<ScopedRoleAssignment>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<WorkLog> WorkLogs => Set<WorkLog>();
    public DbSet<TicketTimelineEvent> TicketTimelineEvents => Set<TicketTimelineEvent>();
    public DbSet<TicketRelation> TicketRelations => Set<TicketRelation>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<SlaEscalationRule> SlaEscalationRules => Set<SlaEscalationRule>();
    public DbSet<TicketSlaState> TicketSlaStates => Set<TicketSlaState>();
    public DbSet<TicketSlaEscalationEvent> TicketSlaEscalationEvents => Set<TicketSlaEscalationEvent>();
    public DbSet<WorkingCalendar> WorkingCalendars => Set<WorkingCalendar>();
    public DbSet<TenantSlaSettings> TenantSlaSettings => Set<TenantSlaSettings>();
    public DbSet<SlaReportSubscription> SlaReportSubscriptions => Set<SlaReportSubscription>();
    public DbSet<SlaReportSendEvent> SlaReportSendEvents => Set<SlaReportSendEvent>();
    public DbSet<AutomationRule> AutomationRules => Set<AutomationRule>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<Change> Changes => Set<Change>();
    public DbSet<ChangeApproval> ChangeApprovals => Set<ChangeApproval>();
    public DbSet<RequestTask> RequestTasks => Set<RequestTask>();
    public DbSet<RequestTaskApproval> RequestTaskApprovals => Set<RequestTaskApproval>();
    public DbSet<TicketCategory> TicketCategories => Set<TicketCategory>();
    public DbSet<IncidentCategoryLink> IncidentCategoryLinks => Set<IncidentCategoryLink>();
    public DbSet<RequestCategoryLink> RequestCategoryLinks => Set<RequestCategoryLink>();
    public DbSet<ChangeCategoryLink> ChangeCategoryLinks => Set<ChangeCategoryLink>();
    public DbSet<EmailTemplate> EmailTemplates => Set<EmailTemplate>();
    public DbSet<EmailLayout> EmailLayouts => Set<EmailLayout>();
    public DbSet<TenantBranding> TenantBrandings => Set<TenantBranding>();
    public DbSet<InstanceBranding> InstanceBrandings => Set<InstanceBranding>();
    public DbSet<InstanceInitialization> InstanceInitializations => Set<InstanceInitialization>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<RequestForm> RequestForms => Set<RequestForm>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<NotificationReadEntity> NotificationReads => Set<NotificationReadEntity>();
    public DbSet<SupportGroup> SupportGroups => Set<SupportGroup>();
    public DbSet<SupportGroupMember> SupportGroupMembers => Set<SupportGroupMember>();
    public DbSet<OrganizationSupportCoverage> OrganizationSupportCoverages => Set<OrganizationSupportCoverage>();
    public DbSet<SupportNotificationSubscription> SupportNotificationSubscriptions => Set<SupportNotificationSubscription>();
    public DbSet<UserSupportNotificationPreference> UserSupportNotificationPreferences => Set<UserSupportNotificationPreference>();
    public DbSet<SupportNotificationDelivery> SupportNotificationDeliveries => Set<SupportNotificationDelivery>();
    public DbSet<M2MConnectivitySettings> M2MConnectivitySettings => Set<M2MConnectivitySettings>();
    public DbSet<NetclawConnectivitySettings> NetclawConnectivitySettings => Set<NetclawConnectivitySettings>();
    public DbSet<AutomationBinding> AutomationBindings => Set<AutomationBinding>();
    public DbSet<DatasetDefinition> DatasetDefinitions => Set<DatasetDefinition>();
    public DbSet<DatasetColumn> DatasetColumns => Set<DatasetColumn>();
    public DbSet<DatasetRow> DatasetRows => Set<DatasetRow>();
    public DbSet<DatasetIngestCredential> DatasetIngestCredentials => Set<DatasetIngestCredential>();
    public DbSet<TenantGraphDatasetSettings> TenantGraphDatasetSettings => Set<TenantGraphDatasetSettings>();
    public DbSet<HangfireRuntimeSettings> HangfireRuntimeSettings => Set<HangfireRuntimeSettings>();
    public DbSet<AiAssistantWebhookConfiguration> AiAssistantWebhookConfigurations => Set<AiAssistantWebhookConfiguration>();
    public DbSet<AiInvestigationInvocation> AiInvestigationInvocations => Set<AiInvestigationInvocation>();
    public DbSet<AiInvestigationWorklogEntry> AiInvestigationWorklogEntries => Set<AiInvestigationWorklogEntry>();
    public DbSet<AiAssistantWebhookAuditRecord> AiAssistantWebhookAuditRecords => Set<AiAssistantWebhookAuditRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // The rc.7 cleanup is an explicit terminal migration, while the
        // historical snapshots must retain their retired native-AI mappings
        // so existing installations can be upgraded. EF Core consequently
        // reports a pending model change for either provider before applying
        // that migration. Always apply the migration chain instead.
        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var isPostgreSql = Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        var isSqlite = Database.IsSqlite();
        modelBuilder.Entity<InstanceInitialization>(entity =>
        {
            entity.ToTable("InstanceInitializations");
            entity.HasKey(initialization => initialization.Id);
            entity.Property(initialization => initialization.SetupVersion).HasMaxLength(64);
            entity.Property(initialization => initialization.TimeZoneId).HasMaxLength(128).HasDefaultValue("UTC");
            entity.HasIndex(initialization => initialization.InstanceId).IsUnique();
        });
        modelBuilder.Entity<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatConversation>(entity =>
        {
            entity.ToTable("AiAssistantChatConversations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProviderProfileFingerprint).HasMaxLength(64);
            entity.HasIndex(x => new { x.OrganizationId, x.TicketType, x.TicketId }).IsUnique().HasFilter("\"State\" <> 4");
            entity.HasIndex(x => new { x.State, x.LastTransportActivityAtUtc });
        });
        modelBuilder.Entity<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatEvent>(entity =>
        {
            entity.ToTable("AiAssistantChatEvents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();
            entity.HasIndex(x => new { x.ConversationId, x.ClientMessageId }).IsUnique().HasFilter("\"ClientMessageId\" IS NOT NULL");
            entity.HasOne<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatInteraction>(entity =>
        {
            entity.ToTable("AiAssistantChatInteractions");
            entity.HasKey(x => new { x.ConversationId, x.CallId });
            entity.HasOne<Helpdesk.Shared.AiAssistant.Chat.AiAssistantChatConversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Ticket>().HasQueryFilter(ticket =>
            _ingressScope ? ticket.OrganizationId == _ingressOrganizationId :
            _tenantContext.IsHelpdeskAdmin ||
            string.IsNullOrWhiteSpace(_tenantContext.TenantId) ||
            ticket.OrganizationId == _tenantContext.TenantId ||
            Organizations.Any(organization =>
                organization.Id == ticket.OrganizationId &&
                organization.ItSupportOrganizationId == _tenantContext.TenantId)
        );

        modelBuilder.Entity<AiAssistantWebhookConfiguration>(entity =>
        {
            entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.IsEnabled, x.IsArchived });
            entity.Property(x => x.TicketAreas).HasConversion(EfJsonConverters.EnumListToJson<AiAssistantTicketArea>()).HasColumnName("TicketAreasJson");
        });
        modelBuilder.Entity<AiInvestigationInvocation>(entity =>
        {
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.TicketArea, x.TicketId, x.CreatedUtc });
            entity.HasIndex(x => x.CorrelationId).IsUnique();
        });
        modelBuilder.Entity<AiInvestigationWorklogEntry>(entity =>
        {
            if (isSqlite)
            {
                entity.Property<long>("OccurredUtcSortTicks")
                    .HasComputedColumnSql("CAST((julianday(\"OccurredUtc\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.HasIndex("OccurredUtcSortTicks");
            }
            entity.HasIndex(x => new { x.OrganizationId, x.TicketArea, x.TicketId, x.OccurredUtc });
            entity.HasIndex(x => x.CallbackEventId).IsUnique().HasFilter("\"CallbackEventId\" IS NOT NULL");
        });
        modelBuilder.Entity<AiAssistantWebhookAuditRecord>().HasIndex(x => new { x.OrganizationId, x.CreatedUtc });

        modelBuilder.Entity<Ticket>()
            .Property(t => t.CcRecipients)
            .HasConversion(EfJsonConverters.StringListToJson)
            .HasColumnName("CcRecipientsJson")
            .HasDefaultValueSql("'[]'");
        modelBuilder.Entity<Ticket>()
            .Property(t => t.EmailExclusionReason)
            .HasConversion<int>()
            .HasDefaultValue(TicketEmailExclusionReason.None);
        modelBuilder.Entity<Ticket>()
            .HasIndex(t => t.State);
        modelBuilder.Entity<Ticket>()
            .HasIndex(t => new { t.OrganizationId, t.ClosedAt });

        modelBuilder.Entity<TicketRelation>(entity =>
        {
            entity.ToTable("TicketRelations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceTicketId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TargetTicketId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RelationType).HasConversion<int>();
            entity.Property(x => x.CreatedByUserId).HasMaxLength(128);
            entity.Property(x => x.CreatedByUserName).HasMaxLength(256);
            entity.HasIndex(x => x.SourceTicketId);
            entity.HasIndex(x => x.TargetTicketId);
            entity.HasIndex(x => new { x.SourceTicketId, x.TargetTicketId, x.RelationType }).IsUnique();
        });

        modelBuilder.Entity<RequestTask>(entity =>
        {
            entity.Property(x => x.Type).HasConversion<int>();
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.IsBlocked).HasDefaultValue(false);
            entity.Property(x => x.Escalated).HasDefaultValue(false);
            entity.Property(x => x.SlaBreached).HasDefaultValue(false);
            entity.Property(x => x.RetryCount).HasDefaultValue(0);
            entity.HasIndex(x => x.RequestId);
            entity.HasIndex(x => new { x.RequestId, x.IsBlocked });
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.Status, x.DueAt });
            entity.HasIndex(x => new { x.Status, x.NextRetryAt });
            if (isSqlite)
            {
                entity.Property<long?>("DueAtUtcTicks")
                    .HasComputedColumnSql("CAST((julianday(\"DueAt\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.Property<long?>("NextRetryAtUtcTicks")
                    .HasComputedColumnSql("CAST((julianday(\"NextRetryAt\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.HasIndex("Status", "DueAtUtcTicks");
                entity.HasIndex("Status", "NextRetryAtUtcTicks");
            }
            entity.HasIndex(x => new { x.Escalated, x.Status });
            entity.HasOne(x => x.Request)
                .WithMany(x => x.Tasks)
                .HasForeignKey(x => x.RequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RequestTaskApproval>(entity =>
        {
            entity.ToTable("RequestTaskApprovals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.ApproverSource).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ApproverId).HasMaxLength(128);
            entity.Property(x => x.ApproverName).IsRequired();
            entity.Property(x => x.ApproverEmail).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(128);
            entity.HasIndex(x => x.RequestId);
            entity.HasIndex(x => x.RequestTaskId);
            entity.HasIndex(x => x.ApproverEmail);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.ViewedAtUtc);
            entity.HasIndex(x => new { x.RequestTaskId, x.ApproverEmail }).IsUnique();
            entity.HasOne(x => x.Request)
                .WithMany()
                .HasForeignKey(x => x.RequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.RequestTask)
                .WithMany()
                .HasForeignKey(x => x.RequestTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Change>(entity =>
        {
            entity.Property(x => x.LifecycleState)
                .HasConversion<int>()
                .HasDefaultValue(ChangeLifecycleState.Draft);
            entity.Property(x => x.ApproverUserIds)
                .HasConversion(EfJsonConverters.StringListToJson)
                .HasColumnName("ApproverUserIdsJson")
                .HasDefaultValueSql("'[]'");
            entity.HasIndex(x => x.RequestedForUserId);
            entity.HasIndex(x => x.ImplementorUserId);
            entity.HasMany(x => x.Approvals)
                .WithOne(x => x.Change)
                .HasForeignKey(x => x.ChangeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChangeApproval>(entity =>
        {
            entity.ToTable("ChangeApprovals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.ApproverEmail).IsRequired();
            entity.Property(x => x.ApproverName).IsRequired();
            entity.HasIndex(x => x.ViewedAtUtc);
            entity.HasIndex(x => x.ChangeId);
            entity.HasIndex(x => x.ApproverEmail);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.ChangeId, x.ApproverEmail }).IsUnique();
        });

        modelBuilder.Entity<Request>(entity =>
        {
            entity.HasIndex(x => new { x.OrganizationId, x.SourceTicketId });
            entity.Property(x => x.SourceTicketId).HasMaxLength(64);
            entity.Property(x => x.SourceAutomationBindingId).HasMaxLength(64);
        });

        modelBuilder.Entity<M2MConnectivitySettings>(entity =>
        {
            entity.ToTable("M2MConnectivitySettings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProviderKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => x.ProviderKey).IsUnique();
            entity.Property(x => x.RemoteBaseUrl).HasMaxLength(1024);
            entity.Property(x => x.RemoteAudience).HasMaxLength(256);
            entity.Property(x => x.RemoteSystemName).HasMaxLength(128);
            entity.Property(x => x.RemoteTokenEndpoint).HasMaxLength(1024);
            entity.Property(x => x.RemoteAuthority).HasMaxLength(512);
            entity.Property(x => x.RemoteScope).HasMaxLength(256);
            entity.Property(x => x.ClientId).HasMaxLength(256);
            entity.Property(x => x.ProtectedClientSecret).HasMaxLength(8192);
            entity.Property(x => x.HealthPath).HasMaxLength(256);
            entity.Property(x => x.IngestPath).HasMaxLength(256);
            entity.Property(x => x.CatalogPath).HasMaxLength(256);
        });

        modelBuilder.Entity<NetclawConnectivitySettings>(entity =>
        {
            entity.ToTable("NetclawConnectivitySettings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProviderKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => x.ProviderKey).IsUnique();
            entity.Property(x => x.Instance).HasMaxLength(64);
            entity.Property(x => x.Endpoint).HasMaxLength(1024);
            entity.Property(x => x.ProtectedDeviceToken).HasMaxLength(8192);
        });

        modelBuilder.Entity<AutomationBinding>(entity =>
        {
            entity.ToTable("AutomationBindings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OrganizationId).HasMaxLength(128);
            entity.Property(x => x.RequestFormId).HasMaxLength(64);
            entity.Property(x => x.OrchestrationRequestDefinitionId).HasMaxLength(256);
            entity.Property(x => x.OrchestrationRequestDefinitionName).HasMaxLength(256);
            entity.Property(x => x.OrchestrationJobDefinitionId).HasMaxLength(256);
            entity.Property(x => x.OrchestrationJobDefinitionName).HasMaxLength(256);
            entity.Property(x => x.SyncState).HasConversion<int>();
            entity.Property(x => x.LastSyncHash).HasMaxLength(256);
            entity.Property(x => x.LastSyncVersion).HasMaxLength(128);
            entity.Property(x => x.LastSyncDirection).HasMaxLength(64);
            entity.Property(x => x.LastCorrelationId).HasMaxLength(128);
            entity.HasIndex(x => new { x.OrganizationId, x.RequestFormId, x.TaskTemplateId }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.OrchestrationRequestDefinitionId });
            if (isSqlite)
            {
                entity.Property<long>("UpdatedAtUtcSortTicks")
                    .HasComputedColumnSql("CAST((julianday(\"UpdatedAtUtc\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.HasIndex("SyncState", "UpdatedAtUtcSortTicks");
            }
        });

        modelBuilder.Entity<DatasetDefinition>(entity =>
        {
            entity.ToTable("DatasetDefinitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceType).HasConversion<int>();
            entity.Property(x => x.SearchColumns)
                .HasConversion(EfJsonConverters.StringListToJson)
                .HasColumnName("SearchColumnsJson")
                .HasDefaultValueSql("'[]'");
            entity.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.SourceType });
        });

        modelBuilder.Entity<DatasetColumn>(entity =>
        {
            entity.ToTable("DatasetColumns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DataType).HasConversion<int>();
            entity.HasIndex(x => new { x.DatasetId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<DatasetRow>(entity =>
        {
            entity.ToTable("DatasetRows");
            entity.HasKey(x => x.Id);
            if (isPostgreSql)
            {
                entity.Property(x => x.DataJson)
                    .HasColumnType("jsonb")
                    .HasConversion(EfJsonConverters.JsonDocumentToString);
            }
            else
            {
                entity.Property(x => x.DataJson)
                    .HasConversion(EfJsonConverters.JsonDocumentToString);
            }
            entity.HasIndex(x => new { x.DatasetId, x.ExternalKey }).IsUnique();
            entity.HasIndex(x => new { x.DatasetId, x.OrganizationId });
        });

        modelBuilder.Entity<DatasetIngestCredential>(entity =>
        {
            if (isSqlite)
            {
                entity.Property<long>("CreatedAtUtcSortTicks")
                    .HasComputedColumnSql("CAST((julianday(\"CreatedAtUtc\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.HasIndex("CreatedAtUtcSortTicks");
            }
            entity.ToTable("DatasetIngestCredentials");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DatasetId, x.Name });
            entity.HasIndex(x => new { x.DatasetId, x.KeyHash });
        });

        modelBuilder.Entity<TenantGraphDatasetSettings>(entity =>
        {
            entity.ToTable("TenantGraphDatasetSettings");
            entity.HasKey(x => x.OrganizationId);
        });

        modelBuilder.Entity<SupportGroup>(entity =>
        {
            entity.ToTable("SupportGroups");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.OwningOrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1024);
            entity.HasIndex(x => new { x.OwningOrganizationId, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.OwningOrganizationId, x.IsEnabled });
        });

        modelBuilder.Entity<SupportGroupMember>(entity =>
        {
            entity.ToTable("SupportGroupMembers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.SupportGroupId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Role).HasConversion<int>();
            entity.Property(x => x.Source).HasConversion<int>();
            entity.HasIndex(x => new { x.SupportGroupId, x.UserId }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.IsEnabled });
            entity.HasOne<SupportGroup>()
                .WithMany()
                .HasForeignKey(x => x.SupportGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrganizationSupportCoverage>(entity =>
        {
            entity.ToTable("OrganizationSupportCoverages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.CustomerOrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ProviderOrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SupportGroupId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Role).HasConversion<int>();
            entity.HasIndex(x => new { x.CustomerOrganizationId, x.ProviderOrganizationId, x.SupportGroupId, x.Role }).IsUnique();
            entity.HasIndex(x => new { x.CustomerOrganizationId, x.IsEnabled });
            entity.HasIndex(x => new { x.SupportGroupId, x.IsEnabled });
            entity.HasOne<SupportGroup>()
                .WithMany()
                .HasForeignKey(x => x.SupportGroupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupportNotificationSubscription>(entity =>
        {
            entity.ToTable("SupportNotificationSubscriptions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.CustomerOrganizationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EventType).HasConversion<int>();
            entity.Property(x => x.RecipientType).HasConversion<int>();
            entity.Property(x => x.RecipientId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Channel).HasConversion<int>();
            entity.HasIndex(x => new { x.CustomerOrganizationId, x.EventType, x.Channel, x.IsEnabled });
            entity.HasIndex(x => new { x.RecipientType, x.RecipientId });
        });

        modelBuilder.Entity<UserSupportNotificationPreference>(entity =>
        {
            entity.ToTable("UserSupportNotificationPreferences");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EventType).HasConversion<int>();
            entity.Property(x => x.Channel).HasConversion<int>();
            entity.HasIndex(x => new { x.UserId, x.EventType, x.Channel }).IsUnique();
        });

        modelBuilder.Entity<SupportNotificationDelivery>(entity =>
        {
            entity.ToTable("SupportNotificationDeliveries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.DeduplicationKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.TicketId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TicketTrackingId).HasMaxLength(64);
            entity.Property(x => x.EventType).HasConversion<int>();
            entity.Property(x => x.Channel).HasConversion<int>();
            entity.Property(x => x.RecipientUserId).HasMaxLength(128);
            entity.Property(x => x.RecipientEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.FailureReason).HasMaxLength(2048);
            entity.HasIndex(x => x.DeduplicationKey).IsUnique();
            entity.HasIndex(x => new { x.TicketId, x.EventType });
            entity.HasIndex(x => new { x.Status, x.CreatedUtc });
        });

        modelBuilder.Entity<HangfireRuntimeSettings>(entity =>
        {
            entity.ToTable("HangfireRuntimeSettings");
            entity.HasKey(x => x.Id);
        });

        modelBuilder.Entity<EmailInboxSettings>(entity =>
        {
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.SourceKey).HasMaxLength(64);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.Scope).IsUnique().HasFilter("\"Archived\" = false AND \"Scope\" = 0");
            entity.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("\"Archived\" = false AND \"Scope\" = 1");
            entity.HasIndex(x => x.MailboxAddress).IsUnique().HasFilter("\"Archived\" = false AND \"Enabled\" = true");
            entity.HasIndex(x => x.SourceKey).IsUnique().HasFilter("\"Archived\" = false AND \"Enabled\" = true");
            entity.ToTable(t => t.HasCheckConstraint("CK_Mailbox_Assignment",
                "(\"Scope\" = 0 AND \"OrganizationId\" IS NULL) OR (\"Scope\" = 1 AND \"OrganizationId\" IS NOT NULL)"));
        });
        modelBuilder.Entity<MailboxLease>(entity =>
        {
            entity.HasKey(x => x.MailboxId);
            entity.HasOne<EmailInboxSettings>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MailboxIngestionState>(entity =>
        {
            entity.HasKey(x => x.MailboxId);
            entity.HasOne<EmailInboxSettings>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<InboundMessageReceipt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasOne<EmailInboxSettings>().WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.MailboxId, x.SourceKey, x.TransportKey }).IsUnique();
            entity.HasIndex(x => new { x.MailboxId, x.Outcome, x.Acknowledged });
            entity.Property(x => x.TransportKey).HasMaxLength(1024);
            entity.Property(x => x.SourceKey).HasMaxLength(64);
        });
        modelBuilder.Entity<MailboxMigrationState>().HasKey(x => x.Id);
        modelBuilder.Entity<MailboxWorkerControl>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<MailboxOutgoingSettings>(entity =>
        {
            entity.HasKey(x => x.MailboxId);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<EmailInboxSettings>().WithOne().HasForeignKey<MailboxOutgoingSettings>(x => x.MailboxId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.SmtpHost).HasMaxLength(253);
            entity.Property(x => x.SmtpUsername).HasMaxLength(320);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
        });
        modelBuilder.Entity<MailboxOutboxEffect>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ReceiptId, x.EffectKey }).IsUnique();
            entity.HasIndex(x => x.DeliveryEventId).IsUnique();
            entity.HasIndex(x => new { x.State, x.AvailableUnixMilliseconds });
            entity.HasIndex(x => new { x.State, x.LeaseExpiresUnixMilliseconds });
            entity.HasIndex(x => new { x.Kind, x.State, x.DispatchGroup, x.AvailableUnixMilliseconds });
            entity.Property(x => x.DispatchGroup).HasMaxLength(80);
            entity.Property(x => x.Owner).HasMaxLength(128);
            entity.Property(x => x.LastErrorCode).HasMaxLength(128);
        });

        modelBuilder.Entity<InboundEmailRule>(entity =>
        {
            entity.ToTable("InboundEmailRules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ScopeType).HasConversion<int>();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.TenantId).HasMaxLength(64);
            var conditionsJson = entity.Property(x => x.ConditionsJson).HasDefaultValue("[]");
            var actionsJson = entity.Property(x => x.ActionsJson).HasDefaultValue("[]");
            if (isPostgreSql)
            {
                conditionsJson.HasColumnType("jsonb");
                actionsJson.HasColumnType("jsonb");
            }
            else
            {
                conditionsJson.HasColumnType("TEXT");
                actionsJson.HasColumnType("TEXT");
            }
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
            entity.Property(x => x.UpdatedBy).HasMaxLength(256);
            entity.HasIndex(x => new { x.Enabled, x.ScopeType, x.TenantId, x.MailboxId, x.Priority });
        });

        modelBuilder.Entity<InboundEmailProcessingLog>(entity =>
        {
            if (isSqlite)
            {
                entity.Property<long>("CreatedAtUtcSortTicks")
                    .HasComputedColumnSql("CAST((julianday(\"CreatedAtUtc\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.HasIndex("CreatedAtUtcSortTicks");
            }
            entity.ToTable("InboundEmailProcessingLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MessageId).HasMaxLength(512).IsRequired();
            entity.Property(x => x.MailboxKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TenantId).HasMaxLength(64);
            entity.Property(x => x.RuleId).HasMaxLength(64);
            entity.Property(x => x.ActionKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasConversion<int>();
            entity.Property(x => x.TicketId).HasMaxLength(64);
            entity.Property(x => x.Error).HasMaxLength(2048);
            entity.HasIndex(x => new { x.MessageId, x.MailboxKey, x.RuleId, x.ActionKey, x.Status });
            entity.HasIndex(x => new { x.MessageId, x.MailboxKey, x.RuleId, x.ActionKey })
                .IsUnique()
                .HasFilter("\"Status\" = 1");
            entity.HasIndex(x => x.TicketId);
        });

        modelBuilder.Entity<Organization>()
            .HasIndex(o => o.DnsName)
            .IsUnique();

        modelBuilder.Entity<Organization>()
            .HasIndex(o => o.ItSupportOrganizationId);

        modelBuilder.Entity<Organization>()
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(o => o.ItSupportOrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.Email)
            .IsUnique();

        modelBuilder.Entity<ScopedRoleAssignment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RoleKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OrganizationId).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.RoleKey, x.OrganizationId }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.RoleKey });
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.Property(x => x.Key).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Scope).HasConversion<int>();
            entity.Property(x => x.OwnerOrganizationId).HasMaxLength(128);
            entity.HasIndex(x => x.Key).IsUnique();
            entity.HasIndex(x => new { x.Scope, x.OwnerOrganizationId });
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasKey(x => new { x.RoleId, x.Permission });
            entity.Property(x => x.RoleId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Permission).HasMaxLength(128).IsRequired();
            entity.HasOne(x => x.Role)
                .WithMany(x => x.Permissions)
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CustomerAuthLink>(entity =>
        {
            entity.ToTable("CustomerAuthLinks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.InviteStatus).HasConversion<int>();
            entity.Property(x => x.AuthProviderType).HasMaxLength(64);
            entity.Property(x => x.OidcIssuer).HasMaxLength(512);
            entity.Property(x => x.OidcSubject).HasMaxLength(256);
            entity.Property(x => x.LocalAccountId).HasMaxLength(128);
            entity.Property(x => x.DomainUserId).HasMaxLength(128);
            entity.Property(x => x.AuthentikUserId).HasMaxLength(64);
            entity.Property(x => x.AuthentikUsername).HasMaxLength(256);
            entity.Property(x => x.AuthentikEmail).HasMaxLength(254);
            entity.HasIndex(x => x.CustomerId);
            entity.HasIndex(x => x.LocalAccountId).IsUnique();
            entity.HasIndex(x => x.DomainUserId).IsUnique();
            entity.HasIndex(x => x.AuthentikUserId);
            entity.HasIndex(x => new { x.OidcIssuer, x.OidcSubject }).IsUnique();
            entity.HasIndex(x => new { x.InviteStatus, x.InviteSentAtUtc });
            entity.HasOne<Customer>()
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailLayout>(entity =>
        {
            entity.HasIndex(x => new { x.Name, x.TenantId }).IsUnique();
        });

        modelBuilder.Entity<EmailTemplate>(entity =>
        {
            entity.HasOne<EmailLayout>()
                .WithMany()
                .HasForeignKey(x => x.LayoutId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TenantBranding>(entity =>
        {
            entity.HasKey(x => x.TenantId);
        });

        modelBuilder.Entity<ImapEmailSettings>()
            .Property(x => x.LastTestStatus)
            .HasConversion<string>();

        modelBuilder.Entity<SlaPolicy>(entity =>
        {
            entity.Property(x => x.ScopeType).HasConversion<int>();
            entity.Property(x => x.AppliesTo).HasConversion<int>();
            entity.HasIndex(x => new { x.TenantId, x.AppliesTo, x.ServiceId, x.Priority, x.IsActive });
            entity.HasIndex(x => new { x.ScopeType, x.AppliesTo, x.ServiceId, x.Priority, x.IsActive });
            entity.HasMany(x => x.Escalations)
                .WithOne()
                .HasForeignKey(x => x.PolicyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SlaEscalationRule>(entity =>
        {
            entity.Property(x => x.Metric).HasConversion<int>();
            entity.Property(x => x.Recipients)
                .HasConversion(EfJsonConverters.StringListToJson)
                .HasColumnName("RecipientsJson")
                .HasDefaultValueSql("'[]'");
            entity.Property(x => x.Targets)
                .HasConversion(EfJsonConverters.JsonList<RecipientTarget>())
                .HasColumnName("TargetsJson")
                .HasDefaultValueSql("'[]'");
            entity.HasIndex(x => new { x.PolicyId, x.Metric, x.TriggerPercent }).IsUnique();
        });

        modelBuilder.Entity<TicketSlaState>(entity =>
        {
            entity.HasKey(x => x.TicketId);
            entity.Property(x => x.Status).HasConversion<int>();
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.Status, x.CompletedAt });
            entity.HasIndex(x => x.ResolutionDueAt);
            entity.HasIndex(x => x.ResumeAt);
            entity.HasIndex(x => x.CalendarId);
            if (isSqlite)
            {
                entity.Property<long?>("CompletedAtUtcTicks").HasComputedColumnSql("CAST((julianday(\"CompletedAt\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.Property<long>("ResolutionDueAtUtcTicks").HasComputedColumnSql("CAST((julianday(\"ResolutionDueAt\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.Property<long>("ResponseDueAtUtcTicks").HasComputedColumnSql("CAST((julianday(\"ResponseDueAt\") - 2440587.5) * 864000000000 + 621355968000000000 AS INTEGER)");
                entity.HasIndex("Status", "CompletedAtUtcTicks");
                entity.HasIndex("ResolutionDueAtUtcTicks");
            }
            entity.HasOne<Ticket>()
                .WithOne()
                .HasForeignKey<TicketSlaState>(x => x.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TicketSlaEscalationEvent>(entity =>
        {
            entity.Property(x => x.Metric).HasConversion<int>();
            entity.Property(x => x.SendStatus).HasConversion<int>();
            entity.HasIndex(x => new { x.TicketId, x.Metric, x.TriggerPercent }).IsUnique();
            entity.HasOne<Ticket>()
                .WithMany()
                .HasForeignKey(x => x.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkingCalendar>(entity =>
        {
            entity.ToTable("WorkingCalendars");
            entity.Property(x => x.ScopeType).HasConversion<int>();
            entity.Property(x => x.WeeklyRules)
                .HasConversion(EfJsonConverters.JsonList<WorkingDayRule>())
                .HasColumnName("WeeklyRulesJson")
                .HasDefaultValueSql("'[]'");
            entity.Property(x => x.Exceptions)
                .HasConversion(EfJsonConverters.JsonList<CalendarException>())
                .HasColumnName("ExceptionsJson")
                .HasDefaultValueSql("'[]'");
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
            entity.HasIndex(x => new { x.ScopeType, x.IsActive });
        });

        modelBuilder.Entity<TenantSlaSettings>(entity =>
        {
            entity.ToTable("TenantSlaSettings");
            entity.HasKey(x => x.TenantId);
            entity.HasIndex(x => x.CalendarId);
        });

        modelBuilder.Entity<SlaReportSubscription>(entity =>
        {
            entity.ToTable("SlaReportSubscriptions");
            entity.Property(x => x.Frequency).HasConversion<int>();
            entity.Property(x => x.TicketType).HasConversion<int?>();
            entity.Property(x => x.Targets)
                .HasConversion(EfJsonConverters.JsonList<RecipientTarget>())
                .HasColumnName("TargetsJson")
                .HasDefaultValueSql("'[]'");
            entity.HasIndex(x => new { x.TenantId, x.IsActive });
            entity.HasIndex(x => new { x.Frequency, x.IsActive });
        });

        modelBuilder.Entity<SlaReportSendEvent>(entity =>
        {
            entity.ToTable("SlaReportSendEvents");
            entity.Property(x => x.Status).HasConversion<int>();
            entity.HasIndex(x => new { x.SubscriptionId, x.PeriodStartUtc, x.PeriodEndUtc }).IsUnique();
            entity.HasOne<SlaReportSubscription>()
                .WithMany()
                .HasForeignKey(x => x.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Change>(entity =>
        {
            entity.Property(x => x.ChangeType).HasMaxLength(32);
            if (isPostgreSql)
            {
                entity.Property(x => x.ChangeTemplateJson).HasColumnType("jsonb");
            }
            else
            {
                entity.Property(x => x.ChangeTemplateJson).HasColumnType("TEXT");
            }
        });

        modelBuilder.Entity<TicketCategory>(entity =>
        {
            entity.ToTable("TicketCategories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Type).HasConversion<int>();
            entity.HasIndex(x => new { x.Type, x.TenantId, x.SortOrder });
            entity.HasIndex(x => new { x.Name, x.Type, x.TenantId }).IsUnique();
            entity.HasOne<TicketCategory>()
                .WithMany()
                .HasForeignKey(x => x.ParentCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IncidentCategoryLink>(entity =>
        {
            entity.ToTable("IncidentCategoryLinks");
            entity.HasKey(x => new { x.IncidentId, x.TicketCategoryId });
            entity.HasOne(x => x.Incident)
                .WithMany(x => x.CategoryLinks)
                .HasForeignKey(x => x.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.TicketCategory)
                .WithMany()
                .HasForeignKey(x => x.TicketCategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RequestCategoryLink>(entity =>
        {
            entity.ToTable("RequestCategoryLinks");
            entity.HasKey(x => new { x.RequestId, x.TicketCategoryId });
            entity.HasOne(x => x.Request)
                .WithMany(x => x.CategoryLinks)
                .HasForeignKey(x => x.RequestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.TicketCategory)
                .WithMany()
                .HasForeignKey(x => x.TicketCategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChangeCategoryLink>(entity =>
        {
            entity.ToTable("ChangeCategoryLinks");
            entity.HasKey(x => new { x.ChangeId, x.TicketCategoryId });
            entity.HasOne(x => x.Change)
                .WithMany(x => x.CategoryLinks)
                .HasForeignKey(x => x.ChangeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.TicketCategory)
                .WithMany()
                .HasForeignKey(x => x.TicketCategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.ApplyConfiguration(new ServiceConfiguration(_tenantContext, _httpContextAccessor));
        modelBuilder.ApplyConfiguration(new RequestFormConfiguration(_tenantContext, isPostgreSql));
        modelBuilder.Entity<RequestForm>().HasQueryFilter(form =>
            _tenantContext.IsHelpdeskAdmin ||
            string.IsNullOrWhiteSpace(_tenantContext.TenantId) ||
            form.OrganizationId == _tenantContext.TenantId ||
            Organizations.Any(organization =>
                organization.Id == form.OrganizationId &&
                organization.ItSupportOrganizationId == _tenantContext.TenantId));
        modelBuilder.ApplyConfiguration(new NotificationEntityConfiguration());
        modelBuilder.ApplyConfiguration(new NotificationReadEntityConfiguration());
    }
}
