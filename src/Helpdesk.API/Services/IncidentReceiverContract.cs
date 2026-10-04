using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Helpdesk.Shared.DTOs.Incident;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Helpdesk.Application.WorkLogs;

namespace Helpdesk.API.Services;

public static partial class IncidentReceiverContract
{
    public const string Version = "rateldesk.incident-create.v1";
    public const string KeyHeader = "Idempotency-Key";
    public const string SourceHeader = "X-NetRatel-Source-Instance";
    public const int MaximumBodyBytes = 131072;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("\\A[A-Za-z0-9._~-]{1,256}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidKeyRegex();
    public static bool ValidKey(string? key) => key is not null && ValidKeyRegex().IsMatch(key);

    public static bool TrySource(IHeaderDictionary headers, out Guid source)
    {
        source = Guid.Empty;
        var values = headers[SourceHeader];
        return values.Count == 1 && Guid.TryParseExact(values[0], "D", out source) && source != Guid.Empty &&
               string.Equals(values[0], source.ToString("D"), StringComparison.Ordinal);
    }

    public static bool TryKey(IHeaderDictionary headers, out string key)
    {
        var values = headers[KeyHeader];
        key = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return values.Count == 1 && ValidKey(key);
    }

    public static bool ValidTarget(ValidateIncidentTargetDto dto) =>
        ValidId(dto.OrganizationId) && ValidId(dto.CustomerId) &&
        (dto.AssignedToId is null || dto.AssignedToId.Length <= 64) && (dto.CategoryIds?.Count ?? 0) <= 128;

    public static bool ValidRequest(CreateIncidentDto dto) =>
        ValidTarget(new() { OrganizationId = dto.OrganizationId, CustomerId = dto.CustomerId,
            AssignedToId = dto.AssignedToId, CategoryIds = dto.CategoryIds }) &&
        !string.IsNullOrWhiteSpace(dto.Title) && dto.Title.Length <= 256 &&
        dto.Description is not null && dto.Description.Length <= 65536 &&
        Enum.IsDefined(dto.Priority) && (dto.Impact?.Length ?? 0) <= 4096 &&
        ValidList(dto.LinkedAssetIds, 512) && ValidList(dto.Attachments, 512) && ValidList(dto.CcRecipients, 254);

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64;
    private static bool ValidList(List<string>? list, int itemLimit) => list is null ||
        list.Count <= 128 && list.All(item => item is not null && item.Length <= itemLimit);

    public static List<Guid> Categories(IEnumerable<Guid>? ids) => ids?.Where(id => id != Guid.Empty)
        .Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToList() ?? [];

    public static List<string> Cc(IEnumerable<string>? values) => values?.Select(value => value.Trim().ToLowerInvariant())
        .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];

    public static DateTime? DueDateUtc(DateTime? value) => value is null ? null : value.Value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value.Value.ToUniversalTime();

    /// <summary>Frozen typed projection. No current entity names or requester email enter the fingerprint.</summary>
    public static string Fingerprint(CreateIncidentDto dto, IHtmlSanitizerService sanitizer, IHtmlToPlainTextConverter converter)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            title = dto.Title,
            description = converter.Convert(sanitizer.Sanitize(dto.Description)),
            priority = (int)dto.Priority,
            customerId = dto.CustomerId,
            organizationId = dto.OrganizationId,
            assignedToId = string.IsNullOrWhiteSpace(dto.AssignedToId) ? null : dto.AssignedToId,
            linkedAssetIds = dto.LinkedAssetIds ?? [],
            attachments = dto.Attachments ?? [],
            dueDate = DueDateUtc(dto.DueDate)?.ToString("O"),
            impact = dto.Impact,
            ccRecipients = Cc(dto.CcRecipients),
            categoryIds = Categories(dto.CategoryIds).Select(id => id.ToString("D")).ToArray()
        }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static IResult Problem(int status, string code, string? field = null) => Results.Problem(
        statusCode: status, title: code, extensions: field is null
            ? new Dictionary<string, object?> { ["code"] = code }
            : new Dictionary<string, object?> { ["code"] = code, ["errors"] = new Dictionary<string, string[]> { [field] = ["Invalid selection."] } });
}
