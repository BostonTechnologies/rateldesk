using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.AgentClient;
using Helpdesk.Mcp.Configuration;
using Helpdesk.Mcp.Tools;
using NSubstitute;

namespace Helpdesk.Tests.Mcp;

public sealed class OperatorMailSupportTests
{
    [Theory]
    [InlineData("""{"pageSize":1}""")]
    [InlineData("""{"pageSize":101}""")]
    [InlineData("""{"sort":"subject"}""")]
    [InlineData("""{"pageSize":25,"extra":true}""")]
    [InlineData("""{"pageSize":25,"pageSize":50}""")]
    public async Task Pending_delivery_queries_enforce_the_advertised_wire_contract(string json)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(json);

        var result = await tools.helpdesk_mail("pending", request.RootElement);

        Assert.False(result.Success);
        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pending_delivery_query_preserves_supported_sort_and_escapes_filters()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"pageSize":25,"sort":"ticket","organizationId":"org & isolated"}""");

        var result = await tools.helpdesk_mail("pending", request.RootElement);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).GetAsync(Arg.Is<string>(path =>
            path.StartsWith("/api/v1/timeline/pending?", StringComparison.Ordinal) &&
            path.Contains("pageSize=25", StringComparison.Ordinal) &&
            path.Contains("sort=ticket", StringComparison.Ordinal) &&
            path.Contains("organizationId=org%20%26%20isolated", StringComparison.Ordinal)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unconfirmed_sender_change_does_not_send_or_reveal_secret_values()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(SenderRequest("synthetic-password-write-only"));

        var result = await tools.helpdesk_email_settings("save_outgoing", request.RootElement);

        Assert.Equal("confirmation_required", result.Status);
        Assert.DoesNotContain("synthetic-password-write-only", JsonSerializer.Serialize(result));
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("synthetic-password-write-only")]
    public async Task Confirmed_sender_change_preserves_empty_and_write_only_secret_input(string password)
    {
        var (client, tools) = Create();
        client.SendAsync(HttpMethod.Put, "/api/v1/email-settings/mailbox-1/outgoing", Arg.Any<JsonNode?>(), true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["version"] = 1, ["hasSmtpPassword"] = true });
        using var request = JsonDocument.Parse(SenderRequest(password));

        var result = await tools.helpdesk_email_settings("save_outgoing", request.RootElement, confirm: true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Put, "/api/v1/email-settings/mailbox-1/outgoing",
            Arg.Is<JsonNode?>(body => body != null && body["smtpPassword"]!.GetValue<string>() == password), true, Arg.Any<CancellationToken>());
        if (password.Length > 0) Assert.DoesNotContain(password, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Selected_delivery_actions_require_a_bounded_explicit_selection_and_reason()
    {
        var (client, tools) = Create();
        foreach (var json in new[]
        {
            """{"body":{"ids":[],"action":"cancel","reason":"fixture cleanup","confirmed":true}}""",
            """{"body":{"ids":["11111111-1111-1111-1111-111111111111"],"action":"retry-all","reason":"fixture cleanup","confirmed":true}}""",
            """{"body":{"ids":["11111111-1111-1111-1111-111111111111"],"action":"cancel","reason":"","confirmed":true}}"""
        })
        {
            using var request = JsonDocument.Parse(json);
            var result = await tools.helpdesk_mail("pending_actions", request.RootElement, confirm: true);
            Assert.False(result.Success);
            Assert.Equal("validation_failed", result.Status);
        }
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Support_membership_update_uses_both_explicit_group_and_member_identifiers()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"groupId":"group/a","memberId":"member?b","body":{"role":0,"source":0,"isEnabled":false}}""");

        var result = await tools.helpdesk_support("update_member", request.RootElement, confirm: true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Put, "/api/v1/support/groups/group%2Fa/members/member%3Fb",
            Arg.Is<JsonNode?>(body => body != null && body["isEnabled"]!.GetValue<bool>() == false && body["groupId"] == null), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Historical_import_rejects_more_than_the_canonical_twenty_five_receipts()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            mailboxId = "mailbox-fixture",
            body = new { receiptIds = Enumerable.Range(0, 26).Select(_ => Guid.NewGuid()).ToArray(), confirmed = true }
        }));

        var result = await tools.helpdesk_email_settings("historical_import", request.RootElement, confirm: true);

        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sender_revision_retry_requires_every_reviewed_fence_field()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"deliveryId":"delivery-fixture","body":{"expectedOutgoingVersion":2,"confirmed":true}}""");

        var result = await tools.helpdesk_mail("retry_current_outgoing", request.RootElement, confirm: true);

        Assert.Equal("validation_failed", result.Status);
        await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sla_tenant_settings_use_the_organization_identifier_string()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"tenantId":"organization-fixture"}""");

        var result = await tools.helpdesk_sla("tenant_settings", request.RootElement);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).GetAsync("/api/v1/tenant-sla-settings/organization-fixture", true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("09:00:00", "2026-10-20", true)]
    [InlineData("tomorrow morning", "2026-10-20", false)]
    [InlineData("09:00:00", "2026-02-31", false)]
    public async Task Calendar_schema_accepts_the_actual_date_and_timespan_wire_formats(string start, string date, bool expected)
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            body = new
            {
                name = "Isolated fixture", timeZoneId = "UTC",
                weeklyRules = new[] { new { dayOfWeek = 1, isWorkingDay = true, workingHours = new[] { new { start, end = "17:00:00" } } } },
                exceptions = new[] { new { date, isWorkingDayOverride = false, workingHoursOverride = Array.Empty<object>() } }
            }
        }));

        var result = await tools.helpdesk_sla("create_calendar", request.RootElement, confirm: true);

        Assert.Equal(expected, result.Success);
        if (!expected)
        {
            Assert.Equal("validation_failed", result.Status);
            await client.DidNotReceive().SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Dataset_update_transmits_bounded_nested_data_with_explicit_deletion_intent()
    {
        var (client, tools) = Create();
        using var request = JsonDocument.Parse("""{"datasetId":"dataset-fixture","body":{"deleteMissing":false,"rows":[{"externalKey":"row-one","data":{"name":"Fixture","enabled":true,"attributes":{"region":"test"},"tags":["one","two"]}}]}}""");

        var result = await tools.helpdesk_datasets("upsert_rows", request.RootElement, confirm: true);

        Assert.True(result.Success, result.Summary);
        await client.Received(1).SendAsync(HttpMethod.Post, "/api/v1/resources/datasets/dataset-fixture/rows",
            Arg.Is<JsonNode?>(body => body != null && body["deleteMissing"]!.GetValue<bool>() == false &&
                body["rows"]![0]!["data"]!["attributes"]!["region"]!.GetValue<string>() == "test"), true, Arg.Any<CancellationToken>());
    }

    private static string SenderRequest(string password) => JsonSerializer.Serialize(new
    {
        mailboxId = "mailbox-1",
        body = new { version = 0, enabled = false, transport = 0, displayName = "", smtpHost = "smtp.example.test", smtpPort = 587, smtpTlsMode = 1, smtpUsername = "sender", smtpPassword = password }
    });

    private static (IHelpdeskAgentClient Client, HelpdeskTools Tools) Create()
    {
        var client = Substitute.For<IHelpdeskAgentClient>();
        client.Configuration.Returns(new AgentClientConfiguration("https://api.example.test", null, null, null, null, null, null) { CredentialMode = "gateway" });
        client.GetAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        client.SendAsync(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new JsonObject());
        return (client, new HelpdeskTools(client, new AgentClientConfigurationStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))));
    }
}
