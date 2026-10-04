using System.Text.Json;
using Helpdesk.API.Services;
using Helpdesk.Infrastructure.Html;
using Helpdesk.Shared.DTOs.Incident;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class IncidentReceiverConformanceTests
{
    public static IEnumerable<object[]> GoldenVectors()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestEnvironment.RepositoryRoot,
            "docs", "contracts", "rateldesk-incident-create.v1.fixtures.json")));
        foreach (var vector in fixtures.RootElement.GetProperty("vectors").EnumerateArray())
            yield return [vector.GetProperty("name").GetString()!, vector.GetProperty("request").GetRawText(), vector.GetProperty("fingerprint").GetString()!];
    }

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void Semantic_projection_matches_published_golden_fixture(string name, string request, string fingerprint)
    {
        Assert.NotEmpty(name);
        var dto = JsonSerializer.Deserialize<CreateIncidentDto>(request, IncidentReceiverContract.Json)!;
        var actual = IncidentReceiverContract.Fingerprint(dto, new HtmlSanitizerService(), new HtmlToPlainTextConverter());
        Assert.Equal(fingerprint, actual);
    }

    [Fact]
    public void Fixture_equivalences_and_material_changes_are_consistent()
    {
        var vectors = GoldenVectors().ToDictionary(vector => (string)vector[0], vector => (string)vector[2]);
        Assert.Equal(vectors["minimal"], vectors["reordered-equivalent-defaults"]);
        Assert.Equal(vectors["minimal"], vectors["sanitized-description-equivalent"]);
        Assert.Equal(vectors["material-ccRecipients"], vectors["cc-set-equivalence"]);
        Assert.Equal(vectors["material-dueDate"], vectors["utc-offset-equivalence"]);
        Assert.Equal(vectors["material-categoryIds"], vectors["category-set-equivalence"]);
        Assert.NotEqual(vectors["ordered-assets"], vectors["reordered-assets-material"]);
        Assert.NotEqual(vectors["ordered-attachments"], vectors["reordered-attachments-material"]);
        Assert.All(vectors.Where(vector => vector.Key.StartsWith("material-", StringComparison.Ordinal)),
            vector => Assert.NotEqual(vectors["minimal"], vector.Value));
    }

    [Theory]
    [InlineData("a\n")]
    [InlineData("a\r")]
    [InlineData("a\t")]
    [InlineData("a\0")]
    [InlineData("percent%25")]
    [InlineData("slash/")]
    [InlineData("é")]
    public void Invalid_key_characters_are_rejected(string key) => Assert.False(IncidentReceiverContract.ValidKey(key));
}
