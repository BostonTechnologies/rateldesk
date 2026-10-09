using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Connectivity;
using Helpdesk.Infrastructure.ServiceIdentity;
using Helpdesk.Infrastructure.Orchestration;
using Helpdesk.Shared.Pairing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Infrastructure.Pairing;

public sealed class SystemPairingService(HelpdeskDbContext db, PairingTransport transport, PairingAuthority authority,
    IntegrationProviderSecretProtector secrets, IServicePublicSettingsResolver settings, ServiceSigningKeyStore keys,
    IServicePrincipalRegistry principals, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new PairingFailure("invalid_saved_state", "The saved connection is invalid. Delete it and pair again.", 409);
    private static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string SecretHash(string salt, string value) => Hash(salt + ":" + value);
    public static string PairId(string first, string second) => Hash(string.Join(':', new[] { Guid.Parse(first).ToString("D"), Guid.Parse(second).ToString("D") }.Order(StringComparer.Ordinal)));
    private static bool Matches(string expected, string actual) => expected.Length == 64 && actual.Length == 64 && expected.All(Uri.IsHexDigit) && actual.All(Uri.IsHexDigit) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    private string Protect<T>(T value) => secrets.Protect(Write(value));
    private T Unprotect<T>(string value) => Read<T>(secrets.Unprotect(value));

    public async Task<PairingMetadata> MetadataAsync(CancellationToken ct)
    {
        var current = (await settings.ResolveAsync(ct)).Identity;
        var receiverId = await db.InstanceInitializations.AsNoTracking().Where(x => x.Id == Helpdesk.Shared.Models.InstanceInitialization.SingletonId).Select(x => (Guid?)x.InstanceId).SingleOrDefaultAsync(ct);
        if (receiverId is null || receiverId == Guid.Empty) throw new PairingFailure("receiver_identity_unavailable", "Restore the persistent incident receiver GUID or complete normal installation bootstrap before pairing.", 503);
        using var signing = await keys.GetSigningKeyAsync(ct);
        return new(PairingContract.Version, "rateldesk", current.InstanceId, "RatelDesk", current.WebBaseUrl,
            current.ApiBaseUrl, null, Convert.ToBase64String(signing.Rsa.ExportSubjectPublicKeyInfo()), receiverId.Value.ToString("D"));
    }
    public async Task<PairingMetadataProof> MetadataProofAsync(string nonce, CancellationToken ct)
    {
        if (nonce.Length is < 32 or > 128 || nonce.Any(char.IsControl)) throw new PairingFailure("invalid_challenge", "A fresh metadata challenge is required.");
        var metadata = await MetadataAsync(ct);
        using var signing = await keys.GetSigningKeyAsync(ct);
        return new(metadata, nonce, Convert.ToBase64String(signing.Rsa.SignData(PairingTransport.ProofPayload(metadata, nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }
    public async Task<PairingCodeDto> GenerateAsync(string ownerId, CancellationToken ct)
    {
        await authority.OwnerAsync(ownerId, ct);
        _ = await MetadataAsync(ct);
        await using var tx = await BeginAsync(ct);
        await LockCodeAsync(ct);
        var row = await db.Set<InstallationPairingCode>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (row is null) { row = new(); db.Add(row); }
        var chars = new char[8]; for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var compact = new string(chars); var code = compact[..4] + "-" + compact[4..];
        row.Salt = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)); row.CodeHash = SecretHash(row.Salt, compact);
        row.OwnerId = ownerId; row.ExpiresAtUtc = time.GetUtcNow().AddMinutes(5); row.Revision++;
        row.ConsumedOperationId = null; row.ConsumedPeerId = null;
        row.FailedAttempts = 0;
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return new(code, row.ExpiresAtUtc);
    }
    public async Task<PairingExchangeResponse> ExchangeAsync(PairingExchangeRequest request, CancellationToken ct)
    {
        ValidateExchange(request);
        var remote = await transport.DiscoverAsync(request.Peer.WebOrigin, ct);
        if (remote != request.Peer || remote.Product != "netratel") throw new PairingFailure("peer_identity_mismatch", "The caller does not match the verified NetRatel installation.", 403);
        VerifyExchangeSignature(request);
        var metadata = await MetadataAsync(ct);
        if (metadata.InstallationId == request.Peer.InstallationId) throw new PairingFailure("same_installation", "Choose the other product installation.");
        await using var tx = await BeginAsync(ct);
        await LockCodeAsync(ct);
        var code = await db.Set<InstallationPairingCode>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        var fingerprint = Hash(Write(request)); var now = time.GetUtcNow();
        var redemptionId = Hash(request.OperationId + ":" + remote.InstallationId);
        var redemption = await db.Set<PairingRedemption>().SingleOrDefaultAsync(x => x.Id == redemptionId, ct);
        if (redemption is not null)
        {
            if (redemption.RequestHash != fingerprint) throw new PairingFailure("pairing_operation_changed", "The retry changed an accepted pairing operation. Use its original values or generate a fresh code.", 409);
            if (redemption.RetryUntilUtc > now && redemption.ProtectedResponse is not null &&
                await db.Set<SystemPair>().AnyAsync(x => x.Id == redemption.PairId && x.Generation == redemption.PairGeneration && x.State == "paired", ct))
            {
                await authority.OwnerAsync(redemption.OwnerId, ct);
                return Unprotect<PairingExchangeResponse>(redemption.ProtectedResponse);
            }
            throw new PairingFailure("pairing_code_used", "The completed pairing retry expired or was deleted. Generate a fresh code to pair again.", 409);
        }
        if (code?.ConsumedOperationId is not null)
        {
            throw new PairingFailure("pairing_code_used", "This pairing code was already used. Generate a new code for another pairing.", 409);
        }
        var compact = request.Code.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (code is null || code.ExpiresAtUtc <= now || !Matches(code.CodeHash, SecretHash(code.Salt, compact)))
        {
            if (code is not null && code.ExpiresAtUtc > now)
            {
                code.FailedAttempts++; code.Revision++;
                if (code.FailedAttempts >= 5) { code.ExpiresAtUtc = now; code.CodeHash = ""; }
                await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
            }
            throw new PairingFailure("pairing_code_rejected", "The pairing code is wrong, expired or replaced. Generate a current code and try again.", 401);
        }
        await authority.OwnerAsync(code.OwnerId, ct);
        await LockPairAsync(PairId(metadata.InstallationId, remote.InstallationId), ct);
        var pair = await EnsurePairAsync(metadata, remote, code.OwnerId, ct);
        var storedPeer = Read<PairingMetadata>(pair.PeerJson);
        if (storedPeer.SigningPublicKey != remote.SigningPublicKey) throw new PairingFailure("peer_identity_changed", "The paired installation signing identity changed. Delete the connection before pairing this replacement.", 409);
        if (pair.ProtectedOutboundSecret is not null && Hash(secrets.Unprotect(pair.ProtectedOutboundSecret)) != Hash(request.InboundSecret)) await RenewGenerationAsync(pair, ct);
        pair.OwnerId = code.OwnerId; pair.ProtectedOutboundSecret = secrets.Protect(request.InboundSecret); pair.PeerJson = Write(remote);
        pair.State = "paired"; pair.Revision++; pair.UpdatedAtUtc = now;
        var result = new PairingExchangeResponse(pair.Id, metadata, secrets.Unprotect(pair.ProtectedInboundSecret));
        using var signing = await keys.GetSigningKeyAsync(ct);
        result = result with { Signature = Convert.ToBase64String(signing.Rsa.SignData(Encoding.UTF8.GetBytes(Write(result)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
        code.ConsumedOperationId = request.OperationId; code.ConsumedPeerId = remote.InstallationId; code.Revision++;
        db.Add(new PairingRedemption { Id = redemptionId, PairId = pair.Id, PairGeneration = pair.Generation, OwnerId = code.OwnerId, RequestHash = fingerprint, ProtectedResponse = Protect(result), RetryUntilUtc = now.AddMinutes(10) });
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
        return result;
    }
    private async Task RenewGenerationAsync(SystemPair pair, CancellationToken ct, bool rotateInbound = true)
    {
        foreach (var row in await db.Set<SystemConnection>().Where(x => x.PairId == pair.Id && x.State != "deleted").ToListAsync(ct))
        {
            row.State = "deleted"; row.Revision++; row.ProtectedInboundCredential = null; row.ProtectedOutboundCredential = null; row.ProtectedSaveResponse = null;
            if (row.InboundPrincipalId is { } old) await principals.RevokeAsync(old, ct);
            foreach (var binding in await db.AutomationBindings.Where(x => x.SystemConnectionId == row.Id).ToListAsync(ct)) binding.Enabled = false;
        }
        foreach (var redemption in await db.Set<PairingRedemption>().Where(x => x.PairId == pair.Id).ToListAsync(ct)) redemption.ProtectedResponse = null;
        pair.Generation++; pair.Revision++;
        if (rotateInbound)
        {
            var localSecret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)); pair.Salt = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            pair.InboundSecretHash = SecretHash(pair.Salt, localSecret); pair.ProtectedInboundSecret = secrets.Protect(localSecret);
        }
        if (rotateInbound) { pair.ProtectedExchangeRequest = null; pair.ConnectOperationId = null; }
        await db.SaveChangesAsync(ct);
    }
    public async Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, string ownerId, CancellationToken ct)
    {
        var startedAt = time.GetUtcNow();
        await authority.OwnerAsync(ownerId, ct);
        if (!Guid.TryParse(request.OperationId, out _) || string.IsNullOrWhiteSpace(request.PairingCode)) throw new PairingFailure("invalid_pairing_request", "Enter a current pairing code and server address.");
        var remote = await transport.DiscoverAsync(request.Address, ct);
        if (remote.Product != "netratel") throw new PairingFailure("wrong_product", "Choose the NetRatel address.");
        var metadata = await MetadataAsync(ct);
        var existing = await db.Set<SystemPair>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == PairId(metadata.InstallationId, remote.InstallationId), ct);
        if (existing?.State == "deleted" && existing.UpdatedAtUtc >= startedAt) throw new PairingFailure("connection_deleted", "This connection was deleted while pairing. Generate a new code to start again.", 409);
        var pair = await EnsurePairAsync(metadata, remote, ownerId, ct);
        if (Read<PairingMetadata>(pair.PeerJson).SigningPublicKey != remote.SigningPublicKey) throw new PairingFailure("peer_identity_changed", "The paired installation signing identity changed. Delete the connection before pairing this replacement.", 409);
        PairingExchangeRequest exchange;
        if (pair.ConnectOperationId == request.OperationId && pair.ProtectedExchangeRequest is not null)
        {
            exchange = Unprotect<PairingExchangeRequest>(pair.ProtectedExchangeRequest);
            if (exchange.Code != request.PairingCode.Trim() || exchange.Peer != metadata) throw new PairingFailure("operation_changed", "This retry changed the original pairing request. Generate a fresh code for a different operation.", 409);
        }
        else
        {
            exchange = new(request.PairingCode.Trim(), request.OperationId, metadata, secrets.Unprotect(pair.ProtectedInboundSecret));
            using var signing = await keys.GetSigningKeyAsync(ct);
            exchange = exchange with { Signature = Convert.ToBase64String(signing.Rsa.SignData(Encoding.UTF8.GetBytes(Write(exchange)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
            pair.ConnectOperationId = request.OperationId; pair.ProtectedExchangeRequest = Protect(exchange); pair.OwnerId = ownerId; pair.Revision++;
            await db.SaveChangesAsync(ct);
        }
        var generation = pair.Generation; var revision = pair.Revision;
        var response = await transport.SendAsync<PairingExchangeResponse>(remote.ApiOrigin, "/exchange", HttpMethod.Post, exchange, null, null, ct);
        try
        {
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(remote.SigningPublicKey), out _);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(Write(response with { Signature = "" })), Convert.FromBase64String(response.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new PairingFailure("peer_identity_unproven", "The peer could not prove ownership of this pairing response.", 403); }
        if (response.Peer != remote || response.PairId != pair.Id || !ValidSecret(response.InboundSecret)) throw new PairingFailure("invalid_exchange", "The pairing response does not match the selected installation.", 502);
        db.Entry(pair).State = EntityState.Detached;
        await using var finalization = await BeginAsync(ct); await LockPairAsync(response.PairId, ct);
        pair = await db.Set<SystemPair>().SingleAsync(x => x.Id == response.PairId, ct);
        if (pair.State == "deleted" || pair.Generation != generation || pair.ConnectOperationId != request.OperationId) throw new PairingFailure("connection_deleted", "This connection was deleted while pairing. Generate a new code to start again.", 409);
        if (pair.ProtectedOutboundSecret is not null && Hash(secrets.Unprotect(pair.ProtectedOutboundSecret)) != Hash(response.InboundSecret)) await RenewGenerationAsync(pair, ct, rotateInbound: false);
        pair.ProtectedOutboundSecret = secrets.Protect(response.InboundSecret); pair.State = "paired"; pair.PeerJson = Write(remote); pair.Revision++; pair.UpdatedAtUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct); if (finalization is not null) await finalization.CommitAsync(ct);
        return PairDto(pair);
    }
    private async Task<SystemPair> EnsurePairAsync(PairingMetadata local, PairingMetadata remote, string owner, CancellationToken ct)
    {
        var id = PairId(local.InstallationId, remote.InstallationId);
        var pair = await db.Set<SystemPair>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (pair is not null && pair.State != "deleted") return pair;
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        if (pair is null) { pair = new() { Id = id, PeerInstallationId = remote.InstallationId }; db.Add(pair); }
        else { pair.Generation++; pair.Revision++; }
        pair.OwnerId = owner; pair.PeerJson = Write(remote); pair.State = "connecting";
        pair.Salt = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)); pair.InboundSecretHash = SecretHash(pair.Salt, secret);
        pair.ProtectedInboundSecret = secrets.Protect(secret); pair.ProtectedOutboundSecret = null; pair.ProtectedExchangeRequest = null; pair.ConnectOperationId = null;
        pair.UpdatedAtUtc = time.GetUtcNow(); await db.SaveChangesAsync(ct);
        return pair;
    }
    public async Task<SystemPair> AuthenticateAsync(string peerId, string secret, string callerHash, CancellationToken ct)
    {
        if (!Guid.TryParse(peerId, out var guid) || secret.Length != 43) throw new PairingFailure("invalid_pairing_auth", "Current system pairing authentication is required.", 401);
        var id = guid.ToString("D");
        var pair = await db.Set<SystemPair>().SingleOrDefaultAsync(x => x.PeerInstallationId == id && x.State == "paired", ct);
        if (pair is null || !Matches(pair.InboundSecretHash, SecretHash(pair.Salt, secret))) throw new PairingFailure("pairing_revoked", "The system pairing is unavailable or has been deleted.", 401);
        if (pair.ProtectedOutboundSecret is null || !Matches(Hash(secrets.Unprotect(pair.ProtectedOutboundSecret)), callerHash ?? "")) throw new PairingFailure("pairing_generation_changed", "The caller belongs to an earlier pairing. Pair again or reload the current connection.", 401);
        await authority.OwnerAsync(pair.OwnerId, ct);
        return pair;
    }
    public async Task<PairingDirectory> DirectoryAsync(SystemPair pair, CancellationToken ct)
    {
        await authority.OwnerAsync(pair.OwnerId, ct);
        var orgs = await db.Organizations.AsNoTracking().Where(x => x.IsEnabled).OrderBy(x => x.Name).Select(x => new PairingChoice(x.Id, x.Name, null)).ToArrayAsync(ct);
        var ids = orgs.Select(x => x.Id).ToArray();
        var customers = await db.Customers.AsNoTracking().Where(x => x.IsEnabled && ids.Contains(x.OrganizationId)).OrderBy(x => x.Name).Select(x => new PairingChoice(x.Id, x.Name, x.OrganizationId)).ToArrayAsync(ct);
        return new(orgs, customers);
    }
    public async Task<PairingSetupDirectory> SetupDirectoryAsync(string pairId, string actor, CancellationToken ct)
    {
        await authority.OwnerAsync(actor, ct);
        var pair = await PairAsync(pairId, ct); var local = await DirectoryAsync(pair, ct);
        var remote = await PeerSendAsync<PairingDirectory>(pair, "/directory", HttpMethod.Get, null, ct);
        ValidateDirectory(remote); return new(remote.Tenants, local.Tenants, local.Customers);
    }
    public async Task<IReadOnlyList<PairingConnectionDto>> ListAsync(string actor, CancellationToken ct)
    {
        await authority.OwnerAsync(actor, ct);
        var pairs = await db.Set<SystemPair>().AsNoTracking().Where(x => x.State != "deleted").ToListAsync(ct);
        var rows = await db.Set<SystemConnection>().AsNoTracking().Where(x => x.State != "deleted").OrderBy(x => x.UpdatedAtUtc).ToListAsync(ct);
        return pairs.SelectMany(pair => rows.Any(x => x.PairId == pair.Id)
            ? rows.Where(x => x.PairId == pair.Id).Select(x => Dto(pair, x)) : [PairDto(pair)]).ToArray();
    }
    public async Task<PairingConnectionDto> SaveAsync(string pairId, PairingMapping mapping, string actor, CancellationToken ct)
    {
        await authority.OwnerAsync(actor, ct); var pair = await PairAsync(pairId, ct);
        if (mapping.PairId != pairId) throw new PairingFailure("wrong_pair", "This mapping belongs to a different system connection.");
        await StageDraftAsync(pair, mapping, actor, ct);
        await authority.ValidateAsync(actor, mapping, ct);
        var directory = await PeerSendAsync<PairingDirectory>(pair, "/directory", HttpMethod.Get, null, ct);
        ValidateDirectory(directory);
        if (!directory.Tenants.Any(x => x.Id == mapping.NetRatelTenantId)) throw new PairingFailure("tenant_not_authorized", "The selected NetRatel tenant is unavailable or no longer authorized.", 403);
        var row = await PrepareMappingAsync(pair, mapping, actor, ct);
        var credential = row.ProtectedInboundCredential is null ? null : Unprotect<PairingBusinessCredential>(row.ProtectedInboundCredential);
        var request = new PairingSaveRequest(row.SaveOperationId!, row.Revision, mapping, credential);
        var generation = pair.Generation; var revision = row.Revision;
        var response = await PeerSendAsync<PairingSaveResponse>(pair, "/mappings/" + mapping.Id, HttpMethod.Put, request, ct);
        if (response.Mapping != mapping) throw new PairingFailure("mapping_mismatch", "The peer saved a different mapping. Review this connection and retry.", 502);
        ValidatePeerCredential(pair, mapping, response.Credential);
        {
            await using var activation = await BeginAsync(ct); await LockPairAsync(pair.Id, ct);
            db.Entry(row).State = EntityState.Detached;
            row = await db.Set<SystemConnection>().SingleAsync(x => x.Id == Guid.Parse(mapping.Id), ct);
            await db.Entry(pair).ReloadAsync(ct);
            if (row.State == "deleted" || row.Revision != revision || pair.State != "paired" || pair.Generation != generation) throw new PairingFailure("mapping_changed", "The connection changed or was deleted while saving. Reload the current configuration.", 409);
            row.ProtectedOutboundCredential = response.Credential is null ? null : Protect(response.Credential);
            row.State = "connected"; row.UpdatedAtUtc = time.GetUtcNow();
            await BindAutomationAsync(mapping, ct); await db.SaveChangesAsync(ct);
            if (activation is not null) await activation.CommitAsync(ct);
        }
        try
        {
            var verified = SafeTestResult(pair, row, await PeerSendAsync<PairingTestResult>(pair, "/mappings/" + mapping.Id + "/test", HttpMethod.Post, null, ct));
            if (!verified.Success) throw new PairingFailure("business_validation_failed", verified.Message, 409);
            await AcceptTestAsync(pair, row.Id, ct);
            await db.Entry(row).ReloadAsync(ct); await db.Entry(pair).ReloadAsync(ct);
            if (row.State != "connected" || row.Revision != revision || pair.State != "paired" || pair.Generation != generation) throw new PairingFailure("mapping_changed", "The connection changed or was deleted while validating. Reload its current configuration.", 409);
        }
        catch (Exception)
        {
            using var preserve = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.Entry(row).ReloadAsync(preserve.Token);
            if (row.State != "deleted" && row.Revision == revision) { row.State = "draft"; await db.SaveChangesAsync(preserve.Token); }
            throw;
        }
        return Dto(pair, row);
    }
    private async Task StageDraftAsync(SystemPair pair, PairingMapping mapping, string owner, CancellationToken ct)
    {
        if (!Guid.TryParse(mapping.Id, out var id) || id == Guid.Empty || Write(mapping).Length > 4096)
            throw new PairingFailure("invalid_mapping", "The selected form is invalid. Refresh the connection and try again.");
        var row = await db.Set<SystemConnection>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row?.State == "deleted") throw new PairingFailure("mapping_deleted", "This connection was deleted. Create a new named connection to continue.", 410);
        if (row is not null && (row.PairId != pair.Id || row.PairGeneration != pair.Generation)) throw new PairingFailure("wrong_pair", "The connection belongs to another pairing generation.", 409);
        if (row?.MappingJson == Write(mapping)) { row.State = "draft"; await db.SaveChangesAsync(ct); return; }
        if (row is null) { row = new() { Id = id, PairId = pair.Id, PairGeneration = pair.Generation }; db.Add(row); }
        else if (row.InboundPrincipalId is { } old) await principals.RevokeAsync(old, ct);
        row.Revision = row.SaveOperationId is null ? 1 : row.Revision + 1;
        row.SaveOperationId = Guid.NewGuid().ToString("D"); row.OwnerId = owner; row.MappingJson = Write(mapping); row.State = "draft";
        row.InboundPrincipalId = null; row.ProtectedInboundCredential = null; row.ProtectedOutboundCredential = null; row.ProtectedSaveResponse = null; row.SaveRequestHash = null;
        row.UpdatedAtUtc = time.GetUtcNow(); await db.SaveChangesAsync(ct);
    }
    public async Task<PairingSaveResponse> AcceptSaveAsync(SystemPair pair, Guid mappingId, PairingSaveRequest request, CancellationToken ct)
    {
        if (request is null || request.Mapping is null || request.Mapping.Id != mappingId.ToString("D") || request.Mapping.PairId != pair.Id || !Guid.TryParse(request.OperationId, out _) || request.Revision < 1)
            throw new PairingFailure("invalid_mapping", "The mapping identity and save operation must match this pairing.");
        await authority.ValidateAsync(pair.OwnerId, request.Mapping, ct); ValidatePeerCredential(pair, request.Mapping, request.Credential);
        var generation = pair.Generation;
        await using var tx = await BeginAsync(ct); await LockPairAsync(pair.Id, ct);
        await db.Entry(pair).ReloadAsync(ct);
        if (pair.State != "paired" || pair.Generation != generation) throw new PairingFailure("pairing_deleted", "This pairing was deleted while saving. Pair again to create a new connection.", 410);
        var row = await db.Set<SystemConnection>().SingleOrDefaultAsync(x => x.Id == mappingId, ct);
        var hash = Hash(Write(request));
        if (row?.State == "deleted") throw new PairingFailure("mapping_deleted", "This connection was deleted. Create a new named connection to continue.", 410);
        if (row?.SaveOperationId == request.OperationId)
        {
            if (row.SaveRequestHash != hash) throw new PairingFailure("save_operation_changed", "The retry changed an already accepted save operation.", 409);
            if (row.ProtectedSaveResponse is not null) return Unprotect<PairingSaveResponse>(row.ProtectedSaveResponse);
        }
        else if (row is not null && request.Revision <= row.Revision) throw new PairingFailure("mapping_revision_changed", "A newer connection configuration exists. Reload and save the current form.", 409);
        row = await PrepareMappingAsync(pair, request.Mapping, pair.OwnerId, ct, request.Revision, request.OperationId);
        row.SaveRequestHash = hash; row.ProtectedOutboundCredential = request.Credential is null ? null : Protect(request.Credential);
        row.State = "connected";
        var response = new PairingSaveResponse(request.Mapping, row.ProtectedInboundCredential is null ? null : Unprotect<PairingBusinessCredential>(row.ProtectedInboundCredential));
        row.ProtectedSaveResponse = Protect(response); row.UpdatedAtUtc = time.GetUtcNow(); await db.SaveChangesAsync(ct);
        await BindAutomationAsync(request.Mapping, ct); if (tx is not null) await tx.CommitAsync(ct); return response;
    }
    private async Task<SystemConnection> PrepareMappingAsync(SystemPair pair, PairingMapping mapping, string owner, CancellationToken ct, long? revision = null, string? operationId = null)
    {
        var id = Guid.Parse(mapping.Id); var row = await db.Set<SystemConnection>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row?.State == "deleted") throw new PairingFailure("mapping_deleted", "This connection was deleted. Create a new named connection to continue.", 410);
        var unchanged = row?.MappingJson == Write(mapping) && row.ProtectedInboundCredential is not null && row.InboundPrincipalId is { } existingPrincipal &&
            await db.Set<ServicePrincipalSecret>().AnyAsync(x => x.ServicePrincipalId == existingPrincipal && x.Status == "active" && x.ExpiresAtUtc > time.GetUtcNow(), ct);
        if (row is null) { row = new() { Id = id, PairId = pair.Id, OwnerId = owner, PairGeneration = pair.Generation }; db.Add(row); }
        else if (row.PairId != pair.Id || row.PairGeneration != pair.Generation) throw new PairingFailure("wrong_pair", "The connection belongs to another pairing generation.", 409);
        if (unchanged && revision is null) return row;
        if (row.InboundPrincipalId is { } old) await principals.RevokeAsync(old, ct);
        var staged = row.MappingJson == Write(mapping) && row.ProtectedInboundCredential is null && row.SaveOperationId is not null;
        row.MappingJson = Write(mapping); row.State = "draft"; row.OwnerId = owner; row.Revision = revision ?? (staged ? row.Revision : row.SaveOperationId is null ? 1 : row.Revision + 1);
        row.SaveOperationId = operationId ?? (staged ? row.SaveOperationId : Guid.NewGuid().ToString("D")); row.SaveRequestHash = null; row.ProtectedSaveResponse = null;
        row.ProtectedInboundCredential = null; row.ProtectedOutboundCredential = null; row.InboundPrincipalId = null;
        row.UpdatedAtUtc = time.GetUtcNow(); await db.SaveChangesAsync(ct);
        var scopes = new List<string>(); if (mapping.CreateIncidents) scopes.AddRange([ServiceIdentityScopes.IncidentCreate, ServiceIdentityScopes.IncidentReceipts, ServiceIdentityScopes.IncidentTargets]); if (mapping.RunAutomation) scopes.Add(ServiceIdentityScopes.Callback);
        var peer = Read<PairingMetadata>(pair.PeerJson); var current = (await settings.ResolveAsync(ct)).Identity;
        var created = await principals.CreateAsync(new(mapping.Name, mapping.RatelDeskOrganizationId, peer.InstallationId, mapping.NetRatelTenantId,
            scopes.ToArray(), mapping.CreateIncidents ? [mapping.RatelDeskCustomerId!] : [], mapping.CreateIncidents ? Guid.Parse(peer.ProducerInstanceId!) : null,
            mapping.CreateIncidents ? id : null, MappingId: id), owner, ct: ct);
        row.InboundPrincipalId = created.Principal.Id;
        row.ProtectedInboundCredential = Protect(new PairingBusinessCredential(created.Principal.ClientId, created.ClientSecret, current.ApiBaseUrl + "/connect/token", current.Audience, current.Issuer,
            scopes.ToArray(), mapping.CreateIncidents ? peer.ProducerInstanceId : null, mapping.CreateIncidents ? id.ToString("D") : null));
        await db.SaveChangesAsync(ct); return row;
    }
    private async Task BindAutomationAsync(PairingMapping mapping, CancellationToken ct)
    {
        var id = Guid.Parse(mapping.Id);
        var bindings = await db.AutomationBindings.Where(x => x.OrganizationId == mapping.RatelDeskOrganizationId && (x.SystemConnectionId == id || x.SystemConnectionId == null)).ToListAsync(ct);
        foreach (var binding in bindings)
        {
            if (binding.SystemConnectionId is null && mapping.RunAutomation) binding.SystemConnectionId = id;
            if (binding.SystemConnectionId == id && !mapping.RunAutomation) binding.Enabled = false;
        }
        await db.SaveChangesAsync(ct);
    }
    private void ValidatePeerCredential(SystemPair pair, PairingMapping mapping, PairingBusinessCredential? credential)
    {
        if (!mapping.RunAutomation) { if (credential is not null) throw new PairingFailure("unexpected_credential", "The peer offered automation access without the selected capability.", 502); return; }
        var peer = Read<PairingMetadata>(pair.PeerJson);
        var expected = new[] { "netratel.orchestration.read", "netratel.orchestration.invoke" };
        if (credential is null || !ServicePrincipalRegistry.IsValidClientId(credential.ClientId) || credential.ClientSecret is null || credential.ClientSecret.Length is < 32 or > 1024 || string.IsNullOrWhiteSpace(credential.Audience) || credential.Audience.Length > 256 || string.IsNullOrWhiteSpace(credential.Issuer) || credential.Issuer.Length > 2048 ||
            credential.TokenEndpoint != peer.ApiOrigin + "/connect/token" || credential.Scopes is null || !credential.Scopes.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)) ||
            credential.SourceInstanceId is not null && credential.SourceInstanceId != peer.ProducerInstanceId || credential.SourceNamespaceId is not null && credential.SourceNamespaceId != mapping.Id)
            throw new PairingFailure("invalid_business_credential", "The peer did not return the exact scoped automation credential for this connection.", 502);
    }
    public async Task<PairingTestResult> TestAsync(string pairId, Guid mappingId, string actor, CancellationToken ct)
    {
        await authority.OwnerAsync(actor, ct); var pair = await PairAsync(pairId, ct); var row = await MappingAsync(pair, mappingId, ct);
        await authority.ValidateAsync(row.OwnerId, Read<PairingMapping>(row.MappingJson), ct);
        var generation = pair.Generation; var revision = row.Revision;
        await AcceptTestAsync(pair, mappingId, ct);
        var result = SafeTestResult(pair, row, await PeerSendAsync<PairingTestResult>(pair, "/mappings/" + mappingId.ToString("D") + "/test", HttpMethod.Post, null, ct));
        await AcceptTestAsync(pair, mappingId, ct);
        await db.Entry(row).ReloadAsync(ct); await db.Entry(pair).ReloadAsync(ct);
        if (row.State != "connected" || row.Revision != revision || pair.State != "paired" || pair.Generation != generation) throw new PairingFailure("connection_deleted", "The connection changed or was deleted during the test.", 409);
        row.LastTestedAtUtc = time.GetUtcNow(); row.LastTestSucceeded = result.Success; row.LastTestMessage = result.Message; await db.SaveChangesAsync(ct);
        return result;
    }
    public async Task<PairingTestResult> AcceptTestAsync(SystemPair pair, Guid mappingId, CancellationToken ct)
    {
        var row = await MappingAsync(pair, mappingId, ct); var mapping = Read<PairingMapping>(row.MappingJson);
        await authority.ValidateAsync(row.OwnerId, mapping, ct);
        if (row.State != "connected" || row.InboundPrincipalId is null || row.ProtectedInboundCredential is null || mapping.RunAutomation && row.ProtectedOutboundCredential is null)
            throw new PairingFailure("connection_incomplete", "Save the selected mapping and capabilities before testing.", 409);
        var credential = Unprotect<PairingBusinessCredential>(row.ProtectedInboundCredential);
        var client = await principals.AuthenticateClientAsync(credential.ClientId, credential.ClientSecret, ct);
        if (client is null || !await principals.CanIssueScopesAsync(client, credential.Scopes, ct)) throw new PairingFailure("business_access_unavailable", "The current incident or automation callback grant is unavailable. Review the selected mapping and permissions.", 403);
        return new(true, "Authenticated connection, mapping and selected capabilities are available. No incident or task was created.", time.GetUtcNow());
    }
    public async Task DeleteAsync(string pairId, Guid? mappingId, string? actor, bool notifyPeer, CancellationToken ct, long? expectedGeneration = null)
    {
        if (actor is not null) await authority.OwnerAsync(actor, ct);
        await using var tx = await BeginAsync(ct); await LockPairAsync(pairId, ct);
        var pair = await db.Set<SystemPair>().SingleOrDefaultAsync(x => x.Id == pairId, ct);
        if (pair is null || pair.State == "deleted") return;
        await db.Entry(pair).ReloadAsync(ct);
        if (pair.State == "deleted") return;
        if (expectedGeneration is { } generation && pair.Generation != generation) throw new PairingFailure("pairing_generation_changed", "An earlier deletion cannot change this renewed pairing.", 410);
        var rows = await db.Set<SystemConnection>().Where(x => x.PairId == pairId && x.State != "deleted" && (mappingId == null || x.Id == mappingId)).ToListAsync(ct);
        if (mappingId is { } deletedId && !await db.Set<SystemConnection>().AnyAsync(x => x.Id == deletedId, ct))
        {
            var tombstone = new SystemConnection { Id = deletedId, PairId = pairId, PairGeneration = pair.Generation, OwnerId = pair.OwnerId, State = "deleted", MappingJson = "{}", UpdatedAtUtc = time.GetUtcNow() };
            db.Add(tombstone);
        }
        foreach (var row in rows)
        {
            foreach (var binding in await db.AutomationBindings.Where(x => x.SystemConnectionId == row.Id).ToListAsync(ct)) binding.Enabled = false;
            row.State = "deleted"; row.Revision++; row.ProtectedInboundCredential = null; row.ProtectedOutboundCredential = null; row.ProtectedSaveResponse = null;
            if (row.InboundPrincipalId is { } id) await principals.RevokeAsync(id, ct);
        }
        var last = !await db.Set<SystemConnection>().AnyAsync(x => x.PairId == pairId && x.State != "deleted" && !rows.Select(r => r.Id).Contains(x.Id), ct);
        if (notifyPeer && pair.ProtectedOutboundSecret is not null)
        {
            var local = await MetadataAsync(ct); var peer = Read<PairingMetadata>(pair.PeerJson);
            db.Add(new PairingCleanup { ProtectedRequest = Protect(new CleanupRequest(peer.ApiOrigin, mappingId is null ? "/pair" : "/mappings/" + mappingId.Value.ToString("D"), secrets.Unprotect(pair.ProtectedOutboundSecret), local.InstallationId, Hash(secrets.Unprotect(pair.ProtectedInboundSecret)))), ExpiresAtUtc = time.GetUtcNow().AddHours(1) });
        }
        if (last)
        {
            pair.State = "deleted"; pair.Revision++; pair.UpdatedAtUtc = time.GetUtcNow(); pair.InboundSecretHash = ""; pair.ProtectedInboundSecret = ""; pair.ProtectedOutboundSecret = null; pair.ProtectedExchangeRequest = null;
            var code = await db.Set<InstallationPairingCode>().SingleOrDefaultAsync(x => x.Id == 1, ct);
            if (code is not null) { code.ExpiresAtUtc = time.GetUtcNow(); code.Revision++; }
            foreach (var redemption in await db.Set<PairingRedemption>().Where(x => x.PairId == pairId).ToListAsync(ct)) redemption.ProtectedResponse = null;
        }
        await db.SaveChangesAsync(ct); if (tx is not null) await tx.CommitAsync(ct);
    }
    public async Task CleanupAsync(CancellationToken ct)
    {
        foreach (var expired in await db.Set<PairingRedemption>().Where(x => x.RetryUntilUtc <= time.GetUtcNow()).Take(64).ToListAsync(ct)) db.Remove(expired);
        var rows = await db.Set<PairingCleanup>().OrderBy(x => x.ExpiresAtUtc).Take(10).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (row.ExpiresAtUtc <= time.GetUtcNow() || row.Attempts >= 3) { db.Remove(row); continue; }
            try { var request = Unprotect<CleanupRequest>(row.ProtectedRequest); await transport.SendAsync<bool>(request.Origin, request.Route, HttpMethod.Delete, null, request.Secret, request.PeerId, ct, callerHash: request.CallerHash); db.Remove(row); }
            catch (PairingFailure) { row.Attempts++; }
        }
        await db.SaveChangesAsync(ct);
    }
    public async Task<SystemPair> PairAsync(string id, CancellationToken ct) => await db.Set<SystemPair>().SingleOrDefaultAsync(x => x.Id == id && x.State == "paired", ct) ?? throw new PairingFailure("pairing_unavailable", "This system pairing is unavailable. Refresh the connection list.", 404);
    private async Task<SystemConnection> MappingAsync(SystemPair pair, Guid id, CancellationToken ct) => await db.Set<SystemConnection>().SingleOrDefaultAsync(x => x.Id == id && x.PairId == pair.Id && x.PairGeneration == pair.Generation && x.State != "deleted", ct) ?? throw new PairingFailure("mapping_unavailable", "This connection was deleted or is unavailable.", 404);
    private async Task<T> PeerSendAsync<T>(SystemPair pair, string route, HttpMethod method, object? body, CancellationToken ct)
    {
        if (pair.ProtectedOutboundSecret is null) throw new PairingFailure("pairing_incomplete", "Complete pairing before configuring a connection.", 409);
        var local = (await settings.ResolveAsync(ct)).Identity;
        return await transport.SendAsync<T>(Read<PairingMetadata>(pair.PeerJson).ApiOrigin, route, method, body, secrets.Unprotect(pair.ProtectedOutboundSecret), local.InstanceId, ct, callerHash: Hash(secrets.Unprotect(pair.ProtectedInboundSecret)));
    }
    private static PairingConnectionDto PairDto(SystemPair pair) => new(pair.Id, pair.Id, null, Read<PairingMetadata>(pair.PeerJson), pair.State == "paired" ? "Systems paired" : "Pairing incomplete", null);
    private static PairingConnectionDto Dto(SystemPair pair, SystemConnection row) => new(row.Id.ToString("D"), pair.Id, Read<PairingMapping>(row.MappingJson), Read<PairingMetadata>(pair.PeerJson), row.State == "connected" ? "Connected" : "Systems paired", row.LastTestedAtUtc is { } tested ? new(row.LastTestSucceeded == true, row.LastTestMessage ?? "", tested) : null);
    private static void VerifyExchangeSignature(PairingExchangeRequest request)
    {
        try
        {
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.Peer.SigningPublicKey), out _);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(Write(request with { Signature = "" })), Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new PairingFailure("caller_identity_unproven", "The pairing caller could not prove ownership of its installation identity.", 403); }
    }
    private static void ValidateExchange(PairingExchangeRequest request)
    {
        if (request is null || request.Peer is null || !Guid.TryParse(request.OperationId, out _) || request.Code is null || request.Code.Length > 32 || request.InboundSecret is null || request.InboundSecret.Length != 43 || request.InboundSecret.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new PairingFailure("invalid_exchange", "A valid code, installation identity and one pairing operation are required.");
        PairingTransport.ValidateMetadata(request.Peer);
    }
    private static bool ValidSecret(string? value) => value is { Length: 43 } && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_');
    private PairingTestResult SafeTestResult(SystemPair pair, SystemConnection row, PairingTestResult result)
    {
        var sensitive = new List<string?> { secrets.Unprotect(pair.ProtectedInboundSecret), pair.ProtectedOutboundSecret is null ? null : secrets.Unprotect(pair.ProtectedOutboundSecret) };
        if (row.ProtectedInboundCredential is not null) sensitive.Add(Unprotect<PairingBusinessCredential>(row.ProtectedInboundCredential).ClientSecret);
        if (row.ProtectedOutboundCredential is not null) sensitive.Add(Unprotect<PairingBusinessCredential>(row.ProtectedOutboundCredential).ClientSecret);
        string? code = null;
        if (pair.ProtectedExchangeRequest is not null) { var exchange = Unprotect<PairingExchangeRequest>(pair.ProtectedExchangeRequest); code = exchange.Code; sensitive.Add(exchange.Code); sensitive.Add(exchange.Code.Replace("-", "", StringComparison.Ordinal)); }
        var message = PairingCodeRedaction.Apply(IntegrationErrorSafety.ProviderMessage(result.Message, 65536, sensitive.ToArray()), code)!;
        return result with { Message = message.Length <= 512 ? message : message[..512] };
    }
    private static void ValidateDirectory(PairingDirectory directory)
    {
        if (directory.Tenants is null || directory.Customers is null || directory.Tenants.Length > 512 || directory.Customers.Length > 1024 || directory.Tenants.Concat(directory.Customers).Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || x.Id.Length > 128 || x.Name is null || x.Name.Length > 256))
            throw new PairingFailure("invalid_directory", "The peer returned an incompatible tenant directory.", 502);
    }
    private async Task<IDbContextTransaction?> BeginAsync(CancellationToken ct) => db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
    private async Task LockCodeAsync(CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(712041901)", ct);
    }
    private async Task LockPairAsync(string id, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({id}))", ct);
    }
    private sealed record CleanupRequest(string Origin, string Route, string Secret, string PeerId, string CallerHash);
}
