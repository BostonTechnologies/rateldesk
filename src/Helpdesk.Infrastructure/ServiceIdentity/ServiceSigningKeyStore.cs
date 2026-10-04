using System.Security.Cryptography;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Infrastructure.ServiceIdentity;

public sealed record OwnedServiceSigningKey(RSA Rsa, RsaSecurityKey Key) : IDisposable
{
    public void Dispose() => Rsa.Dispose();
}

/// <summary>Shared relational keys are protected by the established durable Data Protection ring.</summary>
public sealed class ServiceSigningKeyStore(HelpdeskDbContext db, IDataProtectionProvider protection,
    IOptionsMonitor<ServiceIdentityOptions> options, TimeProvider time)
{
    public async Task<OwnedServiceSigningKey> GetSigningKeyAsync(CancellationToken ct = default)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled) throw new ServiceSigningKeyUnavailableException("Service issuer is disabled.");
        var row = await db.Set<ServiceSigningKey>().AsNoTracking().SingleOrDefaultAsync(x => x.ActiveSlot == 1, ct);
        if (row is null)
        {
            using var generated = RSA.Create(3072);
            var candidate = CreateRow(generated, settings.Issuer);
            db.Set<ServiceSigningKey>().Add(candidate);
            try { await db.SaveChangesAsync(ct); row = candidate; }
            catch (DbUpdateException)
            {
                db.Entry(candidate).State = EntityState.Detached;
                row = await db.Set<ServiceSigningKey>().AsNoTracking().SingleOrDefaultAsync(x => x.ActiveSlot == 1, ct);
                if (row is null) throw new ServiceSigningKeyUnavailableException("No usable durable service signing key is available.");
            }
        }
        if (row.Issuer != settings.Issuer) throw new ServiceSigningKeyUnavailableException("The durable signing identity belongs to a different issuer. A deliberate issuer migration is required.");
        RSA rsa = RSA.Create();
        try
        {
            var encoded = Protector(row.Issuer, row.Kid).Unprotect(row.ProtectedPrivateKey);
            rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(encoded), out _);
            // This RSA instance belongs to one signing operation. IdentityModel's
            // shared provider cache must not retain it after the owner disposes it.
            return new(rsa, new RsaSecurityKey(rsa)
            {
                KeyId = row.Kid,
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            });
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            rsa.Dispose();
            throw new ServiceSigningKeyUnavailableException("The durable service signing key cannot be unprotected. Restore the shared Data Protection ring.", ex);
        }
    }

    public async Task<IReadOnlyList<RsaSecurityKey>> GetValidationKeysAsync(CancellationToken ct = default)
    {
        var issuer = options.CurrentValue.Issuer;
        var rows = await db.Set<ServiceSigningKey>().AsNoTracking().Where(x => x.Issuer == issuer).ToListAsync(ct);
        return rows.Where(x => x.ActiveSlot == 1 || x.ValidateUntilUtc > time.GetUtcNow()).Select(x => new RsaSecurityKey(new RSAParameters
        {
            Modulus = Base64UrlEncoder.DecodeBytes(x.PublicModulus), Exponent = Base64UrlEncoder.DecodeBytes(x.PublicExponent)
        }) { KeyId = x.Kid }).ToArray();
    }

    public async Task<object> GetJwksAsync(CancellationToken ct = default)
    {
        // An empty store is initialized once, protected, and reused by every API replica.
        using var active = await GetSigningKeyAsync(ct);
        var keys = await GetValidationKeysAsync(ct);
        return new { keys = keys.Select(x => new { kty = "RSA", use = "sig", kid = x.KeyId, alg = "RS256", n = Base64UrlEncoder.Encode(x.Parameters.Modulus), e = Base64UrlEncoder.Encode(x.Parameters.Exponent) }).ToArray() };
    }

    public async Task<string> RotateAsync(CancellationToken ct = default)
    {
        using var current = await GetSigningKeyAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<ServiceSigningKey>().SingleAsync(x => x.ActiveSlot == 1, ct);
        row.ActiveSlot = null;
        row.ValidateUntilUtc = time.GetUtcNow().AddSeconds(options.CurrentValue.AccessTokenLifetimeSeconds + options.CurrentValue.ClockSkewSeconds + 60);
        await db.SaveChangesAsync(ct);
        using var rsa = RSA.Create(3072);
        var next = CreateRow(rsa, options.CurrentValue.Issuer);
        db.Set<ServiceSigningKey>().Add(next);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return next.Kid;
    }

    private ServiceSigningKey CreateRow(RSA rsa, string issuer)
    {
        var kid = Guid.NewGuid().ToString("N");
        var publicKey = rsa.ExportParameters(false);
        return new() { Kid = kid, Issuer = issuer, ActiveSlot = 1, CreatedAtUtc = time.GetUtcNow(),
            ProtectedPrivateKey = Protector(issuer, kid).Protect(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey())),
            PublicModulus = Base64UrlEncoder.Encode(publicKey.Modulus), PublicExponent = Base64UrlEncoder.Encode(publicKey.Exponent) };
    }
    private IDataProtector Protector(string issuer, string kid) => protection.CreateProtector("RatelDesk.ServiceIdentity.SigningKey.v1", issuer, kid, "RS256");
}

public sealed class ServiceSigningKeyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
