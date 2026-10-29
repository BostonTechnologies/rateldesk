using Azure.Core;
using Azure.Identity;

namespace Helpdesk.Infrastructure.Resources;

public interface IGraphDatasetCredentialFactory
{
    TokenCredential Create(string tenantId, string clientId, string clientSecret);
}

internal sealed class ClientSecretGraphDatasetCredentialFactory : IGraphDatasetCredentialFactory
{
    public TokenCredential Create(string tenantId, string clientId, string clientSecret) =>
        new ClientSecretCredential(tenantId, clientId, clientSecret);
}
