using Helpdesk.API.Endpoints.PublicIngress;
using Helpdesk.ServiceDefaults.Security;
using Helpdesk.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace Helpdesk.API.Endpoints.Captcha;

public static class CaptchaEndpoints
{
    /// <summary>
    /// Maps the CAPTCHA-related endpoints to the specified endpoint route builder.
    /// </summary>
    /// <remarks>This method registers a group of endpoints under the route <c>/api/v1/captcha</c> with the
    /// tag "Captcha". It returns a CAPTCHA ID and image; the answer stays in the server cache.</remarks>
    /// <param name="app">The <see cref="IEndpointRouteBuilder"/> to which the CAPTCHA endpoints will be added.</param>
    public static void MapCaptchaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/captcha").WithTags("Captcha");

        group.MapGet("/", ([FromServices] ICaptchaService svc, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var challenge = svc.TryGenerateCaptcha(PublicIngressIdentity.Client(context), context.RequestAborted);
            if (challenge is null)
                return PublicAdmissionEndpoints.TooManyRequests(context, retryAfterSeconds: 30);
            var imageBase64 = $"data:image/png;base64,{Convert.ToBase64String(challenge.Png)}";
            return Results.Ok(new CaptchaResponse(challenge.Id, imageBase64));
        }).AllowAnonymous().RequirePublicAdmission(PublicOperation.Captcha);
    }
}

public record CaptchaResponse(Guid id, string imageBase64);
