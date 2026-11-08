namespace Helpdesk.Shared.DTOs.Change;

public sealed record ChangeTemplateValidationRequest(string ChangeType, ChangeTemplateDto Template);

public class ChangeTemplateValidationDto
{
    public bool IsComplete { get; set; }
    public List<string> Errors { get; set; } = new();
}
