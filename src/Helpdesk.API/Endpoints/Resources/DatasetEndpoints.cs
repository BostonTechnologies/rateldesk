using System.Security.Claims;
using Helpdesk.Application.Resources;
using Helpdesk.Application.Services.AI;
using Helpdesk.API.Background;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Resources;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.API.Endpoints.Resources;

public static class DatasetEndpoints
{
    private const string DatasetApiKeyHeader = "X-Dataset-Api-Key";

    public static void MapDatasetEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/resources/datasets")
            .WithTags("Resources")
            .RequireAuthorization("DataManagementAccess");

        admin.MapGet("/", async (
            [FromQuery] string? organizationId,
            ClaimsPrincipal user,
            [FromServices] ICurrentUserAccessService accessService,
            [FromServices] IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            var resolvedOrganizationId = ResolveRequestedOrganizationId(organizationId, access);
            if (resolvedOrganizationId == OrganizationResolution.Missing)
            {
                return Results.BadRequest("OrganizationId is required.");
            }
            if (resolvedOrganizationId == OrganizationResolution.Forbidden)
            {
                return Results.Forbid();
            }

            var datasets = await dataManagementService.ListDatasetsAsync(resolvedOrganizationId.OrganizationId!, token);
            return Results.Ok(datasets);
        })
        .WithName("ListDatasets");

        admin.MapGet("/{datasetId}", async (
            string datasetId,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            var dataset = await dataManagementService.GetDatasetAsync(datasetId, token);
            if (dataset is null)
            {
                return Results.NotFound();
            }

            return CanAccessOrganization(access, dataset.OrganizationId) ? Results.Ok(dataset) : Results.Forbid();
        })
        .WithName("GetDataset");

        admin.MapPost("/", async (
            [FromBody] CreateDatasetDefinitionDto dto,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanAccessOrganization(access, dto.OrganizationId))
            {
                return Results.Forbid();
            }
            if (!access.IsHelpdeskAdmin && dto.SourceType != DatasetSourceType.Custom)
            {
                return Results.Forbid();
            }

            var created = await dataManagementService.CreateDatasetAsync(dto, token);
            return Results.Created($"/api/v1/resources/datasets/{created.Id}", created);
        })
        .WithName("CreateDataset");

        admin.MapPut("/{datasetId}", async (
            string datasetId,
            [FromBody] UpdateDatasetDefinitionDto dto,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            var existing = await dataManagementService.GetDatasetAsync(datasetId, token);
            if (existing is null)
            {
                return Results.NotFound();
            }
            if (!CanAccessOrganization(access, existing.OrganizationId))
            {
                return Results.Forbid();
            }
            if (!access.IsHelpdeskAdmin && (existing.SourceType != DatasetSourceType.Custom || dto.SourceType is not null and not DatasetSourceType.Custom))
            {
                return Results.Forbid();
            }

            var updated = await dataManagementService.UpdateDatasetAsync(datasetId, dto, token);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        })
        .WithName("UpdateDataset");

        admin.MapDelete("/{datasetId}", async (
            string datasetId,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            var existing = await dataManagementService.GetDatasetAsync(datasetId, token);
            if (existing is null)
            {
                return Results.NotFound();
            }
            if (!CanAccessOrganization(access, existing.OrganizationId))
            {
                return Results.Forbid();
            }
            if (!access.IsHelpdeskAdmin && existing.SourceType != DatasetSourceType.Custom)
            {
                return Results.Forbid();
            }

            var deleted = await dataManagementService.DeleteDatasetAsync(datasetId, token);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .WithName("DeleteDataset");

        admin.MapGet("/{datasetId}/rows", async (
            string datasetId,
            [FromQuery] string? query,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            var dataset = await dataManagementService.GetDatasetAsync(datasetId, token);
            if (dataset is null)
            {
                return Results.NotFound();
            }
            if (!CanAccessOrganization(access, dataset.OrganizationId))
            {
                return Results.Forbid();
            }

            var rows = await dataManagementService.GetRowsAsync(datasetId, query, page == 0 ? 1 : page, pageSize == 0 ? 25 : pageSize, token);
            return Results.Ok(rows);
        })
        .WithName("GetDatasetRows");

        admin.MapPost("/{datasetId}/rows", async (
            string datasetId,
            [FromBody] UpsertDatasetRowsDto dto,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            [FromServices] IOperatorDatasetWriter writer,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            var dataset = await dataManagementService.GetDatasetAsync(datasetId, token);
            if (dataset is null) return Results.NotFound();
            if (!CanAccessOrganization(access, dataset.OrganizationId) ||
                !access.IsHelpdeskAdmin && dataset.SourceType != DatasetSourceType.Custom)
                return Results.Forbid();

            try
            {
                var count = await writer.UpsertRowsAsOperatorAsync(datasetId, dataset.OrganizationId,
                    access.IsHelpdeskAdmin, dto, token);
                return Results.Ok(new { rowsUpserted = count });
            }
            catch (DatasetWriteConflictException error)
            {
                return Results.Conflict(new { code = "dataset_changed", message = error.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { message = error.Message });
            }
        })
        .WithName("UpsertDatasetRowsAsOperator")
        .WithSummary("Update dataset rows with authenticated data-management authority");

        admin.MapGet("/{datasetId}/credentials", async (
            string datasetId,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var accessCheck = await CanManageDatasetApiAsync(datasetId, user, accessService, dataManagementService, token);
            if (accessCheck.Result is not null)
            {
                return accessCheck.Result;
            }

            var credentials = await dataManagementService.ListCredentialsAsync(datasetId, token);
            return Results.Ok(credentials);
        })
        .WithName("ListDatasetCredentials");

        admin.MapPost("/{datasetId}/credentials", async (
            string datasetId,
            [FromBody] CreateDatasetIngestCredentialDto dto,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var accessCheck = await CanManageDatasetApiAsync(datasetId, user, accessService, dataManagementService, token);
            if (accessCheck.Result is not null)
            {
                return accessCheck.Result;
            }

            var created = await dataManagementService.CreateCredentialAsync(datasetId, dto, token);
            return Results.Ok(created);
        })
        .WithName("CreateDatasetCredential");

        admin.MapDelete("/{datasetId}/credentials/{credentialId}", async (
            string datasetId,
            string credentialId,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var accessCheck = await CanManageDatasetApiAsync(datasetId, user, accessService, dataManagementService, token);
            if (accessCheck.Result is not null)
            {
                return accessCheck.Result;
            }

            var revoked = await dataManagementService.RevokeCredentialAsync(datasetId, credentialId, token);
            return revoked ? Results.NoContent() : Results.NotFound();
        })
        .WithName("RevokeDatasetCredential");

        admin.MapGet("/graph-settings", async (
            [FromQuery] string? organizationId,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            IGraphDatasetSyncScheduleService graphDatasetSyncScheduleService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            var resolvedOrganizationId = ResolveRequestedOrganizationId(organizationId, access);
            if (resolvedOrganizationId == OrganizationResolution.Missing)
            {
                return Results.BadRequest("OrganizationId is required.");
            }
            if (resolvedOrganizationId == OrganizationResolution.Forbidden)
            {
                return Results.Forbid();
            }

            var settings = await dataManagementService.GetGraphSettingsAsync(resolvedOrganizationId.OrganizationId!, token);
            if (settings is null)
            {
                return Results.NotFound();
            }

            await graphDatasetSyncScheduleService.ApplyDiagnosticsAsync(settings, token);
            return Results.Ok(settings);
        })
        .WithName("GetGraphDatasetSettings");

        admin.MapPut("/graph-settings", async (
            [FromBody] TenantGraphDatasetSettingsDto dto,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            IGraphDatasetSyncScheduleService graphDatasetSyncScheduleService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanAccessOrganization(access, dto.OrganizationId))
            {
                return Results.Forbid();
            }

            var settings = await dataManagementService.UpsertGraphSettingsAsync(dto, token);
            try
            {
                await graphDatasetSyncScheduleService.ReconcileAsync(settings.OrganizationId, token);
                await graphDatasetSyncScheduleService.ApplyDiagnosticsAsync(settings, token);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .WithName("UpsertGraphDatasetSettings");

        admin.MapPost("/graph-settings/{organizationId}/sync", async (
            string organizationId,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            IGraphDatasetSyncScheduleService graphDatasetSyncScheduleService,
            CancellationToken token) =>
        {
            var access = await accessService.ResolveAsync(user, token);
            if (!CanAccessOrganization(access, organizationId))
            {
                return Results.Forbid();
            }

            IReadOnlyList<DatasetDefinitionDto> synced;
            try
            {
                synced = await dataManagementService.SyncBuiltInDatasetsAsync(organizationId, token);
            }
            catch (DatasetWriteConflictException error)
            {
                return Results.Conflict(new { code = "dataset_changed", message = error.Message });
            }
            catch (SecretReentryRequiredException ex)
            {
                return Results.Conflict(new
                {
                    message = ex.Message,
                    actionRequired = "Re-enter the Graph client secret to re-encrypt it with the current DataProtection key ring."
                });
            }

            await graphDatasetSyncScheduleService.ReconcileAsync(organizationId, token);
            return Results.Ok(synced);
        })
        .WithName("SyncBuiltInDatasets");

        var ingest = app.MapGroup("/api/v1/resources/datasets")
            .WithTags("Resources");

        ingest.MapPost("/{datasetId}/rows:upsert", async (
            string datasetId,
            HttpRequest request,
            [FromBody] UpsertDatasetRowsDto dto,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            if (!request.Headers.TryGetValue(DatasetApiKeyHeader, out var apiKeyValues))
            {
                return Results.Unauthorized();
            }

            try
            {
                var count = await dataManagementService.UpsertRowsAsync(datasetId, apiKeyValues.ToString(), dto, token);
                return Results.Ok(new { rowsUpserted = count });
            }
            catch (DatasetWriteConflictException error)
            {
                return Results.Conflict(new { code = "dataset_changed", message = error.Message });
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { message = error.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .WithName("UpsertDatasetRows");

        var selfService = app.MapGroup("/api/v1/self-service/datasets")
            .WithTags("Self Service")
            .RequireAuthorization(HelpdeskPermissions.SelfServiceUser);

        selfService.MapGet("/{datasetId}/options", async (
            string datasetId,
            [FromQuery] string? query,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            ClaimsPrincipal user,
            ICurrentUserAccessService accessService,
            IDataManagementService dataManagementService,
            CancellationToken token) =>
        {
            var dataset = await dataManagementService.GetDatasetAsync(datasetId, token);
            if (dataset is null)
            {
                return Results.NotFound();
            }

            var access = await accessService.ResolveAsync(user, token);
            if (!access.HasPermission(HelpdeskPermissions.SelfServiceUser, dataset.OrganizationId))
            {
                return Results.NotFound();
            }

            var options = await dataManagementService.GetOptionsAsync(
                datasetId,
                dataset.OrganizationId,
                query,
                page == 0 ? 1 : page,
                pageSize == 0 ? 25 : pageSize,
                token);
            return Results.Ok(options);
        })
        .WithName("GetSelfServiceDatasetOptions");
    }

    private static OrganizationResolution ResolveRequestedOrganizationId(string? organizationId, CurrentUserAccessProfile access)
    {
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            var trimmed = organizationId.Trim();
            return CanAccessOrganization(access, trimmed)
                ? OrganizationResolution.Success(trimmed)
                : OrganizationResolution.Forbidden;
        }

        if (!string.IsNullOrWhiteSpace(access.PrimaryOrganizationId))
        {
            return CanAccessOrganization(access, access.PrimaryOrganizationId)
                ? OrganizationResolution.Success(access.PrimaryOrganizationId)
                : OrganizationResolution.Forbidden;
        }

        return OrganizationResolution.Missing;
    }

    private static bool CanAccessOrganization(CurrentUserAccessProfile access, string? organizationId) =>
        access.HasPermission(HelpdeskPermissions.DataManagementAdmin, organizationId);

    private static async Task<DatasetApiAccessCheck> CanManageDatasetApiAsync(
        string datasetId,
        ClaimsPrincipal user,
        ICurrentUserAccessService accessService,
        IDataManagementService dataManagementService,
        CancellationToken token)
    {
        var access = await accessService.ResolveAsync(user, token);
        var dataset = await dataManagementService.GetDatasetAsync(datasetId, token);
        if (dataset is null)
        {
            return new DatasetApiAccessCheck(Results.NotFound());
        }
        if (!CanAccessOrganization(access, dataset.OrganizationId))
        {
            return new DatasetApiAccessCheck(Results.Forbid());
        }
        if (!access.IsHelpdeskAdmin && dataset.SourceType != DatasetSourceType.Custom)
        {
            return new DatasetApiAccessCheck(Results.Forbid());
        }

        return new DatasetApiAccessCheck(null);
    }

    private sealed record DatasetApiAccessCheck(IResult? Result);

    private sealed record OrganizationResolution(string? OrganizationId, OrganizationResolutionStatus Status)
    {
        public static OrganizationResolution Missing { get; } = new(null, OrganizationResolutionStatus.Missing);
        public static OrganizationResolution Forbidden { get; } = new(null, OrganizationResolutionStatus.Forbidden);
        public static OrganizationResolution Success(string organizationId) => new(organizationId, OrganizationResolutionStatus.Success);
    }

    private enum OrganizationResolutionStatus
    {
        Missing,
        Forbidden,
        Success
    }
}
