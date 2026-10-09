using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;

namespace Helpdesk.Infrastructure.Pairing;

public static class PairingOutboundNetwork
{
    public static void Validate(Uri uri, string field, OrchestrationResolvedSettings settings)
    {
        if (settings.Pairing is null || settings.BaseUrl is null || PairingTransport.Origin(uri.GetLeftPart(UriPartial.Authority)) != PairingTransport.Origin(settings.BaseUrl))
            throw new PairingFailure("wrong_business_origin", "The business request must target the paired installation's verified API origin.", 403);
        IntegrationEndpointPolicy.Validate(uri, field, true);
        if (uri.AbsolutePath != "/connect/token" && uri.AbsolutePath != "/internal/health" && uri.AbsolutePath != "/internal/ingest" &&
            !uri.AbsolutePath.StartsWith("/internal/catalog", StringComparison.Ordinal) && uri.AbsolutePath != "/api/v1/system/m2m/ping")
            throw new PairingFailure("wrong_business_route", "The system connection does not authorize this business route.", 403);
    }
    public static void PrepareRequest(HttpRequestMessage request, OrchestrationResolvedSettings settings)
    {
        Validate(request.RequestUri!, "Business endpoint", settings);
        request.Options.Set(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, true);
    }
}
