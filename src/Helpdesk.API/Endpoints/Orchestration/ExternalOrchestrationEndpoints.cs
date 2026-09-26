using Helpdesk.Application.Events;
using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Orchestration;

public static class ExternalOrchestrationEndpoints
{
    public static void MapExternalOrchestrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/orchestration")
            .WithTags("External orchestration")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async (IOrchestrationConnectivityService connectivity, CancellationToken ct) =>
            Results.Ok(await connectivity.GetOrchestrationSettingsAsync(ct)));

        group.MapPut("/", async (
            UpdateOrchestrationConnectivitySettingsDto request,
            IIntegrationProviderSettingsService settings,
            HelpdeskDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            try
            {
                var result = await settings.UpdateOrchestratorSettingsAsync(request, ct);
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = Actor(principal),
                    RelatedEntityId = result.ProviderKey,
                    Message = $"Integration provider settings updated. Provider={result.ProviderKey}; Revision={result.Revision}; Source={result.Source}; Enabled={result.Enabled}; SecretConfigured={result.HasClientSecret}; SecretAction={(request.ClearClientSecret ? "cleared" : !string.IsNullOrWhiteSpace(request.ClientSecret) ? "replaced" : "unchanged")}."
                });
                await db.SaveChangesAsync(ct);
                return Results.Ok(result);
            }
            catch (IntegrationProviderConfigurationConflictException ex)
            {
                return Results.Conflict(new { code = "configuration_conflict", message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_configuration", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { code = "configuration_not_editable", message = ex.Message });
            }
        });

        group.MapPost("/test", async (
            IOrchestrationConnectivityService connectivity,
            IIntegrationProviderSettingsService settings,
            IDomainEventPublisher domainEvents,
            ICorrelationContext correlation,
            ITenantContext tenant,
            HelpdeskDbContext db,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var testedProfile = await settings.GetResolvedOrchestratorSettingsAsync(ct);
            var result = await connectivity.TestOrchestrationConnectivityAsync(testedProfile, ct);
            await settings.RecordOrchestratorTestAsync(
                testedProfile.Revision,
                testedProfile.ProfileFingerprint,
                result.Success,
                ct);
            db.ActivityLogs.Add(new ActivityLog
            {
                UserId = Actor(principal),
                RelatedEntityId = "Orchestrator",
                Message = $"Integration provider connectivity test completed. Provider=Orchestrator; Success={result.Success}; StatusCode={result.StatusCode?.ToString() ?? "none"}."
            });
            await db.SaveChangesAsync(ct);

            if (result.Success)
            {
                await domainEvents.PublishAsync(
                    new OrchestrationTestSucceededEvent(
                        result.StatusCode,
                        Truncate(result.Message, 240),
                        tenant.TenantId,
                        DateTimeOffset.UtcNow,
                        GetCorrelationId(correlation)),
                    ct);
                return Results.Ok(result);
            }

            await domainEvents.PublishAsync(
                new OrchestrationTestFailedEvent(
                    result.StatusCode,
                    Truncate(result.Message, 240),
                    tenant.TenantId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlation)),
                ct);

            return Results.BadRequest(result);
        });

        group.MapPost("/test-draft", async (
            UpdateOrchestrationConnectivitySettingsDto request,
            IOrchestrationConnectivityService connectivity,
            IIntegrationProviderSettingsService settings,
            CancellationToken ct) =>
        {
            try
            {
                var draft = await settings.ResolveOrchestratorDraftAsync(request, ct);
                var result = await connectivity.TestOrchestrationConnectivityAsync(draft, ct, useTokenCache: false);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            }
            catch (IntegrationProviderConfigurationConflictException ex)
            {
                return Results.Conflict(new { code = "configuration_conflict", message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { code = "invalid_configuration", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { code = "draft_not_testable", message = ex.Message });
            }
        });

        group.MapGet("/catalog/jobs", async (
            IOrchestrationCatalogService catalog,
            CancellationToken ct) =>
        {
            try
            {
                var results = await catalog.ListJobsAsync(ct);
                return Results.Ok(results);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapGet("/catalog/tenants", async (
            IOrchestrationCatalogService catalog,
            CancellationToken ct) =>
        {
            try
            {
                var results = await catalog.ListTenantsAsync(ct);
                return Results.Ok(results);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapGet("/catalog/request-definitions", async (
            IOrchestrationCatalogService catalog,
            CancellationToken ct) =>
        {
            try
            {
                var results = await catalog.ListRequestDefinitionsAsync(ct);
                return Results.Ok(results);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/catalog/request-definitions", async (
            CreateOrchestrationCatalogRequestDefinitionDto dto,
            IOrchestrationCatalogService catalog,
            CancellationToken ct) =>
        {
            try
            {
                var created = await catalog.CreateRequestDefinitionAsync(dto, ct);
                return Results.Created($"/api/v1/admin/orchestration/catalog/request-definitions/{created.RequestDefinitionId}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapGet("/bindings", async (
            [FromQuery] string? requestFormId,
            IAutomationBindingService bindings,
            CancellationToken ct) =>
        {
            var results = await bindings.ListAsync(requestFormId, ct);
            return Results.Ok(results);
        });

        group.MapGet("/bindings/{id}", async (
            [FromRoute] string id,
            IAutomationBindingService bindings,
            CancellationToken ct) =>
        {
            var binding = await bindings.GetAsync(id, ct);
            return binding is null ? Results.NotFound() : Results.Ok(binding);
        });

        group.MapGet("/bindings/by-task", async (
            [FromQuery] string requestFormId,
            [FromQuery] Guid taskTemplateId,
            IAutomationBindingService bindings,
            CancellationToken ct) =>
        {
            try
            {
                var binding = await bindings.GetByTaskTemplateAsync(requestFormId, taskTemplateId, ct);
                return binding is null ? Results.NotFound() : Results.Ok(binding);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/bindings", async (
            CreateAutomationBindingDto dto,
            IAutomationBindingService bindings,
            IDomainEventPublisher domainEvents,
            ICorrelationContext correlation,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            try
            {
                var created = await bindings.CreateAsync(dto, ct);
                await domainEvents.PublishAsync(
                    new OrchestrationBindingCreatedEvent(
                        created.Id,
                        created.RequestFormId,
                        created.OrchestrationRequestDefinitionId,
                        tenant.TenantId,
                        DateTimeOffset.UtcNow,
                        GetCorrelationId(correlation)),
                    ct);

                return Results.Created($"/api/v1/admin/orchestration/bindings/{created.Id}", created);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPut("/bindings/{id}", async (
            [FromRoute] string id,
            UpdateAutomationBindingDto dto,
            IAutomationBindingService bindings,
            IDomainEventPublisher domainEvents,
            ICorrelationContext correlation,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            try
            {
                var updated = await bindings.UpdateAsync(id, dto, ct);
                if (updated is null)
                {
                    return Results.NotFound();
                }

                await domainEvents.PublishAsync(
                    new OrchestrationBindingUpdatedEvent(
                        updated.Id,
                        updated.RequestFormId,
                        updated.OrchestrationRequestDefinitionId,
                        tenant.TenantId,
                        DateTimeOffset.UtcNow,
                        GetCorrelationId(correlation)),
                    ct);

                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapDelete("/bindings/{id}", async (
            [FromRoute] string id,
            IAutomationBindingService bindings,
            IDomainEventPublisher domainEvents,
            ICorrelationContext correlation,
            ITenantContext tenant,
            CancellationToken ct) =>
        {
            var deleted = await bindings.DeleteAsync(id, ct);
            if (!deleted)
            {
                return Results.NotFound();
            }

            await domainEvents.PublishAsync(
                new OrchestrationBindingDeletedEvent(
                    id,
                    tenant.TenantId,
                    DateTimeOffset.UtcNow,
                    GetCorrelationId(correlation)),
                ct);

            return Results.NoContent();
        });

        group.MapPost("/bindings/{id}/sync-inputs", async (
            [FromRoute] string id,
            IAutomationBindingSchemaSyncService schemaSync,
            CancellationToken ct) =>
        {
            try
            {
                var updated = await schemaSync.SyncAsync(id, ct);
                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/bindings/refresh-drift", async (
            [FromQuery] string? requestFormId,
            IAutomationBindingDriftService drift,
            CancellationToken ct) =>
        {
            try
            {
                var updated = await drift.RefreshAsync(requestFormId, ct);
                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/bindings/{id}/mark-import-pending", async (
            [FromRoute] string id,
            IAutomationBindingDriftService drift,
            CancellationToken ct) =>
        {
            try
            {
                var updated = await drift.MarkImportPendingAsync(id, ct);
                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapGet("/bindings/{id}/drift-preview", async (
            [FromRoute] string id,
            IAutomationBindingDriftService drift,
            CancellationToken ct) =>
        {
            try
            {
                var preview = await drift.PreviewAsync(id, ct);
                return Results.Ok(preview);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        group.MapPost("/bindings/{id}/import-inputs", async (
            [FromRoute] string id,
            IAutomationBindingImportService imports,
            CancellationToken ct) =>
        {
            try
            {
                var updated = await imports.ImportAsync(id, ct);
                return Results.Ok(updated);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
    }

    private static string GetCorrelationId(ICorrelationContext correlationContext)
    {
        return correlationContext.GetCorrelationId() ?? $"corr-{Guid.NewGuid():N}";
    }

    private static string Actor(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue("sub")
        ?? principal.Identity?.Name
        ?? "unknown";

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
