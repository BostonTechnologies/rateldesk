namespace Helpdesk.Infrastructure.Configuration;

public sealed class OrchestrationM2MOptions
{
    public bool Enabled { get; set; }
    public string ProviderName { get; set; } = "External orchestration provider";
    public string? BaseUrl { get; set; }
    public string? Audience { get; set; }
    public string? Scope { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string HealthPath { get; set; } = "/internal/health";
    public string IngestPath { get; set; } = "/internal/ingest";
    public string CatalogPath { get; set; } = "/internal/catalog";
    public string[] AllowedCallerClientIds { get; set; } = Array.Empty<string>();
}
