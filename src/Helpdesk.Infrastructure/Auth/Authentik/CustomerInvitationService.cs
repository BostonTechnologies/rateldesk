using Helpdesk.Application.Services.Email;
using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.Auth.Authentik;

public sealed class CustomerInvitationService(
    HelpdeskDbContext db,
    IAuthentikAdminClient authentik,
    IEmailService emailService,
    IRepository<EmailTemplate> templateRepository,
    IEmailLayoutResolver layoutResolver,
    ITenantBrandingResolver tenantBrandingResolver,
    IEmailTemplateRenderer templateRenderer,
    IOptions<AuthentikOptions> options,
    IConfiguration configuration,
    ILogger<CustomerInvitationService> logger) : ICustomerInvitationService
{
    private readonly HelpdeskDbContext _db = db;
    private readonly IAuthentikAdminClient _authentik = authentik;
    private readonly IEmailService _emailService = emailService;
    private readonly IRepository<EmailTemplate> _templateRepository = templateRepository;
    private readonly IEmailLayoutResolver _layoutResolver = layoutResolver;
    private readonly ITenantBrandingResolver _tenantBrandingResolver = tenantBrandingResolver;
    private readonly IEmailTemplateRenderer _templateRenderer = templateRenderer;
    private readonly AuthentikOptions _options = options.Value;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<CustomerInvitationService> _logger = logger;
    private const string CustomerInvitationTemplateName = "CustomerInvitation";

    public async Task<CustomerAuthStatusDto> GetStatusAsync(string customerId, CancellationToken ct = default)
    {
        var customer = await FindCustomerAsync(customerId, ct);
        var link = await FindLinkAsync(customerId, ct);
        var status = ToStatus(customer, link);
        if (await IsRetiredAsync(link, ct))
        {
            status.InviteStatus = CustomerInviteStatus.Disabled;
            status.StatusText = "Application access deleted";
            status.CanInvite = false;
            status.CanResend = false;
            status.CanDisableLogin = false;
        }
        return status;
    }

    public Task<CustomerAuthStatusDto> InviteAsync(string customerId, string invitedByUserId, CancellationToken ct = default)
        => SendInviteAsync(customerId, invitedByUserId, allowExistingInvite: false, ct);

    public Task<CustomerAuthStatusDto> ResendInviteAsync(string customerId, string invitedByUserId, CancellationToken ct = default)
        => SendInviteAsync(customerId, invitedByUserId, allowExistingInvite: true, ct);

    public async Task<CustomerAuthStatusDto> DisableLoginAsync(string customerId, string disabledByUserId, CancellationToken ct = default)
    {
        var customer = await FindCustomerAsync(customerId, ct);
        var link = await FindOrCreateLinkAsync(customer, ct);

        try
        {
            if (!string.IsNullOrWhiteSpace(link.AuthentikUserId))
            {
                foreach (var group in _options.CustomerDefaultGroups.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    await _authentik.RemoveUserFromGroupAsync(link.AuthentikUserId, group, ct);
                }
            }

            link.InviteStatus = CustomerInviteStatus.Disabled;
            link.DisabledAtUtc = DateTimeOffset.UtcNow;
            link.DisabledByUserId = disabledByUserId;
            link.LastAuthError = null;
            await _db.SaveChangesAsync(ct);
            await AuditAsync(customer.Id, disabledByUserId, "Customer login disabled.", ct);
        }
        catch (Exception ex)
        {
            link.LastAuthError = ex.Message;
            await _db.SaveChangesAsync(ct);
            _logger.LogError(ex, "Failed to disable Authentik login for customer {CustomerId}.", customer.Id);
            throw;
        }

        return ToStatus(customer, link);
    }

    public async Task<CustomerAuthStatusDto> SyncAuthentikAsync(string customerId, CancellationToken ct = default)
    {
        var customer = await FindCustomerAsync(customerId, ct);
        var link = await FindLinkAsync(customerId, ct) ?? await FindOrCreateLinkAsync(customer, ct);

        if (string.IsNullOrWhiteSpace(link.AuthentikUserId))
        {
            link.LastAuthSyncAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return ToStatus(customer, link);
        }

        var user = await _authentik.GetUserAsync(link.AuthentikUserId, ct);
        if (user is null)
        {
            link.LastAuthError = "Authentik user not found.";
            link.LastAuthSyncAtUtc = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return ToStatus(customer, link);
        }

        link.AuthentikUsername = user.Username;
        link.AuthentikEmail = user.Email;
        link.LastLoginAtUtc = user.LastLogin;
        link.LastAuthSyncAtUtc = DateTimeOffset.UtcNow;
        link.LastAuthError = null;
        if (user.LastLogin.HasValue && link.InviteStatus == CustomerInviteStatus.Pending)
        {
            link.InviteStatus = CustomerInviteStatus.Active;
            link.InviteAcceptedAtUtc ??= user.LastLogin;
        }

        await _db.SaveChangesAsync(ct);
        return ToStatus(customer, link);
    }

    private async Task<CustomerAuthStatusDto> SendInviteAsync(
        string customerId,
        string invitedByUserId,
        bool allowExistingInvite,
        CancellationToken ct)
    {
        EnsureConfigured();
        var customer = await FindCustomerAsync(customerId, ct);
        if (!customer.IsEnabled)
        {
            throw new InvalidOperationException("Disabled customers cannot be invited.");
        }

        var organization = await _db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customer.OrganizationId, ct);
        if (organization is null || !organization.IsEnabled)
        {
            throw new InvalidOperationException("Customers can only be invited for an enabled organization.");
        }

        var link = await FindOrCreateLinkAsync(customer, ct);
        if (await IsRetiredAsync(link, ct))
            throw new InvalidOperationException("This identity's application access was deleted. Invitations cannot restore deleted users; create a separately reviewed identity instead.");
        if (!allowExistingInvite && link.InviteStatus is CustomerInviteStatus.Pending or CustomerInviteStatus.Active)
        {
            return ToStatus(customer, link);
        }

        try
        {
            var authentikUser = string.IsNullOrWhiteSpace(link.AuthentikUserId)
                ? await _authentik.FindUserByEmailAsync(customer.Email, ct)
                    ?? await _authentik.CreateUserAsync(customer.Name, customer.Email, ct)
                : await _authentik.GetUserAsync(link.AuthentikUserId, ct)
                    ?? await _authentik.CreateUserAsync(customer.Name, customer.Email, ct);

            link.AuthentikUserId = authentikUser.Id.ToString();
            link.AuthentikUsername = authentikUser.Username;
            link.AuthentikEmail = authentikUser.Email;
            link.LastLoginAtUtc = authentikUser.LastLogin;

            foreach (var group in _options.CustomerDefaultGroups.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                await _authentik.AssignUserToGroupAsync(link.AuthentikUserId, group, ct);
            }

            var inviteLink = await _authentik.CreateRecoveryLinkAsync(link.AuthentikUserId, ct);
            link.InviteStatus = CustomerInviteStatus.Pending;
            link.InviteSentAtUtc = DateTimeOffset.UtcNow;
            link.InviteLinkExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(Math.Max(1, _options.InviteLifetimeDays));
            link.InvitedByUserId = invitedByUserId;
            link.DisabledAtUtc = null;
            link.DisabledByUserId = null;
            link.LastAuthError = null;

            var inviteEmail = await BuildInviteEmailAsync(customer, organization, inviteLink, link.InviteLinkExpiresAtUtc, ct);
            var sent = await _emailService.SendEmailAsync(
                customer.Email,
                inviteEmail.Subject,
                inviteEmail.Body,
                suppressTimeline: true,
                fromName: inviteEmail.FromName,
                replyTo: inviteEmail.ReplyTo,
                ct: ct);

            if (!sent)
            {
                link.InviteStatus = CustomerInviteStatus.Failed;
                link.LastAuthError = "Invite email could not be sent.";
            }

            await _db.SaveChangesAsync(ct);
            await AuditAsync(customer.Id, invitedByUserId, allowExistingInvite ? "Customer invite resent." : "Customer invite sent.", ct);
        }
        catch (AuthentikAdminRequestException ex)
        {
            link.InviteStatus = CustomerInviteStatus.Failed;
            link.LastAuthError = ex.Message;
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(
                ex,
                "Customer invite failed for {CustomerId}. AuthentikStatusCode={StatusCode} Operation={Operation} Path={Path}",
                customer.Id,
                (int)ex.StatusCode,
                ex.Operation,
                ex.Path);
            throw;
        }
        catch (Exception ex)
        {
            link.InviteStatus = CustomerInviteStatus.Failed;
            link.LastAuthError = ex.Message;
            await _db.SaveChangesAsync(ct);
            _logger.LogError(ex, "Customer invite failed for {CustomerId}.", customer.Id);
            throw;
        }

        return ToStatus(customer, link);
    }

    private async Task<Customer> FindCustomerAsync(string customerId, CancellationToken ct)
    {
        return await _db.Customers.FirstOrDefaultAsync(x => x.Id == customerId, ct)
            ?? throw new InvalidOperationException("Customer not found.");
    }

    private Task<CustomerAuthLink?> FindLinkAsync(string customerId, CancellationToken ct)
        => _db.CustomerAuthLinks.FirstOrDefaultAsync(
            x => x.CustomerId == customerId && x.AuthProviderType != "Local",
            ct);

    private async Task<CustomerAuthLink> FindOrCreateLinkAsync(Customer customer, CancellationToken ct)
    {
        var link = await FindLinkAsync(customer.Id, ct);
        if (link is not null)
        {
            return link;
        }

        EnsureConfigured();

        link = new CustomerAuthLink
        {
            CustomerId = customer.Id,
            AuthProviderType = "Authentik",
            InviteStatus = CustomerInviteStatus.NotInvited
        };
        _db.CustomerAuthLinks.Add(link);
        await _db.SaveChangesAsync(ct);
        return link;
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
        {
            throw new AuthentikAdminConfigurationException(_options.ConfigurationError);
        }
    }

    private async Task AuditAsync(string customerId, string userId, string message, CancellationToken ct)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            RelatedEntityId = customerId,
            UserId = userId,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task<InviteEmail> BuildInviteEmailAsync(
        Customer customer,
        Organization organization,
        string inviteLink,
        DateTimeOffset? expiresAt,
        CancellationToken ct)
    {
        var publicUrl = _configuration["PublicWebAppUrl"]?.TrimEnd('/') ?? "https://helpdesk.example.com";
        var template = await _templateRepository.Query()
            .FirstOrDefaultAsync(x => x.Name == CustomerInvitationTemplateName, ct);
        if (template is null)
        {
            _logger.LogWarning("Email template '{TemplateName}' not found. Falling back to built-in customer invitation email.", CustomerInvitationTemplateName);
            return BuildFallbackInviteEmail(customer, organization, inviteLink, expiresAt, publicUrl);
        }

        var layout = await _layoutResolver.ResolveAsync(template.LayoutId, ct);
        var branding = await _tenantBrandingResolver.ResolveAsync(organization.Id, ct);
        var context = new EmailTemplateContext
        {
            UserName = string.IsNullOrWhiteSpace(customer.Name) ? customer.Email : customer.Name,
            OrganizationName = organization.Name ?? string.Empty,
            InviteLink = inviteLink,
            InviteExpiresAt = FormatInviteExpiry(expiresAt),
            HelpdeskUrl = publicUrl,
            LayoutHtml = layout?.HtmlContent ?? string.Empty,
            BrandName = branding.BrandName,
            Brand = branding.TemplateBrand,
            LogoHtml = branding.LogoHtml,
            FooterHtml = branding.FooterHtml,
            PrimaryColor = branding.PrimaryColor
        };

        var subject = RenderSubject(template.Subject ?? "Your RatelDesk invitation", context);
        var body = _templateRenderer.Render(template.HtmlContent ?? string.Empty, context);
        return new InviteEmail(subject, body, branding.FromName, branding.ReplyTo);
    }

    private InviteEmail BuildFallbackInviteEmail(
        Customer customer,
        Organization organization,
        string inviteLink,
        DateTimeOffset? expiresAt,
        string publicUrl)
    {
        var expiry = FormatInviteExpiry(expiresAt);
        var safeName = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(customer.Name) ? customer.Email : customer.Name);
        var safeOrganization = System.Net.WebUtility.HtmlEncode(organization.Name);
        var safeInviteLink = System.Net.WebUtility.HtmlEncode(inviteLink);
        var safePublicUrl = System.Net.WebUtility.HtmlEncode(publicUrl);
        var safeExpiry = System.Net.WebUtility.HtmlEncode(expiry);

        var body = $"""
            <p>Hello {safeName},</p>
            <p>You have been invited to use RatelDesk for <strong>{safeOrganization}</strong>.</p>
            <p>
              <a href="{safeInviteLink}" style="display:inline-block; padding:12px 18px; background:#f59e0b; border:1px solid #d97706; color:#111827 !important; text-decoration:none !important; font-weight:700; line-height:20px; border-radius:6px;">Activate Helpdesk Access</a>
            </p>
            <p>If the button does not work, copy and paste this link into your browser:<br /><a href="{safeInviteLink}">{safeInviteLink}</a></p>
            <p>Helpdesk URL: <a href="{safePublicUrl}">{safePublicUrl}</a></p>
            <p>This invitation expires on {safeExpiry}.</p>
            <p>If you were not expecting this invitation, contact your support team.</p>
            """;

        return new InviteEmail("Your RatelDesk invitation", body, null, null);
    }

    private static string FormatInviteExpiry(DateTimeOffset? expiresAt) =>
        expiresAt.HasValue ? $"{expiresAt.Value:yyyy-MM-dd HH:mm} UTC" : "the expiry time shown in Helpdesk";

    private static string RenderSubject(string subject, EmailTemplateContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USER_NAME"] = context.UserName,
            ["ORGANIZATION_NAME"] = context.OrganizationName,
            ["INVITE_LINK"] = context.InviteLink,
            ["INVITE_EXPIRES_AT"] = context.InviteExpiresAt,
            ["HELPDESK_URL"] = context.HelpdeskUrl,
            ["UserName"] = context.UserName,
            ["OrganizationName"] = context.OrganizationName,
            ["InviteLink"] = context.InviteLink,
            ["InviteExpiresAt"] = context.InviteExpiresAt,
            ["HelpdeskUrl"] = context.HelpdeskUrl
        };

        var rendered = subject ?? string.Empty;
        foreach (var (key, value) in values)
        {
            rendered = rendered
                .Replace("{{{" + key + "}}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return rendered;
    }

    private Task<bool> IsRetiredAsync(CustomerAuthLink? link, CancellationToken ct) => link is null
        ? Task.FromResult(false)
        : _db.Users.AsNoTracking().AnyAsync(user => user.IsDeleted &&
            (user.Id == link.DomainUserId || user.Id == link.LocalAccountId), ct);

    private CustomerAuthStatusDto ToStatus(Customer customer, CustomerAuthLink? link)
    {
        var status = link?.InviteStatus ?? CustomerInviteStatus.NotInvited;
        return new CustomerAuthStatusDto
        {
            CustomerId = customer.Id,
            InviteStatus = status,
            StatusText = GetStatusText(status),
            AuthProviderType = link?.AuthProviderType,
            LocalAccountId = link?.LocalAccountId,
            DomainUserId = link?.DomainUserId,
            IsLinkedLogin = link is not null,
            IdentitySummary = link is null
                ? "This contact has no linked login."
                : !string.IsNullOrWhiteSpace(link.AuthentikEmail) ? link.AuthentikEmail
                : !string.IsNullOrWhiteSpace(link.AuthentikUsername) ? link.AuthentikUsername
                : "External identity linked",
            OidcIssuer = link?.OidcIssuer,
            OidcSubject = link?.OidcSubject,
            AuthentikUserId = link?.AuthentikUserId,
            AuthentikUsername = link?.AuthentikUsername,
            AuthentikEmail = link?.AuthentikEmail,
            InviteSentAtUtc = link?.InviteSentAtUtc,
            InviteAcceptedAtUtc = link?.InviteAcceptedAtUtc,
            LastLoginAtUtc = link?.LastLoginAtUtc,
            DisabledAtUtc = link?.DisabledAtUtc,
            LastAuthSyncAtUtc = link?.LastAuthSyncAtUtc,
            LastAuthError = link?.LastAuthError,
            InviteLinkExpiresAtUtc = link?.InviteLinkExpiresAtUtc,
            CanInvite = _options.IsConfigured && customer.IsEnabled &&
                status is CustomerInviteStatus.NotInvited or CustomerInviteStatus.Failed or CustomerInviteStatus.Expired,
            CanResend = _options.IsConfigured && customer.IsEnabled &&
                status is CustomerInviteStatus.Pending or CustomerInviteStatus.Failed or CustomerInviteStatus.Expired,
            CanDisableLogin = _options.IsConfigured &&
                status is CustomerInviteStatus.Pending or CustomerInviteStatus.Active
        };
    }

    private static string GetStatusText(CustomerInviteStatus status) => status switch
    {
        CustomerInviteStatus.NotInvited => "Not invited",
        CustomerInviteStatus.Pending => "Invitation pending",
        CustomerInviteStatus.Active => "Login active",
        CustomerInviteStatus.Disabled => "Disabled",
        CustomerInviteStatus.Failed => "Invite failed",
        CustomerInviteStatus.Expired => "Invite expired",
        _ => status.ToString()
    };

    private sealed record InviteEmail(string Subject, string Body, string? FromName, string? ReplyTo);
}
