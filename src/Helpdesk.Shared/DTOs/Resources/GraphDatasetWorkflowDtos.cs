using Helpdesk.Shared.Enums;

namespace Helpdesk.Shared.DTOs.Resources;

public enum GraphAccessOutcome
{
    Succeeded, InvalidConfiguration, AuthenticationFailed, AccessDenied, Throttled,
    TimedOut, Unavailable, InvalidResponse, SecretReentryRequired
}

public sealed class GraphConnectionTestRequestDto
{
    public string OrganizationId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool UseStoredSecret { get; set; }
    public bool EnableUsers { get; set; }
    public bool EnableDevices { get; set; }
    public bool EnableGroups { get; set; }
    public bool EnableSharePointSites { get; set; }
}

public sealed class GraphDatasetAccessResultDto
{
    public DatasetSourceType SourceType { get; set; }
    public bool Succeeded { get; set; }
    public GraphAccessOutcome Outcome { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? HttpStatusCode { get; set; }
}

public sealed class GraphConnectionTestResultDto
{
    public bool AuthenticationSucceeded { get; set; }
    public GraphAccessOutcome Outcome { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<GraphDatasetAccessResultDto> Sources { get; set; } = [];
    public string? TestToken { get; set; }
    public DateTimeOffset TestedAtUtc { get; set; }
}

public sealed class GraphDatasetImportRequestDto
{
    public Guid RequestId { get; set; }
    public GraphConnectionTestRequestDto Connection { get; set; } = new();
    public string TestToken { get; set; } = string.Empty;
    public bool PreserveExistingSchedule { get; set; } = true;
}

public sealed class GraphDatasetImportSourceResultDto
{
    public DatasetSourceType SourceType { get; set; }
    public string Status { get; set; } = "Pending";
    public string? DatasetId { get; set; }
    public int? RowCount { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? Message { get; set; }
}

public sealed class GraphDatasetImportStatusDto
{
    public Guid RequestId { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public bool ConfigurationSaved { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public List<GraphDatasetImportSourceResultDto> Sources { get; set; } = [];
    public string? Message { get; set; }
    public bool CanReconcile { get; set; }
    public DateTimeOffset? RecoveryAvailableAtUtc { get; set; }
}
