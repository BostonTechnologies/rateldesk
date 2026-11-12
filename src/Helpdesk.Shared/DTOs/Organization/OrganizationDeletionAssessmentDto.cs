namespace Helpdesk.Shared.DTOs.Organization;

public sealed record OrganizationDependencyDto(string Code, int Count, string Message, string NextAction, string? Href, bool RequiresResolution);

public sealed record OrganizationDeletionAssessmentDto(bool CanDelete, bool CanRetire, bool IsDisabled, IReadOnlyList<OrganizationDependencyDto> Dependencies);
