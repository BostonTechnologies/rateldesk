using System.Security.Cryptography;
using System.Text;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

public static class IntegrationProviderSecretBinding
{
    public static string Fingerprint(string providerKey, params string?[] values)
    {
        var material = string.Join("\n", new[] { providerKey }.Concat(values.Select(Normalize)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
