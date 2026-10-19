namespace Helpdesk.Shared.DTOs.RequestForm;

using Helpdesk.Shared.Models;

/// <summary>
/// PATCH-style update. All properties optional.
/// To MOVE a form, set ServiceIdHasValue=true and ServiceId to the target (or null to detach from parent if supported).
/// </summary>
public class UpdateRequestFormDto
{
    // Optional updates
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? JsonSchema { get; set; }

    /// <summary>
    /// Optional move. If ServiceIdHasValue is true, ServiceId will be applied.
    /// If ServiceIdHasValue is false, ServiceId is ignored (no move).
    /// </summary>
    public bool ServiceIdHasValue { get; set; } = false;
    public string? ServiceId { get; set; }

    public string? OrganizationId { get; set; }
    // Omission preserves the saved audience; an explicit empty list clears it.
    public List<string>? AllowedOrganizationIds { get; set; }
    public RequestFormReleaseStatus? ReleaseStatus { get; set; }
}
