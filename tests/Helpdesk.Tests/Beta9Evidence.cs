using System.Reflection;
using System.Text.Json;

namespace Helpdesk.Tests;

// Opt-in source-regression receipts. Call only after assertions pass, and pass
// selected synthetic observations rather than credentials, requests or signed URLs.
internal static class Beta9Evidence
{
    public static void Record(string findingId, string scenario, object observations)
    {
        var directory = Environment.GetEnvironmentVariable("RATELDESK_BETA9_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var receipt = new
        {
            findingId, scenario, observedAt = DateTimeOffset.UtcNow, outcome = "PASS",
            scope = "Synthetic source regression; does not establish a deployed campaign pass",
            productVersion = typeof(Beta9Evidence).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ciRevision = Environment.GetEnvironmentVariable("GITHUB_SHA"), observations
        };
        File.WriteAllText(Path.Combine(directory, findingId + "-" + scenario + ".json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }
}
