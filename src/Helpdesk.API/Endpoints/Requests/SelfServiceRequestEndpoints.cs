using System.Text.Json;
using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.API.Services;
using Helpdesk.API.Endpoints.Orchestration;
using AppTicketServices = Helpdesk.Application.Services.Tickets;
using Helpdesk.Application.Services.Notifications;
using Helpdesk.Application.Sla;
using Helpdesk.Application.Workflow;
using Helpdesk.Application.Resources;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Request;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Requests;

public static class SelfServiceRequestEndpoints
{
    private const int MaxPayloadBytes = 64 * 1024;
    private const string OrganizationMismatchMessage = "This form's automation is configured for a different organization. Choose a form available in the displayed requester organization, or ask an administrator to review the form and saved connection mapping.";

    public static void MapSelfServiceRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var selfService = app.MapGroup("/api/v1/self-service")
            .WithTags("Self Service")
            .RequireAuthorization("SelfService.User");

        selfService.MapGet("/request-users", SearchRequestUsers)
            .WithName("SearchSelfServiceRequestUsers")
            .WithSummary("Search users and customer contacts available for submit-on-behalf self-service requests.");
        selfService.MapGet("/submission-context", GetSubmissionContext)
            .WithName("GetSelfServiceSubmissionContext")
            .WithSummary("Describe the persisted requester organization and form eligibility without changing identity links.");

        var group = app.MapGroup("/api/v1/self-service/requests")
            .WithTags("Self Service")
            .RequireAuthorization("SelfService.User");

        group.MapPost("/", SubmitRequest)
            .WithName("SubmitSelfServiceRequest")
            .WithSummary("Submit request from self-service portal")
            .WithDescription("Creates a Request from a RequestForm schema payload and generates RequestTasks.");
    }

    private static async Task<IResult> SubmitRequest(
        [FromBody] SubmitSelfServiceRequestDto dto,
        [FromServices] HelpdeskDbContext db,
        [FromServices] IRepository<RequestForm> requestForms,
        [FromServices] IRepository<Request> requests,
        [FromServices] IAutomationBindingPayloadContractService automationPayloadContractService,
        [FromServices] IRequestFormSchemaParser schemaParser,
        [FromServices] ISelfServiceDatasetBindingResolver datasetBindingResolver,
        [FromServices] ISelfServiceAudienceService selfServiceAudienceService,
        [FromServices] IRequestTaskGenerationService requestTaskGeneration,
        [FromServices] IWorkflowEngine workflowEngine,
        [FromServices] ITicketSlaInitializer ticketSlaInitializer,
        [FromServices] AppTicketServices.ITicketRefGeneratorService refs,
        [FromServices] ITicketNotificationService ticketNotificationService,
        [FromServices] ITenantContext tenant,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] IDomainEventPublisher domainEvents,
        [FromServices] ICorrelationContext correlation,
        [FromServices] ILoggerFactory loggerFactory,
        ClaimsPrincipal user,
        HttpContext http,
        CancellationToken token)
    {
        var logger = loggerFactory.CreateLogger("SelfServiceRequestEndpoints");
        if (string.IsNullOrWhiteSpace(dto.RequestFormId))
        {
            return Results.BadRequest("RequestFormId is required.");
        }

        if (string.IsNullOrWhiteSpace(dto.PayloadJson))
        {
            return Results.BadRequest("PayloadJson is required.");
        }

        if (global::System.Text.Encoding.UTF8.GetByteCount(dto.PayloadJson) > MaxPayloadBytes)
        {
            return Results.BadRequest($"PayloadJson exceeds maximum size of {MaxPayloadBytes} bytes.");
        }

        try
        {
            using var _ = JsonDocument.Parse(dto.PayloadJson);
        }
        catch (JsonException)
        {
            return Results.BadRequest("PayloadJson must be valid JSON.");
        }

        var requestForm = await requestForms.GetAsync(dto.RequestFormId);
        if (requestForm is null)
        {
            return Results.NotFound("RequestForm not found.");
        }

        if (!await selfServiceAudienceService.CanAccessRequestFormAsync(requestForm, token))
        {
            return SubmissionError(http, 403, "request_form_denied", "This form is not available to your account. Choose an eligible form or ask an administrator to review its audience.");
        }

        var access = await accessService.ResolveAsync(user, token);
        var submitterEmail = access.Email;

        var submitterKey = SubmissionOwner(user, access);
        var submissionId = dto.SubmissionId?.ToString("N");
        var submissionHash = SubmissionHash(dto);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        if (db.Database.IsNpgsql() && submitterKey is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({submitterKey}, 0))", token);
        access = await accessService.ResolveAsync(user, token);
        Customer? customer;
        string? requestedForNameResult;
        if (string.IsNullOrWhiteSpace(dto.RequestedForPersonId))
        {
            var requester = await SelfServiceRequesterResolver.ResolveAsync(db,
                http.RequestServices.GetService<RatelDeskIdentityDbContext>(), access, user, token);
            if (!requester.IsEligible)
                return SubmissionError(http, 403, requester.ErrorCode!, requester.Message!);
            if (!await IsCompatibleOrganizationAsync(db, requestForm, requester.OrganizationId, token))
                return SubmissionError(http, 409, "request_organization_mismatch", OrganizationMismatchMessage);
            customer = await SelfServiceRequesterResolver.EnsureCustomerAsync(db, requester, token);
            requestedForNameResult = requester.Name;
        }
        else
        {
            (customer, requestedForNameResult) = await ResolveRequestedForCustomerAsync(dto, db, access,
                http.RequestServices.GetService<RatelDeskIdentityDbContext>(), token);
        }
        if (customer is null)
        {
            return SubmissionError(http, 403, "requester_unavailable", "The selected requester is unavailable or outside your permitted organizations. Choose an eligible requester and retry.");
        }
        if (!await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == customer.OrganizationId && x.IsEnabled, token))
            return SubmissionError(http, 403, "request_organization_denied", "The requester's organization is unavailable. Ask an administrator to review its access.");
        if (!await IsCompatibleOrganizationAsync(db, requestForm, customer.OrganizationId, token))
            return SubmissionError(http, 409, "request_organization_mismatch", OrganizationMismatchMessage);

        if (submissionId is not null)
        {
            if (submitterKey is null)
                return SubmissionError(http, 403, "requester_unavailable", "Your account has no eligible requester profile. Sign in again or ask an administrator to review it.");
            var prior = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.SelfServiceSubmitterId == submitterKey && x.SelfServiceSubmissionId == submissionId, token);
            if (prior is not null)
                return prior.SelfServiceSubmissionHash == submissionHash
                    ? Results.Ok(new SelfServiceRequestSubmittedDto { RequestId = prior.Id, TrackingId = prior.TrackingId })
                    : SubmissionError(http, 409, "submission_already_created", "This submission already created a request. Open My Requests to view its progress before starting another request.");
        }

        var requestedForName = requestedForNameResult ?? customer.Name;

        var parsedSchema = schemaParser.Parse(requestForm.JsonSchema);
        var normalizedPayload = await datasetBindingResolver.NormalizePayloadAsync(
            customer.OrganizationId,
            parsedSchema.Fields,
            dto.PayloadJson,
            token);
        if (!normalizedPayload.Success)
        {
            return Results.BadRequest(string.Join(Environment.NewLine, normalizedPayload.Errors));
        }

        var automationPayloadValidation = await automationPayloadContractService.ValidateBoundRequestPayloadAsync(
            requestForm,
            normalizedPayload.PayloadJson,
            token);
        if (!automationPayloadValidation.Success)
        {
            return Results.BadRequest(string.Join(Environment.NewLine, automationPayloadValidation.Errors));
        }

        var schema = ExtractSchemaMetadata(requestForm.JsonSchema);
        var request = new Request
        {
            Title = string.IsNullOrWhiteSpace(dto.Title) ? schema.Title : dto.Title,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? schema.Description : dto.Description,
            RequestFormId = requestForm.Id,
            ServiceId = requestForm.ServiceId,
            PayloadJson = normalizedPayload.PayloadJson,
            OrganizationId = customer.OrganizationId,
            CustomerId = customer.Id,
            SelfServiceSubmitterId = submissionId is null ? null : submitterKey,
            SelfServiceSubmissionId = submissionId,
            SelfServiceSubmissionHash = submissionId is null ? null : submissionHash,
            RequesterEmail = customer.Email,
            TrackingId = await refs.NextReferenceAsync("REQ"),
            Priority = TicketPriority.Low,
            State = TicketState.New
        };
        if (!string.IsNullOrWhiteSpace(dto.RequestedForPersonId))
        {
            request.CcRecipients = BuildListeners(customer.Email, submitterEmail);
        }

        var created = await requests.CreateAsync(request);
        try
        {
            await ticketSlaInitializer.InitializeAsync(created);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SLA initialization failed for self-service request {RequestId}", created.Id);
        }

        await requestTaskGeneration.GenerateForRequestAsync(created, token);
        var automationContinuation = await db.RequestTasks
            .Where(task => task.RequestId == created.Id && task.Type == RequestTaskType.Automation)
            .OrderBy(task => task.Order).ThenBy(task => task.Id).FirstOrDefaultAsync(token);
        if (automationContinuation is not null)
            automationContinuation.AutomationContinuationPending = true;
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);

        if (automationContinuation is null)
            await workflowEngine.RunAsync(created.Id, WorkflowRunReason.RequestCreated, token);
        else
            await http.RequestServices.GetRequiredService<AutomationExecutionOutcomeService>()
                .ContinueAsync(automationContinuation.Id, automationContinuation.AutomationAttemptCorrelationId, token);

        await domainEvents.PublishAsync(
            new SelfServiceRequestSubmittedEvent(
                created.Id,
                requestForm.Id,
                requestForm.ServiceId,
                created.TrackingId,
                customer.Id,
                customer.OrganizationId,
                DateTimeOffset.UtcNow,
                GetCorrelationId(correlation)),
            token);

        _ = await ticketNotificationService.SendSelfServiceRequestCreatedAsync(
            created,
            requestForm,
            customer.Email,
            requestedForName,
            created.CcRecipients,
            token);

        return Results.Ok(new SelfServiceRequestSubmittedDto
        {
            RequestId = created.Id,
            TrackingId = created.TrackingId
        });
    }

    private static async Task<IResult> GetSubmissionContext(
        [FromQuery] string requestFormId,
        [FromServices] HelpdeskDbContext db,
        [FromServices] IRepository<RequestForm> requestForms,
        [FromServices] ICurrentUserAccessService accessService,
        [FromServices] ISelfServiceAudienceService audience,
        HttpContext http, CancellationToken token)
    {
        var form = await requestForms.GetAsync(requestFormId);
        if (form is null) return Results.NotFound();
        var access = await accessService.ResolveAsync(http.User, token);
        var requester = await SelfServiceRequesterResolver.ResolveAsync(db,
            http.RequestServices.GetService<RatelDeskIdentityDbContext>(), access, http.User, token);
        var compatible = requester.IsEligible && await audience.CanAccessRequestFormAsync(form, token)
            && await IsCompatibleOrganizationAsync(db, form, requester.OrganizationId, token);
        return Results.Ok(new SelfServiceSubmissionContextDto
        {
            OrganizationId = requester.OrganizationId, OrganizationName = requester.OrganizationName,
            RequestedForName = requester.Name, CanSubmit = compatible,
            ErrorCode = requester.ErrorCode ?? (compatible ? null : "request_organization_mismatch"),
            Message = requester.Message ?? (compatible ? null : OrganizationMismatchMessage)
        });
    }

    private static Task<bool> IsCompatibleOrganizationAsync(HelpdeskDbContext db, RequestForm form, string? organizationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(organizationId) ||
            (!string.IsNullOrWhiteSpace(form.OrganizationId) && form.OrganizationId != organizationId) ||
            (form.AllowedOrganizationIds is { Count: > 0 } && !form.AllowedOrganizationIds.Contains(organizationId)))
            return Task.FromResult(false);
        return NoForeignBindingsAsync();
        async Task<bool> NoForeignBindingsAsync() => !await db.AutomationBindings.AsNoTracking()
            .AnyAsync(x => x.RequestFormId == form.Id && x.Enabled && x.OrganizationId != organizationId, ct);
    }

    private static IResult SubmissionError(HttpContext http, int status, string code, string message) =>
        Results.Problem(statusCode: status, title: "Request could not be submitted", detail: message,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = http.TraceIdentifier });

    private static string? SubmissionOwner(ClaimsPrincipal principal, CurrentUserAccessProfile access)
    {
        var accountId = principal.FindFirstValue("auth_mode") is "local" or "integration"
            ? principal.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        if (!string.IsNullOrWhiteSpace(accountId)) return "account:" + accountId;
        return string.IsNullOrWhiteSpace(access.CustomerId) ? null : "customer:" + access.CustomerId;
    }

    private static string SubmissionHash(SubmitSelfServiceRequestDto dto) =>
        Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { dto.RequestFormId, dto.Title, dto.Description, dto.PayloadJson, dto.RequestedForPersonId, dto.RequestedForPersonSource }))));

    private static async Task<IResult> SearchRequestUsers(
        [FromQuery] string? query,
        [FromQuery] int? pageSize,
        [FromServices] HelpdeskDbContext db,
        [FromServices] ITenantContext tenant,
        [FromServices] ICurrentUserAccessService accessService,
        ClaimsPrincipal user,
        CancellationToken token)
    {
        var access = await accessService.ResolveAsync(user, token);
        var visibleOrganizationIds = await ResolveVisibleOrganizationIdsAsync(db, access, token);
        var isAdmin = access.IsHelpdeskAdmin || tenant.IsHelpdeskAdmin;
        var take = Math.Clamp(pageSize ?? 25, 1, 100);
        var search = query?.Trim();

        var organizationNames = await db.Organizations
            .AsNoTracking()
            .Where(x => isAdmin || visibleOrganizationIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, token);

        var usersQuery = db.Users.AsNoTracking().Where(x => !x.IsDeleted);
        if (!isAdmin)
        {
            usersQuery = usersQuery.Where(x => x.OrganizationId != null && visibleOrganizationIds.Contains(x.OrganizationId));
        }

        var customersQuery = db.Customers.AsNoTracking()
            .Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled);
        if (!isAdmin)
        {
            customersQuery = customersQuery.Where(x => visibleOrganizationIds.Contains(x.OrganizationId));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            usersQuery = usersQuery.Where(x =>
                EF.Functions.Like(x.Name, pattern) ||
                EF.Functions.Like(x.Email, pattern));
            customersQuery = customersQuery.Where(x =>
                EF.Functions.Like(x.Name, pattern) ||
                EF.Functions.Like(x.Email, pattern));
        }

        var users = await usersQuery
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Email)
            .Take(take)
            .Select(x => new SelfServiceRequestUserDto
            {
                Id = x.Id,
                Name = x.Name,
                Email = x.Email,
                Source = SelfServiceRequestPersonSource.User,
                OrganizationId = x.OrganizationId,
                Role = x.Role
            })
            .ToListAsync(token);

        var remaining = Math.Max(0, take - users.Count);
        var customers = remaining == 0
            ? new List<SelfServiceRequestUserDto>()
            : await customersQuery
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Email)
                .Take(remaining)
                .Select(x => new SelfServiceRequestUserDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    Email = x.Email,
                    Source = SelfServiceRequestPersonSource.Customer,
                    OrganizationId = x.OrganizationId,
                    Role = "Customer"
                })
                .ToListAsync(token);

        var results = users
            .Concat(customers)
            .Where(x => !string.IsNullOrWhiteSpace(x.Email))
            .DistinctBy(x => $"{x.Source}:{x.Id}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();

        foreach (var result in results)
        {
            if (!string.IsNullOrWhiteSpace(result.OrganizationId)
                && organizationNames.TryGetValue(result.OrganizationId, out var organizationName))
            {
                result.OrganizationName = organizationName;
            }
        }

        return Results.Ok(results);
    }

    private static async Task<(Customer? Customer, string? Name)> ResolveRequestedForCustomerAsync(
        SubmitSelfServiceRequestDto dto,
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        RatelDeskIdentityDbContext? identityDb,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(dto.RequestedForPersonId))
        {
            var customer = string.IsNullOrWhiteSpace(access.CustomerId)
                ? null
                : await db.Customers.FirstOrDefaultAsync(
                    candidate => candidate.Id == access.CustomerId && candidate.State == Helpdesk.Shared.Models.EntityState.Enabled,
                    token);
            return customer is not null && access.HasPermission(HelpdeskPermissions.SelfServiceUser, customer.OrganizationId)
                ? (customer, customer.Name)
                : (null, null);
        }

        if (dto.RequestedForPersonSource is null)
        {
            return (null, null);
        }

        var visibleOrganizationIds = await ResolveVisibleOrganizationIdsAsync(db, access, token);
        var isAdmin = access.IsHelpdeskAdmin;
        return dto.RequestedForPersonSource.Value switch
        {
            SelfServiceRequestPersonSource.Customer => await ResolveRequestedCustomerAsync(
                db,
                dto.RequestedForPersonId,
                visibleOrganizationIds,
                isAdmin,
                token),
            SelfServiceRequestPersonSource.User => await ResolveRequestedUserAsCustomerAsync(
                db,
                identityDb,
                dto.RequestedForPersonId,
                visibleOrganizationIds,
                isAdmin,
                token),
            _ => (null, null)
        };
    }

    private static async Task<(Customer? Customer, string? Name)> ResolveRequestedCustomerAsync(
        HelpdeskDbContext db,
        string id,
        IReadOnlySet<string> visibleOrganizationIds,
        bool isAdmin,
        CancellationToken token)
    {
        var customer = await db.Customers
            .FirstOrDefaultAsync(x => x.Id == id && x.State == Helpdesk.Shared.Models.EntityState.Enabled, token);
        if (customer is null || (customer.IdentityUserId is not null && !await db.Users.AnyAsync(x => x.Id == customer.IdentityUserId && !x.IsDeleted, token))
            || !CanUseOrganization(customer.OrganizationId, visibleOrganizationIds, isAdmin))
        {
            return (null, null);
        }

        return (customer, customer.Name);
    }

    private static async Task<(Customer? Customer, string? Name)> ResolveRequestedUserAsCustomerAsync(
        HelpdeskDbContext db,
        RatelDeskIdentityDbContext? identityDb,
        string id,
        IReadOnlySet<string> visibleOrganizationIds,
        bool isAdmin,
        CancellationToken token)
    {
        var selectedUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, token);
        if (selectedUser is null || selectedUser.IsDeleted
            || string.IsNullOrWhiteSpace(selectedUser.OrganizationId)
            || string.IsNullOrWhiteSpace(selectedUser.Email)
            || !CanUseOrganization(selectedUser.OrganizationId, visibleOrganizationIds, isAdmin))
        {
            return (null, null);
        }
        if (identityDb is not null && await identityDb.Users.AsNoTracking()
            .AnyAsync(account => account.Id == selectedUser.Id && !account.IsEnabled, token))
            return (null, null);

        var link = await db.CustomerAuthLinks.SingleOrDefaultAsync(x => x.DomainUserId == selectedUser.Id || x.LocalAccountId == selectedUser.Id, token);
        if (link is not null)
        {
            var linkedCustomer = await db.Customers.SingleOrDefaultAsync(x => x.Id == link.CustomerId, token);
            return link.DisabledAtUtc is null && link.InviteStatus != CustomerInviteStatus.Disabled &&
                linkedCustomer?.IsEnabled == true && linkedCustomer.OrganizationId == selectedUser.OrganizationId
                ? (linkedCustomer, selectedUser.Name) : (null, null);
        }
        var existing = await db.Customers.SingleOrDefaultAsync(x => x.IdentityUserId == selectedUser.Id, token);
        if (existing is not null && (!existing.IsEnabled || existing.OrganizationId != selectedUser.OrganizationId)) return (null, null);
        var hasLocalAccount = identityDb is not null && await identityDb.Users.AnyAsync(x => x.Id == selectedUser.Id && x.IsEnabled, token);
        var customer = await SelfServiceRequesterResolver.EnsureCustomerAsync(db,
            new SelfServiceRequester(existing, selectedUser, selectedUser.OrganizationId, null, selectedUser.Name, null, null), token, hasLocalAccount);
        return (customer, selectedUser.Name);
    }

    private static async Task<HashSet<string>> ResolveVisibleOrganizationIdsAsync(
        HelpdeskDbContext db,
        CurrentUserAccessProfile access,
        CancellationToken token)
    {
        var organizationIds = access.OrganizationIdsFor(HelpdeskPermissions.SelfServiceUser)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(access.PrimaryOrganizationId)
            && access.HasPermission(HelpdeskPermissions.SelfServiceUser, access.PrimaryOrganizationId))
        {
            organizationIds.Add(access.PrimaryOrganizationId);
        }

        if (access.IsHelpdeskAdmin)
        {
            var allEnabled = await db.Organizations
                .AsNoTracking()
                .Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled)
                .Select(x => x.Id)
                .ToListAsync(token);
            foreach (var organizationId in allEnabled)
            {
                organizationIds.Add(organizationId);
            }
        }

        return organizationIds;
    }

    private static bool CanUseOrganization(
        string? organizationId,
        IReadOnlySet<string> visibleOrganizationIds,
        bool isAdmin) =>
        isAdmin || !string.IsNullOrWhiteSpace(organizationId) && visibleOrganizationIds.Contains(organizationId);

    private static List<string> BuildListeners(params string?[] emails)
    {
        return emails
            .Where(LooksLikeEmail)
            .Select(email => email!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (string Title, string Description) ExtractSchemaMetadata(JsonDocument schema)
    {
        var title = "Request";
        var description = string.Empty;

        if (schema.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (schema.RootElement.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String)
            {
                title = titleNode.GetString() ?? title;
            }

            if (schema.RootElement.TryGetProperty("description", out var descriptionNode) && descriptionNode.ValueKind == JsonValueKind.String)
            {
                description = descriptionNode.GetString() ?? description;
            }
        }

        return (string.IsNullOrWhiteSpace(title) ? "Request" : title, description);
    }

    private static string GetCorrelationId(ICorrelationContext correlation)
    {
        return correlation.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private static bool LooksLikeEmail(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Contains('@', StringComparison.Ordinal);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

}
