using Helpdesk.API.Authentication;
using Helpdesk.API.Services.AiAssistant;
using Helpdesk.Application.AiAssistant.Chat;
using Helpdesk.Infrastructure.AiAssistant.Chat.OpenAi;
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
            if (await resolver.ResolveAsync(http.User, http.RequestAborted) is null) return Results.Forbid();
            try { return await next(context); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "The profile changed. Reload it before saving." }); }
            catch (ChatConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
            catch (HttpRequestException ex) when (ex.StatusCode is not null)
            {
                return Results.Problem("OpenAI rejected the verification request.", statusCode: (int)ex.StatusCode.Value);
            }
            catch (HttpRequestException) { return Results.Problem("The OpenAI API could not be reached.", statusCode: 502); }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            {
                return Results.Problem("OpenAI did not respond before the verification deadline.", statusCode: 504);
            }
        });

        group.MapGet("", ([FromServices] OpenAiChatProfiles profiles, CancellationToken ct) => profiles.ListAsync(ct));
        group.MapGet("/read-operations", () => OpenAiChatMcpAuthority.ReadOperations);
        group.MapGet("/openai/{id:guid}", (Guid id, [FromServices] OpenAiChatProfiles profiles, CancellationToken ct) => profiles.GetAsync(id, ct));
        group.MapPut("/openai/{id:guid}", async (Guid id, OpenAiChatProfileUpdate request, [FromServices] OpenAiChatProfiles profiles, CancellationToken ct) =>
        {
            if (request.Settings is null || request.Id != id || request.Settings.ReadOperations is null ||
                request.Settings.ReadOperations.Except(OpenAiChatMcpAuthority.ReadOperations.Select(item => item.Key), StringComparer.Ordinal).Any())
                return Results.BadRequest(new { message = "Select operations from the supported ticket read catalog." });
            return Results.Ok(await profiles.SaveAsync(request, ct));
        });
        group.MapPut("/default", async (ChatProviderDefault request, [FromServices] OpenAiChatProfiles profiles, CancellationToken ct) =>
        {
            await profiles.SetDefaultAsync(request.ProfileId, ct);
            return Results.NoContent();
        });
        group.MapPost("/openai/{id:guid}/verify", (Guid id, [FromServices] OpenAiChatProfiles profiles, IHttpClientFactory http, CancellationToken ct) =>
            profiles.VerifyAsync(id, http, ct));
    }
}
