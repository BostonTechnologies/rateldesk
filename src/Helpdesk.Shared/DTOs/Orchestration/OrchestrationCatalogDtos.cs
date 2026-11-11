using System.Text.Json.Serialization;

namespace Helpdesk.Shared.DTOs.Orchestration;

public sealed class OrchestrationCatalogInputDefinitionDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public string? HelpText { get; set; }
    public string? OptionsJson { get; set; }
    public int Order { get; set; }
}

public sealed class OrchestrationCatalogJobDto
{
    public int ContractVersion { get; set; } = 1;
    public OrchestrationAutomationTargetDto? Target { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = "/";
    public int? TenantId { get; set; }
    public string? TenantName { get; set; }
    [JsonPropertyName("tenant_id")]
    public int? TenantIdSnake { get => TenantId; set => TenantId = value; }
    [JsonPropertyName("tenant_name")]
    public string? TenantNameSnake { get => TenantName; set => TenantName = value; }
    public int? OrchestrationTenantId { get => TenantId; set => TenantId = value; }
    public string? OrchestrationTenantName { get => TenantName; set => TenantName = value; }
    public string? Description { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public string? ClientDisplayName { get; set; }
    public string? ClientHostName { get; set; }
    public string? ClientName { get; set; }
    public string? ClientShortId { get; set; }
    public string? ScriptType { get; set; }
    public IReadOnlyList<OrchestrationCatalogInputDefinitionDto> Inputs { get; set; } = [];
    public int ExpectedRuntimeSeconds { get; set; } = 1800;
    public int GraceSeconds { get; set; }
    public int HardTimeoutSeconds { get; set; } = 1800;
}

public sealed class OrchestrationCatalogRequestDefinitionDto
{
    public int ContractVersion { get; set; } = 1;
    public OrchestrationAutomationTargetDto? Target { get; set; }
    public string RequestDefinitionId { get; set; } = string.Empty;
    public string RequestDefinitionName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string FolderPath { get; set; } = "/";
    public int? TenantId { get; set; }
    public string? TenantName { get; set; }
    [JsonPropertyName("tenant_id")]
    public int? TenantIdSnake { get => TenantId; set => TenantId = value; }
    [JsonPropertyName("tenant_name")]
    public string? TenantNameSnake { get => TenantName; set => TenantName = value; }
    public int? OrchestrationTenantId { get => TenantId; set => TenantId = value; }
    public string? OrchestrationTenantName { get => TenantName; set => TenantName = value; }
    public string? OrchestrationJobDefinitionId { get; set; }
    public string? OrchestrationJobDefinitionName { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public string? ClientDisplayName { get; set; }
    public string? ClientHostName { get; set; }
    public string? ClientName { get; set; }
    public string? ClientShortId { get; set; }
    public string? ScriptType { get; set; }
    public IReadOnlyList<OrchestrationCatalogInputDefinitionDto> Inputs { get; set; } = [];
    public int ExpectedRuntimeSeconds { get; set; } = 1800;
    public int GraceSeconds { get; set; }
    public int HardTimeoutSeconds { get; set; } = 1800;
}

public sealed class OrchestrationCatalogTenantDto
{
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class CreateOrchestrationCatalogRequestDefinitionDto
{
    public OrchestrationJobTargetDefinitionDto? Target { get; set; }
    public OrchestrationJobExecutionPolicyDto? ExecutionPolicy { get; set; }
    public string? Runner { get; set; }
    public string? Command { get; set; }
    public IReadOnlyList<OrchestrationCatalogInputDefinitionDto> Inputs { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = "/";
    public string? Description { get; set; }
    public int? TenantId { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
}

/// <summary>Canonical NetRatel target wire fields; IDs retain their exact string representation.</summary>
public sealed class OrchestrationJobTargetDefinitionDto
{
    public int Version { get; set; } = 1;
    public int Source { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ExecutionGroupId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<string>? AgentIds { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Filter { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public OrchestrationRuntimeTargetDefinitionDto? RuntimeInput { get; set; }
}

public sealed class OrchestrationRuntimeTargetDefinitionDto
{
    public int Kind { get; set; }
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<string>? DefaultValues { get; set; }
    public bool AllOf { get; set; }
}

public sealed class OrchestrationJobExecutionPolicyDto
{
    public int ThreadCount { get; set; } = 1;
    public int NodeFailurePolicy { get; set; }
}

public sealed class OrchestrationAutomationTargetDto
{
    public int Version { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public OrchestrationJobTargetDefinitionDto? Definition { get; set; }
    public string? LegacyAgentId { get; set; }
    public string Readiness { get; set; } = string.Empty;
    public IReadOnlyList<string> RequiredInputs { get; set; } = [];
}

public sealed class OrchestrationAutomationCapabilitiesDto
{
    public int ContractVersion { get; set; } = 1;
    public IReadOnlyList<string> TargetKinds { get; set; } = ["legacyAgent"];
    public bool Catalogue { get; set; } = true;
    public bool Authoring { get; set; }
    public bool AggregateExecution { get; set; }
    public string? Message { get; set; }
}

public sealed class OrchestrationAutomationTargetChoiceDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public OrchestrationJobTargetDefinitionDto Target { get; set; } = new();
}

public sealed record OrchestrationProviderErrorDto(string Operation, int Status, string Code, string Message, string? CorrelationId);
