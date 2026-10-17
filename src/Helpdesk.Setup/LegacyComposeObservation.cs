using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;

namespace Helpdesk.Setup;

/// <summary>Consumes a fresh owner-Compose observation supplied explicitly by the host process.</summary>
internal sealed record LegacyComposeObservation(JsonObject Expected, JsonObject Actual)
{
    internal bool CompleteModelsMatch => Canonical(Expected) == Canonical(Actual);
    internal const string ApprovedRawHashVariable = "RATELDESK_SETUP_APPROVED_RAW_SHA256";
    internal const string ExpectedSourceVariable = "RATELDESK_SETUP_EXPECTED_COMPOSE_SOURCE";
    internal const string ExpectedHashVariable = "RATELDESK_SETUP_EXPECTED_COMPOSE_SHA256";
    internal const string ActualSourceVariable = "RATELDESK_SETUP_ACTUAL_COMPOSE_SOURCE";
    internal const string ActualHashVariable = "RATELDESK_SETUP_ACTUAL_COMPOSE_SHA256";
    internal const string ReviewedDeploymentHashVariable = "RATELDESK_SETUP_REVIEWED_DEPLOYMENT_SHA256";
    internal const string ApprovedOverlayHashVariable = "RATELDESK_SETUP_APPROVED_OVERLAY_SHA256";
    private static readonly string[] Variables =
        [ApprovedRawHashVariable, ExpectedSourceVariable, ExpectedHashVariable, ActualSourceVariable, ActualHashVariable, ReviewedDeploymentHashVariable, ApprovedOverlayHashVariable];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Prefix = ".legacy-observation-";
    private const string Invalid = "HOST_COMPOSE_OBSERVATION_INVALID: The fresh owner Compose observation is incomplete or changed. Run the host launcher again; no deployment approval was renewed.";

    internal static LegacyComposeObservation? TryRead(HttpMcpDeploymentProposal approved, string originalText, JsonObject current)
    {
        var values = ReadProofValues();
        if (values is null) return null;
        try
        {
            if (approved.Intent != HttpMcpHostIntent.LegacyInitializer ||
                string.IsNullOrWhiteSpace(approved.SnapshotPath) || !Path.IsPathFullyQualified(approved.SnapshotPath))
                throw new AgentClientValidationException(Invalid);
            var directory = Path.GetDirectoryName(Path.GetFullPath(approved.SnapshotPath))!;
            SecureConfigurationFiles.ValidatePrivateDirectory(directory);
            var expectedPath = ObservationPath(values[1]!, directory, "expected");
            var actualPath = ObservationPath(values[3]!, directory, "actual");
            if (Path.GetFileName(expectedPath)[..^"-expected.json".Length] != Path.GetFileName(actualPath)[..^"-actual.json".Length])
                throw new AgentClientValidationException(Invalid);

            // Read each private file once. Every digest and parsed model below uses
            // these exact bounded bytes, never a subsequently reopened path.
            var receipt = JsonSerializer.Deserialize<HostApprovalReceipt>(SecureConfigurationFiles.ReadText(
                Path.Combine(directory, "host-approval.json"), requirePrivate: true), Json);
            var rawText = SecureConfigurationFiles.ReadPrivateDocument(Path.Combine(directory, "approved-proposal.json"));
            var deploymentText = SecureConfigurationFiles.ReadPrivateDocument(Path.Combine(directory, "deployment.json"));
            var expectedText = SecureConfigurationFiles.ReadPrivateDocument(expectedPath);
            var actualText = SecureConfigurationFiles.ReadPrivateDocument(actualPath);
            if (receipt is null || !receipt.Approved || receipt.Intent != HttpMcpHostIntent.LegacyInitializer ||
                string.IsNullOrWhiteSpace(receipt.ApprovedBy) || receipt.ExportHashes is null ||
                !receipt.ExportHashes.TryGetValue("approved-proposal.json", out var receiptRawHash) ||
                !EqualDigest(receipt.SourceHash, approved.SourceHash) || !EqualDigest(receipt.ProposalHash, approved.ProposalHash) ||
                !EqualDigest(HttpMcpEffectiveDeployment.Hash(originalText), approved.SourceHash) ||
                !EqualDigest(HttpMcpEffectiveDeployment.Hash(rawText), values[0]) || !EqualDigest(receiptRawHash, values[0]) ||
                !EqualDigest(HttpMcpEffectiveDeployment.Hash(deploymentText), values[5]) ||
                !EqualDigest(HttpMcpEffectiveDeployment.Hash(expectedText), values[2]) ||
                !EqualDigest(HttpMcpEffectiveDeployment.Hash(actualText), values[4]))
                throw new AgentClientValidationException(Invalid);
            ValidateOverlay(approved, directory, receipt, values[6]);
            var raw = HttpMcpEffectiveDeployment.Parse(rawText);
            if (!EqualDigest(HttpMcpEffectiveDeployment.ProposalRevision(raw, approved.SourceHash, approved.Intent), approved.ProposalHash) ||
                !EqualDigest(HttpMcpEffectiveDeployment.ProposalRevision(approved.CompleteDeployment, approved.SourceHash, approved.Intent), approved.ProposalHash))
                throw new AgentClientValidationException(Invalid);
            var expected = HttpMcpEffectiveDeployment.Parse(expectedText);
            var actual = HttpMcpEffectiveDeployment.Parse(actualText);
            if (expected.Any(entry => entry.Key.StartsWith("x-rateldesk-", StringComparison.OrdinalIgnoreCase)) ||
                actual.Any(entry => entry.Key.StartsWith("x-rateldesk-", StringComparison.OrdinalIgnoreCase)))
                throw new AgentClientValidationException(Invalid);
            var currentCompose = current.DeepClone().AsObject();
            foreach (var key in currentCompose.Select(entry => entry.Key).Where(key => key.StartsWith("x-rateldesk-", StringComparison.OrdinalIgnoreCase)).ToArray())
                currentCompose.Remove(key);
            if (Canonical(currentCompose) != Canonical(actual))
                throw new AgentClientValidationException(Invalid);
            return new(expected, actual);
        }
        catch (Exception error) when (error is AgentClientValidationException or JsonException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new AgentClientValidationException(Invalid);
        }
    }

    internal static void ValidateOverlay(HttpMcpDeploymentProposal approved, string directory, HostApprovalReceipt receipt, string? hostHash = null)
    {
        if (approved.Intent != HttpMcpHostIntent.LegacyInitializer) return;
        try
        {
            hostHash ??= ReadProofValues()?[6];
            SecureConfigurationFiles.ValidatePrivateDirectory(directory);
            var text = SecureConfigurationFiles.ReadPrivateDocument(Path.Combine(directory, "compose.host.json"));
            var hash = HttpMcpEffectiveDeployment.Hash(text);
            if (receipt.ExportHashes is null || !receipt.ExportHashes.TryGetValue("compose.host.json", out var receiptHash) ||
                !EqualDigest(hash, receiptHash) || hostHash is not null && !EqualDigest(hash, hostHash) ||
                Canonical(HttpMcpEffectiveDeployment.Parse(text)) != Canonical(approved.Overlay))
                throw new AgentClientValidationException(Invalid);
        }
        catch (Exception error) when (error is AgentClientValidationException or JsonException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new AgentClientValidationException(Invalid);
        }
    }

    private static string?[]? ReadProofValues()
    {
        var values = Variables.Select(Environment.GetEnvironmentVariable).ToArray();
        if (values.All(value => value is null)) return null;
        if (values.Any(string.IsNullOrWhiteSpace) || !Digest(values[0]) || !Digest(values[2]) || !Digest(values[4]) || !Digest(values[5]) || !Digest(values[6]))
            throw new AgentClientValidationException(Invalid);
        return values;
    }

    private static string ObservationPath(string path, string directory, string role)
    {
        if (!Path.IsPathFullyQualified(path)) throw new AgentClientValidationException(Invalid);
        var normalized = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var name = Path.GetFileName(normalized);
        var suffix = "-" + role + ".json";
        if (!string.Equals(Path.GetDirectoryName(normalized), directory, comparison) ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            throw new AgentClientValidationException(Invalid);
        var attempt = name[Prefix.Length..^suffix.Length];
        if (!Guid.TryParseExact(attempt, "N", out _) && !Guid.TryParseExact(attempt, "D", out _))
            throw new AgentClientValidationException(Invalid);
        return normalized;
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool EqualDigest(string? left, string? right) => Digest(left) && Digest(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Canonical(JsonNode? value) => value switch
    {
        JsonObject map => "{" + string.Join(',', map.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => JsonSerializer.Serialize(entry.Key) + ":" + Canonical(entry.Value))) + "}",
        JsonArray array => "[" + string.Join(',', array.Select(Canonical)) + "]", _ => value?.ToJsonString() ?? "null"
    };
}
