using Helpdesk.Application.Services.EmailTemplates;
using Helpdesk.Infrastructure.Email;
using Helpdesk.Infrastructure.Security;
using Helpdesk.Shared.DTOs.PublicAccess;

namespace Helpdesk.API.Endpoints.PublicIngress;

public static class PublicLinkEndpoints
{
    public static void MapPublicLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(PublicAccessProtocol.ApiPrefix).WithTags("Public Access").AllowAnonymous();
        group.MapPost("/exchange", async (PublicLinkExchangeRequest request, PublicLinkService links, CancellationToken ct) =>
        {
            var exchanged = await links.ExchangeAsync(request, ct);
            return exchanged is null ? Results.Unauthorized() : Results.Ok(exchanged);
        })
        .Produces<PublicLinkExchangeResponse>()
        .WithName("ExchangePublicLink")
        .RequirePublicAdmission(PublicOperation.LinkExchange);
        group.MapPost("/replacement", async (PublicLinkExchangeRequest request, PublicLinkService links,
            ITenantBrandingResolver branding, MailboxSenderResolver mailboxes, MailboxOutboxStore outbox, CancellationToken ct) =>
        {
            await links.ReplaceAsync(request, branding, mailboxes, outbox, ct);
            return Results.Accepted();
        })
        .WithName("ReplacePublicLink")
        .RequirePublicAdmission(PublicOperation.LinkReplacement);
    }

    internal static Task<PublicLinkCapability?> AuthorizeAsync(HttpContext http, PublicLinkService links, Guid accessId, CancellationToken ct, bool forDecision = false)
    {
        var value = http.Request.Headers[PublicAccessProtocol.GrantHeader];
        return links.AuthorizeAsync(accessId, value.Count == 1 ? value[0] : null, ct, forDecision);
    }
}

internal sealed class PublicGrantRequiredMetadata;
