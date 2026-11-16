using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.EmailTemplates;

public sealed class InternalIncidentTemplateRecord
{
    public Guid Id { get; set; }
    public long Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string Purpose { get; set; } = "netratel-internal-incident";
    public string SubjectTemplate { get; set; } = "";
    public string HtmlTemplate { get; set; } = "";
    public int? LayoutId { get; set; }
    public string SourceKindsJson { get; set; } = "[]";
    public bool Retired { get; set; }
    public int PublishedRevision { get; set; }
}
public sealed class InternalIncidentTemplateRevision
{
    public Guid TemplateId { get; set; }
    public int Revision { get; set; }
    public string OrganizationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SubjectTemplate { get; set; } = "";
    public string ExpandedHtml { get; set; } = "";
    public string SourceKindsJson { get; set; } = "[]";
    public string ContentDigest { get; set; } = "";
    public string LayoutDigest { get; set; } = "";
    public bool Revoked { get; set; }
    public DateTimeOffset PublishedAtUtc { get; set; }
}
public sealed class AutomationIncidentReportRecord
{
    public Guid Id { get; set; }
    public string IncidentId { get; set; } = "";
    public string? SourceRequestId { get; set; }
    public string Html { get; set; } = "";
    public string PlainText { get; set; } = "";
    public string ContextJson { get; set; } = "";
    public string PresentationJson { get; set; } = "";
    public string FlowRunId { get; set; } = "";
    public string ActionId { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
public static class InternalIncidentTemplateModel
{
    public static void ConfigureAutomationPresentation(this ModelBuilder model)
    {
        model.Entity<InternalIncidentTemplateRecord>(e =>
        {
            e.ToTable("InternalIncidentTemplates"); e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Name).HasMaxLength(256); e.Property(x => x.OrganizationId).HasMaxLength(64);
            e.Property(x => x.Purpose).HasMaxLength(64); e.HasIndex(x => new { x.OrganizationId, x.Retired });
        });
        model.Entity<InternalIncidentTemplateRevision>(e =>
        {
            e.ToTable("InternalIncidentTemplateRevisions"); e.HasKey(x => new { x.TemplateId, x.Revision });
            e.Property(x => x.OrganizationId).HasMaxLength(64); e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.ContentDigest).HasMaxLength(64); e.Property(x => x.LayoutDigest).HasMaxLength(64);
            e.HasOne<InternalIncidentTemplateRecord>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AutomationIncidentReportRecord>(e =>
        {
            e.ToTable("AutomationIncidentReports"); e.HasKey(x => x.Id);
            e.Property(x => x.IncidentId).HasMaxLength(64); e.HasIndex(x => x.IncidentId).IsUnique();
            e.Property(x => x.SourceRequestId).HasMaxLength(64); e.HasIndex(x => x.SourceRequestId);
            e.Property(x => x.FlowRunId).HasMaxLength(36); e.Property(x => x.ActionId).HasMaxLength(36);
            e.HasOne<Helpdesk.Shared.Models.Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
