using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Orchestration;
using Helpdesk.Application.RequestTasks;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.Infrastructure.Orchestration;

public sealed class RequestTaskPayloadBuilder(
    IRepository<Request> requests,
    IRepository<RequestForm> requestForms,
    IRepository<Organization> organizations,
    IRequestFormSchemaParser schemaParser,
    IAutomationBindingService automationBindings,
    IAutomationBindingPayloadContractService automationPayloadContractService,
    AutomationOriginSnapshotBuilder? originSnapshots = null) : IRequestTaskPayloadBuilder
{
    private readonly IRepository<Request> _requests = requests;
    private readonly IRepository<RequestForm> _requestForms = requestForms;
    private readonly IRepository<Organization> _organizations = organizations;
    private readonly IRequestFormSchemaParser _schemaParser = schemaParser;
    private readonly IAutomationBindingService _automationBindings = automationBindings;
    private readonly IAutomationBindingPayloadContractService _automationPayloadContractService = automationPayloadContractService;

    public async Task<RequestTaskPayloadBuildResult> BuildAsync(
        RequestTask task,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var request = await _requests.GetAsync(task.RequestId);
        if (request is null)
        {
            return RequestTaskPayloadBuildResult.Failed("Parent request was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestFormId))
        {
            return RequestTaskPayloadBuildResult.Failed("Parent request does not reference a request form.");
        }

        var requestForm = await _requestForms.GetAsync(request.RequestFormId);
        if (requestForm is null)
        {
            return RequestTaskPayloadBuildResult.Failed("Request form for this task was not found.");
        }

        var schema = _schemaParser.Parse(requestForm.JsonSchema);
        var template = ResolveTemplate(task, schema.Tasks);
        if (template is null)
        {
            return RequestTaskPayloadBuildResult.Failed("Task template was not found in the request form schema.");
        }

        AutomationBindingDto? binding = null;
        if (Guid.TryParse(task.TemplateId, out var taskTemplateId))
        {
            binding = await _automationBindings.GetByTaskTemplateAsync(request.RequestFormId, taskTemplateId, cancellationToken);
        }

        var orchestratorTarget = ResolveAutomationTarget(binding);
        if (string.IsNullOrWhiteSpace(orchestratorTarget))
        {
            return RequestTaskPayloadBuildResult.Failed("Automation task is missing an enabled External orchestration binding target.");
        }

        var inputBuild = _automationPayloadContractService.BuildTaskInput(
            requestForm,
            template,
            request.PayloadJson ?? "{}");
        if (!inputBuild.Success)
        {
            return RequestTaskPayloadBuildResult.Failed(inputBuild.Error ?? "Automation payload validation failed.");
        }

        var inputNode = inputBuild.InputNode;

        var envelope = new JsonObject
        {
            ["meta"] = new JsonObject
            {
                ["requestId"] = request.Id,
                ["taskId"] = task.Id,
                ["trackingId"] = request.TrackingId,
                ["correlationId"] = correlationId
            },
            ["input"] = inputNode
        };

        var runtimeMeta = envelope["meta"]!.AsObject();
        if (task.ExpectedRuntimeSeconds is not null)
        {
            runtimeMeta["expectedRuntimeSeconds"] = task.ExpectedRuntimeSeconds.Value;
        }
        if (task.GraceSeconds is not null)
        {
            runtimeMeta["graceSeconds"] = task.GraceSeconds.Value;
        }
        if (task.HardTimeoutSeconds is not null)
        {
            runtimeMeta["hardTimeoutSeconds"] = task.HardTimeoutSeconds.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.OrganizationId))
        {
            envelope["meta"]!.AsObject()["organizationId"] = request.OrganizationId;

            var organization = await _organizations.GetAsync(request.OrganizationId);
            if (organization?.OrchestrationTenantId is not null)
            {
                envelope["meta"]!.AsObject()["orchestrationTenantId"] = organization.OrchestrationTenantId.Value;
                if (!string.IsNullOrWhiteSpace(organization.OrchestrationTenantName))
                {
                    envelope["meta"]!.AsObject()["orchestrationTenantName"] = organization.OrchestrationTenantName;
                }
            }
        }

        if (binding is not null)
        {
            var meta = envelope["meta"]!.AsObject();
            meta["automationBindingId"] = binding.Id;
            meta["orchestrationRequestDefinitionId"] = binding.OrchestrationRequestDefinitionId;
            if (!string.IsNullOrWhiteSpace(binding.OrchestrationJobDefinitionId))
            {
                meta["orchestrationJobDefinitionId"] = binding.OrchestrationJobDefinitionId;
            }
        }

        var payloadJson = envelope.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AutomationTrace.Contracts.AutomationOriginContext? origin;
        try
        {
            origin = originSnapshots is null ? null : await originSnapshots.PrepareAsync(
                task, request, requestForm, correlationId, payloadJson, cancellationToken);
        }
        catch (OrchestrationProviderException exception)
        {
            var error = exception.Error;
            var code = SafeDiagnosticIdentifier(error.Code) ?? "capability_check_failed";
            var layer = code switch
            {
                "capability_target_unavailable" => "target",
                "capability_credentials_unavailable" or "authentication_rejected" or "access_denied" => "authorization",
                "transport_unavailable" or "transport_timeout" => "transport",
                "invalid_contract" or "unsupported_contract" => "protocol",
                _ when error.UpstreamStatus is 401 or 403 => "authorization",
                _ => "provider"
            };
            var retryable = layer switch
            {
                "transport" => true,
                "target" or "authorization" or "protocol" => false,
                _ => error.UpstreamStatus is { } status ? (bool?)(status is 408 or 429 or >= 500) : null
            };
            var failure = new AutomationPreDispatchFailure("capabilities", layer, code, error.UpstreamStatus, retryable,
                correlationId, SafeDiagnosticIdentifier(error.CorrelationId),
                System.Diagnostics.Activity.Current?.TraceId.ToHexString(),
                Guid.TryParse(task.OrchestrationLinkId, out var connectionId) ? connectionId : null);
            var statusText = failure.UpstreamStatus is { } statusCode ? $", HTTP {statusCode}" : "";
            return RequestTaskPayloadBuildResult.Failed(
                $"NetRatel capability check failed before dispatch ({failure.Code}{statusText}). Review the saved connection. Attempt reference: {correlationId}.",
                failure);
        }
        return RequestTaskPayloadBuildResult.Succeeded(
            orchestratorTarget,
            task.AutomationDispatchPayloadJson ?? payloadJson,
            binding?.Id,
            binding?.OrchestrationRequestDefinitionId,
            binding?.OrchestrationJobDefinitionId, origin);
    }

    private static string? SafeDiagnosticIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')
            ? value : null;

    private static string? ResolveAutomationTarget(AutomationBindingDto? binding)
    {
        if (binding is not null && binding.Enabled)
        {
            return FirstNonEmpty(
                binding.OrchestrationJobDefinitionName,
                binding.OrchestrationJobDefinitionId,
                binding.OrchestrationRequestDefinitionName,
                binding.OrchestrationRequestDefinitionId);
        }

        return null;
    }

    private static Helpdesk.Shared.DTOs.RequestForm.RequestTaskTemplateModel? ResolveTemplate(
        RequestTask task,
        IReadOnlyCollection<Helpdesk.Shared.DTOs.RequestForm.RequestTaskTemplateModel> templates)
    {
        if (templates.Count == 0)
        {
            return null;
        }

        if (Guid.TryParse(task.TemplateId, out var templateId))
        {
            var byId = templates.FirstOrDefault(x => x.Id == templateId);
            if (byId is not null)
            {
                return byId;
            }
        }

        return templates
            .OrderBy(x => x.Order)
            .FirstOrDefault(x =>
                x.Order == task.Order &&
                string.Equals(x.Name, task.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
