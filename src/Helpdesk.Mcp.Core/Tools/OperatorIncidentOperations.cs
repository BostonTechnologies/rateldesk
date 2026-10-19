using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Helpdesk.Shared.Enums;

namespace Helpdesk.Mcp.Tools;

internal static class OperatorIncidentOperations
{
    public static IReadOnlyList<OperatorOperation> All { get; } =
    [
        new("helpdesk_incidents", "relations", "GET", "/api/v1/incidents/{incidentId}/relations",
            Authority: "Visibility of both related incidents", Description: "Read the current incident's duplicate and related links. Links to incidents outside the caller's visibility are excluded."),
        new("helpdesk_incidents", "create_relation", "POST", "/api/v1/incidents/{incidentId}/relations", typeof(OperatorCreateIncidentRelation),
            ExternalIo: true, Authority: "Incident.Manager in both incident organizations or HelpdeskAdmin",
            Description: "Link two incidents in the same organization and copy source listeners to the target. relationType 0 means DuplicateOf and always resolves the source; 1 means RelatedTo and follows the explicit closeSourceTicket choice. Closing completes the source SLA and publishes the canonical event. Read both incidents and relations after an uncertain result before another attempt; an existing identical relation returns 409."),
        new("helpdesk_incidents", "delete_relation", "DELETE", "/api/v1/incidents/{incidentId}/relations/{relationId}", QueryType: typeof(OperatorIncidentRelationSelector),
            Authority: "Incident.Manager for both related incidents or HelpdeskAdmin", Description: "Remove the selected relationship after reading its UUID. This removes only the link; it does not reopen the source or remove listeners already copied to the target."),
        new("helpdesk_incidents", "bulk_delete", "POST", "/api/v1/incidents/bulk/delete", typeof(OperatorIncidentIds),
            Authority: "Incident deletion authority for every selected incident", Description: "Permanently delete 1–100 explicitly selected incidents through the canonical silent deletion action. Requesters are not notified. Check deleted and missing counts and read back the selected IDs before repeating an uncertain batch."),
        new("helpdesk_incidents", "bulk_marketing_spam", "POST", "/api/v1/incidents/bulk/marketing-spam", typeof(OperatorIncidentIds),
            Authority: "Incident.Manager for every selected incident or HelpdeskAdmin", Description: "Silently resolve 1–100 explicitly selected incidents, complete their SLA, and exclude them from requester outbound email as marketing or spam. Check updated and missing counts and read back the selected incidents.")
    ];

    internal static void RefineRequestSchema(OperatorOperation operation, JsonObject schema)
    {
        if (operation.Tool != "helpdesk_incidents" || operation.BodyType != typeof(OperatorIncidentIds)) return;
        schema["properties"]!["body"]!["properties"]!["ids"]!["items"] = new JsonObject
        {
            ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128, ["pattern"] = "\\S"
        };
    }
}

internal sealed record OperatorCreateIncidentRelation(
    [property: Required, MaxLength(128), RegularExpression(@".*\S.*")] string TargetTicketId,
    [property: Required] TicketRelationType RelationType,
    [property: Required] bool CloseSourceTicket,
    [property: MaxLength(4000)] string? Note = null);
internal sealed record OperatorIncidentRelationSelector([property: Required] Guid RelationId);
internal sealed record OperatorIncidentIds([property: Required, MinLength(1), MaxLength(100)] IReadOnlyList<string> Ids);
