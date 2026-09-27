using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

public sealed class IntegrationProviderSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector(
        "RatelDesk.IntegrationProviderCredentials", "v1");

    public string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? string.Empty : protector.Protect(plaintext);

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return string.Empty;

        try
        {
            return protector.Unprotect(protectedValue);
        }
        catch (CryptographicException exception)
        {
            throw new IntegrationProviderSecretUnavailableException(
                "A stored integration provider secret could not be decrypted. Restore the shared Data Protection key ring or replace the secret.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new IntegrationProviderSecretUnavailableException(
                "A stored integration provider secret is invalid. Restore the shared Data Protection key ring or replace the secret.",
                exception);
        }
    }
}

public sealed class IntegrationProviderSecretUnavailableException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
