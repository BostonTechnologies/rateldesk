using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace Helpdesk.AgentClient;

public sealed record ConfigurationProfileSaveResult(string Path, string Revision, [property: JsonIgnore] AgentClientConfiguration Configuration)
{
    public override string ToString() => $"ConfigurationProfileSaveResult {{ path = {Path}, secrets = [REDACTED] }}";
}

/// <summary>Explicit profiles are isolated by surface; no selected MCP identity is inferred from CLI state.</summary>
public sealed partial class ConfigurationProfiles(string? rootPath = null, ConfigurationSurface surface = ConfigurationSurface.Cli)
{
    public string RootPath { get; } = System.IO.Path.Combine(rootPath ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "rateldesk", "profiles"), surface.ToString().ToLowerInvariant());

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(RootPath)) return [];
        // Checking an existing child validates the directory chain as well.
        _ = SecureConfigurationFiles.Revision(System.IO.Path.Combine(RootPath, ".profile-check"));
        return Directory.EnumerateFiles(RootPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(System.IO.Path.GetFileNameWithoutExtension).Where(name => name is not null && ProfileName().IsMatch(name)).Select(name => name!).Order(StringComparer.Ordinal).ToArray();
    }

    public string PathFor(string name)
    {
        if (!ProfileName().IsMatch(name ?? string.Empty)) throw new AgentClientValidationException("Profile names must contain 1–64 lowercase letters, digits, or hyphens.");
        return System.IO.Path.Combine(RootPath, name + ".json");
    }

    public AgentClientConfiguration Load(string name) => new AgentClientConfigurationStore(PathFor(name)).Load();

    /// <summary>Null permits a reviewed replacement; empty revision requires a new profile.</summary>
    public ConfigurationProfileSaveResult Save(string name, AgentClientConfiguration configuration, string? expectedRevision = null, CancellationToken cancellationToken = default)
        => SaveFile(PathFor(name), configuration, expectedRevision, cancellationToken);

    public static ConfigurationProfileSaveResult SaveFile(string path, AgentClientConfiguration configuration, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        using var operation = SecureConfigurationFiles.AcquireOperationLease(path, cancellationToken);
        return SaveFileUnderLease(path, configuration, expectedRevision, cancellationToken);
    }

    internal static ConfigurationProfileSaveResult SaveFileUnderLease(string path, AgentClientConfiguration configuration, string? expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentClientConfigurationValidator.ValidateChoice(configuration);
        if (string.IsNullOrWhiteSpace(configuration.ApiBaseUrl)) throw new AgentClientValidationException("A profile requires an explicit apiBaseUrl.");
        var target = AgentClientConfigurationValidator.ApiUri(configuration.ApiBaseUrl);
        if (!string.IsNullOrWhiteSpace(configuration.CredentialTarget) && !AgentClientConfigurationValidator.SameTarget(target, AgentClientConfigurationValidator.ApiUri(configuration.CredentialTarget)))
            throw new AgentClientValidationException("A credential cannot be saved against a different approved target.");
        if (configuration.ClientTransport is not (null or "cli" or "stdio" or "http")) throw new AgentClientValidationException("clientTransport must be cli, stdio, or http.");
        if (configuration.CredentialPurpose is not (null or "api" or "mcp")) throw new AgentClientValidationException("credentialPurpose must be api or mcp.");
        if (configuration.CredentialPurpose == "mcp")
        {
            AgentClientConfigurationValidator.ValidateHttpClientProfile(configuration, requireCredential: false);
        }
        path = System.IO.Path.GetFullPath(path);
        if (File.Exists(path) && new FileInfo(path).IsReadOnly)
            throw new AgentClientValidationException("The selected profile is read-only; choose a writable profile or export directory.");
        // A writer that waited for pairing cleanup cannot publish an already removed reference.
        ValidateSecretReferences(configuration);
        var currentRevision = SecureConfigurationFiles.Revision(path);
        if (expectedRevision is not null && !string.Equals(expectedRevision, currentRevision ?? string.Empty, StringComparison.Ordinal))
            throw new AgentClientValidationException("Configuration changed; reload it before saving.");
        // Replacing a profile with the same credential must not silently rebind it.
        if (currentRevision is not null)
        {
            var old = new AgentClientConfigurationStore(path).Load();
            if (!AgentClientConfigurationValidator.SameTargetOrEmpty(old.ApiBaseUrl, configuration.ApiBaseUrl) &&
                SameIdentity(old, configuration)) throw new AgentClientValidationException("The existing credential requires reauthorization before its target can change.");
        }
        var createdSecrets = new List<string>();
        var saved = configuration;
        try
        {
            if (!string.IsNullOrWhiteSpace(saved.IntegrationCredential))
            {
                var secretPath = CreateSecret(path, saved.IntegrationCredential, createdSecrets);
                saved = saved with { IntegrationCredential = null, IntegrationCredentialFile = secretPath };
            }
            if (!string.IsNullOrWhiteSpace(saved.AuthentikAppPassword))
            {
                var secretPath = CreateSecret(path, saved.AuthentikAppPassword, createdSecrets);
                saved = saved with { AuthentikAppPassword = null, AuthentikAppPasswordFile = secretPath };
            }
            if (saved.IntegrationCredentialFile is not null || saved.AuthentikAppPasswordFile is not null)
                saved = saved with { CredentialTarget = target.AbsoluteUri.TrimEnd('/') };
            cancellationToken.ThrowIfCancellationRequested();
            var revision = SecureConfigurationFiles.WriteAtomic(path, JsonSerializer.Serialize(saved, AgentClientConfiguration.JsonOptions) + Environment.NewLine, expectedRevision ?? currentRevision ?? string.Empty);
            return new(path, revision, saved);
        }
        catch
        {
            foreach (var secret in createdSecrets) File.Delete(secret);
            throw;
        }
    }

    public void Remove(string name, string? expectedRevision = null)
    {
        var path = PathFor(name);
        using var operation = SecureConfigurationFiles.AcquireOperationLease(path);
        var revision = SecureConfigurationFiles.Revision(path);
        if (expectedRevision is not null && !string.Equals(expectedRevision, revision, StringComparison.Ordinal)) throw new AgentClientValidationException("Configuration changed; reload it before removing.");
        if (revision is null) throw new AgentClientValidationException("The selected profile does not exist.");
        SecureConfigurationFiles.DeleteAtomic(path, expectedRevision ?? revision);
    }

    /// <summary>Rolls back an unacknowledged save only if no later writer changed the reviewed file.</summary>
    public static string? RestoreFile(string path, AgentClientConfiguration? previous, string expectedRevision)
    {
        using var operation = SecureConfigurationFiles.AcquireOperationLease(path);
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new AgentClientValidationException("A saved revision is required for rollback.");
        if (previous is null)
        {
            SecureConfigurationFiles.DeleteAtomic(path, expectedRevision);
            return null;
        }
        ValidateSecretReferences(previous);
        return SecureConfigurationFiles.WriteAtomic(path, JsonSerializer.Serialize(previous, AgentClientConfiguration.JsonOptions) + Environment.NewLine, expectedRevision);
    }

    internal static void ValidateSecretReferences(AgentClientConfiguration configuration)
    {
        if (configuration.IntegrationCredentialFile is not null)
            _ = AgentClientConfigurationValidator.IntegrationSecret(SecureConfigurationFiles.ReadText(configuration.IntegrationCredentialFile, 16384, true).TrimEnd('\r', '\n'));
        if (configuration.AuthentikAppPasswordFile is not null && string.IsNullOrWhiteSpace(SecureConfigurationFiles.ReadText(configuration.AuthentikAppPasswordFile, 16384, true)))
            throw new AgentClientValidationException("The referenced application password is empty.");
    }

    private static string CreateSecret(string profilePath, string secret, List<string> created)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(profilePath)!, "secrets", $"credential-{Guid.NewGuid():N}.secret");
        SecureConfigurationFiles.WriteAtomic(path, secret + Environment.NewLine, string.Empty);
        created.Add(path);
        return path;
    }
    private static bool SameIdentity(AgentClientConfiguration left, AgentClientConfiguration right)
    {
        static byte[]? Identity(AgentClientConfiguration value)
        {
            var inline = value.IntegrationCredential ?? value.AuthentikAppPassword;
            var file = value.IntegrationCredentialFile ?? value.AuthentikAppPasswordFile;
            var secret = inline ?? (file is null ? null : SecureConfigurationFiles.ReadText(file, 16384, true).TrimEnd('\r', '\n'));
            return string.IsNullOrWhiteSpace(secret) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        }
        var leftIdentity = Identity(left); var rightIdentity = Identity(right);
        return leftIdentity is not null && rightIdentity is not null && CryptographicOperations.FixedTimeEquals(leftIdentity, rightIdentity);
    }
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ProfileName();
}
