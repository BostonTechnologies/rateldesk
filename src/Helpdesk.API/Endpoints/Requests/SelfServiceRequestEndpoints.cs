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
using Helpdesk.Shared.DTOs;
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
            .WithSummary("Describe eligible requester organizations without changing identity links.");
        selfService.MapGet("/submission-organizations", SearchSubmissionOrganizations)
            .WithName("SearchSelfServiceSubmissionOrganizations")
            .WithSummary("Search the eligible organizations for a self-service form.");

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
        [FromServices] IRepository<Request> requests,
        [FromServices] IAutomationBindingPayloadContractService automationPayloadContractService,
        [FromServices] IRequestFormSchemaParser schemaParser,
        [FromServices] ISelfServiceDatasetBindingResolver datasetBindingResolver,
        [FromServices] SelfServiceAudienceService audience,
        [FromServices] IRequestTaskGenerationService requestTaskGeneration,
        [FromServices] IWorkflowEngine workflowEngine,
        [FromServices] ITicketSlaInitializer ticketSlaInitializer,
        [FromServices] AppTicketServices.ITicketRefGeneratorService refs,
        [FromServices] ITicketNotificationService ticketNotificationService,
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

        if (dto.TargetOrganizationId is not null && string.IsNullOrWhiteSpace(dto.TargetOrganizationId))
            return SubmissionError(http, 400, "request_organization_required", "Choose an eligible organization for this request.");

        var requestedTarget = dto.TargetOrganizationId?.Trim();
        var submissionId = dto.SubmissionId?.ToString("N");
        var submitterKey = SubmissionOwner(user, await accessService.ResolveAsync(user, token));
        await using var tx = await db.Database.BeginTransactionAsync(token);
        if (db.Database.IsNpgsql() && submitterKey is not null)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({submitterKey}, 0))", token);

        var authority = await audience.ResolveAuthorityAsync(user, token);
        var access = authority.Access;
        if (submissionId is not null && (submitterKey is null || submitterKey != SubmissionOwner(user, access)))
            return SubmissionError(http, 403, "requester_unavailable", SelfServiceRequesterResolver.UnavailableMessage);

        var requestForm = await audience.FindFormAsync(authority, dto.RequestFormId, token);
        if (requestForm is null)
            return SubmissionError(http, 404, "request_form_denied", "This form is unavailable. Choose a form from your catalogue.");
        var catalogue = await audience.LoadCatalogueAsync(authority, token, requestForm.Id);
        var catalogueOrganizations = catalogue.FormOrganizations(requestForm);
        if (!access.IsHelpdeskAdmin && catalogueOrganizations.Count == 0)
            return SubmissionError(http, 404, "request_form_denied", "This form is unavailable. Choose a form from your catalogue.");

        var onBehalf = !string.IsNullOrWhiteSpace(dto.RequestedForPersonId);
        var identityDb = http.RequestServices.GetService<RatelDeskIdentityDbContext>();
        RequestedPerson? recipient = null;
        string organizationId;
        Customer? customer;
        if (onBehalf)
        {
            recipient = await ResolveRequestedPersonAsync(dto, db, authority, identityDb, token);
            if (recipient is null)
                return SubmissionError(http, 403, "requester_unavailable", "The selected requester is unavailable or outside your permitted organizations.");
            organizationId = recipient.OrganizationId;
            customer = recipient.Customer;
            if ((requestedTarget is not null && requestedTarget != organizationId) ||
                (!access.IsHelpdeskAdmin && !catalogueOrganizations.Any(org => org.Id == organizationId)) ||
                !catalogue.AllowsRecipient(requestForm, organizationId, customer?.Id))
                return SubmissionError(http, 409, "request_organization_mismatch", OrganizationMismatchMessage);
        }
        else
        {
            if (authority.Identity is null)
                return SubmissionError(http, 403, "requester_unavailable", SelfServiceRequesterResolver.UnavailableMessage);

            var eligible = catalogueOrganizations.Where(org => authority.CanSubmitAsSelf(org.Id)).ToList();
            var target = requestedTarget is null
                ? eligible.Count == 1 ? eligible[0] : null
                : eligible.SingleOrDefault(org => org.Id == requestedTarget);
            if (target is null)
            {
                if (requestedTarget is not null)
                    return SubmissionError(http, 403, "request_organization_denied", "That organization is unavailable for this form. Refresh and choose an eligible organization.");
                return eligible.Count == 0
                    ? SubmissionError(http, 409, "request_organization_mismatch", OrganizationMismatchMessage)
                    : SubmissionError(http, 400, "request_organization_required", "Choose an organization before submitting this request.");
            }
            organizationId = target.Id;
            customer = authority.CustomerFor(organizationId);
        }

        if (submissionId is not null)
        {
            var prior = await db.Requests.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                x.SelfServiceSubmitterId == submitterKey && x.SelfServiceSubmissionId == submissionId, token);
            if (prior is not null)
            {
                if (prior.OrganizationId == organizationId && customer is not null && prior.CustomerId == customer.Id &&
                    (prior.SelfServiceSubmissionHash == SubmissionHash(dto, organizationId, customer.Id) ||
                     prior.SelfServiceSubmissionHash == LegacySubmissionHash(dto)))
                    return Results.Ok(SubmissionReceipt(prior, authority.Organizations[organizationId]));
                return SubmissionError(http, 409, "submission_already_created", "This submission already created a request. Open My Requests to view its progress before starting another request.");
            }
        }

        using var organizationScope = db.BindSelfServiceOrganization(organizationId);
        var parsedSchema = schemaParser.Parse(requestForm.JsonSchema);
        var normalizedPayload = await datasetBindingResolver.NormalizePayloadAsync(
            organizationId,
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

        if (!onBehalf)
        {
            customer = await SelfServiceRequesterResolver.EnsureCustomerAsync(db, authority.Identity!, organizationId, token);
        }
        else if (recipient is { NeedsHomeLink: true, DomainUser: { } domainUser })
        {
            customer = await SelfServiceRequesterResolver.EnsureCustomerAsync(db, new(customer, domainUser, false),
                organizationId, token, recipient.HasLocalAccount, createHomeLink: true);
        }
        if (customer is null)
            return SubmissionError(http, 403, "requester_unavailable", SelfServiceRequesterResolver.UnavailableMessage);

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
            SelfServiceSubmitterId = submitterKey,
            SelfServiceSubmissionId = submissionId,
            SelfServiceSubmissionHash = submissionId is null ? null : SubmissionHash(dto, organizationId, customer.Id),
            RequesterEmail = customer.Email,
            TrackingId = await refs.NextReferenceAsync("REQ"),
            Priority = TicketPriority.Low,
            State = TicketState.New
        };
        if (onBehalf)
        {
            request.CcRecipients = BuildListeners(customer.Email, access.Email);
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
            recipient?.DomainUser?.Name ?? customer.Name,
            created.CcRecipients,
            token);

        return Results.Ok(SubmissionReceipt(created, authority.Organizations[organizationId]));
    }

    private static SelfServiceRequestSubmittedDto SubmissionReceipt(Request request, SelfServiceTargetOrganizationDto organization) =>
        new()
        {
            RequestId = request.Id, TrackingId = request.TrackingId,
            OrganizationId = organization.Id, OrganizationName = organization.Name
        };

    private static async Task<IResult> GetSubmissionContext(
        [FromQuery] string requestFormId,
        [FromQuery] string? targetOrganizationId,
        SelfServiceAudienceService audience,
        HttpContext http, CancellationToken token)
    {
        var context = await ResolveSubmissionTargetsAsync(audience, http.User, requestFormId, token);
        if (context is null) return Results.NotFound();
        var (authority, eligible) = context.Value;

        var selected = targetOrganizationId is null
            ? eligible.Count == 1 ? eligible[0] : null
            : eligible.SingleOrDefault(org => org.Id == targetOrganizationId.Trim());
        var (code, message) = selected is not null ? (null, null)
            : authority.Identity is null ? ("requester_unavailable", SelfServiceRequesterResolver.UnavailableMessage)
            : targetOrganizationId is not null ? ("request_organization_denied", "Choose an eligible organization for this request.")
            : eligible.Count > 1 ? ("request_organization_required", "Choose an organization for this request.")
            : ("request_organization_mismatch", OrganizationMismatchMessage);
        return Results.Ok(new SelfServiceSubmissionContextDto
        {
            EligibleOrganizations = eligible, OrganizationId = selected?.Id, OrganizationName = selected?.Name,
            RequestedForName = authority.Identity?.Name, CanSubmit = selected is not null,
            ErrorCode = code, Message = message
        });
    }

    private static async Task<IResult> SearchSubmissionOrganizations(
        [FromQuery] string requestFormId, [FromQuery] string? q, [FromQuery] string? id, [FromQuery] int? limit,
        SelfServiceAudienceService audience, ClaimsPrincipal user, CancellationToken token)
    {
        var context = await ResolveSubmissionTargetsAsync(audience, user, requestFormId, token);
        if (context is null) return Results.NotFound();
        var organizations = context.Value.Organizations;
        var search = q?.Trim();
        var filtered = id is not null ? organizations.Where(org => org.Id == id)
            : organizations.Where(org => string.IsNullOrEmpty(search) || org.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var items = filtered.Take(take + 1).ToList();
        return Results.Ok(new CreationLookupResult<SelfServiceTargetOrganizationDto>(
            items.Take(take).ToList(), organizations.Count, items.Count > take));
    }

    private static async Task<(SelfServiceAuthority Authority, List<SelfServiceTargetOrganizationDto> Organizations)?> ResolveSubmissionTargetsAsync(
        SelfServiceAudienceService audience, ClaimsPrincipal user, string requestFormId, CancellationToken token)
    {
        var authority = await audience.ResolveAuthorityAsync(user, token);
        var form = await audience.FindFormAsync(authority, requestFormId, token);
        if (form is null) return null;
        var catalogue = await audience.LoadCatalogueAsync(authority, token, form.Id);
        var organizations = catalogue.FormOrganizations(form);
        if (!authority.Access.IsHelpdeskAdmin && organizations.Count == 0) return null;
        return (authority, organizations.Where(org => authority.CanSubmitAsSelf(org.Id)).ToList());
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

    private static string LegacySubmissionHash(SubmitSelfServiceRequestDto dto) =>
        HashSubmission(JsonSerializer.Serialize(new
        { dto.RequestFormId, dto.Title, dto.Description, dto.PayloadJson, dto.RequestedForPersonId, dto.RequestedForPersonSource }));

    private static string SubmissionHash(SubmitSelfServiceRequestDto dto, string organizationId, string customerId) =>
        HashSubmission(JsonSerializer.Serialize(new
        { dto.RequestFormId, dto.Title, dto.Description, dto.PayloadJson, dto.RequestedForPersonId, dto.RequestedForPersonSource,
            OrganizationId = organizationId, CustomerId = customerId }));

    private static string HashSubmission(string intent) =>
        Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(intent)));

    private static async Task<IResult> SearchRequestUsers(
        [FromQuery] string? query, [FromQuery] int? pageSize, [FromQuery] string? targetOrganizationId,
        HelpdeskDbContext db, SelfServiceAudienceService audience,
        ClaimsPrincipal user, CancellationToken token)
    {
        var authority = await audience.ResolveAuthorityAsync(user, token);
        var visibleOrganizationIds = authority.Organizations.Keys
            .Where(id => targetOrganizationId is null || id == targetOrganizationId).ToArray();
        var take = Math.Clamp(pageSize ?? 25, 1, 100);
        var search = query?.Trim();

        var usersQuery = db.Users.AsNoTracking().Where(x => !x.IsDeleted &&
            x.OrganizationId != null && visibleOrganizationIds.Contains(x.OrganizationId) && x.Email != "");
        var customersQuery = db.Customers.AsNoTracking().Where(x =>
            x.State == Helpdesk.Shared.Models.EntityState.Enabled && visibleOrganizationIds.Contains(x.OrganizationId) && x.Email != "");

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
                && authority.Organizations.TryGetValue(result.OrganizationId, out var organization))
            {
                result.OrganizationName = organization.Name;
            }
        }

        return Results.Ok(results);
    }

    private sealed record RequestedPerson(string OrganizationId, Customer? Customer, User? DomainUser = null, bool HasLocalAccount = false, bool NeedsHomeLink = false);

    private static async Task<RequestedPerson?> ResolveRequestedPersonAsync(
        SubmitSelfServiceRequestDto dto, HelpdeskDbContext db, SelfServiceAuthority authority,
        RatelDeskIdentityDbContext? identityDb, CancellationToken token)
    {
        if (dto.RequestedForPersonSource == SelfServiceRequestPersonSource.Customer)
        {
            var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == dto.RequestedForPersonId, token);
            if (customer?.IsEnabled != true || !authority.Organizations.ContainsKey(customer.OrganizationId) ||
                (customer.IdentityUserId is not null && !await db.Users.AsNoTracking()
                    .AnyAsync(x => x.Id == customer.IdentityUserId && !x.IsDeleted, token)))
                return null;
            return new RequestedPerson(customer.OrganizationId, customer);
        }
        if (dto.RequestedForPersonSource != SelfServiceRequestPersonSource.User) return null;

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == dto.RequestedForPersonId, token);
        if (user is null || user.IsDeleted || string.IsNullOrWhiteSpace(user.OrganizationId) ||
            string.IsNullOrWhiteSpace(user.Email) || !authority.Organizations.ContainsKey(user.OrganizationId))
            return null;
        var account = identityDb is null ? null : await identityDb.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == user.Id, token);
        if (account is { IsEnabled: false } or { IsDeleted: true }) return null;

        var link = await db.CustomerAuthLinks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DomainUserId == user.Id || x.LocalAccountId == user.Id, token);
        if (link is not null)
        {
            var linked = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == link.CustomerId, token);
            return link.DisabledAtUtc is null && link.InviteStatus != CustomerInviteStatus.Disabled &&
                linked?.IsEnabled == true && linked.OrganizationId == user.OrganizationId
                ? new RequestedPerson(user.OrganizationId, linked, user) : null;
        }
        var existing = await db.Customers.AsNoTracking().SingleOrDefaultAsync(
            x => x.IdentityUserId == user.Id && x.OrganizationId == user.OrganizationId, token);
        return existing is { IsEnabled: false } ? null : new RequestedPerson(user.OrganizationId, existing, user, account is not null, NeedsHomeLink: true);
    }

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
