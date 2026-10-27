namespace Helpdesk.Infrastructure.AiOps;

public sealed class AiGuidancePrompt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrganizationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Content { get; set; } = "";
    public int Revision { get; set; } = 1;
    public bool IsDefault { get; set; }
    public bool Archived { get; set; }
}

public sealed class AiOpsInvestigation
{
    public Guid Id { get; set; }
    public string OrganizationId { get; set; } = "";
    public string Title { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string TicketType { get; set; } = "";
    public string TicketIdsJson { get; set; } = "[]";
    public string RequestFingerprint { get; set; } = "";
    public string ContextSnapshot { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
