using System.Text.Json.Nodes;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.DTOs.RequestForm;
using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Orchestration;

public interface IAutomationBindingService
{
    Task<IReadOnlyList<AutomationBindingDto>> ListAsync(string? requestFormId = null, CancellationToken cancellationToken = default);
    Task<AutomationBindingDto?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<AutomationBindingDto?> GetByTaskTemplateAsync(string requestFormId, Guid taskTemplateId, CancellationToken cancellationToken = default);
    Task<AutomationBindingDto> CreateAsync(CreateAutomationBindingDto dto, CancellationToken cancellationToken = default);
    Task<AutomationBindingDto?> UpdateAsync(string id, UpdateAutomationBindingDto dto, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}

public interface IAutomationBindingSchemaSyncService
{
    Task<AutomationBindingDto> SyncAsync(string bindingId, CancellationToken cancellationToken = default);
}

public interface IAutomationBindingDriftService
{
    Task<IReadOnlyList<AutomationBindingDto>> RefreshAsync(string? requestFormId = null, CancellationToken cancellationToken = default);
    Task<AutomationBindingDto> MarkImportPendingAsync(string bindingId, CancellationToken cancellationToken = default);
    Task<AutomationBindingDriftPreviewDto> PreviewAsync(string bindingId, CancellationToken cancellationToken = default);
    Task<AutomationInputReviewResultDto> ApplyReviewAsync(string bindingId, ApplyAutomationInputReviewDto request,
        CancellationToken cancellationToken = default);
}

public interface IAutomationBindingImportService
{
    Task<AutomationBindingDto> ImportAsync(string bindingId, CancellationToken cancellationToken = default);
}

public sealed class AutomationPayloadValidationResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static AutomationPayloadValidationResult Passed() => new()
    {
        Success = true
    };

    public static AutomationPayloadValidationResult Failed(params string[] errors) => new()
    {
        Success = false,
        Errors = errors
    };
}

public sealed class AutomationTaskInputBuildResult
{
    public bool Success { get; init; }
    public JsonObject InputNode { get; init; } = new();
    public IReadOnlyList<string> MultiSelectInputs { get; init; } = [];
    public string? Error { get; init; }

    public static AutomationTaskInputBuildResult Succeeded(JsonObject inputNode) => new()
    {
        Success = true,
        InputNode = inputNode
    };

    public static AutomationTaskInputBuildResult Succeeded(JsonObject inputNode, IReadOnlyList<string> multiSelectInputs) => new()
    {
        Success = true,
        InputNode = inputNode,
        MultiSelectInputs = multiSelectInputs
    };

    public static AutomationTaskInputBuildResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };
}

public interface IAutomationBindingPayloadContractService
{
    Task<AutomationPayloadValidationResult> ValidateBoundRequestPayloadAsync(
        RequestForm requestForm,
        string payloadJson,
        CancellationToken cancellationToken = default);

    AutomationTaskInputBuildResult BuildTaskInput(
        RequestForm requestForm,
        RequestTaskTemplateModel taskTemplate,
        string payloadJson);
}
