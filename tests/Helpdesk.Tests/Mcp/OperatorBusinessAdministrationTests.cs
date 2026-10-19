using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorBusinessAdministrationTests
{
    [Theory]
    [InlineData("update_background_worker_settings", """{"body":{}}""")]
    [InlineData("create_asset", """{"body":{"name":"Fixture"}}""")]
    [InlineData("create_asset", """{"body":{"name":"Fixture","organizationId":"fixture-org","id":"forged-id"}}""")]
    [InlineData("update_asset", """{"assetId":"asset-fixture","body":{"name":"Fixture","organizationId":"fixture-org"}}""")]
    [InlineData("update_instance_branding", """{"body":{"applicationName":"Fixture"}}""")]
    public async Task Administration_writes_reject_implicit_changes_and_unknown_fields(string operation, string json)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(json);

        var result = await tools.helpdesk_tenant_admin(operation, request.RootElement, confirm: true);

        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rule_update_preserves_explicit_clears_and_escaped_route_identifier()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"ruleId":"rule/a","body":{"name":"Fixture","trigger":"ticket.created","conditions":null,"actions":"review"}}""");

        var preview = await tools.helpdesk_automation("update_rule", request.RootElement);
        Assert.Equal("confirmation_required", preview.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var result = await tools.helpdesk_automation("update_rule", request.RootElement, confirm: true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Put, "/api/v1/automation/rules/rule%2Fa",
            Arg.Is<JsonNode?>(body => body != null && body.AsObject().ContainsKey("conditions") && body["conditions"] == null &&
                body["actions"]!.GetValue<string>() == "review" && !body.AsObject().ContainsKey("id")), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Instance_branding_replacement_accepts_explicit_null_reset_fields()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""
            {"body":{"applicationName":"Fixture","organizationName":null,"applicationUrl":null,"organizationUrl":null,
            "supportUrl":null,"supportEmail":null,"logoUrl":null,"compactLogoUrl":null,"faviconUrl":null,
            "emailFromDisplayName":null,"tagline":null}}
            """);

        var result = await tools.helpdesk_tenant_admin("update_instance_branding", request.RootElement, confirm: true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Put, "/api/v1/admin/branding/",
            Arg.Is<JsonNode?>(body => body != null && body.AsObject().Count == 11 && body.AsObject().ContainsKey("logoUrl") && body["logoUrl"] == null), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Explicitly_unassigned_asset_and_disabled_worker_preserve_canonical_values()
    {
        var (client, tools) = Create();
        using var asset = JsonDocument.Parse("""{"body":{"name":"Fixture","organizationId":""}}""");
        using var worker = JsonDocument.Parse("""{"body":{"enabled":false}}""");

        var assetResult = await tools.helpdesk_tenant_admin("create_asset", asset.RootElement, confirm: true);
        var workerResult = await tools.helpdesk_tenant_admin("update_background_worker_settings", worker.RootElement, confirm: true);

        Assert.True(assetResult.Success, assetResult.Summary);
        Assert.True(workerResult.Success, workerResult.Summary);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/assets/",
            Arg.Is<JsonNode?>(body => body != null && body["organizationId"]!.GetValue<string>() == ""), true, Arg.Any<CancellationToken>());
        await client.Received(1).SendAsync(HttpMethod.Put, "/api/v1/ops/hangfire/settings",
            Arg.Is<JsonNode?>(body => body != null && body["enabled"]!.GetValue<bool>() == false), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"brandingTenantId":12}""", true)]
    [InlineData("""{"brandingTenantId":"organization-fixture"}""", false)]
    [InlineData("""{"brandingTenantId":0}""", false)]
    public async Task Tenant_branding_uses_its_canonical_integer_identifier(string json, bool succeeds)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(json);

        var result = await tools.helpdesk_tenant_admin("tenant_branding", request.RootElement);

        Assert.Equal(succeeds, result.Success);
        if (succeeds)
            await client.Received(1).GetAsync("/api/v1/tenants/12/branding", true, Arg.Any<CancellationToken>());
        else
            await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static (IHelpdeskAgentClient Client, HelpdeskTools Tools) Create()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example.test", null, null, null, null, null, null) { CredentialMode = "gateway" });
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return (client, new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
    }
}
