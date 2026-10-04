using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Helpdesk.Infrastructure.Persistence.Connectivity;

public sealed class IntegrationProviderSecretProtector(IDataProtectionProvider provider)
{
    private const string BoundPrefix = "bound.v2:";
    private readonly IDataProtector protector = provider.CreateProtector(
        "RatelDesk.IntegrationProviderCredentials", "v1");

    public string Protect(string plaintext) =>
        string.IsNullOrEmpty(plaintext) ? string.Empty : protector.Protect(plaintext);

    public string Protect(string plaintext, string ownershipBinding) => string.IsNullOrEmpty(plaintext)
        ? string.Empty : BoundPrefix + provider.CreateProtector("RatelDesk.IntegrationProviderCredentials", "v2", ownershipBinding).Protect(plaintext);

    public string Unprotect(string protectedValue, string ownershipBinding)
    {
        if (!protectedValue.StartsWith(BoundPrefix, StringComparison.Ordinal)) return Unprotect(protectedValue);
        try
        {
            return provider.CreateProtector("RatelDesk.IntegrationProviderCredentials", "v2", ownershipBinding)
                .Unprotect(protectedValue[BoundPrefix.Length..]);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new IntegrationProviderSecretUnavailableException("The saved provider secret is unavailable for this profile ownership.", exception);
        }
    }

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
