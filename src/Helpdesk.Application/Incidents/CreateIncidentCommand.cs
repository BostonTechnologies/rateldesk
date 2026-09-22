using Helpdesk.Shared.Models;
using Helpdesk.Application.Events;
using Helpdesk.Application.Sla;
using AppServices = Helpdesk.Application.Services;
using Helpdesk.Shared.Services;
using Helpdesk.Application.Messaging;
using Helpdesk.Application.Services.SupportNotifications;
using Dodo.Primitives;
using Helpdesk.Application.Services.Email;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Helpdesk.Application.Incidents;

/// <summary>
/// Represents a command to create a new incident with the specified details.
/// </summary>
/// <remarks>This command is used to initiate the creation of an incident in the system.  It includes details such
/// as the title, description, priority, and other optional metadata  related to the incident. The result of this
/// command is an <see cref="Incident"/> object  representing the created incident.</remarks>
/// <param name="Title">The title of the incident. This value cannot be null or empty.</param>
/// <param name="Description">A detailed description of the incident. This value cannot be null or empty.</param>
/// <param name="Priority">The priority of the incident. If null, the default priority will be used.</param>
/// <param name="CustomerId">The identifier of the customer associated with the incident. Can be null if not applicable.</param>
/// <param name="OrganizationId">The identifier of the organization associated with the incident. Can be null if not applicable.</param>
/// <param name="LinkedAssetIds">A collection of asset identifiers linked to the incident. Can be null or empty if no assets are linked.</param>
/// <param name="Attachments">A collection of file paths or identifiers for attachments related to the incident. Can be null or empty if no
/// attachments are provided.</param>
/// <param name="DueDate">The due date for resolving the incident. Can be null if no due date is specified.</param>
/// <param name="Impact">A description of the impact of the incident. Can be null if no impact description is provided.</param>
public record CreateIncidentCommand(
    string Title,
    string Description,
    TicketPriority? Priority,
    string? CustomerId,
    string? OrganizationId,
    IEnumerable<string>? LinkedAssetIds,
    IEnumerable<string>? Attachments,
    DateTime? DueDate,
    string? Impact,
    string? RequesterEmail,
    IEnumerable<string>? CcRecipients,
    string? AssignedToId) : IRequest<Incident>;

/// <summary>
/// Handles the creation of a new incident based on the provided command.
/// </summary>
/// <remarks>This handler creates a deterministic service-desk incident, assigns its tracking ID,
/// initializes its SLA state, and publishes the normal ticket events.</remarks>
/// <param name="incidents">The repository used to persist incidents.</param>
/// <param name="refGenerator">The service used to generate unique tracking references for incidents.</param>
public class CreateIncidentCommandHandler(
    IRepository<Incident> incidents,
    AppServices.Tickets.ITicketRefGeneratorService refGenerator,
    ISupportNotificationService supportNotificationService,
    ITicketSlaInitializer? ticketSlaInitializer = null,
    IDomainEventPublisher? domainEvents = null,
    ICorrelationContext? correlationContext = null,
    IIngressEffectContext? ingress = null,
    ILogger<CreateIncidentCommandHandler>? logger = null)
    : IRequestHandler<CreateIncidentCommand, Incident>
{
    private readonly ITicketSlaInitializer? _ticketSlaInitializer = ticketSlaInitializer;
    private readonly IDomainEventPublisher _domainEvents = domainEvents ?? NoopDomainEventPublisher.Instance;
    private readonly ICorrelationContext? _correlationContext = correlationContext;

    public async Task<Incident> Handle(CreateIncidentCommand request, CancellationToken cancellationToken)
    {
        var incident = new Incident
        {
            Id = ingress?.IsActive == true
                ? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{ingress.ReceiptId:D}:incident:{ingress.NextCreationOrdinal()}"))[..16]).ToString()
                : Uuid.CreateVersion7().ToString(),
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority ?? TicketPriority.Low,
            CustomerId = request.CustomerId ?? "Unknown",
            OrganizationId = request.OrganizationId ?? "Unknown",
            DueDate = request.DueDate,
            Impact = request.Impact,
            RequesterEmail = request.RequesterEmail,
            AssignedToId = string.IsNullOrWhiteSpace(request.AssignedToId) ? null : request.AssignedToId,
        };
        if (request.LinkedAssetIds is not null)
        {
            incident.LinkedAssetIds.AddRange(request.LinkedAssetIds);
        }
        if (request.Attachments is not null)
        {
            incident.Attachments.AddRange(request.Attachments);
        }
        if (request.CcRecipients is not null)
        {
            incident.CcRecipients.AddRange(request.CcRecipients);
        }

        incident.TrackingId = await refGenerator.NextReferenceAsync("INC");
        incident = await incidents.CreateAsync(incident);

        try
        {
            if (_ticketSlaInitializer is not null)
            {
                await _ticketSlaInitializer.InitializeAsync(incident);
            }
        }
        catch (Exception error)
        {
            if (ingress?.IsActive == true) throw;
            logger?.LogWarning("Incident SLA initialization failed ({ErrorCode}).", error.GetType().Name);
        }

        await _domainEvents.PublishAsync(
            new TicketCreatedEvent(
                incident.TrackingId,
                incident.Id,
                incident.OrganizationId,
                incident.RequesterEmail,
                GetCorrelationId()),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(incident.AssignedToId))
        {
            try
            {
                await supportNotificationService.NotifyTicketCreatedUnassignedAsync(incident, cancellationToken);
            }
            catch (Exception error)
            {
                if (ingress?.IsActive == true) throw;
                logger?.LogWarning("Incident support notification routing failed ({ErrorCode}).", error.GetType().Name);
            }
        }

        return incident;
    }

    private string GetCorrelationId()
    {
        return _correlationContext?.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }
}
