using Helpdesk.Shared.Pairing;

namespace HelpDesk.NewWeb.Services;

public static class PairingMetadataEndpoints
{
    public static void MapPairingMetadataEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(PairingContract.Root + "/metadata", async (HttpContext context, IHttpClientFactory clients) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var nonce = context.Request.Headers["X-Pairing-Nonce"].ToString();
            if (nonce.Length is < 32 or > 128 || nonce.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                return Results.BadRequest(new { code = "pairing-nonce-invalid", message = "A valid metadata challenge is required." });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, PairingContract.Root + "/metadata");
                request.Headers.Add("X-Pairing-Nonce", nonce);
                using var response = await clients.CreateClient("SystemApiNoAuth").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) return Results.StatusCode((int)response.StatusCode);
                await response.Content.LoadIntoBufferAsync(32 * 1024, deadline.Token);
                return Results.Bytes(await response.Content.ReadAsByteArrayAsync(deadline.Token), "application/json");
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();
    }
}
