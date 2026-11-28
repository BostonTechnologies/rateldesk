using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AutomationTrace;
using AutomationTrace.Contracts;

namespace Helpdesk.API.Services;

internal static class IncidentQueueContract
{
    public const string Version = "rateldesk.incident-create.v3";

    public static bool ValidGroupId(string? id) => id is { Length: > 0 and <= 64 } &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static string Fingerprint(AutomationIncidentCreate incident, string groupId)
    {
        var value = $"{Version}\n{AutomationTraceValidation.Fingerprint(incident)}\n{groupId}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record Create(string ContractVersion, string AssignedSupportGroupId, AutomationIncidentCreate Incident);
}
