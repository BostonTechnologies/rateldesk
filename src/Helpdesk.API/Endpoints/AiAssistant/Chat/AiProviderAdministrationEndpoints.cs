using System.Net;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services.AiAssistant;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Shared.Services;
using System.Text.Json;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
using Helpdesk.Infrastructure.AiAssistant.Chat.Codex;
using Helpdesk.Shared.AiAssistant.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.API.Endpoints.AiAssistant.Chat;

public static class AiProviderAdministrationEndpoints
{
    public static void MapAiProviderAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/ai-providers").RequireAuthorization("HelpdeskAdmin").WithTags("AI Providers");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var resolver = http.RequestServices.GetRequiredService<IIntegrationCredentialOwnerResolver>();
            if (await resolver.ResolveAsync(http.User, http.RequestAborted) is not { } owner) return Results.Forbid();
            http.Items[typeof(IntegrationCredentialOwner)] = owner.UserId;
            try { return await next(context); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "The profile changed. Reload it before saving." }); }
            catch (ChatConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (IntegrationProviderSecretUnavailableException)
            {
                return Results.Problem("The saved project key is unavailable. Restore the shared encryption key ring or replace the key.", statusCode: 409);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return Results.Problem("OpenAI returned an unexpected verification response.", statusCode: 502);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is not null)
            {
                return Results.Problem(ex.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "OpenAI rejected the project key.",
                    HttpStatusCode.Forbidden => "The project key lacks permission for this check.",
                    HttpStatusCode.TooManyRequests => "OpenAI reported a rate or quota limit.",
                    _ => "OpenAI could not complete the verification request."
                }, statusCode: (int)ex.StatusCode.Value);
            }
            catch (HttpRequestException) { return Results.Problem("The OpenAI API could not be reached.", statusCode: 502); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or global::System.ComponentModel.Win32Exception)
            {
                return Results.Problem("The configured Codex runtime could not complete the request.", statusCode: 503);
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            {
                return Results.Problem("OpenAI did not respond before the verification deadline.", statusCode: 504);
            }
        });

        group.MapGet("", ([FromServices] OpenAiChatProfiles profiles, HttpContext context, CancellationToken ct) =>
            profiles.ListAsync(ct, Owner(context)));
        group.MapGet("/read-operations", () => OpenAiChatMcpAuthority.ReadOperations);
        group.MapGet("/setup-options", ([FromServices] OpenAiChatProfiles profiles, [FromServices] IntegrationCredentialPairingService pairing) =>
            profiles.SetupOptions(pairing.PublicOrigin)).WithName("GetAiProviderSetupOptions");
        group.MapPost("/openai/{id:guid}/models", async (Guid id, OpenAiProjectCatalogRequest request,
            [FromServices] OpenAiChatProfiles profiles, [FromServices] OpenAiAgentsHttpClient http,
            [FromServices] ICurrentUserAccessService access, HttpContext context, CancellationToken ct) =>
        {
            if (!await CanManageOrganizationAsync(request.OrganizationId, access, context, ct))
                return Results.Forbid();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await profiles.ModelsAsync(id, request, http, ct));
        }).WithName("DiscoverOpenAiProjectModels");
        group.MapGet("/openai/{id:guid}", (Guid id, [FromServices] OpenAiChatProfiles profiles, CancellationToken ct) => profiles.GetAsync(id, ct));
        group.MapPut("/openai/{id:guid}", async (Guid id, OpenAiChatProfileUpdate request, [FromServices] OpenAiChatProfiles profiles,
            [FromServices] ICurrentUserAccessService access, HttpContext context, CancellationToken ct) =>
        {
            if (request.Settings is null || request.Id != id || !SupportedReads(request.Settings.ReadOperations))
                return Results.BadRequest(new { message = "Select operations from the supported ticket read catalog." });
            if (!await CanManageOrganizationAsync(request.Settings.OrganizationId, access, context, ct))
                return Results.Forbid();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await profiles.SaveAsync(request, ct));
        });
        group.MapPut("/default", async (ChatProviderDefault request, [FromServices] OpenAiChatProfiles profiles, HttpContext context, CancellationToken ct) =>
        {
            await profiles.SetDefaultAsync(request.ProfileId, ct, Owner(context));
            return Results.NoContent();
        });
        group.MapPost("/openai/{id:guid}/verify", (Guid id, [FromServices] OpenAiChatProfiles profiles, [FromServices] OpenAiAgentsHttpClient http, CancellationToken ct) =>
            profiles.VerifyAsync(id, http, ct));
        MapCodex(group);
    }

    private static string Owner(HttpContext context) => (string)context.Items[typeof(IntegrationCredentialOwner)]!;

    private static void MapCodex(RouteGroupBuilder group)
    {
        var codex = group.MapGroup("/codex");
        codex.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        codex.MapGet("/runtime", async ([FromServices] CodexAppServerHost host, CancellationToken ct) =>
        {
            await host.CheckAsync(ct);
            return Results.Ok(new { version = CodexAppServerHost.Version });
        }).WithName("CheckCodexAppServerRuntime");
        codex.MapGet("/connections", async (string organizationId, [FromServices] CodexConnections connections,
            [FromServices] ICurrentUserAccessService access, HttpContext context, CancellationToken ct) =>
        {
            if (!await CanManageOrganizationAsync(organizationId, access, context, ct)) return Results.Forbid();
            return Results.Ok(await connections.ListAsync(Owner(context), organizationId, ct));
        }).WithName("ListCodexConnections");
        codex.MapPost("/connections", async (CodexConnectRequest request, [FromServices] CodexConnections connections,
            [FromServices] ICurrentUserAccessService access, HttpContext context, CancellationToken ct) =>
        {
            if (!await CanManageOrganizationAsync(request.OrganizationId, access, context, ct)) return Results.Forbid();
            return Results.Ok(await connections.BeginAsync(Owner(context), request.OrganizationId, ct));
        }).WithName("StartCodexDeviceLogin");
        codex.MapGet("/connections/{id:guid}", async (Guid id, string organizationId, [FromServices] CodexConnections connections,
            [FromServices] ICurrentUserAccessService access, HttpContext context, CancellationToken ct) =>
        {
            if (!await CanManageOrganizationAsync(organizationId, access, context, ct)) return Results.Forbid();
            return Results.Ok(await connections.StatusAsync(id, Owner(context), organizationId, ct));
        }).WithName("GetCodexConnectionStatus");
        codex.MapDelete("/connections/{id:guid}", async (Guid id, string organizationId, [FromServices] CodexConnections connections,
            [FromServices] ICurrentUserAccessService access, HttpContext context, CancellationToken ct) =>
        {
            if (!await CanManageOrganizationAsync(organizationId, access, context, ct)) return Results.Forbid();
            return Results.Ok(await connections.EndAsync(id, Owner(context), organizationId, ct));
        }).WithName("DisconnectCodexConnection");
        codex.MapPost("/connections/{id:guid}/models", async (Guid id, CodexConnectRequest request, [FromServices] CodexConnections connections,
            [FromServices] ICurrentUserAccessService access, HttpContext context, CancellationToken ct) =>
        {
            if (!await CanManageOrganizationAsync(request.OrganizationId, access, context, ct)) return Results.Forbid();
            return Results.Ok(await connections.ModelsAsync(id, Owner(context), request.OrganizationId, ct));
        }).WithName("DiscoverCodexModels");
        codex.MapGet("/profiles/{id:guid}", (Guid id, [FromServices] OpenAiChatProfiles profiles, HttpContext context, CancellationToken ct) =>
            profiles.GetCodexAsync(id, Owner(context), ct)).WithName("GetCodexProviderProfile");
        codex.MapPut("/profiles/{id:guid}", async (Guid id, CodexProfileUpdate request, [FromServices] OpenAiChatProfiles profiles,
            [FromServices] IntegrationCredentialPairingService pairing, [FromServices] ICurrentUserAccessService access,
            HttpContext context, CancellationToken ct) =>
        {
            if (request.Id != id || !SupportedReads(request.ReadOperations))
                return Results.BadRequest(new { message = "Select operations from the supported ticket read catalog." });
            if (!await CanManageOrganizationAsync(request.OrganizationId, access, context, ct)) return Results.Forbid();
            return Results.Ok(await profiles.SaveCodexAsync(request, Owner(context), profiles.SetupOptions(pairing.PublicOrigin).McpResources, ct));
        }).WithName("SaveCodexProviderProfile");
    }

    private static bool SupportedReads(string[]? operations) => operations is not null &&
        !operations.Except(OpenAiChatMcpAuthority.ReadOperations.Select(item => item.Key), StringComparer.Ordinal).Any();

    private static async Task<bool> CanManageOrganizationAsync(string? organization, ICurrentUserAccessService access,
        HttpContext context, CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(organization) && (await access.ResolveAsync(context.User, ct)).CanManageIntegrationCredentialsFor(organization);
}
