using Helpdesk.Application.Services.AI;
using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Application.Services.Branding;
using Helpdesk.Application.Services.Changes;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Services.SupportNotifications;
using Helpdesk.Application.Services.Tenants;
using Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Sla;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.Tickets;
using Helpdesk.Application.Notifications;
using Helpdesk.Application.Resources;
using Helpdesk.Application.Events;
using Helpdesk.Application.Timeline;
using Helpdesk.Application.WorkLogs;
using Helpdesk.Application.Workflow;
using Helpdesk.Application.AiAssistant;
using Helpdesk.Infrastructure.Configuration;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Infrastructure.Services;
using Helpdesk.Infrastructure.Events;
using Helpdesk.Infrastructure.Changes;
using Helpdesk.Infrastructure.EmailTemplates;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.RequestTasks;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Infrastructure.Storage;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Infrastructure.Resources;
using Helpdesk.Infrastructure.AiAssistant;
using Helpdesk.Infrastructure.Branding;
using Helpdesk.Infrastructure.Auth.Authentik;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Microsoft.Extensions.Http.Resilience;

namespace Helpdesk.Infrastructure;

public static class DependencyInjection
{
    private const string SqliteMigrationsAssembly = "Helpdesk.Infrastructure.SqliteMigrations";

    public static IServiceCollection AddHelpdeskInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var legacyPostgreSqlConnectionString = configuration.GetConnectionString("HelpdeskDb");
        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var databaseProvider = databaseOptions.ResolveProvider(legacyPostgreSqlConnectionString);
        var connectionString = databaseProvider switch
        {
            DatabaseProvider.PostgreSql when !string.IsNullOrWhiteSpace(legacyPostgreSqlConnectionString) => legacyPostgreSqlConnectionString,
            DatabaseProvider.PostgreSql => throw new InvalidOperationException(
                "ConnectionStrings:HelpdeskDb is required when Database:Provider is PostgreSql."),
            DatabaseProvider.Sqlite when !string.IsNullOrWhiteSpace(databaseOptions.Sqlite.Path) => CreateSqliteConnectionString(databaseOptions.Sqlite),
            _ => throw new InvalidOperationException("Database:Sqlite:Path is required when Database:Provider is Sqlite.")
        };

        services.AddHttpContextAccessor();
        services.AddOptions<Helpdesk.Infrastructure.AiAssistant.Chat.AiAssistantChatOptions>()
            .Bind(configuration.GetSection("AiAssistantChat"))
            .Validate(x => !x.Enabled || databaseProvider is DatabaseProvider.PostgreSql,
                "Native AI Assistant chat requires PostgreSQL for durable session ownership. Set AiAssistantChat:Enabled=false to use SQLite with webhook AI assistance.")
            .Validate(x => x.IsValid(), "Enabled chat requires Dev instance, session hub, credential, positive limits, and an activity heartbeat shorter than the turn inactivity timeout; private HTTP requires explicit opt-in.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<Helpdesk.Application.AiAssistant.Chat.IAiAssistantChatStore, Helpdesk.Infrastructure.AiAssistant.Chat.AiAssistantChatStore>();
        services.AddSingleton<Helpdesk.Application.AiAssistant.Chat.IChatLiveFeed, Helpdesk.Infrastructure.AiAssistant.Chat.ChatLiveFeed>();
        services.AddSingleton<Helpdesk.Infrastructure.AiAssistant.Chat.AiAssistantChatSessionManager>();
        services.AddSingleton<Helpdesk.Infrastructure.AiAssistant.Chat.IAiAssistantChatClientFactory, Helpdesk.Infrastructure.AiAssistant.Chat.AiAssistantChatClientFactory>();
        services.AddSingleton<Helpdesk.Application.AiAssistant.Chat.IAiAssistantChatTransport>(sp => sp.GetRequiredService<Helpdesk.Infrastructure.AiAssistant.Chat.AiAssistantChatSessionManager>());
        services.AddHostedService(sp => sp.GetRequiredService<Helpdesk.Infrastructure.AiAssistant.Chat.AiAssistantChatSessionManager>());
        services.AddScoped<ITenantContext, TenantContext>();
        services.Configure<StorageOptions>(configuration.GetSection("StorageOptions"));
        services.Configure<SlaReportingOptions>(configuration.GetSection("SlaReporting"));
        services.Configure<OrchestrationM2MOptions>(configuration.GetSection("Orchestration:Provider"));
        services.Configure<M2MClientOptions>(configuration.GetSection("M2M"));
        services.Configure<AuthentikOptions>(configuration.GetSection("Authentication:AuthentikAdmin"));

        services.AddSingleton(databaseOptions);
        services.AddDbContext<HelpdeskDbContext>(options => ConfigureDatabase(options, databaseProvider, connectionString));
        services.AddDbContext<RatelDeskIdentityDbContext>(options => ConfigureDatabase(options, databaseProvider, connectionString));
        services.AddRatelDeskLocalIdentity();

        AddRepositoryRegistrations(services);

        services.AddHttpClient("OrchestrationInternalApi")
            .SetHandlerLifetime(TimeSpan.FromMinutes(10));
        services.AddHttpClient("AiAssistantWebhook")
            .SetHandlerLifetime(TimeSpan.FromMinutes(10));
        services.AddHttpClient<IAuthentikAdminClient, AuthentikAdminClient>()
            .SetHandlerLifetime(TimeSpan.FromMinutes(10));

        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.AddScoped<ITicketNotificationService, TicketNotificationService>();
        services.AddScoped<ISupportAccessService, SupportAccessService>();
        services.AddScoped<ISupportNotificationRecipientResolver, SupportNotificationRecipientResolver>();
        services.AddScoped<ISupportNotificationService, SupportNotificationService>();
        services.AddScoped<ISupportNotificationBootstrapper, SupportNotificationBootstrapper>();
        services.AddSingleton<TicketAttachmentFileStore>();
        services.AddHostedService(sp => sp.GetRequiredService<TicketAttachmentFileStore>());
        services.AddScoped<ITicketAttachmentService, TicketAttachmentService>();
        services.AddScoped<ISecretProtector, DataProtectionSecretProtector>();
        services.AddScoped<IInboundInlineImageResolver, InboundInlineImageResolver>();
        services.AddScoped<IGraphEmailProcessor, GraphEmailProcessor>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxCredentialProtector>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxOutgoingCredentialProtector>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxOutgoingSettingsService>();
        services.AddScoped<Helpdesk.Infrastructure.Email.SmtpMailboxSender>();
        services.AddScoped<Helpdesk.Infrastructure.Email.GraphMailboxSender>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxSenderResolver>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxDestinationPolicy>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxSettingsService>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxConfigurationMigration>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxLeaseStore>();
        services.AddScoped<Helpdesk.Infrastructure.Email.InboundTenantRouter>();
        services.AddScoped<IIngressEffectContext, IngressEffectContext>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxOutboxStore>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxOutgoingRetryService>();
        services.AddHostedService<Helpdesk.Infrastructure.Email.MailboxOutboxDispatcher>();
        services.AddScoped<IInboundMailboxAdapter, Helpdesk.Infrastructure.Email.GraphMailboxAdapter>();
        services.AddScoped<IInboundMailboxAdapter>(sp => new Helpdesk.Infrastructure.Email.ProtocolMailboxAdapter(
            Helpdesk.Shared.Models.InboundMailboxProvider.Imap, sp.GetRequiredService<Helpdesk.Infrastructure.Email.MailboxDestinationPolicy>(), sp.GetRequiredService<Helpdesk.Infrastructure.Email.MailboxCredentialProtector>()));
        services.AddScoped<IInboundMailboxAdapter>(sp => new Helpdesk.Infrastructure.Email.ProtocolMailboxAdapter(
            Helpdesk.Shared.Models.InboundMailboxProvider.Pop3, sp.GetRequiredService<Helpdesk.Infrastructure.Email.MailboxDestinationPolicy>(), sp.GetRequiredService<Helpdesk.Infrastructure.Email.MailboxCredentialProtector>()));
        services.AddScoped<InboundTicketProcessor>(sp => ActivatorUtilities.CreateInstance<InboundTicketProcessor>(sp,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<InboundTicketProcessor>>()));

        services.AddScoped<IForwardedEmailParser, ForwardedEmailParser>();
        services.AddScoped<IInboundEmailRuleProcessor, InboundEmailRuleProcessor>();
        services.AddScoped<IInboundEmailActionExecutor, InboundEmailActionExecutor>();
        services.AddScoped<IEmailIngestionService, EmailIngestionService>();
        services.AddSingleton<IImapEmailService, ImapEmailService>();
        services.AddScoped<GraphEmailService>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxEmailService>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxWorkerPolicy>();
        services.AddScoped<Helpdesk.Infrastructure.Email.MailboxSyncService>();
        services.AddScoped<IEmailService, Helpdesk.Infrastructure.Email.IngressEmailService>();
        services.AddScoped<IEmailSettingsProvider, EmailSettingsProvider>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<ITwoFactorService, TwoFactorService>();
        services.AddSingleton<ICaptchaService, CaptchaService>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddSingleton<NotificationEventBus>();
        services.AddScoped<INotificationEventBus, Helpdesk.Infrastructure.Email.IngressNotificationEventBus>();
        services.AddScoped<IDomainEventPublisher, NotificationDomainEventPublisher>();
        services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
        services.AddScoped<ITimelineService, TimelineService>();
        services.AddSingleton<TimelineEventBus>();
        services.AddScoped<ITimelineEventBus, Helpdesk.Infrastructure.Email.IngressTimelineEventBus>();
        services.AddSingleton<IAiInvestigationEventBus, AiInvestigationEventBus>();
        services.AddScoped<IAiAssistantAiAssistantService, AiAssistantAiAssistantService>();
        services.AddScoped<ISlaPolicyResolver, SlaPolicyResolver>();
        services.AddScoped<ISlaPolicyValidator, SlaPolicyValidator>();
        services.AddScoped<IWorkingCalendarResolver, WorkingCalendarResolver>();
        services.AddScoped<IBusinessTimeCalculator, BusinessTimeCalculator>();
        services.AddScoped<IRecipientResolver, RecipientResolver>();
        services.AddScoped<ITicketSlaInitializer, TicketSlaInitializer>();
        services.AddScoped<ISlaBreachEvaluator, SlaBreachEvaluator>();
        services.AddScoped<ISlaClockService, SlaClockService>();
        services.AddScoped<ITicketSlaService, TicketSlaService>();
        services.AddScoped<ISlaEscalationEvaluator, SlaEscalationEvaluator>();
        services.AddScoped<ISlaEvaluationJob, SlaEvaluationJob>();
        services.AddScoped<ITicketSlaCompletionService, TicketSlaCompletionService>();
        services.AddScoped<IRequestFormSchemaParser, RequestFormSchemaParser>();
        services.AddScoped<IRequestTaskDependencyGraphValidator, RequestTaskDependencyGraphValidator>();
        services.AddScoped<IRequestTaskGenerationService, RequestTaskGenerationService>();
        services.AddScoped<IRequestTaskLifecycleService, RequestTaskLifecycleService>();
        services.AddScoped<IRequestTaskApprovalService, RequestTaskApprovalService>();
        services.AddScoped<IRequestTaskApprovalTimeoutProcessor, RequestTaskApprovalTimeoutProcessor>();
        services.AddScoped<ITaskEscalationProcessor, TaskEscalationProcessor>();
        services.AddScoped<IRequestTaskRetryProcessor, RequestTaskRetryProcessor>();
        services.AddScoped<IRequestTaskStateService, RequestTaskStateService>();
        services.AddScoped<IWorkflowDependencyEvaluator, WorkflowDependencyEvaluator>();
        services.AddScoped<IWorkflowConditionEvaluator, WorkflowConditionEvaluator>();
        services.AddScoped<IFailurePolicyEngine, FailurePolicyEngine>();
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        services.AddScoped<IOrchestrationConnectivityService, OrchestrationConnectivityService>();
        services.AddScoped<IAutomationBindingService, AutomationBindingService>();
        services.AddScoped<IAutomationBindingPayloadContractService, AutomationBindingPayloadContractService>();
        services.AddScoped<IAutomationBindingSchemaSyncService, AutomationBindingSchemaSyncService>();
        services.AddScoped<IAutomationBindingDriftService, AutomationBindingDriftService>();
        services.AddScoped<IAutomationBindingImportService, AutomationBindingImportService>();
        services.AddSingleton<IOrchestrationTokenService, OrchestrationTokenService>();
        services.AddScoped<IOrchestrationInternalClient, OrchestrationInternalClient>();
        services.AddScoped<IOrchestrationCatalogService, OrchestrationCatalogService>();
        services.AddScoped<IRequestTaskPayloadBuilder, RequestTaskPayloadBuilder>();
        services.AddScoped<IDataManagementService, DataManagementService>();
        services.AddScoped<IRequestFormDatasetBindingValidator, RequestFormDatasetBindingValidator>();
        services.AddScoped<ISelfServiceDatasetBindingResolver, SelfServiceDatasetBindingResolver>();
        services.AddScoped<ISlaReportingQueryService, SlaReportingQueryService>();
        services.AddScoped<ISlaReportGenerator, SlaReportGenerator>();
        services.AddScoped<ISlaReportDispatcher, SlaReportDispatcher>();
        services.AddScoped<ISlaEmailTemplate, SlaEmailTemplate>();
        services.AddScoped<IEmailSender, SlaEmailSender>();
        services.AddScoped<ITemplateEngine, TemplateEngine>();
        services.AddScoped<IEmailTemplateRenderer, EmailTemplateRenderer>();
        services.AddScoped<IEmailLayoutResolver, EmailLayoutResolver>();
        services.AddScoped<IInstanceBrandingProvider, InstanceBrandingProvider>();
        services.AddScoped<ITenantBrandingResolver, TenantBrandingResolver>();
        services.AddScoped<IHtmlSanitizerService, HtmlSanitizerService>();
        services.AddSingleton<IImageLinkSigner, ImageLinkSigner>();
        services.AddSingleton<IPublicTicketLinkSigner, PublicTicketLinkSigner>();
        services.AddScoped<IInlineImageStorageService, InlineImageStorageService>();
        services.AddScoped<IEmailTemplateImageStorageService, EmailTemplateImageStorageService>();
        services.AddScoped<ITenantBrandAssetStorageService, TenantBrandAssetStorageService>();
        services.AddScoped<IWorklogImageStorageService, WorklogImageStorageService>();
        services.AddScoped<IHtmlToPlainTextConverter, HtmlToPlainTextConverter>();
        services.AddScoped<ICustomerInvitationService, CustomerInvitationService>();
        services.AddScoped<ICurrentUserAccessService, CurrentUserAccessService>();
        services.AddScoped<IAuthorizationScopeService, AuthorizationScopeService>();

        services.AddMemoryCache();
        services.Configure<ExchangeEmailOptions>(
            configuration.GetSection("ExchangeEmail"));
        services.AddOptions<ExchangeEmailOptions>()
            .Bind(configuration.GetSection("ExchangeEmail"))
            .Validate(
                options =>
                    !options.Enabled ||
                    (!string.IsNullOrWhiteSpace(options.TenantId) &&
                     !string.IsNullOrWhiteSpace(options.ClientId) &&
                     !string.IsNullOrWhiteSpace(options.ClientSecret) &&
                     !string.IsNullOrWhiteSpace(options.MailboxAddress)),
                "Enabled ExchangeEmail configuration requires tenant, client, secret, and mailbox values.")
            .ValidateOnStart();
        return services;
    }

    private static void ConfigureDatabase(
        DbContextOptionsBuilder options,
        DatabaseProvider provider,
        string connectionString)
    {
        if (provider is DatabaseProvider.PostgreSql)
        {
            options.UseNpgsql(connectionString);
            return;
        }

        options.UseSqlite(connectionString, sqlite =>
            sqlite.MigrationsAssembly(SqliteMigrationsAssembly));
    }

    private static string CreateSqliteConnectionString(SqliteDatabaseOptions options)
    {
        var path = Path.GetFullPath(options.Path);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Database:Sqlite:Path must include a directory.");
        }

        if (options.CreateIfMissing)
        {
            Directory.CreateDirectory(directory);
        }
        return new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
            ForeignKeys = true,
            Mode = options.CreateIfMissing
                ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate
                : Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite
        }.ToString();
    }

    private static void AddRepositoryRegistrations(IServiceCollection services)
    {
        services.AddScoped<IRepository<Incident>, EfRepository<Incident>>();
        services.AddScoped<IRepository<Request>, EfRepository<Request>>();
        services.AddScoped<IRepository<RequestTask>, EfRepository<RequestTask>>();
        services.AddScoped<IRepository<Change>, EfRepository<Change>>();
        services.AddScoped<IChangeTemplateService, ChangeTemplateService>();
        services.AddScoped<IRepository<WorkLog>, EfRepository<WorkLog>>();
        services.AddScoped<IRepository<TicketTimelineEvent>, EfRepository<TicketTimelineEvent>>();
        services.AddScoped<IRepository<Ticket>, EfRepository<Ticket>>();
        services.AddScoped<IRepository<TicketEvent>, EfRepository<TicketEvent>>();
        services.AddScoped<IRepository<Role>, EfRepository<Role>>();
        services.AddScoped<IRepository<User>, EfRepository<User>>();
        services.AddScoped<IRepository<Organization>, EfRepository<Organization>>();
        services.AddScoped<IRepository<Customer>, EfRepository<Customer>>();
        services.AddScoped<IRepository<CustomerAuthLink>, EfRepository<CustomerAuthLink>>();
        services.AddScoped<IRepository<Asset>, EfRepository<Asset>>();
        services.AddScoped<IRepository<SlaPolicy>, SlaPolicyRepository>();
        services.AddScoped<IRepository<WorkingCalendar>, EfRepository<WorkingCalendar>>();
        services.AddScoped<IRepository<TenantSlaSettings>, EfRepository<TenantSlaSettings>>();
        services.AddScoped<IRepository<SlaReportSubscription>, EfRepository<SlaReportSubscription>>();
        services.AddScoped<IRepository<SlaReportSendEvent>, EfRepository<SlaReportSendEvent>>();
        services.AddScoped<ISlaPolicyRepository, SlaPolicyRepository>();
        services.AddScoped<IWorkingCalendarRepository, WorkingCalendarRepository>();
        services.AddScoped<ITenantSlaSettingsRepository, TenantSlaSettingsRepository>();
        services.AddScoped<ISlaReportSendEventRepository, SlaReportSendEventRepository>();
        services.AddScoped<ITicketSlaRepository, TicketSlaRepository>();
        services.AddScoped<ITicketSlaQueryRepository, TicketSlaQueryRepository>();
        services.AddScoped<ITicketSlaEscalationEventRepository, TicketSlaEscalationEventRepository>();
        services.AddScoped<IRepository<AutomationRule>, EfRepository<AutomationRule>>();
        services.AddScoped<IRepository<ActivityLog>, EfRepository<ActivityLog>>();
        services.AddScoped<IRepository<BlockedEntity>, EfRepository<BlockedEntity>>();
        services.AddScoped<IRepository<EmailTemplate>, EfRepository<EmailTemplate>>();
        services.AddScoped<IRepository<EmailLayout>, EfRepository<EmailLayout>>();
        services.AddScoped<IRepository<TenantBranding>, EfRepository<TenantBranding>>();
        services.AddScoped<IRepository<InstanceBranding>, EfRepository<InstanceBranding>>();
        services.AddScoped<IRepository<Service>, EfRepository<Service>>();
        services.AddScoped<IRepository<RequestForm>, EfRepository<RequestForm>>();
        services.AddScoped<IRepository<AutomationBinding>, EfRepository<AutomationBinding>>();
        services.AddScoped<IRepository<DatasetDefinition>, EfRepository<DatasetDefinition>>();
        services.AddScoped<IRepository<DatasetColumn>, EfRepository<DatasetColumn>>();
        services.AddScoped<IRepository<DatasetRow>, EfRepository<DatasetRow>>();
        services.AddScoped<IRepository<DatasetIngestCredential>, EfRepository<DatasetIngestCredential>>();
        services.AddScoped<IRepository<TenantGraphDatasetSettings>, EfRepository<TenantGraphDatasetSettings>>();
        services.AddScoped<IRepository<PasswordResetToken>, EfRepository<PasswordResetToken>>();
        services.AddScoped<IRepository<TwoFactorCode>, EfRepository<TwoFactorCode>>();
        services.AddScoped<IRepository<SupportGroup>, EfRepository<SupportGroup>>();
        services.AddScoped<IRepository<SupportGroupMember>, EfRepository<SupportGroupMember>>();
        services.AddScoped<IRepository<OrganizationSupportCoverage>, EfRepository<OrganizationSupportCoverage>>();
        services.AddScoped<IRepository<SupportNotificationSubscription>, EfRepository<SupportNotificationSubscription>>();
        services.AddScoped<IRepository<UserSupportNotificationPreference>, EfRepository<UserSupportNotificationPreference>>();
        services.AddScoped<IRepository<SupportNotificationDelivery>, EfRepository<SupportNotificationDelivery>>();
        services.AddScoped<IRepository<InboundEmailRule>, EfRepository<InboundEmailRule>>();
        services.AddScoped<IRepository<InboundEmailProcessingLog>, EfRepository<InboundEmailProcessingLog>>();
    }
}
