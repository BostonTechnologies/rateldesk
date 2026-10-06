using Helpdesk.Application.Orchestration;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Microsoft.Extensions.Options;

namespace Helpdesk.Infrastructure.ServiceLink;

/// <summary>Managed business requests share the current service-link network boundary.</summary>
public static class ServiceLinkOutboundNetwork
{
    public const string TokenClientName = "ServiceLinkOrchestrationToken";
    public const string BusinessClientName = "ServiceLinkOrchestrationInternalApi";

    public static bool Validate(Uri uri, string fieldName, OrchestrationResolvedSettings settings,
        IOptionsMonitor<ServiceLinkOptions>? currentOptions)
    {
        var allowPrivateHttp = settings.AllowPrivateHttp;
        if (settings.ServiceLink is not null)
        {
            var linking = currentOptions?.CurrentValue;
            if (linking?.Enabled != true)
                throw new InvalidOperationException("The current service-link business sender is unavailable.");
            allowPrivateHttp = linking.AllowPrivateHttp;
        }
        try
        {
            if (settings.ServiceLink is null) IntegrationEndpointPolicy.Validate(uri, fieldName, allowPrivateHttp);
            else IntegrationEndpointPolicy.ValidateServiceLink(uri, fieldName, allowPrivateHttp);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The configured NetRatel endpoint is not allowed by the outbound integration policy.");
        }
        return allowPrivateHttp;
    }

    public static void PrepareRequest(HttpRequestMessage request, OrchestrationResolvedSettings settings,
        IOptionsMonitor<ServiceLinkOptions>? currentOptions)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("The NetRatel request has no absolute endpoint.");
        var allowPrivateHttp = Validate(uri, "NetRatel endpoint", settings, currentOptions);
        request.Options.Set(IntegrationSafeHttpMessageHandler.AllowPrivateHttpOption, allowPrivateHttp);
    }
}
