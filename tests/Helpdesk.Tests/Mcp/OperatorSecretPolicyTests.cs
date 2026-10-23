using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Mcp.Tools;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorSecretPolicyTests
{
    [Theory]
    [InlineData("helpdesk_tenant_admin", "reset_local_account", "activationToken", true)]
    [InlineData("helpdesk_tenant_admin", "reset_local_account", "activationToken", false)]
    [InlineData("helpdesk_datasets", "create_credential", "plainTextKey", true)]
    [InlineData("helpdesk_datasets", "create_credential", "plainTextKey", false)]
    [InlineData("helpdesk_connections", "generate_pairing_code", "code", true)]
    [InlineData("helpdesk_connections", "generate_pairing_code", "code", false)]
    public void Only_successful_named_root_delivery_preserves_a_secret_while_all_echoes_are_scrubbed(
        string tool, string operation, string field, bool success)
    {
        const string secret = "new+private/credential=with space";
        const string storedSecret = "existing+stored/credential=";
        var encoded = Uri.EscapeDataString(secret);
        var formEncoded = System.Net.WebUtility.UrlEncode(secret);
        var upstream = new HelpdeskToolResponse(success, success ? "completed" : "failed", "Server response " + secret,
            Data: new JsonObject
            {
                [field] = secret,
                ["message"] = "New credential " + secret + "; previous " + storedSecret,
                ["link"] = "https://desk.example.test/activate?token=" + encoded,
                ["links"] = new JsonArray("https://desk.example.test/activate?token=" + formEncoded),
                ["nested"] = new JsonObject { [field] = secret, ["echo"] = secret },
                ["password"] = storedSecret,
                ["hasSigningSecret"] = true,
                ["sensitiveFields"] = new JsonArray(field)
            },
            Evidence: new JsonObject { [field] = secret, ["link"] = encoded, ["message"] = storedSecret },
            AffectedIds: [secret], NextActions: [encoded, formEncoded], CorrelationId: secret,
            Failure: new HelpdeskToolFailure(secret, false, 500, [encoded]),
            Confirmation: new HelpdeskConfirmation("confirm", true, secret, [encoded]));

        var result = Sanitize(tool, operation, upstream);
        Assert.Equal(success ? secret : "[redacted]", result.Data![field]!.GetValue<string>());
        Assert.True(result.Data["hasSigningSecret"]!.GetValue<bool>());
        Assert.Equal(field, result.Data["sensitiveFields"]![0]!.GetValue<string>());
        if (success) Assert.True(result.Data["sensitiveResult"]!.GetValue<bool>());
        var deliveredRootRemoved = result.Data.DeepClone().AsObject();
        deliveredRootRemoved[field] = "[authorized delivery removed for evidence]";
        var evidence = JsonSerializer.Serialize(result with { Data = deliveredRootRemoved }, new JsonSerializerOptions
            { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        foreach (var forbidden in new[] { secret, encoded, formEncoded, storedSecret, Uri.EscapeDataString(storedSecret) })
            Assert.DoesNotContain(forbidden, evidence, StringComparison.Ordinal);
        Assert.Equal(secret, upstream.Data![field]!.GetValue<string>());
    }

    [Fact]
    public void A_returned_stored_secret_is_scrubbed_from_siblings_even_without_secret_input()
    {
        const string secret = "stored+device/token=";
        var result = Sanitize("helpdesk_connections", "netclaw_settings", new HelpdeskToolResponse(true, "completed", "Read " + secret,
            new JsonObject { ["deviceToken"] = secret, ["message"] = secret,
                ["url"] = "https://desk.example.test/?token=" + Uri.EscapeDataString(secret), ["hasDeviceToken"] = true }));
        Assert.Equal("[redacted]", result.Data!["deviceToken"]!.GetValue<string>());
        Assert.Equal("[redacted]", result.Data["message"]!.GetValue<string>());
        Assert.Equal("Read [redacted]", result.Summary);
        Assert.True(result.Data["hasDeviceToken"]!.GetValue<bool>());
        Assert.DoesNotContain(Uri.EscapeDataString(secret), result.Data["url"]!.GetValue<string>());
    }

    private static HelpdeskToolResponse Sanitize(string tool, string operation, HelpdeskToolResponse response)
    {
        // Exercise the internal boundary with adversarial envelope fields that the
        // current public transport normally fills with constant safe text.
        var assembly = typeof(HelpdeskTools).Assembly;
        var catalog = assembly.GetType("Helpdesk.Mcp.Tools.OperatorOperationCatalog", throwOnError: true)!;
        var adapter = catalog.GetMethod("Find", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [tool, operation]);
        Assert.NotNull(adapter);
        var policy = assembly.GetType("Helpdesk.Mcp.Tools.OperatorSecretPolicy", throwOnError: true)!;
        return Assert.IsType<HelpdeskToolResponse>(policy.GetMethod("SanitizeResponse", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [adapter, new JsonObject(), response]));
    }
}
